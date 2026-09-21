using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace StarSimCore.Interop;

public enum NativeCancellationReason
{
    None,
    UserRequested,
    RequestSuperseded,
    Shutdown,
    ResourceGovernor,
    NativeInternalUnexpected,
}

public sealed record NativeOperationMetadata(
    string OperationName,
    string? Module = null,
    string? Algorithm = null,
    string Parameters = "",
    uint ImageWidth = 0,
    uint ImageHeight = 0);

public sealed record NativeDiagnosticRecord(
    NativeStatus StatusCode,
    string OperationName,
    Guid RequestId,
    string? Module,
    string? Algorithm,
    string Parameters,
    uint ImageWidth,
    uint ImageHeight,
    int ManagedThreadId,
    DateTimeOffset TimestampUtc,
    TimeSpan Duration,
    string NativeErrorText,
    NativeCancellationReason CancellationReason,
    string? StackTrace)
{
    public bool IsIntentionalCancellation =>
        CancellationReason is NativeCancellationReason.UserRequested
            or NativeCancellationReason.RequestSuperseded
            or NativeCancellationReason.Shutdown
            or NativeCancellationReason.ResourceGovernor;

    public string ErrorCode => $"SSC-{(int)StatusCode:000}";

    public string ToTechnicalDetails()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"ErrorCode: {ErrorCode}");
        builder.AppendLine($"StatusCode: {StatusCode} ({(int)StatusCode})");
        builder.AppendLine($"Operation: {OperationName}");
        builder.AppendLine($"RequestId: {RequestId:D}");
        builder.AppendLine($"Module: {Module ?? "n/a"}");
        builder.AppendLine($"Algorithm: {Algorithm ?? "n/a"}");
        builder.AppendLine($"Parameters: {(string.IsNullOrWhiteSpace(Parameters) ? "n/a" : Parameters)}");
        builder.AppendLine($"Image: {(ImageWidth == 0 ? "n/a" : $"{ImageWidth}x{ImageHeight}")}");
        builder.AppendLine($"ManagedThreadId: {ManagedThreadId}");
        builder.AppendLine($"TimestampUtc: {TimestampUtc:O}");
        builder.AppendLine($"DurationMs: {Duration.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}");
        builder.AppendLine($"CancellationReason: {CancellationReason}");
        builder.AppendLine($"NativeError: {NativeErrorText}");
        if (!string.IsNullOrWhiteSpace(StackTrace))
        {
            builder.AppendLine("StackTrace:");
            builder.AppendLine(StackTrace);
        }
        return builder.ToString();
    }
}

public sealed class NativeOperationCanceledException : OperationCanceledException
{
    public NativeOperationCanceledException(NativeDiagnosticRecord diagnostic, CancellationToken cancellationToken)
        : base(
            $"Native operation '{diagnostic.OperationName}' was cancelled ({diagnostic.CancellationReason}, request {diagnostic.RequestId:D}).",
            cancellationToken)
    {
        Diagnostic = diagnostic;
        Data[nameof(NativeDiagnosticRecord)] = diagnostic.ToTechnicalDetails();
    }

    public NativeDiagnosticRecord Diagnostic { get; }
    public bool IsIntentional => Diagnostic.IsIntentionalCancellation;
}

public sealed class DiagnosticService
{
    private const long MaximumLogBytes = 5 * 1024 * 1024;
    private readonly object writeGate = new();

    private DiagnosticService()
    {
        var configured = Environment.GetEnvironmentVariable("STARSIMCORE_LOG_DIR");
        LogDirectory = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(AppContext.BaseDirectory, "logs")
            : Path.GetFullPath(configured);
    }

    public static DiagnosticService Current { get; } = new();
    public string LogDirectory { get; }
    public event EventHandler<NativeDiagnosticRecord>? NativeFailureReported;

    public void RecordModeSwitchAudit(
        string fromMode,
        string toMode,
        string beforeHash,
        string afterHash,
        string waveletBeforeHash,
        string waveletAfterHash,
        long parameterWritesBefore,
        long parameterWritesAfter)
    {
        var unchanged = string.Equals(beforeHash, afterHash, StringComparison.Ordinal) &&
                        string.Equals(waveletBeforeHash, waveletAfterHash, StringComparison.Ordinal) &&
                        parameterWritesBefore == parameterWritesAfter;
        var text = $"[{DateTimeOffset.UtcNow:O}] [MODE-AUDIT] from={fromMode}; to={toMode}; " +
                   $"beforeSha256={beforeHash}; afterSha256={afterHash}; " +
                   $"waveletBeforeSha256={waveletBeforeHash}; waveletAfterSha256={waveletAfterHash}; " +
                   $"parameterWrites={parameterWritesAfter - parameterWritesBefore}; invariant={(unchanged ? "PASS" : "FAIL")}";
        Debug.WriteLine(text);
        Trace.WriteLine(text);
        WriteFile(text);
    }

    public void Record(NativeDiagnosticRecord record)
    {
        var severity = record.StatusCode == NativeStatus.Cancelled ? "CANCEL" : "ERROR";
        var text = $"[{record.TimestampUtc:O}] [{severity}] {record.ToTechnicalDetails()}";
        Debug.WriteLine(text);
        Trace.WriteLine(text);
        WriteFile(text);
        if (record.StatusCode != NativeStatus.Cancelled || !record.IsIntentionalCancellation)
            NativeFailureReported?.Invoke(this, record);
    }

