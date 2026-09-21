using System.Collections.Immutable;

namespace StarSimCore.Application.Processing;

public static class WaveletParameterMigration
{
    public static ImmutableArray<double> Upgrade(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Count switch
        {
            WaveletMacroMapper.ParameterCount => values.ToImmutableArray(),
            WaveletMacroMapper.PreviousParameterCount => UpgradePrevious(values),
            WaveletMacroMapper.LegacyParameterCount => UpgradeLegacy(values),
            _ => throw new ArgumentException(
                $"Expected {WaveletMacroMapper.LegacyParameterCount}, " +
                $"{WaveletMacroMapper.PreviousParameterCount}, or {WaveletMacroMapper.ParameterCount} wavelet values.",
                nameof(values)),
        };
    }

    /// <summary>
    /// Upgrades the legacy 28-value Amount/Sharpen model to the production
    /// 30-value EnhancementFactor/GaussianWidth model. Legacy post-sharpen is
    /// folded into the single enhancement factor; it is never reinterpreted as width.
    /// </summary>
    public static ImmutableArray<double> UpgradeLegacy(IReadOnlyList<double> legacyValues)
    {
        ArgumentNullException.ThrowIfNull(legacyValues);
        if (legacyValues.Count != WaveletMacroMapper.LegacyParameterCount)
            throw new ArgumentException(
                $"Expected {WaveletMacroMapper.LegacyParameterCount} legacy wavelet values.",
                nameof(legacyValues));

        var schema = BuiltInProcessors.All
            .Single(processor => processor.Id == BuiltInProcessors.WaveletId)
            .ParameterSchema;
        var result = schema.Select(parameter => parameter.DefaultValue).ToArray();
        result[WaveletMacroMapper.GlobalStrengthIndex] = legacyValues[WaveletMacroMapper.GlobalStrengthIndex];
        result[WaveletMacroMapper.InitialLayerWidthIndex] = legacyValues[WaveletMacroMapper.RadiusScaleIndex];
        result[WaveletMacroMapper.LinkedIndex] = legacyValues[WaveletMacroMapper.LinkedIndex];

        for (var layer = 0; layer < WaveletMacroMapper.LayerCount; layer++)
        {
            var offset = WaveletMacroMapper.GetScaleParamOffset(layer);
            result[offset] = Math.Clamp(
                legacyValues[offset] * (1 + legacyValues[offset + 1]),
                schema[offset].Minimum,
                schema[offset].Maximum);
            result[offset + 1] = 0; // no width override; use the selected scheme
            result[offset + 2] = legacyValues[offset + 2];
            result[offset + 3] = legacyValues[offset + 3];
        }

        result[WaveletMacroMapper.SoloLayerIndex] = legacyValues[WaveletMacroMapper.SoloLayerIndex];
        result[WaveletMacroMapper.ScaleSchemeIndex] = (double)WaveletScaleScheme.Dyadic;
        result[WaveletMacroMapper.StepIncrementIndex] = 1;
        result[WaveletMacroMapper.BackendIndex] = (double)WaveletDecompositionBackend.RecursiveGaussian;
        return result.ToImmutableArray();
    }

    public static ImmutableArray<double> UpgradePrevious(IReadOnlyList<double> previousValues)
    {
        ArgumentNullException.ThrowIfNull(previousValues);
        if (previousValues.Count != WaveletMacroMapper.PreviousParameterCount)
            throw new ArgumentException(
                $"Expected {WaveletMacroMapper.PreviousParameterCount} previous wavelet values.",
                nameof(previousValues));

        var result = previousValues.ToList();
        result.Add((double)WaveletDecompositionBackend.RecursiveGaussian);
        return result.ToImmutableArray();
    }
}
