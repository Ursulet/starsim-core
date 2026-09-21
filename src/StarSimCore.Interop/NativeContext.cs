namespace StarSimCore.Interop;

public static class NativeContext
{
    public static StarSimCoreSafeHandle Create()
    {
        var operation = NativeCall.Start(new NativeOperationMetadata("starsim_core_context_create"));
        var status = NativeMethods.CreateContext(out var nativeHandle);
        operation.Complete(status);
        if (nativeHandle == 0)
            throw new InvalidOperationException("Native context creation returned a null handle after reporting success.");

        return StarSimCoreSafeHandle.FromNative(nativeHandle);
    }
}
