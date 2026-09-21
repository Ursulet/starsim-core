namespace StarSimCore.Domain.Imaging;

public sealed record ImageMetadata(
    uint Width,
    uint Height,
    uint ChannelCount,
    uint SourceBitDepth,
    ImageColorModel ColorModel,
    WorkingPixelFormat WorkingPixelFormat = WorkingPixelFormat.Float32Planar)
{
    public ulong PixelValueCount => checked((ulong)Width * Height * ChannelCount);
}

