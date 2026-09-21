using System.Diagnostics;
using StarSimCore.Application.Imaging;
using StarSimCore.Application.Performance;
using StarSimCore.Interop;

namespace StarSimCore.Application.Processing;

public sealed class ImageProcessingService : IDisposable
{
    private readonly object gate = new();
    private readonly PipelineEngine engine;
    private readonly ResourceGovernor governor;
    private readonly ManualResetEventSlim idle = new(initialState: true);
    private ProcessingRequest? running;
    private ProcessingRequest? pending;
    private bool disposed;
    private long submittedCount;
    private long completedCount;
    private long coalescedCount;
    private long discardedCount;
    private int maximumObservedDepth;
    private long latestSubmittedRequestId;
    private long latestSubmittedRevision;
    private Guid currentDocumentContext;
    private ulong currentSourceIdentity;

    public ImageProcessingService(
        ResourceGovernor? governor = null,
        IReadOnlyList<IImageProcessor>? processorDefinitions = null)
    {
        this.governor = governor ?? ResourceGovernor.Shared;
        engine = new PipelineEngine(() => Math.Max(
            16UL * 1024 * 1024,
            this.governor.GetLimits().MemoryBudgetBytes / 3),
            processorDefinitions);
        engine.ModuleTimingRecorded += OnModuleTimingRecorded;
    }

    public event EventHandler<ProcessingSchedulerProgress>? ProgressChanged;
    public event EventHandler<ProcessorTiming>? ModuleTimingRecorded;

    public ProcessingSchedulerMetrics SchedulerMetrics
    {
        get { lock (gate) return CreateMetrics(); }
    }

    public void CancelActive(NativeCancellationReason reason = NativeCancellationReason.UserRequested)
    {
        lock (gate)
        {
            running?.Cancellation.Cancel(reason);
            pending?.Cancellation.Cancel(reason);
        }
    }

    public async Task CancelAndWaitForIdleAsync(
        NativeCancellationReason reason = NativeCancellationReason.RequestSuperseded,
        CancellationToken cancellationToken = default)
    {
        CancelActive(reason);
        lock (gate)
        {
            if (running is null && pending is null) return;
        }

        await Task.Run(() => idle.Wait(cancellationToken), CancellationToken.None).ConfigureAwait(false);
    }

    public Task<ProcessingFrame?> EnqueueAsync(
        ImageDocument document,
        PipelineSnapshot snapshot,
        ProcessingQuality quality,
        float resolutionScale = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var admission = governor.Admit(document.Source.Metadata, snapshot, quality, resolutionScale);
        ProcessingRequest request;
        ProcessingRequest? replacedPending = null;
        ProcessingSchedulerProgress progress;
        var startWorker = false;

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            request = new ProcessingRequest(document, snapshot, quality, admission, cancellationToken);
            submittedCount++;
            request.RequestId = submittedCount;
            latestSubmittedRequestId = request.RequestId;
            latestSubmittedRevision = request.Snapshot.Revision;

            if (currentDocumentContext != Guid.Empty && currentDocumentContext != request.DocumentContext)
            {
                running?.Cancellation.Cancel(NativeCancellationReason.RequestSuperseded);
                pending?.Cancellation.Cancel(NativeCancellationReason.RequestSuperseded);
            }
            currentDocumentContext = request.DocumentContext;
            currentSourceIdentity = request.SourceIdentity;

            if (running is null)
            {
                running = request;
                idle.Reset();
                startWorker = true;
            }
            else
            {
                if (quality == ProcessingQuality.InteractivePreview &&
                    running.Quality != ProcessingQuality.InteractivePreview &&
                    governor.Settings.PrioritizeUiResponsiveness)
                {
                    running.Cancellation.Cancel(NativeCancellationReason.RequestSuperseded);
                }
                replacedPending = pending;
                pending = request;
                if (replacedPending is not null) coalescedCount++;
            }

            var depth = CurrentDepth;
            maximumObservedDepth = Math.Max(maximumObservedDepth, depth);
            Debug.Assert(depth <= 2, "The image scheduler must retain at most one running and one pending request.");
            progress = CreateProgress(request, replacedPending is null ? "Scheduled" : "Latest pending updated", null);
        }

