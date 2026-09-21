using Microsoft.Win32.SafeHandles;

namespace StarSimCore.Interop;

public sealed class SscImageSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private SscImageSafeHandle()
        : base(ownsHandle: true)
    {
    }

    internal static SscImageSafeHandle FromNative(nint nativeHandle)
    {
        var safeHandle = new SscImageSafeHandle();
        safeHandle.SetHandle(nativeHandle);
        return safeHandle;
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.ReleaseImage(handle);
        return true;
    }
}

