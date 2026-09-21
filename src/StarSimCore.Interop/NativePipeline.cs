namespace StarSimCore.Interop;

public sealed class NativePipeline : IDisposable
{
    private readonly StarSimCore.Domain.Imaging.ImageMetadata metadata;

    private NativePipeline(SscPipelineSafeHandle handle, StarSimCore.Domain.Imaging.ImageMetadata metadata)
    {
        Handle = handle;
        this.metadata = metadata;
    }

    private SscPipelineSafeHandle Handle { get; }

    public static NativePipeline Create(NativeImage immutableMaster)
    {
        ArgumentNullException.ThrowIfNull(immutableMaster);
        var metadata = immutableMaster.GetMetadata();
        var nativeHandle = immutableMaster.UseHandle(
            pointer =>
            {
                var operation = NativeCall.Start(new NativeOperationMetadata(
                    "ssc_pipeline_create",
                    Module: "Pipeline",
                    Algorithm: "Pipeline snapshot",
                    ImageWidth: metadata.Width,
                    ImageHeight: metadata.Height));
                var status = NativeMethods.CreatePipeline(pointer, out var pipeline);
                operation.Complete(status);
                return pipeline;
            });
        return new NativePipeline(SscPipelineSafeHandle.FromNative(nativeHandle), metadata);
    }

    public NativeImage GetOutputSnapshot()
    {
        var nativeHandle = UseHandle(
            pointer =>
            {
                var operation = NativeCall.Start(new NativeOperationMetadata(
                    "ssc_pipeline_get_output_snapshot",
                    Module: "Pipeline",
                    Algorithm: "Output snapshot",
                    ImageWidth: metadata.Width,
                    ImageHeight: metadata.Height));
                var status = NativeMethods.GetPipelineOutputSnapshot(pointer, out var image);
                operation.Complete(status);
                return image;
            });
        return NativeImage.FromHandle(nativeHandle, metadata);
    }

    public void Dispose() => Handle.Dispose();

    private T UseHandle<T>(Func<nint, T> action)
    {
        var addedReference = false;
        try
        {
            Handle.DangerousAddRef(ref addedReference);
            return action(Handle.DangerousGetHandle());
        }
        finally
        {
            if (addedReference)
            {
                Handle.DangerousRelease();
            }
        }
    }
}
