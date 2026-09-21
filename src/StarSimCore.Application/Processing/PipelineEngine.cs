using System.Globalization;
using System.Diagnostics;
using StarSimCore.Interop;

namespace StarSimCore.Application.Processing;

internal sealed class PipelineEngine : IDisposable
{
    private readonly IReadOnlyDictionary<string, IImageProcessor> processors;
    private readonly PipelineCache cache;

    internal PipelineEngine(
        Func<ulong>? maximumCacheBytes = null,
        IReadOnlyList<IImageProcessor>? processorDefinitions = null)
    {
        processors = (processorDefinitions ?? BuiltInProcessors.All)
            .ToDictionary(processor => processor.Id, StringComparer.Ordinal);
        cache = new PipelineCache(96, maximumCacheBytes ?? (() => 256UL * 1024 * 1024));
    }

    internal event EventHandler<ProcessorTiming>? ModuleTimingRecorded;

    internal NativeImage? Execute(
        NativeImage immutableMaster,
        PipelineSnapshot snapshot,
        ProcessorContext context,
        CancellationToken cancellationToken)
    {
        var sourceIdentity = immutableMaster.GetIdentity().SourceIdentity;
        NativeImage? previewBase = null;
        NativeImage? current = null;
        var precedingKey = sourceIdentity.ToString(CultureInfo.InvariantCulture);
        try
        {
            var pipelineInput = immutableMaster;
            var effectiveContext = context;
            if (context.ResolutionScale < 0.999f)
            {
                previewBase = immutableMaster.CreateScaledPreview(context.ResolutionScale, cancellationToken);
                pipelineInput = previewBase;
                effectiveContext = context with { Metadata = previewBase.GetMetadata() };
            }
            var enabledModuleCount = Math.Max(1, snapshot.Modules.Count(module => module.Enabled));
            var completedModules = 0;
            foreach (var module in snapshot.Modules)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    current?.Dispose();
                    return null;
                }
                if (!processors.TryGetValue(module.Id, out var processor))
                {
                    throw new InvalidOperationException($"Processor '{module.Id}' is not registered.");
                }
                if (!module.Enabled) continue;
                processor.Validate(module, effectiveContext.Metadata);
                var key = CreateKey(precedingKey, sourceIdentity, processor, module, effectiveContext);
                precedingKey = key;

                effectiveContext.ReportProgress?.Invoke(new ProcessorProgress(
                    processor.Id,
                    processor.Name,
                    processor.Name,
                    0,
                    0,
                    null));

                var moduleProgressBase = completedModules;
                var moduleContext = effectiveContext with
                {
                    ReportProgress = update => effectiveContext.ReportProgress?.Invoke(update with
                    {
                        Fraction = update.Fraction is { } fraction
                            ? Math.Clamp((moduleProgressBase + fraction) / enabledModuleCount, 0, 1)
                            : null,
                    }),
                };

                NativeImage next;
                var stopwatch = Stopwatch.StartNew();
                var cacheHit = cache.TryGet(key, out next!);
                if (!cacheHit)
                {
                    next = processor.Process(current ?? pipelineInput, module, moduleContext, cancellationToken);
                    cache.Store(key, next);
                }
                if (cacheHit)
                {
                    effectiveContext.ReportProgress?.Invoke(new ProcessorProgress(
                        processor.Id,
                        processor.Name,
                        $"{processor.Name} (cached)",
                        1,
                        1,
                        (completedModules + 1D) / enabledModuleCount));
                }
                else
                {
                    effectiveContext.ReportProgress?.Invoke(new ProcessorProgress(
                        processor.Id,
                        processor.Name,
                        processor.Name,
                        1,
                        1,
                        (completedModules + 1D) / enabledModuleCount));
                }
                stopwatch.Stop();
                var timing = new ProcessorTiming(
                    processor.Id,
                    processor.Name,
                    stopwatch.Elapsed,
                    cacheHit,
                    effectiveContext.Quality,
                    NativeRequestDiagnostics.CurrentRequestId);
                ModuleTimingRecorded?.Invoke(this, timing);
                DiagnosticService.Current.RecordProcessorTiming(
                    timing.ModuleId,
                    timing.ModuleName,
                    timing.Elapsed,
                    timing.CacheHit,
                    timing.Quality.ToString(),
                    timing.RequestId);
                current?.Dispose();
                current = next;
                completedModules++;
            }
            if (cancellationToken.IsCancellationRequested)
            {
                current?.Dispose();
                return null;
            }
            return current ?? pipelineInput.CloneWorking();
        }
        catch
        {
            current?.Dispose();
            throw;
        }
        finally
        {
            previewBase?.Dispose();
        }
    }

    private static string CreateKey(
        string precedingKey,
        ulong sourceIdentity,
        IImageProcessor processor,
        ProcessorState state,
        ProcessorContext context) =>
        string.Join(
            '|',
            precedingKey,
            sourceIdentity.ToString(CultureInfo.InvariantCulture),
            processor.Id,
            processor.SemanticVersion,
            state.Enabled,
            string.Join(',', state.Parameters.Select(value => value.ToString("R", CultureInfo.InvariantCulture))),
            context.Quality,
            context.ResolutionScale.ToString("R", CultureInfo.InvariantCulture));

    public void Dispose() => cache.Dispose();

    private sealed class PipelineCache(int capacity, Func<ulong> maximumBytes) : IDisposable
    {
        private sealed record Entry(NativeImage Image, ulong SizeBytes, LinkedListNode<string> RecencyNode);

        private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> recency = new();
        private readonly object gate = new();
        private ulong retainedBytes;

        internal bool TryGet(string key, out NativeImage image)
        {
            lock (gate)
            {
                if (entries.TryGetValue(key, out var cached))
                {
                    recency.Remove(cached.RecencyNode);
                    recency.AddLast(cached.RecencyNode);
                    image = cached.Image.CloneWorking();
                    return true;
                }
            }
            image = null!;
            return false;
        }

        internal void Store(string key, NativeImage image)
        {
            var metadata = image.GetMetadata();
            var sizeBytes = SaturatingMultiply(
                SaturatingMultiply(metadata.Width, metadata.Height),
                SaturatingMultiply(metadata.ChannelCount, sizeof(float)));
            lock (gate)
            {
                if (entries.TryGetValue(key, out var existing))
                {
                    recency.Remove(existing.RecencyNode);
                    recency.AddLast(existing.RecencyNode);
                    return;
                }
                var byteLimit = Math.Max(16UL * 1024 * 1024, maximumBytes());
                if (sizeBytes > byteLimit) return;
                while ((entries.Count >= capacity || retainedBytes > byteLimit - sizeBytes) && recency.First is { } oldest)
                {
                    recency.RemoveFirst();
                    if (entries.Remove(oldest.Value, out var removed))
                    {
                        retainedBytes -= removed.SizeBytes;
                        removed.Image.Dispose();
                    }
                }
                var node = recency.AddLast(key);
                entries.Add(key, new Entry(image.CloneWorking(), sizeBytes, node));
                retainedBytes += sizeBytes;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                foreach (var entry in entries.Values) entry.Image.Dispose();
                entries.Clear();
                recency.Clear();
                retainedBytes = 0;
            }
        }

        private static ulong SaturatingMultiply(ulong left, ulong right) =>
            left == 0 || right == 0
                ? 0
                : left > ulong.MaxValue / right
                    ? ulong.MaxValue
                    : left * right;
    }
}
