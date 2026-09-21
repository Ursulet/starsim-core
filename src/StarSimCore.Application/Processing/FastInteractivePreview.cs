using System.Diagnostics;

namespace StarSimCore.Application.Processing;

public static class FastInteractivePreview
{
    private const double Epsilon = 1e-6;
    private static readonly HashSet<string> CheapModuleIds = new(StringComparer.Ordinal)
    {
        BuiltInProcessors.ExposureId,
        BuiltInProcessors.ContrastId,
        BuiltInProcessors.GammaId,
        BuiltInProcessors.RgbBalanceId,
        BuiltInProcessors.SaturationId,
    };

    public static bool TryRender(
        byte[] completedBgraPixels,
        uint width,
        uint height,
        uint channelCount,
        PipelineSnapshot completedSnapshot,
        PipelineSnapshot targetSnapshot,
        out byte[] previewBgraPixels,
        out TimeSpan elapsed)
    {
        ArgumentNullException.ThrowIfNull(completedBgraPixels);
        ArgumentNullException.ThrowIfNull(completedSnapshot);
        ArgumentNullException.ThrowIfNull(targetSnapshot);
        previewBgraPixels = [];
        elapsed = TimeSpan.Zero;

        if (completedBgraPixels.Length != checked((int)(width * height * 4)) ||
            !HasOnlyCheapChanges(completedSnapshot, targetSnapshot, channelCount))
        {
            return false;
        }

        var oldExposure = State(completedSnapshot, BuiltInProcessors.ExposureId);
        var newExposure = State(targetSnapshot, BuiltInProcessors.ExposureId);
        var oldContrast = State(completedSnapshot, BuiltInProcessors.ContrastId);
        var newContrast = State(targetSnapshot, BuiltInProcessors.ContrastId);
        var oldGamma = State(completedSnapshot, BuiltInProcessors.GammaId);
        var newGamma = State(targetSnapshot, BuiltInProcessors.GammaId);
        var oldBalance = State(completedSnapshot, BuiltInProcessors.RgbBalanceId);
        var newBalance = State(targetSnapshot, BuiltInProcessors.RgbBalanceId);
        var oldSaturation = State(completedSnapshot, BuiltInProcessors.SaturationId);
        var newSaturation = State(targetSnapshot, BuiltInProcessors.SaturationId);

        var previousExposureGain = oldExposure.Enabled ? Math.Pow(2, oldExposure.Parameters[0]) : 1;
        var targetExposureGain = newExposure.Enabled ? Math.Pow(2, newExposure.Parameters[0]) : 1;
        var previousContrast = oldContrast.Enabled ? oldContrast.Parameters[0] : 1;
        var targetContrast = newContrast.Enabled ? newContrast.Parameters[0] : 1;
        var previousGamma = oldGamma.Enabled ? oldGamma.Parameters[0] : 1;
        var targetGamma = newGamma.Enabled ? newGamma.Parameters[0] : 1;
        var previousSaturation = channelCount == 3 && oldSaturation.Enabled ? oldSaturation.Parameters[0] : 1;
        var targetSaturation = channelCount == 3 && newSaturation.Enabled ? newSaturation.Parameters[0] : 1;
        var previousBalance = Balance(oldBalance, channelCount);
        var targetBalance = Balance(newBalance, channelCount);

        if (previousContrast <= Epsilon || previousSaturation <= Epsilon ||
            previousBalance.Any(value => value <= Epsilon))
        {
            return false;
        }

        var inverseGamma = CreateLookup(value => Math.Pow(value, previousGamma), 255);
        var targetGammaLookup = CreateLookup(
            value => Math.Pow(Math.Max(0, value), 1 / targetGamma),
            8192);
        var stopwatch = Stopwatch.StartNew();
        previewBgraPixels = GC.AllocateUninitializedArray<byte>(completedBgraPixels.Length);

        for (var pixel = 0; pixel < completedBgraPixels.Length; pixel += 4)
        {
            var blue = inverseGamma[completedBgraPixels[pixel]];
            var green = inverseGamma[completedBgraPixels[pixel + 1]];
            var red = inverseGamma[completedBgraPixels[pixel + 2]];

            red = UndoTone(red, previousContrast, previousExposureGain);
            green = UndoTone(green, previousContrast, previousExposureGain);
            blue = UndoTone(blue, previousContrast, previousExposureGain);

            if (channelCount == 3)
            {
                var luminance = Luminance(red, green, blue);
                red = luminance + (red - luminance) / previousSaturation;
                green = luminance + (green - luminance) / previousSaturation;
                blue = luminance + (blue - luminance) / previousSaturation;

                red = red / previousBalance[0] * targetBalance[0];
                green = green / previousBalance[1] * targetBalance[1];
                blue = blue / previousBalance[2] * targetBalance[2];

                luminance = Luminance(red, green, blue);
                red = luminance + (red - luminance) * targetSaturation;
                green = luminance + (green - luminance) * targetSaturation;
                blue = luminance + (blue - luminance) * targetSaturation;
            }

            previewBgraPixels[pixel] = ToByte(ApplyTone(blue, targetExposureGain, targetContrast), targetGammaLookup);
            previewBgraPixels[pixel + 1] = ToByte(ApplyTone(green, targetExposureGain, targetContrast), targetGammaLookup);
            previewBgraPixels[pixel + 2] = ToByte(ApplyTone(red, targetExposureGain, targetContrast), targetGammaLookup);
            previewBgraPixels[pixel + 3] = completedBgraPixels[pixel + 3];
        }

        stopwatch.Stop();
        elapsed = stopwatch.Elapsed;
        return true;
    }

