using StarSimCore.Domain.Imaging;
using StarSimCore.Interop;

namespace StarSimCore.Application.Imaging;

public sealed class ImageDocument : IDisposable
{
    private NativeImage? master;
    private NativeImage? processed;

    internal ImageDocument(SourceImageInfo source, NativeImage master, NativeImage processed)
    {
        Source = source;
        this.master = master;
        this.processed = processed;
        ContextId = Guid.NewGuid();
        SourceIdentity = master.GetIdentity().SourceIdentity;
    }

    public SourceImageInfo Source { get; }
    public Guid ContextId { get; }
    public ulong SourceIdentity { get; }

    public bool IsDisposed => master is null;

    internal NativeImage Master =>
        master ?? throw new ObjectDisposedException(nameof(ImageDocument));

    internal NativeImage Processed =>
        processed ?? throw new ObjectDisposedException(nameof(ImageDocument));

    public byte[] RenderOriginalBgra8() => Master.RenderBgra8();

    public byte[] RenderProcessedBgra8() => Processed.RenderBgra8();

    public NativeImage CloneImmutableSource() => Master.CloneWorking();

    /// <summary>
    /// Creates an independent native document containing a rectangular region of
    /// the immutable source. The original document and its processed image are not
    /// modified. This is used only for fast region-of-interest previews.
    /// </summary>
    public ImageDocument CreateRegion(uint x, uint y, uint width, uint height)
    {
        var sourceMetadata = Source.Metadata;
        if (width == 0 || height == 0 || x >= sourceMetadata.Width || y >= sourceMetadata.Height ||
            width > sourceMetadata.Width - x || height > sourceMetadata.Height - y)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "The ROI must be inside the source image.");
        }

        var sourcePixels = Master.CopyPlanarPixels();
        var sourcePlaneLength = checked((int)((ulong)sourceMetadata.Width * sourceMetadata.Height));
        var regionPlaneLength = checked((int)((ulong)width * height));
        var regionPixels = new float[checked(regionPlaneLength * (int)sourceMetadata.ChannelCount)];

        for (var channel = 0; channel < sourceMetadata.ChannelCount; channel++)
        {
            var sourcePlaneOffset = checked((int)channel * sourcePlaneLength);
            var regionPlaneOffset = checked((int)channel * regionPlaneLength);
            for (var row = 0U; row < height; row++)
            {
                var sourceOffset = checked(sourcePlaneOffset + (int)((y + row) * sourceMetadata.Width + x));
                var regionOffset = checked(regionPlaneOffset + (int)(row * width));
                Array.Copy(sourcePixels, sourceOffset, regionPixels, regionOffset, checked((int)width));
            }
        }

        var regionMetadata = sourceMetadata with { Width = width, Height = height };
        NativeImage? regionMaster = null;
        NativeImage? regionProcessed = null;
        try
        {
            regionMaster = NativeImage.CreateMasterFromPlanar(regionMetadata, regionPixels);
            regionProcessed = regionMaster.CloneWorking();
            var identity = regionMaster.GetIdentity().SourceIdentity;
            var regionSource = Source with
            {
                FileName = $"{Source.FileName} [ROI {x},{y} {width}×{height}]",
                Metadata = regionMetadata,
                SourceIdentity = identity,
            };
            var result = new ImageDocument(regionSource, regionMaster, regionProcessed);
            regionMaster = null;
            regionProcessed = null;
            return result;
        }
        finally
        {
            regionProcessed?.Dispose();
            regionMaster?.Dispose();
        }
    }

    internal void ReplaceProcessed(NativeImage replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var previous = Interlocked.Exchange(ref processed, replacement);
        previous?.Dispose();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref processed, null)?.Dispose();
        Interlocked.Exchange(ref master, null)?.Dispose();
    }
}
