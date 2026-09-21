namespace StarSimCore.Interop;

public sealed record NativeBufferPoolMetrics(
    ulong RetainedBytes,
    uint RetainedBufferCount,
    ulong ReuseCount,
    ulong AllocationCount);

public static class NativeBufferPool
{
    public static void Configure(ulong maximumRetainedBytes) =>
        NativeMethods.ConfigureBufferPool(maximumRetainedBytes);

    public static void Trim() => NativeMethods.TrimBufferPool();

    public static NativeBufferPoolMetrics GetMetrics()
    {
        NativeMethods.GetBufferPoolMetrics(out var bytes, out var count, out var reuses, out var allocations);
        return new NativeBufferPoolMetrics(bytes, count, reuses, allocations);
    }
}
