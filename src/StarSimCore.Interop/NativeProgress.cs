using System.Runtime.InteropServices;

namespace StarSimCore.Interop;

public enum NativeProgressStage : int
{
    Processor = 1,
    WaveletLayer = 2,
    RichardsonLucyIteration = 3,
}

public sealed record NativeProgressUpdate(
    NativeProgressStage Stage,
    uint CurrentStep,
    uint TotalSteps,
    double? Fraction);

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeProgressInfo
{
    private const int ReservedCount = 4;

    internal uint StructSize;
    internal uint AbiVersion;
    internal NativeProgressStage Stage;
    internal uint CurrentStep;
    internal uint TotalSteps;
    internal float Fraction;
    internal fixed ulong Reserved[ReservedCount];

    internal readonly NativeProgressUpdate ToUpdate() => new(
        Stage,
        CurrentStep,
        TotalSteps,
        TotalSteps == 0 || Fraction < 0 ? null : Math.Clamp(Fraction, 0, 1));
}