    public void RecordManagedException(string operation, Exception exception, Guid? requestId = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var timestamp = DateTimeOffset.UtcNow;
        var text = $"[{timestamp:O}] [MANAGED-ERROR] Operation: {operation}{Environment.NewLine}" +
                   $"RequestId: {requestId?.ToString("D") ?? "n/a"}{Environment.NewLine}{exception}";
        Debug.WriteLine(text);
        Trace.WriteLine(text);
        WriteFile(text);
    }

    public void RecordPluginEvent(string level, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var normalizedLevel = level.Trim().ToUpperInvariant();
        var text = $"[{DateTimeOffset.UtcNow:O}] [PLUGIN-{normalizedLevel}] {message}";
        Debug.WriteLine(text);
        Trace.WriteLine(text);
        WriteFile(text);
    }

    [Conditional("DEBUG")]
    public void RecordProcessorTiming(
        string moduleId,
        string moduleName,
        TimeSpan elapsed,
        bool cacheHit,
        string quality,
        Guid requestId)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"[{DateTimeOffset.UtcNow:O}] [PERF] module={moduleName} ({moduleId}); elapsedMs={elapsed.TotalMilliseconds:F3}; cache={(cacheHit ? "hit" : "miss")}; quality={quality}; requestId={requestId:D}");
        Debug.WriteLine(text);
        Trace.WriteLine(text);
    }

    [Conditional("DEBUG")]
    public void RecordFastPreviewTiming(TimeSpan elapsed, long revision, uint width, uint height)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"[{DateTimeOffset.UtcNow:O}] [PERF] module=Fast Interactive Preview; elapsedMs={elapsed.TotalMilliseconds:F3}; cache=completed-preview-buffer; quality=fast-interactive; revision={revision}; image={width}x{height}");
        Debug.WriteLine(text);
        Trace.WriteLine(text);
    }

    public void RecordSchedulerCancellation(
        Guid requestId,
        NativeCancellationReason reason,
        string stage,
        string parameters,
        uint imageWidth,
        uint imageHeight,
        TimeSpan duration)
    {
        Record(new NativeDiagnosticRecord(
            NativeStatus.Cancelled,
            "managed-pipeline-scheduler",
            requestId,
            "Pipeline",
            stage,
            parameters,
            imageWidth,
            imageHeight,
            Environment.CurrentManagedThreadId,
            DateTimeOffset.UtcNow,
            duration,
            "The managed scheduler ended this request before another native result was published.",
            reason,
            new StackTrace(skipFrames: 1, fNeedFileInfo: true).ToString()));
    }

    [Conditional("DEBUG")]
    public void RecordSchedulerCoalesced(
        Guid replacedRequestId,
        Guid replacementRequestId,
        long revision,
        long requestSequence)
    {
        var text = $"[{DateTimeOffset.UtcNow:O}] [SCHEDULER] pending request {replacedRequestId:D} replaced by {replacementRequestId:D}; sequence={requestSequence}; revision={revision}; depth=1-running+1-pending";
        Debug.WriteLine(text);
        Trace.WriteLine(text);
    }

    [Conditional("DEBUG")]
    public void RecordSchedulerState(
        Guid requestId,
        long revision,
        int running,
        int pending,
        int depth,
        int maximumObservedDepth)
    {
        var text = $"[{DateTimeOffset.UtcNow:O}] [SCHEDULER] requestId={requestId:D}; revision={revision}; running={running}; pending={pending}; depth={depth}; maxDepth={maximumObservedDepth}; invariant=depth<=2";
        Debug.WriteLine(text);
        Trace.WriteLine(text);
    }

    [Conditional("DEBUG")]
    public void RecordSchedulerLifecycle(
        long schedulerRequestId,
        Guid diagnosticRequestId,
        string quality,
        string disposition,
        long stateRevision,
        ulong sourceIdentity,
        Guid contextId,
        TimeSpan elapsed)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"[{DateTimeOffset.UtcNow:O}] [SCHEDULER] requestId={schedulerRequestId}; diagnosticRequestId={diagnosticRequestId:D}; quality={quality}; disposition={disposition}; revision={stateRevision}; sourceIdentity={sourceIdentity}; contextId={contextId:D}; elapsedMs={elapsed.TotalMilliseconds:F3}");
        Debug.WriteLine(text);
        Trace.WriteLine(text);
    }

    private void WriteFile(string text)
    {
        try
        {
            lock (writeGate)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = Path.Combine(LogDirectory, $"starsim-core-{DateTime.Now:yyyyMMdd}.log");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumLogBytes)
                {
                    var rotated = path + ".1";
                    if (File.Exists(rotated)) File.Delete(rotated);
                    File.Move(path, rotated);
                }
                File.AppendAllText(path, text + Environment.NewLine, new UTF8Encoding(false));
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"StarSim Core could not write its diagnostic log: {exception}");
        }
    }
}

public static class NativeRequestDiagnostics
{
    private sealed record ScopeState(Guid RequestId, Func<NativeCancellationReason> ReasonProvider, ScopeState? Parent);
    private static readonly AsyncLocal<ScopeState?> CurrentScope = new();

    public static Guid CurrentRequestId => CurrentScope.Value?.RequestId ?? Guid.Empty;
    internal static NativeCancellationReason CurrentCancellationReason =>
        CurrentScope.Value?.ReasonProvider() ?? NativeCancellationReason.None;

    public static IDisposable Begin(Guid requestId, Func<NativeCancellationReason> reasonProvider)
    {
        ArgumentNullException.ThrowIfNull(reasonProvider);
        var previous = CurrentScope.Value;
        CurrentScope.Value = new ScopeState(requestId, reasonProvider, previous);
        return new Scope(previous);
    }

    private sealed class Scope(ScopeState? previous) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CurrentScope.Value = previous;
        }
    }
}
