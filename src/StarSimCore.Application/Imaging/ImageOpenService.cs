using System.Security.Cryptography;
using StarSimCore.Domain.Imaging;
using StarSimCore.Interop;

namespace StarSimCore.Application.Imaging;

public sealed class ImageOpenService
{
    public Task<ImageDocument> OpenAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Open(path, cancellationToken), cancellationToken);

    private static ImageDocument Open(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalPath = SourceGuard.Canonicalize(path);
        var file = new FileInfo(canonicalPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("The selected image does not exist.", canonicalPath);
        }

        var hashBefore = ComputeSha256(canonicalPath, cancellationToken);
        LoadedNativeImage? loaded = null;
        NativeImage? processed = null;
        try
        {
            loaded = NativeImage.LoadFile(canonicalPath);
            cancellationToken.ThrowIfCancellationRequested();
            processed = loaded.Image.CloneWorking();
            var identity = loaded.Image.GetIdentity();
            var metadata = loaded.Image.GetMetadata();
            var hashAfter = ComputeSha256(canonicalPath, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(hashBefore),
                    Convert.FromHexString(hashAfter)))
            {
                throw new IOException("The source image changed while it was being opened.");
            }

            var source = new SourceImageInfo(
                canonicalPath,
                file.Name,
                loaded.FileFormat == NativeImageFileFormat.Png ? "PNG" : "TIFF",
                file.Length,
                file.LastWriteTimeUtc,
                hashBefore,
                loaded.HadAlpha,
                metadata,
                identity.SourceIdentity);
            var document = new ImageDocument(source, loaded.Image, processed);
            loaded = null;
            processed = null;
            return document;
        }
        finally
        {
            processed?.Dispose();
            loaded?.Image.Dispose();
        }
    }

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.SequentialScan);
        using var sha256 = SHA256.Create();
        var buffer = new byte[1024 * 128];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sha256.TransformBlock(buffer, 0, read, null, 0);
        }
        sha256.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha256.Hash!);
    }
}
