using System.Runtime.InteropServices;

namespace StarSimCore.Interop;

public enum NativeProcessorKind : int
{
    LinearExposure = 1,
    Contrast = 2,
    Gamma = 3,
    RgbBalance = 4,
    Saturation = 5,
    Wavelet = 10,
    UnsharpMask = 11,
    MultiScaleSharpen = 12,
    NoiseReduction = 13,
    Deringing = 14,
    RichardsonLucy = 15,
    RgbAlign = 16,
    AdvancedColor = 17,
    AdvancedTone = 18,
    LocalDetail = 19,
}

public sealed record NativeExpertProcessorRequest(
    NativeProcessorKind Kind,
    bool Enabled,
    NativeProcessingQuality Quality,
    float ResolutionScale,
    IReadOnlyList<float> Values);

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeExpertProcessorParameters
{
    internal const int ValueCapacity = 32;
    private const int ReservedCount = 4;

    internal uint StructSize;
    internal uint AbiVersion;
    internal NativeProcessorKind ProcessorKind;
    internal uint Enabled;
    internal NativeProcessingQuality Quality;
    internal float ResolutionScale;
    internal uint ValueCount;
    internal uint Reserved0;
    internal fixed float Values[ValueCapacity];
    internal fixed ulong Reserved[ReservedCount];

    internal static NativeExpertProcessorParameters FromRequest(NativeExpertProcessorRequest request)
    {
        if (request.Values.Count > ValueCapacity)
            throw new ArgumentOutOfRangeException(nameof(request), "Expert processor has too many values.");
        var result = new NativeExpertProcessorParameters
        {
            StructSize = (uint)sizeof(NativeExpertProcessorParameters),
            AbiVersion = NativeAbi.ExpectedVersion,
            ProcessorKind = request.Kind,
            Enabled = request.Enabled ? 1U : 0U,
            Quality = request.Quality,
            ResolutionScale = request.ResolutionScale,
            ValueCount = (uint)request.Values.Count,
        };
        for (var index = 0; index < request.Values.Count; index++) result.Values[index] = request.Values[index];
        return result;
    }
}

public enum NativeProcessingQuality : int
{
    InteractivePreview = 1,
    FullResolution = 2,
}

public sealed record NativeProcessorRequest(
    NativeProcessorKind Kind,
    bool Enabled,
    NativeProcessingQuality Quality,
    float ResolutionScale,
    float Value0,
    float Value1 = 0,
    float Value2 = 0,
    float Value3 = 0);

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeProcessorParameters
{
    private const int ReservedCount = 4;

    internal uint StructSize;
    internal uint AbiVersion;
    internal NativeProcessorKind ProcessorKind;
    internal uint Enabled;
    internal NativeProcessingQuality Quality;
    internal float ResolutionScale;
    internal float Value0;
    internal float Value1;
    internal float Value2;
    internal float Value3;
    internal fixed ulong Reserved[ReservedCount];

    internal static NativeProcessorParameters FromRequest(NativeProcessorRequest request) =>
        new()
        {
            StructSize = (uint)sizeof(NativeProcessorParameters),
            AbiVersion = NativeAbi.ExpectedVersion,
            ProcessorKind = request.Kind,
            Enabled = request.Enabled ? 1U : 0U,
            Quality = request.Quality,
            ResolutionScale = request.ResolutionScale,
            Value0 = request.Value0,
            Value1 = request.Value1,
            Value2 = request.Value2,
            Value3 = request.Value3,
        };
}
