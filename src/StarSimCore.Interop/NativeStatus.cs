namespace StarSimCore.Interop;

public enum NativeStatus : int
{
    Ok = 0,
    InvalidArgument = 1,
    OutOfMemory = 2,
    AbiMismatch = 3,
    BufferTooSmall = 4,
    Unsupported = 5,
    InternalError = 6,
    IoError = 7,
    DecodeError = 8,
    Cancelled = 9,
}
