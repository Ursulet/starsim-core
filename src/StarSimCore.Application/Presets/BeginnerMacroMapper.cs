using System.Collections.Immutable;
using StarSimCore.Application.Processing;
using StarSimCore.Domain.Imaging;

namespace StarSimCore.Application.Presets;

public enum BeginnerMacroControl
{
    PresetStrength,
    Detail,
    NoiseReduction,
    Color,
    Brightness,
    Contrast,
    ColorStyle,
}

public static class BeginnerMacroMapper
{
    private static readonly double[] NeutralWaveletGains = [1, 1, 1, 1, 1, 1];

    // Beginner profiles need useful detail headroom without flattening their
    // target-specific L1-L6 shape. At the recipe's nominal Detail value, L1 is
    // raised by exactly ten gain points and every other layer's contribution
    // above neutral (gain 1) is scaled by the same factor. This makes a profile
    // L1 value of 47 resolve to 57 while a broad gain of 2 remains close to 2,
    // rather than being incorrectly raised to 12.
    private const double BeginnerFineDetailBoostPoints = 10.0;

    public static PipelineSnapshot Map(
        PresetRecipe recipe,
        BeginnerMacroValues requestedMacros,
        ColorStyle colorStyle,
        ImageMetadata metadata,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(metadata);
        PresetCatalog.Validate(recipe);
        var macros = requestedMacros.Clamp();
        var modules = ImmutableArray.CreateBuilder<ProcessorState>(BuiltInProcessors.All.Count);

        foreach (var definition in BuiltInProcessors.All)
        {
            var source = ResolveConfiguration(recipe, definition);
            var parameters = CreateBaseParameters(definition, source, macros.PresetStrength);
            ApplyOwnedValues(definition.Id, parameters, recipe, macros, colorStyle, metadata);

            var enabled = source.Enabled && IsCompatible(definition, metadata);
            if (definition.Id == BuiltInProcessors.WaveletId)
                enabled = IsCompatible(definition, metadata);
            else if (definition.Id == BuiltInProcessors.NoiseReductionId)
                enabled = macros.NoiseReduction > 0.01 && IsCompatible(definition, metadata);

            modules.Add(new ProcessorState(definition.Id, enabled, parameters.ToImmutableArray()));
        }

        return new PipelineSnapshot(modules.ToImmutable(), revision);
    }

    /// <summary>
    /// Applies only the parameters owned by one Beginner control. A simple
    /// Brightness, Noise, or Color adjustment must not rebuild unrelated Expert
    /// state or overwrite calibrated Wavelet advanced parameters.
    /// </summary>
    public static PipelineSnapshot ApplyControl(
        PipelineSnapshot current,
        PresetRecipe recipe,
        BeginnerMacroValues requestedMacros,
        ColorStyle colorStyle,
        ImageMetadata metadata,
        BeginnerMacroControl control,
        long revision)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(metadata);
        var macros = requestedMacros.Clamp();
        var modules = current.Modules.ToBuilder();

        void ReplaceConfigured(string processorId)
        {
            var definition = BuiltInProcessors.All.First(item => item.Id == processorId);
            var source = ResolveConfiguration(recipe, definition);
            var parameters = CreateBaseParameters(definition, source, macros.PresetStrength);
            ApplyOwnedValues(processorId, parameters, recipe, macros, colorStyle, metadata);
            ReplaceModule(
                modules,
                processorId,
                source.Enabled && IsCompatible(definition, metadata),
                parameters);
        }

