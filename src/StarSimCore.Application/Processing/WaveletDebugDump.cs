using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
using StarSimCore.Interop;

namespace StarSimCore.Application.Processing;

/// <summary>
/// Expert diagnostic export from the production wavelet processor. It contains no
/// decomposition math: D1..D6, L6, and reconstruction are all requested from the
/// registered core.wavelet implementation.
/// </summary>
public static class WaveletDebugDump
{
    public enum TiffEncoding
    {
        Float32,
        UInt16,
    }

    public static IReadOnlyList<string> Dump(
        NativeImage source,
        ProcessorState waveletState,
        string destinationDirectory,
        TiffEncoding encoding = TiffEncoding.Float32,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(waveletState);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var processor = BuiltInProcessors.All.Single(item => item.Id == BuiltInProcessors.WaveletId);
        var context = new ProcessorContext(
            ProcessingQuality.FullResolution,
            1,
            source.GetMetadata());
        var written = new List<string>();
        var bandMetrics = new List<object>();
        var presetMetrics = new List<object>();
        var layerProcessingMilliseconds = new double[WaveletMacroMapper.LayerCount];
        var lastLayerTimestamp = Stopwatch.GetTimestamp();
        var layerTimingContext = context with
        {
            ReportProgress = progress =>
            {
                var now = Stopwatch.GetTimestamp();
                if (progress.CurrentStep == 0)
                {
                    lastLayerTimestamp = now;
                    return;
                }
                if (!string.Equals(progress.Stage, "Wavelet layer", StringComparison.Ordinal)) return;
                var layerIndex = (int)((progress.CurrentStep - 1) % WaveletMacroMapper.LayerCount);
                layerProcessingMilliseconds[layerIndex] +=
                    Stopwatch.GetElapsedTime(lastLayerTimestamp, now).TotalMilliseconds;
                lastLayerTimestamp = now;
            },
        };
        var exportFormat = encoding == TiffEncoding.Float32
            ? NativeExportFormat.TiffFloat32
            : NativeExportFormat.Tiff16;
        var encodingName = encoding == TiffEncoding.Float32 ? "float" : "uint16";

        var sourcePath = Path.Combine(destinationDirectory, $"source.{encodingName}.tif");
        source.ExportFile(sourcePath, exportFormat, cancellationToken);
        written.Add(sourcePath);

        var neutral = waveletState.Parameters.ToBuilder();
        neutral[WaveletMacroMapper.GlobalStrengthIndex] = 1;
        neutral[WaveletMacroMapper.LinkedIndex] = 0;
        var isAtrous = neutral.Count > WaveletMacroMapper.BackendIndex &&
            neutral[WaveletMacroMapper.BackendIndex] == (double)WaveletDecompositionBackend.AtrousB3Spline;
        for (var layer = 0; layer < WaveletMacroMapper.LayerCount; layer++)
        {
            var offset = WaveletMacroMapper.GetScaleParamOffset(layer);
            neutral[offset] = 1;
            neutral[offset + 2] = 0;
            neutral[offset + 3] = 0;
        }

        for (var layer = 0; layer < WaveletMacroMapper.LayerCount; layer++)
        {
            var solo = neutral.ToImmutable().SetItem(WaveletMacroMapper.SoloLayerIndex, layer);
            using var centeredBand = processor.Process(
                source,
                waveletState with { Enabled = true, Parameters = solo },
                layer == 0 ? layerTimingContext : context,
                cancellationToken);
            var raw = centeredBand.CopyPlanarPixels();
            for (var index = 0; index < raw.Length; index++) raw[index] -= 0.5f;
            var exportPixels = encoding == TiffEncoding.Float32
                ? raw
                : raw.Select(value => value + 0.5f).ToArray();
            using var rawBand = NativeImage.CreateMasterFromPlanar(context.Metadata, exportPixels);
            var path = Path.Combine(destinationDirectory, $"D{layer + 1}.{encodingName}.tif");
            rawBand.ExportFile(path, exportFormat, cancellationToken);
            written.Add(path);

            var width = isAtrous ? Math.Pow(2, layer) : ResolveWidth(neutral, layer);
            var cumulativeSigma = isAtrous
                ? Math.Sqrt(Enumerable.Range(0, layer + 1).Sum(index => Math.Pow(4, index)))
                : ResolveCumulativeSigma(neutral, layer);
            var stats = Measure(raw);
            bandMetrics.Add(new
            {
                Layer = layer + 1,
                Backend = isAtrous ? "AtrousB3Spline" : "RecursiveGaussian",
                AnalysisScalePixels = width,
                KernelSupportRadiusPixels = isAtrous
                    ? 2 * (1 << layer)
                    : Math.Min(64, Math.Max(1, (int)Math.Ceiling(3 * width))),
                CumulativeScalePixels = cumulativeSigma,
                ApproximateStructureDiameterPixels = 2 * cumulativeSigma,
                stats.Rms,
                stats.Minimum,
                stats.Maximum,
                ProcessingMilliseconds = layerProcessingMilliseconds[layer],
            });
        }

        var residual = neutral.ToImmutable().SetItem(WaveletMacroMapper.SoloLayerIndex, -1);
        for (var layer = 0; layer < WaveletMacroMapper.LayerCount; layer++)
            residual = residual.SetItem(WaveletMacroMapper.GetScaleParamOffset(layer), 0);
        using (var lowPass = processor.Process(
                   source,
                   waveletState with { Enabled = true, Parameters = residual },
                   context,
                   cancellationToken))
        {
            var path = Path.Combine(destinationDirectory, $"L6.{encodingName}.tif");
            lowPass.ExportFile(path, exportFormat, cancellationToken);
            written.Add(path);
        }

        var reconstructionTimer = Stopwatch.StartNew();
        var reconstructionParameters = waveletState.Parameters
            .SetItem(WaveletMacroMapper.SoloLayerIndex, -1);
        using (var reconstruction = processor.Process(
                   source,
                   waveletState with { Enabled = true, Parameters = reconstructionParameters },
                   context,
                   cancellationToken))
        {
            reconstructionTimer.Stop();
            var path = Path.Combine(destinationDirectory, $"reconstruction.{encodingName}.tif");
            reconstruction.ExportFile(path, exportFormat, cancellationToken);
            written.Add(path);
        }

        // Deterministic calibration states: residual plus exactly one enabled
        // detail band. These execute the registered production processor.
        foreach (var layer in Enumerable.Range(0, WaveletMacroMapper.LayerCount))
        foreach (var gain in new[] { 0.0, 0.5, 1.0, 1.5, 2.0 })
        {
            var preset = neutral.ToImmutable().SetItem(WaveletMacroMapper.SoloLayerIndex, -1);
            for (var index = 0; index < WaveletMacroMapper.LayerCount; index++)
                preset = preset.SetItem(WaveletMacroMapper.GetScaleParamOffset(index), index == layer ? gain : 0);

            var timer = Stopwatch.StartNew();
            using var result = processor.Process(
                source,
                waveletState with { Enabled = true, Parameters = preset },
                context,
                cancellationToken);
            timer.Stop();
            var stats = Measure(result.CopyPlanarPixels());
            presetMetrics.Add(new
            {
                Name = FormattableString.Invariant($"L{layer + 1}-only-gain-{gain:0.0}"),
                Layer = layer + 1,
                Gain = gain,
                OtherLayerGains = 0,
                stats.Rms,
                stats.Minimum,
                stats.Maximum,
                ProcessingMilliseconds = timer.Elapsed.TotalMilliseconds,
            });
        }

        var combined = neutral.ToImmutable().SetItem(WaveletMacroMapper.SoloLayerIndex, -1);
        for (var layer = 0; layer < WaveletMacroMapper.LayerCount; layer++)
            combined = combined.SetItem(WaveletMacroMapper.GetScaleParamOffset(layer), layer < 3 ? 1.5 : 0);
        var combinedTimer = Stopwatch.StartNew();
        using (var combinedResult = processor.Process(
                   source,
                   waveletState with { Enabled = true, Parameters = combined },
                   context,
                   cancellationToken))
        {
            combinedTimer.Stop();
            var combinedPath = Path.Combine(destinationDirectory, $"L1-L2-L3.gain-1.5.{encodingName}.tif");
            combinedResult.ExportFile(combinedPath, exportFormat, cancellationToken);
            written.Add(combinedPath);
            var stats = Measure(combinedResult.CopyPlanarPixels());
            presetMetrics.Add(new
            {
                Name = "L1-L2-L3-gain-1.5",
                Layer = 0,
                Gain = 1.5,
                OtherLayerGains = 0,
                stats.Rms,
                stats.Minimum,
                stats.Maximum,
                ProcessingMilliseconds = combinedTimer.Elapsed.TotalMilliseconds,
            });
        }

        var manifestPath = Path.Combine(destinationDirectory, "wavelet-debug-dump.json");
        File.WriteAllText(
            manifestPath,
            JsonSerializer.Serialize(new
            {
                Processor = BuiltInProcessors.WaveletId,
                ProcessorVersion = processor.SemanticVersion.ToString(),
                Pipeline = "production",
                Encoding = encoding.ToString(),
                DecompositionBackend = isAtrous ? "AtrousB3Spline" : "RecursiveGaussian",
                ReconstructionProcessingMilliseconds = reconstructionTimer.Elapsed.TotalMilliseconds,
                WidthScheme = neutral[WaveletMacroMapper.ScaleSchemeIndex] == (double)WaveletScaleScheme.Dyadic
                    ? "Dyadic"
                    : "Linear",
                Bands = bandMetrics,
                TestPresets = presetMetrics,
                Files = written.Select(Path.GetFileName).ToArray(),
            }, new JsonSerializerOptions { WriteIndented = true }));
        written.Add(manifestPath);
        return written;
    }

