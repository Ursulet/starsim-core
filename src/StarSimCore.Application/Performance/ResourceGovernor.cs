using System.Runtime.InteropServices;
using StarSimCore.Application.Processing;
using StarSimCore.Domain.Imaging;
using StarSimCore.Interop;

namespace StarSimCore.Application.Performance;

public enum PerformanceProfile
{
    Eco,
    Balanced,
    Maximum,
}

public enum InteractivePreviewQuality
{
    Auto,
    Full,
    Half,
    Quarter,
}

public sealed record ResourceGovernorSettings(
    PerformanceProfile Profile = PerformanceProfile.Balanced,
    InteractivePreviewQuality PreviewQuality = InteractivePreviewQuality.Auto,
    bool PrioritizeUiResponsiveness = true,
    bool ReduceCpuOnBattery = true,
    int? ManualMaxThreads = null,
    ulong? ManualMemoryBudgetBytes = null);

public sealed record ResourceLimits(
    int LogicalCpuCount,
    int WorkerBudget,
    ulong AvailableMemoryBytes,
    ulong MemoryBudgetBytes,
    ulong SystemReserveBytes,
    bool IsOnBattery,
    PerformanceProfile EffectiveProfile);

public sealed record ResourceGovernorSnapshot(
    ResourceLimits Limits,
    ulong TrackedAllocationBytes,
    ulong BufferPoolRetainedBytes,
    uint BufferPoolRetainedCount,
    ulong BufferPoolReuseCount,
    ulong BufferPoolAllocationCount,
    int ActiveJobs);

public sealed record ResourceAdmission(
    ulong EstimatedBytes,
    float ResolutionScale,
    bool UsedMemoryFallback,
    string Description);

public sealed class ResourceAdmissionException(string message) : InvalidOperationException(message);

public interface IResourceEnvironment
{
    int LogicalCpuCount { get; }
    ulong AvailablePhysicalMemoryBytes { get; }
    bool IsOnBattery { get; }
}

public sealed class ResourceGovernor
{
    private const ulong OneGiB = 1024UL * 1024 * 1024;
    private readonly object gate = new();
    private readonly IResourceEnvironment environment;
    private ResourceGovernorSettings settings = new();
    private ulong trackedAllocationBytes;
    private int activeJobs;

    public ResourceGovernor(IResourceEnvironment? environment = null) =>
        this.environment = environment ?? new SystemResourceEnvironment();

    public static ResourceGovernor Shared { get; } = new();

    public event EventHandler<ResourceGovernorSnapshot>? SnapshotChanged;

    public ResourceGovernorSettings Settings
    {
        get { lock (gate) return settings; }
    }

    public void UpdateSettings(ResourceGovernorSettings next)
    {
        ArgumentNullException.ThrowIfNull(next);
        var logical = Math.Max(1, environment.LogicalCpuCount);
        if (next.ManualMaxThreads is <= 0 || next.ManualMaxThreads > logical)
            throw new ArgumentOutOfRangeException(nameof(next), $"Manual threads must be between 1 and {logical}.");
        if (next.ManualMemoryBudgetBytes is > 0 and < 64UL * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(next), "Manual memory budget must be at least 64 MiB.");
        if (next.ManualMemoryBudgetBytes is { } manualMemory)
        {
            var available = Math.Max(256UL * 1024 * 1024, environment.AvailablePhysicalMemoryBytes);
            var reserve = Math.Max(OneGiB, (ulong)(available * 0.15));
            var safeMaximum = available > reserve ? available - reserve : available / 2;
            if (manualMemory > safeMaximum)
                throw new ArgumentOutOfRangeException(
                    nameof(next),
                    $"Manual memory budget cannot exceed the safe available-memory limit of {FormatBytes(safeMaximum)}.");
        }
        lock (gate) settings = next;
        ApplyPoolPolicy(GetLimits());
        PublishSnapshot();
    }

    public ResourceLimits GetLimits() => ResolveLimits(environment, Settings);