        switch (control)
        {
            case BeginnerMacroControl.PresetStrength:
                ReplaceConfigured(BuiltInProcessors.ExposureId);
                ReplaceConfigured(BuiltInProcessors.ContrastId);
                ReplaceConfigured(BuiltInProcessors.GammaId);
                ReplaceConfigured(BuiltInProcessors.RgbBalanceId);
                ReplaceConfigured(BuiltInProcessors.SaturationId);
                break;

            case BeginnerMacroControl.Detail:
            {
                var state = GetModule(modules, BuiltInProcessors.WaveletId);
                var parameters = state.Parameters.ToArray();
                ApplyWaveletGains(parameters, recipe, macros);
                ReplaceModule(modules, state.Id, true, parameters);
                break;
            }

            case BeginnerMacroControl.NoiseReduction:
            {
                var state = GetModule(modules, BuiltInProcessors.NoiseReductionId);
                var parameters = state.Parameters.ToArray();
                ApplyNoiseReduction(parameters, macros, metadata);
                ReplaceModule(modules, state.Id, macros.NoiseReduction > 0.01, parameters);
                break;
            }

            case BeginnerMacroControl.Color:
            case BeginnerMacroControl.ColorStyle:
                ReplaceConfigured(BuiltInProcessors.RgbBalanceId);
                ReplaceConfigured(BuiltInProcessors.SaturationId);
                break;

            case BeginnerMacroControl.Brightness:
                ReplaceConfigured(BuiltInProcessors.ExposureId);
                break;

            case BeginnerMacroControl.Contrast:
                ReplaceConfigured(BuiltInProcessors.ContrastId);
                break;
        }

