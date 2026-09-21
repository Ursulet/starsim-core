using System.Collections.Immutable;
using StarSimCore.Domain.Imaging;
using StarSimCore.Interop;

namespace StarSimCore.Application.Processing;

[Flags]
public enum ProcessorCapabilities
{
    None = 0,
    Grayscale = 1,
    Rgb = 2,
    InteractivePreview = 4,
    FullResolution = 8,
}

public enum ProcessingQuality
{
    InteractivePreview,
    DefinitivePreview,
    FullResolution = DefinitivePreview,
}

public enum PreviewQualityHint
{
    Immediate,
    Debounced,
}

public enum TilingCapability
{
    NotSupported,
    IndependentTiles,
    RequiresHalo,
}

public sealed record ProcessorParameterSchema(
    string Id,
    string DisplayName,
    double Minimum,
    double Maximum,
    double DefaultValue,
    double? RecommendedStep = null);

public sealed record ProcessorState(
    string Id,
    bool Enabled,
    ImmutableArray<double> Parameters);

public sealed record PipelineSnapshot(
    ImmutableArray<ProcessorState> Modules,
    long Revision)
{
    public ProcessorState GetModule(string id) =>
        Modules.First(module => string.Equals(module.Id, id, StringComparison.Ordinal));

    public PipelineSnapshot WithParameter(string id, int index, double value)
    {
        var modules = Modules.ToBuilder();
        var moduleIndex = FindModuleIndex(id);
        if (moduleIndex < 0) throw new ArgumentOutOfRangeException(nameof(id));
        var module = modules[moduleIndex];
        if ((uint)index >= (uint)module.Parameters.Length) throw new ArgumentOutOfRangeException(nameof(index));
        modules[moduleIndex] = module with { Parameters = module.Parameters.SetItem(index, value) };
        return new PipelineSnapshot(modules.ToImmutable(), Revision + 1);
    }

    public PipelineSnapshot WithEnabled(string id, bool enabled)
    {
        var modules = Modules.ToBuilder();
        var index = FindModuleIndex(id);
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(id));
        modules[index] = modules[index] with { Enabled = enabled };
        return new PipelineSnapshot(modules.ToImmutable(), Revision + 1);
    }

    private int FindModuleIndex(string id)
    {
        for (var index = 0; index < Modules.Length; index++)
        {
            if (string.Equals(Modules[index].Id, id, StringComparison.Ordinal)) return index;
        }
        return -1;
    }
}

public sealed record ProcessorContext(
    ProcessingQuality Quality,
    float ResolutionScale,
    ImageMetadata Metadata,
    Action<ProcessorProgress>? ReportProgress = null);

public sealed record ProcessorProgress(
    string ModuleId,
    string ModuleName,
    string Stage,
    uint CurrentStep,
    uint TotalSteps,
    double? Fraction);

public interface IImageProcessor
{
    string Id { get; }
    string Name { get; }
    string Category { get; }
    Version SemanticVersion { get; }
    IReadOnlyList<ProcessorParameterSchema> ParameterSchema { get; }
    bool IsEnabledByDefault { get; }
    ProcessorCapabilities Capabilities { get; }
    PreviewQualityHint PreviewQualityHint { get; }
    TilingCapability TilingCapability { get; }
    void Validate(ProcessorState state, ImageMetadata metadata);
    NativeImage Process(NativeImage input, ProcessorState state, ProcessorContext context, CancellationToken cancellationToken);
}

public sealed record HistogramData(
    ulong[] RedOrGray,
    ulong[] Green,
    ulong[] Blue,
    float[] Minimum,
    float[] Maximum,
    double[] Mean,
    uint ChannelCount);

public sealed record ProcessingFrame(
    byte[] BgraPixels,
    HistogramData Histogram,
    long Revision,
    long RequestSequence,
    ProcessingQuality Quality,
    float ResolutionScale,
    ulong SourceIdentity,
    Guid DocumentContext);

public sealed record ProcessingSchedulerProgress(
    long Submitted,
    long Completed,
    long Coalesced,
    long Discarded,
    int Running,
    int Pending,
    int CurrentDepth,
    int MaximumObservedDepth,
    double? Fraction,
    uint CurrentStep,
    uint TotalSteps,
    bool CanCancel,
    ProcessingQuality Quality,
    long RequestId,
    long CurrentRevision,
    string Stage);

public sealed record ProcessingSchedulerMetrics(
    long Submitted,
    long Completed,
    long Coalesced,
    long Discarded,
    int Running,
    int Pending,
    int CurrentDepth,
    int MaximumObservedDepth);

public sealed record ProcessorTiming(
    string ModuleId,
    string ModuleName,
    TimeSpan Elapsed,
    bool CacheHit,
    ProcessingQuality Quality,
    Guid RequestId);
