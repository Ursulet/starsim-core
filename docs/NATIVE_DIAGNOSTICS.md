# Native Diagnostics

Every managed call returning `NativeStatus` is completed through `NativeCall.NativeOperation`. The operation starts its timestamp and stopwatch before the ABI invocation and, for every status other than `Ok`, immediately reads the thread-local C++ message through `ssc_get_last_error_utf8` before another native call can overwrite it.

## Diagnostic record

Each failure or cancellation records:

- numeric and symbolic `StatusCode` plus stable display code `SSC-NNN`;
- exact exported operation name;
- per-processing-request `RequestId`;
- processor ID, algorithm name, and named parameter values where applicable;
- image width and height;
- managed thread ID, UTC timestamp, and elapsed native-call duration;
- the unmodified native C++ error text;
- cancellation reason and a full managed stack trace.

`NativeOperationException` represents real native failures and retains the diagnostic record, friendly message, native text, and any available inner exception. `NativeOperationCanceledException` remains an `OperationCanceledException`, but also retains the complete diagnostic record.

## Cancellation provenance

`ImageProcessingService` assigns every request a GUID and a shared cancellation state. The state travels through `AsyncLocal` into worker tasks and the ABI boundary. Cancellation reasons are:

- `UserRequested`: an external/user token or explicit cancellation;
- `RequestSuperseded`: a new source image replaced work belonging to the previous document;
- `Shutdown`: application/service disposal;
- `ResourceGovernor`: cancellation requested by a future memory/CPU governor;
- `NativeInternalUnexpected`: C++ returned `Cancelled` although no managed cancellation source was active.

Before any native cancellation exception is thrown, the complete record and the real C++ cancellation message are logged. Intentional cancellations are not raised as UI errors. `NativeInternalUnexpected` is logged and displayed as a processing error.

Slider changes use one running plus one replaceable latest-pending snapshot. Replacing a pending state is logged as scheduler coalescence and never enters the native cancellation path. A new interactive request may supersede lower-priority definitive work when UI prioritization is enabled. Debug scheduler records contain numeric RequestId, source identity, document context, revision, quality, elapsed time, disposition, `running`, `pending`, `depth`, and `maxDepth`, with the invariant `depth <= 2`. Source-image replacement, shutdown, explicit user action, and resource-governor cancellation remain distinguishable normal scheduler outcomes.

Debug performance records are emitted once per enabled processor with module ID/name, elapsed milliseconds, cache hit/miss, preview/full quality, and request ID. Fast interactive color/tone transforms emit their own elapsed time and source-buffer marker.

The native progress callback is C-only and carries `struct_size`, ABI version, stage, completed steps, total steps, and a real fraction. No Avalonia object crosses the ABI. Current exact stages are Wavelet layer and Richardson–Lucy iteration; unknown stages are represented as indeterminate instead of a fabricated percentage.

## Outputs and rotation

`DiagnosticService` writes the same record to `Debug`/`Trace` output and to `logs/starsim-core-YYYYMMDD.log` under the application directory. The current day's file rotates to `.log.1` at 5 MiB. `STARSIMCORE_LOG_DIR` can override the directory for diagnostics or deployment.

Logging failures never replace the original processing exception. Unhandled AppDomain and unobserved Task exceptions are also written to the managed diagnostic log.

## User interface

Debug builds show a non-modal `Processing Error` panel with the friendly summary, code, expandable technical details, full stack/native context, `Copy details`, and `Open log folder`. Release builds show only the friendly summary and error code; the full record remains in the log.
