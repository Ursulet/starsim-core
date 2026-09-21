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
