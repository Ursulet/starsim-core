using System.Runtime.InteropServices;

namespace StarSimCore.Interop;

internal sealed class NativeCancellationFlag : IDisposable
{
    private readonly CancellationTokenRegistration registration;
    private nint pointer;

    internal NativeCancellationFlag(CancellationToken cancellationToken)
    {
        pointer = Marshal.AllocHGlobal(sizeof(int));
        Marshal.WriteInt32(pointer, cancellationToken.IsCancellationRequested ? 1 : 0);
        registration = cancellationToken.Register(
            static state => Marshal.WriteInt32((nint)state!, 1),
            pointer);
    }

    internal nint Pointer => pointer;

    public void Dispose()
    {
        registration.Dispose();
        var value = Interlocked.Exchange(ref pointer, 0);
        if (value != 0)
        {
            Marshal.FreeHGlobal(value);
        }
    }
}
