namespace StarSimCore.Domain.Imaging;

public sealed record SourceImageInfo(
    string CanonicalPath,
    string FileName,
    string FileFormat,
    long FileSize,
    DateTimeOffset LastWriteTimeUtc,
    string Sha256,
    bool HadAlpha,
    ImageMetadata Metadata,
    ulong SourceIdentity);
