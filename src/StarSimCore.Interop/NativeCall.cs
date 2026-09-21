using System.Text;

namespace StarSimCore.Interop;

internal static class NativeCall
{
    internal static NativeOperation Start(NativeOperationMetadata metadata) => new(metadata);

    internal static unsafe string GetLastError()
    {
        var queryStatus = NativeMethods.GetLastErrorUtf8(0, 0, out var requiredSize);
        if (queryStatus != NativeStatus.Ok || requiredSize <= 1)
        {
            return "Native operation failed without additional error detail.";
        }

        var buffer = new byte[requiredSize];
        fixed (byte* pointer = buffer)
        {
            var readStatus = NativeMethods.GetLastErrorUtf8(
                (nint)pointer,
                requiredSize,
                out _);
            if (readStatus != NativeStatus.Ok)
            {
                return "Native operation failed and its error detail could not be retrieved.";
            }
        }

        return Encoding.UTF8.GetString(buffer, 0, buffer.Length - 1);
    }

    internal sealed class NativeOperation
    {
        private readonly NativeOperationMetadata metadata;
        private readonly DateTimeOffset timestamp = DateTimeOffset.UtcNow;
        private readonly System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        private readonly Guid requestId;

        internal NativeOperation(NativeOperationMetadata metadata)
        {
            this.metadata = metadata;
            requestId = NativeRequestDiagnostics.CurrentRequestId;
            if (requestId == Guid.Empty) requestId = Guid.NewGuid();
        }

        internal void Complete(
            NativeStatus status,
            CancellationToken cancellationToken = default,
            Exception? innerException = null)
        {
            stopwatch.Stop();
            if (status == NativeStatus.Ok) return;

            var cancellationReason = NativeCancellationReason.None;
            if (status == NativeStatus.Cancelled)
            {
                cancellationReason = NativeRequestDiagnostics.CurrentCancellationReason;
                if (cancellationReason == NativeCancellationReason.None)
                    cancellationReason = cancellationToken.IsCancellationRequested
                        ? NativeCancellationReason.UserRequested
                        : NativeCancellationReason.NativeInternalUnexpected;
            }

            var diagnostic = new NativeDiagnosticRecord(
                status,
                metadata.OperationName,
                requestId,
                metadata.Module,
                metadata.Algorithm,
                metadata.Parameters,
                metadata.ImageWidth,
                metadata.ImageHeight,
                Environment.CurrentManagedThreadId,
                timestamp,
                stopwatch.Elapsed,
                GetLastError(),
                cancellationReason,
                new System.Diagnostics.StackTrace(skipFrames: 1, fNeedFileInfo: true).ToString());
            DiagnosticService.Current.Record(diagnostic);

            if (status == NativeStatus.Cancelled)
                throw new NativeOperationCanceledException(diagnostic, cancellationToken);

            throw new NativeOperationException(diagnostic, FriendlyMessage(status), innerException);
        }

        private static string FriendlyMessage(NativeStatus status) => status switch
        {
            NativeStatus.InvalidArgument => "The processing operation received an invalid value.",
            NativeStatus.OutOfMemory => "There is not enough memory to complete this processing operation.",
            NativeStatus.AbiMismatch => "The native processing engine is incompatible with this application build.",
            NativeStatus.BufferTooSmall => "The native processing buffer was too small.",
            NativeStatus.Unsupported => "This image or operation is not supported.",
            NativeStatus.IoError => "The image file could not be read.",
            NativeStatus.DecodeError => "The image data could not be decoded.",
            _ => "The native processing engine reported an internal error.",
        };
    }
}