    private static double ResolveWidth(IReadOnlyList<double> values, int layer)
    {
        var overrideWidth = values[WaveletMacroMapper.GetScaleParamOffset(layer) + 1];
        if (overrideWidth > 0) return overrideWidth;
        var initial = values[WaveletMacroMapper.InitialLayerWidthIndex];
        return values[WaveletMacroMapper.ScaleSchemeIndex] == (double)WaveletScaleScheme.Dyadic
            ? initial * Math.Pow(2, layer)
            : initial + values[WaveletMacroMapper.StepIncrementIndex] * layer;
    }

    private static double ResolveCumulativeSigma(IReadOnlyList<double> values, int layer)
    {
        double variance = 0;
        for (var index = 0; index <= layer; index++)
        {
            var width = ResolveWidth(values, index);
            variance += width * width;
        }
        return Math.Sqrt(variance);
    }

    private static (double Rms, float Minimum, float Maximum) Measure(float[] values)
    {
        double sumSquares = 0;
        var minimum = float.PositiveInfinity;
        var maximum = float.NegativeInfinity;
        foreach (var value in values)
        {
            sumSquares += value * value;
            minimum = Math.Min(minimum, value);
            maximum = Math.Max(maximum, value);
        }
        return (Math.Sqrt(sumSquares / values.Length), minimum, maximum);
    }
}