        DiagnosticService.Current.RecordSchedulerState(
            request.Cancellation.DiagnosticRequestId,
            request.Snapshot.Revision,
            progress.Running,
            progress.Pending,
            progress.CurrentDepth,
            progress.MaximumObservedDepth);
        if (replacedPending is not null)
        {
            RecordLifecycle(replacedPending, "coalesced-before-execution");
            replacedPending.CompleteCoalesced();
            DiagnosticService.Current.RecordSchedulerCoalesced(
                replacedPending.Cancellation.DiagnosticRequestId,
                request.Cancellation.DiagnosticRequestId,
                request.Snapshot.Revision,
                request.RequestId);
        }

        RaiseProgress(progress);
        if (startWorker) _ = Task.Run(() => RunWorker(request));
        return request.Task;
    }

    private void RunWorker(ProcessingRequest first)
    {
        ProcessingRequest? current = first;
        while (current is not null)
        {
            var completedRequest = current;
            ProcessingFrame? completedFrame = null;
            Exception? failure = null;
            try
            {
                using var resources = governor.Acquire(current.Admission, current.Cancellation.Token);
                completedFrame = ProcessRequest(current);
            }
            catch (OperationCanceledException) when (current.Cancellation.Token.IsCancellationRequested)
            {
                completedFrame = CompleteCancellation(current, "Resource admission/worker wait");
            }
            catch (Exception exception)
            {
                RecordLifecycle(current, $"failed-{exception.GetType().Name}");
                failure = exception;
            }
            finally
            {
                current.Dispose();
            }

            ProcessingSchedulerProgress progress;
            lock (gate)
            {
                completedCount++;
                current = pending;
                pending = null;
                running = current;
                if (current is null) idle.Set();
                progress = current is null
                    ? CreateProgress(null, "Complete", 1)
                    : CreateProgress(current, "Processing latest pending", null);
            }
            if (failure is null) completedRequest.Complete(completedFrame);
            else completedRequest.Fail(failure);
            RaiseProgress(progress);
        }
    }

    private ProcessingFrame? ProcessRequest(ProcessingRequest request)
    {
        NativeImage? output = null;
        using var diagnosticScope = NativeRequestDiagnostics.Begin(
            request.Cancellation.DiagnosticRequestId,
            () => request.Cancellation.Reason);
        try
        {
            RecordLifecycle(request, "started");
            if (request.Cancellation.Token.IsCancellationRequested)
                return CompleteCancellation(request, "Single-flight scheduler");

            PublishProgress(request, "Preparing pipeline", null);
            var context = new ProcessorContext(
                request.Quality,
                request.Admission.ResolutionScale,
                request.Document.Source.Metadata,
                update => PublishProcessorProgress(request, update));
            output = ExecuteWithoutIntentionalCancellationException(request, context);
            if (output is null) return CompleteCancellation(request, "Processor pipeline");
            if (!IsCurrent(request)) return Discard(request, "discarded-stale-after-pipeline");

            PublishProgress(request, "Computing histogram", null);
            var histogram = ComputeHistogramWithoutIntentionalCancellationException(output, request.Cancellation);
            if (histogram is null) return CompleteCancellation(request, "Histogram");
            if (request.Cancellation.Token.IsCancellationRequested)
                return CompleteCancellation(request, "Publication gate");
            if (!IsCurrent(request)) return Discard(request, "discarded-stale-before-publication");

            PublishProgress(request, "Publishing preview", 0.98);
            var outputMetadata = output.GetMetadata();
            var pixels = output.RenderBgra8();
            pixels = PreviewBufferScaler.UpscaleBgra(
                pixels,
                outputMetadata.Width,
                outputMetadata.Height,
                request.Document.Source.Metadata.Width,
                request.Document.Source.Metadata.Height);

            if (request.Quality != ProcessingQuality.InteractivePreview &&
                request.Admission.ResolutionScale >= 0.999f)
            {
                request.Document.ReplaceProcessed(output);
                output = null;
            }

            var frame = new ProcessingFrame(
                pixels,
                new HistogramData(
                    histogram.RedOrGray,
                    histogram.Green,
                    histogram.Blue,
                    histogram.Minimum,
                    histogram.Maximum,
                    histogram.Mean,
                    histogram.ChannelCount),
                request.Snapshot.Revision,
                request.RequestId,
                request.Quality,
                request.Admission.ResolutionScale,
                request.SourceIdentity,
                request.DocumentContext);
            RecordLifecycle(request, "applied");
            return frame;
        }
        finally
        {
            output?.Dispose();
        }
    }

    private NativeImage? ExecuteWithoutIntentionalCancellationException(
        ProcessingRequest request,
        ProcessorContext context)
    {
        try
        {
            return engine.Execute(request.Document.Master, request.Snapshot, context, request.Cancellation.Token);
        }
        catch (NativeOperationCanceledException exception) when (exception.IsIntentional)
        {
            request.Cancellation.MarkDiagnosticRecorded();
            return null;
        }
        catch (OperationCanceledException) when (request.Cancellation.Token.IsCancellationRequested)
        {
            return null;
        }
    }

    private static NativeHistogram? ComputeHistogramWithoutIntentionalCancellationException(
        NativeImage output,
        ProcessingCancellation cancellation)
    {
        try
        {
            return output.ComputeHistogram(256, cancellation.Token);
        }
        catch (NativeOperationCanceledException exception) when (exception.IsIntentional)
        {
            cancellation.MarkDiagnosticRecorded();
            return null;
        }
        catch (OperationCanceledException) when (cancellation.Token.IsCancellationRequested)
        {
            return null;
        }
    }

    private ProcessingFrame? Discard(ProcessingRequest request, string disposition)
    {
        lock (gate) discardedCount++;
        RecordLifecycle(request, disposition);
        return null;
    }

    private static ProcessingFrame? CompleteCancellation(ProcessingRequest request, string stage)
    {
        var cancellation = request.Cancellation;
        var reason = cancellation.Reason == NativeCancellationReason.None
            ? NativeCancellationReason.UserRequested
            : cancellation.Reason;
        if (!cancellation.DiagnosticWasRecorded)
        {
            DiagnosticService.Current.RecordSchedulerCancellation(
                cancellation.DiagnosticRequestId,
                reason,
                stage,
                $"requestId={request.RequestId};revision={request.Snapshot.Revision};quality={request.Quality}",
                request.Document.Source.Metadata.Width,
                request.Document.Source.Metadata.Height,
                cancellation.Elapsed);
            cancellation.MarkDiagnosticRecorded();
        }
        RecordLifecycle(request, $"cancelled-{reason}");
        return null;
    }

    private bool IsCurrent(ProcessingRequest request)
    {
        lock (gate)
        {
            return !disposed &&
                   !request.Cancellation.Token.IsCancellationRequested &&
                   request.RequestId == latestSubmittedRequestId &&
                   request.Snapshot.Revision == latestSubmittedRevision &&
                   request.DocumentContext == currentDocumentContext &&
                   request.SourceIdentity == currentSourceIdentity;
        }
    }

    private void PublishProcessorProgress(ProcessingRequest request, ProcessorProgress update)
    {
        var stage = update.TotalSteps > 0
            ? $"{update.ModuleName}: {update.CurrentStep} / {update.TotalSteps}"
            : $"Processing {update.ModuleName}…";
        PublishProgress(
            request,
            stage,
            update.Fraction is { } fraction ? Math.Clamp(fraction * 0.9, 0, 0.9) : null,
            update.CurrentStep,
            update.TotalSteps);
    }

    private void PublishProgress(
        ProcessingRequest request,
        string stage,
        double? fraction,
        uint currentStep = 0,
        uint totalSteps = 0)
    {
        ProcessingSchedulerProgress progress;
        lock (gate)
        {
            if (request.RequestId != latestSubmittedRequestId || request.DocumentContext != currentDocumentContext)
                return;
            progress = CreateProgress(request, stage, fraction, currentStep, totalSteps);
        }
        RaiseProgress(progress);
    }

    private ProcessingSchedulerProgress CreateProgress(
        ProcessingRequest? request,
        string stage,
        double? fraction,
        uint currentStep = 0,
        uint totalSteps = 0)
    {
        var metrics = CreateMetrics();
        request ??= running;
        return new ProcessingSchedulerProgress(
            metrics.Submitted,
            metrics.Completed,
            metrics.Coalesced,
            metrics.Discarded,
            metrics.Running,
            metrics.Pending,
            metrics.CurrentDepth,
            metrics.MaximumObservedDepth,
            fraction,
            currentStep,
            totalSteps,
            request is not null,
            request?.Quality ?? ProcessingQuality.DefinitivePreview,
            request?.RequestId ?? latestSubmittedRequestId,
            request?.Snapshot.Revision ?? 0,
            stage);
    }

    private ProcessingSchedulerMetrics CreateMetrics()
    {
        var runningCount = running is null ? 0 : 1;
        var pendingCount = pending is null ? 0 : 1;
        return new ProcessingSchedulerMetrics(
            submittedCount,
            completedCount,
            coalescedCount,
            discardedCount,
            runningCount,
            pendingCount,
            runningCount + pendingCount,
            maximumObservedDepth);
    }

    private void RaiseProgress(ProcessingSchedulerProgress progress)
    {
        try { ProgressChanged?.Invoke(this, progress); }
        catch (Exception exception)
        {
            DiagnosticService.Current.RecordManagedException("Processing progress subscriber", exception);
        }
    }

    private int CurrentDepth => (running is null ? 0 : 1) + (pending is null ? 0 : 1);

    private static void RecordLifecycle(ProcessingRequest request, string disposition) =>
        DiagnosticService.Current.RecordSchedulerLifecycle(
            request.RequestId,
            request.Cancellation.DiagnosticRequestId,
            request.Quality.ToString(),
            disposition,
            request.Snapshot.Revision,
            request.SourceIdentity,
            request.DocumentContext,
            request.Elapsed);

    private void OnModuleTimingRecorded(object? sender, ProcessorTiming timing) =>
        ModuleTimingRecorded?.Invoke(this, timing);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            running?.Cancellation.Cancel(NativeCancellationReason.Shutdown);
            pending?.Cancellation.Cancel(NativeCancellationReason.Shutdown);
        }
        idle.Wait();
        engine.ModuleTimingRecorded -= OnModuleTimingRecorded;
        engine.Dispose();
        idle.Dispose();
    }

    private sealed class ProcessingRequest : IDisposable
    {
        private readonly TaskCompletionSource<ProcessingFrame?> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal ProcessingRequest(
            ImageDocument document,
            PipelineSnapshot snapshot,
            ProcessingQuality quality,
            ResourceAdmission admission,
            CancellationToken cancellationToken)
        {
            Document = document;
            Snapshot = snapshot;
            Quality = quality;
            Admission = admission;
            Cancellation = new ProcessingCancellation(cancellationToken);
        }

        internal ImageDocument Document { get; }
        internal PipelineSnapshot Snapshot { get; }
        internal ProcessingQuality Quality { get; }
        internal ResourceAdmission Admission { get; }
        internal long RequestId { get; set; }
        internal ProcessingCancellation Cancellation { get; }
        internal Guid DocumentContext => Document.ContextId;
        internal ulong SourceIdentity => Document.SourceIdentity;
        internal TimeSpan Elapsed => Cancellation.Elapsed;
        internal Task<ProcessingFrame?> Task => completion.Task;

        internal void Complete(ProcessingFrame? frame) => completion.TrySetResult(frame);
        internal void Fail(Exception exception) => completion.TrySetException(exception);
        internal void CompleteCoalesced()
        {
            completion.TrySetResult(null);
            Dispose();
        }
        public void Dispose() => Cancellation.Dispose();
    }

    private sealed class ProcessingCancellation : IDisposable
    {
        private readonly CancellationTokenSource source = new();
        private readonly CancellationTokenRegistration externalRegistration;
        private readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private int reason;
        private int diagnosticWasRecorded;

        internal ProcessingCancellation(CancellationToken externalToken)
        {
            DiagnosticRequestId = Guid.NewGuid();
            externalRegistration = externalToken.Register(
                static state => ((ProcessingCancellation)state!).Cancel(NativeCancellationReason.UserRequested),
                this);
        }

        internal Guid DiagnosticRequestId { get; }
        internal CancellationToken Token => source.Token;
        internal NativeCancellationReason Reason => (NativeCancellationReason)Volatile.Read(ref reason);
        internal TimeSpan Elapsed => stopwatch.Elapsed;
        internal bool DiagnosticWasRecorded => Volatile.Read(ref diagnosticWasRecorded) != 0;

        internal void Cancel(NativeCancellationReason cancellationReason)
        {
            if (cancellationReason == NativeCancellationReason.None)
                cancellationReason = NativeCancellationReason.UserRequested;
            Interlocked.CompareExchange(ref reason, (int)cancellationReason, (int)NativeCancellationReason.None);
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        internal void MarkDiagnosticRecorded() => Interlocked.Exchange(ref diagnosticWasRecorded, 1);

        public void Dispose()
        {
            externalRegistration.Dispose();
            source.Dispose();
        }
    }
}
