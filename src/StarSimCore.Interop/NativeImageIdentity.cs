namespace StarSimCore.Interop;

public readonly record struct NativeImageIdentity(
    ulong SourceIdentity,
    ulong InstanceIdentity,
    bool IsMaster);