    public ResourceAdmission Admit(
        ImageMetadata metadata,
        PipelineSnapshot snapshot,
        ProcessingQuality quality,
        float requestedScale = 1)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(snapshot);
        var limits = GetLimits();
        ApplyPoolPolicy(limits);
        var scale = quality == ProcessingQuality.InteractivePreview
            ? ResolveInteractiveScale(metadata, snapshot, requestedScale)
            : 1.0f;
        var estimate = PipelineMemoryEstimator.Estimate(metadata, snapshot, scale);
        var fallback = false;
        if (estimate > limits.MemoryBudgetBytes && quality == ProcessingQuality.InteractivePreview)
        {
            foreach (var candidate in new[] { 0.5f, 0.25f })
            {
                if (candidate >= scale) continue;
                var candidateEstimate = PipelineMemoryEstimator.Estimate(metadata, snapshot, candidate);
                if (candidateEstimate > limits.MemoryBudgetBytes) continue;
                scale = candidate;
                estimate = candidateEstimate;
                fallback = true;
                break;
            }
        }
        if (estimate > limits.MemoryBudgetBytes)
        {
            NativeBufferPool.Trim();
            throw new ResourceAdmissionException(
                $"This operation needs approximately {FormatBytes(estimate)}, but the {limits.EffectiveProfile} memory budget is {FormatBytes(limits.MemoryBudgetBytes)}. Reduce image size or choose a higher Performance profile.");
        }
        return new ResourceAdmission(
            estimate,
            scale,
            fallback,
            fallback ? $"Memory-safe preview at {scale:P0}" : $"Admitted at {scale:P0}");
    }

    public IDisposable Acquire(ResourceAdmission admission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(admission);
        lock (gate)
        {
            while (true)
            {
                var limits = ResolveLimits(environment, settings);
                if (admission.EstimatedBytes > limits.MemoryBudgetBytes)
                {
                    throw new ResourceAdmissionException(
                        $"The current memory budget changed to {FormatBytes(limits.MemoryBudgetBytes)} before this {FormatBytes(admission.EstimatedBytes)} operation could start.");
                }
                var exceedsWorkers = activeJobs >= limits.WorkerBudget;
                var exceedsMemory = trackedAllocationBytes > limits.MemoryBudgetBytes - admission.EstimatedBytes;
                if (!exceedsWorkers && !exceedsMemory) break;
                cancellationToken.ThrowIfCancellationRequested();
                Monitor.Wait(gate, TimeSpan.FromMilliseconds(50));
            }
            cancellationToken.ThrowIfCancellationRequested();
            activeJobs++;
            trackedAllocationBytes = SaturatingAdd(trackedAllocationBytes, admission.EstimatedBytes);
        }
        PublishSnapshot();
        return new ResourceLease(this, admission.EstimatedBytes);
    }

    public ResourceGovernorSnapshot GetSnapshot()
    {
        var pool = SafePoolMetrics();
        lock (gate)
        {
            return new ResourceGovernorSnapshot(
                ResolveLimits(environment, settings),
                trackedAllocationBytes,
                pool.RetainedBytes,
                pool.RetainedBufferCount,
                pool.ReuseCount,
                pool.AllocationCount,
                activeJobs);
        }
    }

    public static ResourceLimits ResolveLimits(
        IResourceEnvironment environment,
        ResourceGovernorSettings settings)
    {
        var logical = Math.Max(1, environment.LogicalCpuCount);
        var effectiveProfile = settings.Profile;
        if (environment.IsOnBattery && settings.ReduceCpuOnBattery)
        {
            effectiveProfile = settings.Profile switch
            {
                PerformanceProfile.Maximum => PerformanceProfile.Balanced,
                PerformanceProfile.Balanced => PerformanceProfile.Eco,
                _ => PerformanceProfile.Eco,
            };
        }
        var workers = effectiveProfile switch
        {
            PerformanceProfile.Eco => logical <= 2 ? 1 : Math.Max(1, (int)Math.Ceiling(logical * 0.45)),
            PerformanceProfile.Balanced => logical <= 2
                ? 1
                : logical <= 4
                    ? logical - 1
                    : Math.Min(logical - 2, Math.Max(1, (int)Math.Ceiling(logical * 0.75))),
            _ => logical,
        };
        if (settings.ManualMaxThreads is { } manualThreads)
            workers = Math.Clamp(manualThreads, 1, logical);

        var available = Math.Max(256UL * 1024 * 1024, environment.AvailablePhysicalMemoryBytes);
        var reserve = Math.Max(OneGiB, (ulong)(available * 0.15));
        var usable = available > reserve ? available - reserve : available / 2;
        var profileFraction = effectiveProfile switch
        {
            PerformanceProfile.Eco => 0.18,
            PerformanceProfile.Balanced => 0.35,
            _ => 0.65,
        };
        var automaticBudget = Math.Min(usable, (ulong)(available * profileFraction));
        var budget = settings.ManualMemoryBudgetBytes is { } manualMemory
            ? Math.Min(manualMemory, usable)
            : automaticBudget;
        budget = Math.Max(Math.Min(64UL * 1024 * 1024, usable), budget);
        return new ResourceLimits(logical, workers, available, budget, reserve, environment.IsOnBattery, effectiveProfile);
    }

    private float ResolveInteractiveScale(
        ImageMetadata metadata,
        PipelineSnapshot snapshot,
        float requestedScale)
    {
        var configured = Settings.PreviewQuality;
        if (configured != InteractivePreviewQuality.Auto)
        {
            return configured switch
            {
                InteractivePreviewQuality.Full => 1,
                InteractivePreviewQuality.Half => 0.5f,
                InteractivePreviewQuality.Quarter => 0.25f,
                _ => 1,
            };
        }
        if (!PipelineMemoryEstimator.HasExpensiveEnabledProcessor(snapshot)) return 1;
        var longestSide = Math.Max(metadata.Width, metadata.Height);
        var automatic = longestSide >= 2048 ? 0.25f : longestSide >= 768 ? 0.5f : 1.0f;
        return Math.Min(Math.Clamp(requestedScale, 0.25f, 1), automatic);
    }

    private void ApplyPoolPolicy(ResourceLimits limits)
    {
        try
        {
            var retention = Math.Min(512UL * 1024 * 1024, limits.MemoryBudgetBytes / 8);
            NativeBufferPool.Configure(retention);
            if (limits.AvailableMemoryBytes <= limits.SystemReserveBytes * 2)
                NativeBufferPool.Trim();
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static NativeBufferPoolMetrics SafePoolMetrics()
    {
        try { return NativeBufferPool.GetMetrics(); }
        catch (DllNotFoundException) { return new(0, 0, 0, 0); }
        catch (EntryPointNotFoundException) { return new(0, 0, 0, 0); }
    }

    private void Release(ulong estimatedBytes)
    {
        lock (gate)
        {
            activeJobs = Math.Max(0, activeJobs - 1);
            trackedAllocationBytes = trackedAllocationBytes > estimatedBytes
                ? trackedAllocationBytes - estimatedBytes
                : 0;
            Monitor.PulseAll(gate);
        }
        PublishSnapshot();
    }

    private void PublishSnapshot()
    {
        try { SnapshotChanged?.Invoke(this, GetSnapshot()); }
        catch (Exception exception)
        {
            DiagnosticService.Current.RecordManagedException("Resource governor observer", exception);
        }
    }

    private static ulong SaturatingAdd(ulong left, ulong right) =>
        ulong.MaxValue - left < right ? ulong.MaxValue : left + right;

    private static string FormatBytes(ulong bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.00} GiB";

    private sealed class ResourceLease(ResourceGovernor governor, ulong bytes) : IDisposable
    {
        private ResourceGovernor? owner = governor;
        public void Dispose() => Interlocked.Exchange(ref owner, null)?.Release(bytes);
    }
}

public static class PipelineMemoryEstimator
{
    public static ulong Estimate(ImageMetadata metadata, PipelineSnapshot snapshot, float resolutionScale)
    {
        var scaledPixels = Math.Max(1D, metadata.Width * (double)metadata.Height * resolutionScale * resolutionScale);
        var imageBytes = scaledPixels * metadata.ChannelCount * sizeof(float);
        var temporaryFactor = 2D;
        foreach (var module in snapshot.Modules.Where(module => module.Enabled))
        {
            temporaryFactor = Math.Max(temporaryFactor, module.Id switch
            {
                BuiltInProcessors.WaveletId => 16,
                BuiltInProcessors.RichardsonLucyId when module.Parameters[2] > 0 => 8,
                BuiltInProcessors.MultiScaleSharpenId or BuiltInProcessors.LocalDetailId => 5,
                BuiltInProcessors.NoiseReductionId or BuiltInProcessors.RgbAlignId => 4,
                BuiltInProcessors.UnsharpMaskId or BuiltInProcessors.DeringingId => 3,
                _ => 2,
            });
        }
        var estimate = imageBytes * (2 + temporaryFactor);
        return estimate >= ulong.MaxValue ? ulong.MaxValue : (ulong)Math.Ceiling(estimate);
    }

    public static bool HasExpensiveEnabledProcessor(PipelineSnapshot snapshot) =>
        snapshot.Modules.Any(module => module.Enabled && module.Id is
            BuiltInProcessors.WaveletId or
            BuiltInProcessors.RichardsonLucyId or
            BuiltInProcessors.MultiScaleSharpenId or
            BuiltInProcessors.NoiseReductionId or
            BuiltInProcessors.DeringingId or
            BuiltInProcessors.RgbAlignId or
            BuiltInProcessors.LocalDetailId);
}

internal sealed class SystemResourceEnvironment : IResourceEnvironment
{
    public int LogicalCpuCount => Environment.ProcessorCount;

    public ulong AvailablePhysicalMemoryBytes
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref status)) return status.AvailablePhysical;
            }
            return (ulong)Math.Max(256L * 1024 * 1024, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        }
    }

    public bool IsOnBattery
    {
        get
        {
            if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var status)) return false;
            return status.AcLineStatus == 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        internal uint Length;
        internal uint MemoryLoad;
        internal ulong TotalPhysical;
        internal ulong AvailablePhysical;
        internal ulong TotalPageFile;
        internal ulong AvailablePageFile;
        internal ulong TotalVirtual;
        internal ulong AvailableVirtual;
        internal ulong AvailableExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        internal byte AcLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);
}
