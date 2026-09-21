using System.Text.Json.Serialization;

namespace StarSimCore.Application.Presets;

public sealed record PresetRecipe(
    int SchemaVersion,
    string PresetId,
    string TargetId,
    string Name,
    string Description,
    string RecipeVersion,
    IReadOnlyList<PresetProcessorConfiguration> Processors,
    BeginnerMacroValues Macros,
    PresetCompatibility Compatibility);

public sealed record PresetProcessorConfiguration(
    string Id,
    bool Enabled,
    IReadOnlyDictionary<string, double> Parameters);

public sealed record BeginnerMacroValues(
    double PresetStrength,
    double Detail,
    double NoiseReduction,
    double Color,
    double Brightness,
    double Contrast,
    IReadOnlyList<double>? WaveletGains = null)
{
    public BeginnerMacroValues Clamp() =>
        this with
        {
            PresetStrength = Math.Clamp(PresetStrength, 0, 100),
            Detail = Math.Clamp(Detail, 0, 100),
            NoiseReduction = Math.Clamp(NoiseReduction, 0, 100),
            Color = Math.Clamp(Color, 0, 100),
            Brightness = Math.Clamp(Brightness, -100, 100),
            Contrast = Math.Clamp(Contrast, -100, 100),
        };
}

public sealed record PresetCompatibility(
    string MinimumAppVersion,
    IReadOnlyList<string> ColorModels,
    IReadOnlyList<string> RequiredProcessors);

[JsonConverter(typeof(JsonStringEnumConverter<ColorStyle>))]
public enum ColorStyle
{
    Natural,
    Neutral,
    Warm,
    Vivid,
}
