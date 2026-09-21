using Microsoft.Win32.SafeHandles;

namespace StarSimCore.Interop;

public sealed class StarSimCoreSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private StarSimCoreSafeHandle()
        : base(ownsHandle: true)
    {
    }

    internal static StarSimCoreSafeHandle FromNative(nint nativeHandle)
    {
        var safeHandle = new StarSimCoreSafeHandle();
        safeHandle.SetHandle(nativeHandle);
        return safeHandle;
    }

    protected override bool ReleaseHandle()
    {
        NativeMethods.DestroyContext(handle);
        return true;
    }
}

