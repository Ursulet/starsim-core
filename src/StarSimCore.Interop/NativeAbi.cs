namespace StarSimCore.Interop;

public static class NativeAbi
{
    public const uint ExpectedVersion = 1;

    public static uint GetLoadedVersion() => NativeMethods.GetAbiVersion();
}

