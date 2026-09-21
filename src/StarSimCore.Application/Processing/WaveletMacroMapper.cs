namespace StarSimCore.Application.Processing;

public static class WaveletMacroMapper
{
    public const int LayerCount = 6;
    public const int ParametersPerLayer = 4;

    public record WaveletScaleParameters(double EnhancementFactor);

    /// <summary>
    /// Central conversion from the Classic [0,100] presentation range to the
    /// canonical LayerEnhancementFactor. The displayed value is the actual band
    /// multiplier: 0 removes the band and 1 is neutral. Keeping this conversion
    /// explicit prevents the UI from silently weakening a requested factor (for
    /// example, the former mapping turned 52.7 into only 1.527).
    /// </summary>
    public static WaveletScaleParameters MapClassicSlider(double sliderValue)
    {
        return new WaveletScaleParameters(Math.Clamp(sliderValue, 0.0, 100.0));
    }

    /// <summary>
    /// Inverse mapping for the direct Classic factor display.
    /// </summary>
    public static double InvertToClassicSlider(double enhancementFactor)
    {
        return Math.Clamp(enhancementFactor, 0.0, 100.0);
    }

    /// <summary>
    /// Returns the parameter index offset for scale i (0..5) in the wavelet schema.
    /// Schema: [0]=globalStrength, [1]=initialLayerWidth, [2]=linked,
    /// followed by 6 * 4 layer parameters
    /// (LayerEnhancementFactor, GaussianWidthOverride, Denoise, Threshold),
    /// [27]=soloLayer, [28]=scaleScheme, [29]=stepIncrement,
    /// [30]=decompositionBackend.
    /// </summary>
    public static int GetScaleParamOffset(int scaleIndex)
    {
        if (scaleIndex < 0 || scaleIndex >= LayerCount)
            throw new ArgumentOutOfRangeException(nameof(scaleIndex));
        return 3 + scaleIndex * ParametersPerLayer;
    }

    public const int GlobalStrengthIndex = 0;
    public const int InitialLayerWidthIndex = 1;
    public const int RadiusScaleIndex = InitialLayerWidthIndex; // compatibility alias
    public const int LinkedIndex = 2;
    public const int SoloLayerIndex = 27;
    public const int ScaleSchemeIndex = 28;
    public const int StepIncrementIndex = 29;
    public const int BackendIndex = 30;
    public const int ParameterCount = 31;
    public const int PreviousParameterCount = 30;
    public const int LegacyParameterCount = 28;
}

public enum WaveletScaleScheme
{
    Linear = 0,
    Dyadic = 1,
}

public enum WaveletDecompositionBackend
{
    RecursiveGaussian = 0,
    AtrousB3Spline = 1,
}
