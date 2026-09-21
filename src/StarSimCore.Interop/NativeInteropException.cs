namespace StarSimCore.Interop;

public class NativeInteropException : Exception
{
    public NativeInteropException(NativeStatus status, string message)
        : base(message)
    {
        Status = status;
    }

    protected NativeInteropException(NativeStatus status, string message, Exception? innerException)
        : base(message, innerException)
    {
        Status = status;
    }

    public NativeStatus Status { get; }
}

public sealed class NativeOperationException : NativeInteropException
{
    public NativeOperationException(
        NativeDiagnosticRecord diagnostic,
        string friendlyMessage,
        Exception? innerException = null)
        : base(
            diagnostic.StatusCode,
            $"{friendlyMessage} [{diagnostic.ErrorCode}] {diagnostic.NativeErrorText}",
            innerException)
    {
        Diagnostic = diagnostic;
        FriendlyMessage = friendlyMessage;
        Data[nameof(NativeDiagnosticRecord)] = diagnostic.ToTechnicalDetails();
    }

    public NativeStatus StatusCode => Diagnostic.StatusCode;
    public NativeDiagnosticRecord Diagnostic { get; }
    public string FriendlyMessage { get; }
}