    public static bool HasOnlyCheapChanges(
        PipelineSnapshot completed,
        PipelineSnapshot target,
        uint channelCount)
    {
        if (completed.Modules.Length != target.Modules.Length) return false;
        for (var index = 0; index < completed.Modules.Length; index++)
        {
            var previous = completed.Modules[index];
            var next = target.Modules[index];
            if (!string.Equals(previous.Id, next.Id, StringComparison.Ordinal)) return false;
            if (previous.Enabled != next.Enabled) return false;
            if (!CheapModuleIds.Contains(previous.Id) && !StateEquals(previous, next)) return false;
            if (channelCount != 3 &&
                previous.Id is BuiltInProcessors.RgbBalanceId or BuiltInProcessors.SaturationId)
            {
                continue;
            }
        }
        return true;
    }

    private static bool StateEquals(ProcessorState left, ProcessorState right) =>
        left.Enabled == right.Enabled &&
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        left.Parameters.AsSpan().SequenceEqual(right.Parameters.AsSpan());

    private static ProcessorState State(PipelineSnapshot snapshot, string id) => snapshot.GetModule(id);

    private static double[] Balance(ProcessorState state, uint channelCount) =>
        channelCount == 3 && state.Enabled ? state.Parameters.ToArray() : [1, 1, 1];

    private static double UndoTone(double value, double contrast, double exposureGain) =>
        ((value - 0.5) / contrast + 0.5) / exposureGain;

    private static double ApplyTone(double value, double exposureGain, double contrast) =>
        (value * exposureGain - 0.5) * contrast + 0.5;

    private static double Luminance(double red, double green, double blue) =>
        0.2126 * red + 0.7152 * green + 0.0722 * blue;

    private static double[] CreateLookup(Func<double, double> transform, int maximumIndex)
    {
        var result = new double[maximumIndex + 1];
        for (var index = 0; index <= maximumIndex; index++)
            result[index] = transform(index / (double)maximumIndex);
        return result;
    }

    private static byte ToByte(double value, double[] gammaLookup)
    {
        var normalized = Math.Clamp(value, 0, 1);
        var index = (int)Math.Round(normalized * (gammaLookup.Length - 1));
        return (byte)Math.Clamp((int)Math.Round(gammaLookup[index] * 255), 0, 255);
    }
}