        return new PipelineSnapshot(modules.ToImmutable(), revision);
    }

    private static PresetProcessorConfiguration ResolveConfiguration(
        PresetRecipe recipe,
        IImageProcessor definition) =>
        recipe.Processors.FirstOrDefault(processor => processor.Id == definition.Id) ??
        new PresetProcessorConfiguration(
            definition.Id,
            definition.IsEnabledByDefault,
            definition.ParameterSchema.ToDictionary(parameter => parameter.Id, parameter => parameter.DefaultValue));

    private static double[] CreateBaseParameters(
        IImageProcessor definition,
        PresetProcessorConfiguration source,
        double presetStrength)
    {
        var strength = Math.Clamp(presetStrength, 0, 100) / 100.0;
        return definition.ParameterSchema
            .Select(parameter => InterpolateParameter(definition.Id, parameter.Id, source.Parameters[parameter.Id], strength))
            .ToArray();
    }

    private static void ApplyOwnedValues(
        string processorId,
        double[] parameters,
        PresetRecipe recipe,
        BeginnerMacroValues macros,
        ColorStyle colorStyle,
        ImageMetadata metadata)
    {
        switch (processorId)
        {
            case BuiltInProcessors.ExposureId:
                parameters[0] = Math.Clamp(parameters[0] + macros.Brightness / 100.0, -4, 4);
                break;

            case BuiltInProcessors.ContrastId:
                parameters[0] = Math.Clamp(1 + (parameters[0] - 1) + macros.Contrast / 200.0, 0, 3);
                break;

            case BuiltInProcessors.RgbBalanceId:
                ApplyColorStyle(parameters, colorStyle, metadata);
                break;

            case BuiltInProcessors.SaturationId:
                ApplySaturation(parameters, macros.Color, colorStyle, metadata);
                break;

            case BuiltInProcessors.WaveletId:
                ApplyWaveletGains(parameters, recipe, macros);
                break;

            case BuiltInProcessors.NoiseReductionId:
                ApplyNoiseReduction(parameters, macros, metadata);
                break;
        }
    }

    private static void ApplyWaveletGains(
        double[] parameters,
        PresetRecipe recipe,
        BeginnerMacroValues macros)
    {
        var profile = recipe.Macros.WaveletGains is { Count: WaveletMacroMapper.LayerCount } gains
            ? gains
            : NeutralWaveletGains;
        var referenceDetail = Math.Max(1.0, recipe.Macros.Detail - 50.0);
        var detailIntensity = Math.Clamp((macros.Detail - 50.0) / referenceDetail, 0.0, 2.0);
        var attenuation = Math.Clamp(macros.Detail / 50.0, 0.0, 1.0);
        var fineContribution = Math.Max(0.0, profile[0] - 1.0);
        var contributionBoost = fineContribution > 1e-9
            ? (fineContribution + BeginnerFineDetailBoostPoints) / fineContribution
            : 1.0;

        for (var layer = 0; layer < WaveletMacroMapper.LayerCount; layer++)
        {
            var offset = WaveletMacroMapper.GetScaleParamOffset(layer);
            parameters[offset] = macros.Detail < 50
                ? attenuation
                : Math.Clamp(
                    1.0 + (profile[layer] - 1.0) * contributionBoost * detailIntensity,
                    0.0,
                    100.0);
        }
    }

    private static void ApplyNoiseReduction(
        double[] parameters,
        BeginnerMacroValues macros,
        ImageMetadata metadata)
    {
        var amount = Math.Clamp(macros.NoiseReduction, 0, 100);
        parameters[0] = amount / 200.0;
        parameters[1] = metadata.ColorModel == ImageColorModel.Rgb ? amount / 180.0 : 0;
        parameters[2] = Math.Clamp(0.7 + amount / 500.0, 0, 1);
        parameters[3] = 1.0;
    }

    private static void ApplySaturation(
        double[] parameters,
        double color,
        ColorStyle colorStyle,
        ImageMetadata metadata)
    {
        var colorAmount = 0.75 + Math.Clamp(color, 0, 100) / 200.0;
        var styleAmount = colorStyle switch
        {
            ColorStyle.Neutral => 0.92,
            ColorStyle.Warm => 1.02,
            ColorStyle.Vivid => 1.15,
            _ => 1.0,
        };
        parameters[0] = metadata.ColorModel == ImageColorModel.Grayscale
            ? 1
            : Math.Clamp(parameters[0] * colorAmount * styleAmount, 0, 2);
    }

    private static double InterpolateParameter(string processorId, string parameterId, double recipe, double strength)
    {
        var neutral = BuiltInProcessors.All
            .First(processor => processor.Id == processorId)
            .ParameterSchema.First(parameter => parameter.Id == parameterId)
            .DefaultValue;
        if (strength == 0 || recipe == neutral) return neutral;
        if (neutral > 0 && recipe > 0 && processorId != BuiltInProcessors.ExposureId)
        {
            return Math.Exp(Math.Log(neutral) + (Math.Log(recipe) - Math.Log(neutral)) * strength);
        }
        return neutral + (recipe - neutral) * strength;
    }

    private static void ApplyColorStyle(double[] values, ColorStyle style, ImageMetadata metadata)
    {
        if (metadata.ColorModel == ImageColorModel.Grayscale)
        {
            values[0] = values[1] = values[2] = 1;
            return;
        }
        switch (style)
        {
            case ColorStyle.Neutral:
                for (var index = 0; index < 3; index++) values[index] = 1 + (values[index] - 1) * 0.5;
                break;
            case ColorStyle.Warm:
                values[0] *= 1.04;
                values[2] *= 0.96;
                break;
            case ColorStyle.Vivid:
                for (var index = 0; index < 3; index++) values[index] = 1 + (values[index] - 1) * 1.1;
                break;
        }
        for (var index = 0; index < 3; index++) values[index] = Math.Clamp(values[index], 0, 2);
    }

    private static ProcessorState GetModule(ImmutableArray<ProcessorState>.Builder modules, string id)
    {
        for (var index = 0; index < modules.Count; index++)
        {
            if (modules[index].Id == id) return modules[index];
        }
        throw new ArgumentOutOfRangeException(nameof(id));
    }

    private static void ReplaceModule(
        ImmutableArray<ProcessorState>.Builder modules,
        string id,
        bool enabled,
        IReadOnlyList<double> parameters)
    {
        for (var index = 0; index < modules.Count; index++)
        {
            if (modules[index].Id != id) continue;
            modules[index] = new ProcessorState(id, enabled, parameters.ToImmutableArray());
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(id));
    }

    private static bool IsCompatible(IImageProcessor processor, ImageMetadata metadata) =>
        metadata.ColorModel == ImageColorModel.Rgb
            ? processor.Capabilities.HasFlag(ProcessorCapabilities.Rgb)
            : processor.Capabilities.HasFlag(ProcessorCapabilities.Grayscale);
}
