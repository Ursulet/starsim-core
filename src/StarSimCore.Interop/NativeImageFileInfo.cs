using System.Runtime.InteropServices;

namespace StarSimCore.Interop;

public enum NativeImageFileFormat : int
{
    Png = 1,
    Tiff = 2,
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeImageFileInfo
{
    private const int ReservedCount = 4;

    internal uint StructSize;
    internal uint AbiVersion;
    internal NativeImageFileFormat FileFormat;
    internal uint HadAlpha;
    internal fixed ulong Reserved[ReservedCount];

    internal static NativeImageFileInfo Create() =>
        new()
        {
            StructSize = (uint)sizeof(NativeImageFileInfo),
            AbiVersion = NativeAbi.ExpectedVersion,
        };
}

public sealed record LoadedNativeImage(
    NativeImage Image,
    NativeImageFileFormat FileFormat,
    bool HadAlpha);
