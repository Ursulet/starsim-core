using System.Runtime.InteropServices;

namespace StarSimCore.Interop;

public sealed record NativeHistogram(
    ulong[] RedOrGray,
    ulong[] Green,
    ulong[] Blue,
    float[] Minimum,
    float[] Maximum,
    double[] Mean,
    uint ChannelCount);

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeHistogramStats
{
    private const int ReservedCount = 4;

    internal uint StructSize;
    internal uint AbiVersion;
    internal uint ChannelCount;
    internal uint BinCount;
    internal fixed float Minimum[3];
    internal fixed float Maximum[3];
    internal fixed double Mean[3];
    internal fixed ulong Reserved[ReservedCount];

    internal static NativeHistogramStats Create(uint binCount) =>
        new()
        {
            StructSize = (uint)sizeof(NativeHistogramStats),
            AbiVersion = NativeAbi.ExpectedVersion,
            BinCount = binCount,
        };

    internal readonly NativeHistogram ToResult(ulong[] red, ulong[] green, ulong[] blue)
    {
        var minimum = new float[3];
        var maximum = new float[3];
        var mean = new double[3];
        fixed (float* minimumPointer = Minimum)
        fixed (float* maximumPointer = Maximum)
        fixed (double* meanPointer = Mean)
        {
            for (var index = 0; index < 3; index++)
            {
                minimum[index] = minimumPointer[index];
                maximum[index] = maximumPointer[index];
                mean[index] = meanPointer[index];
            }
        }
        return new NativeHistogram(red, green, blue, minimum, maximum, mean, ChannelCount);
    }
}
