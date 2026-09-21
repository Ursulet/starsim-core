using System.Runtime.InteropServices;
using StarSimCore.Domain.Imaging;

namespace StarSimCore.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeImageDescriptor
{
    internal uint StructSize;
    internal uint AbiVersion;
    internal uint Width;
    internal uint Height;
    internal uint ChannelCount;
    internal uint SourceBitDepth;
    internal ImageColorModel ColorModel;
    internal WorkingPixelFormat WorkingPixelFormat;
    internal ulong Reserved0;
    internal ulong Reserved1;
    internal ulong Reserved2;
    internal ulong Reserved3;
    internal ulong Reserved4;
    internal ulong Reserved5;
    internal ulong Reserved6;
    internal ulong Reserved7;

    internal static NativeImageDescriptor FromMetadata(ImageMetadata metadata, uint abiVersion) =>
        new()
        {
            StructSize = (uint)Marshal.SizeOf<NativeImageDescriptor>(),
            AbiVersion = abiVersion,
            Width = metadata.Width,
            Height = metadata.Height,
            ChannelCount = metadata.ChannelCount,
            SourceBitDepth = metadata.SourceBitDepth,
            ColorModel = metadata.ColorModel,
            WorkingPixelFormat = metadata.WorkingPixelFormat,
        };

    internal ImageMetadata ToMetadata() =>
        new(Width, Height, ChannelCount, SourceBitDepth, ColorModel, WorkingPixelFormat);
}

