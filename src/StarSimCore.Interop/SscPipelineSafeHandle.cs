using Microsoft.Win32.SafeHandles;

namespace StarSimCore.Interop;

public sealed class SscPipelineSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SscPipelineSafeHandle()
        : base(ownsHandle: true)
    {
    }

    internal static SscPipelineSafeHandle FromNative(nint nativeHandle)
    {
        var safeHandle = new SscPipelineSafeHandle();
        safeHandle.SetHandle(nativeHandle);
        return safeHandle;
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ReleasePipeline(handle);
        return true;
    }
}

