namespace StarSimCore.Application.Imaging;

public static class SourceGuard
{
    public static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool IsSourcePath(string sourceCanonicalPath, string candidatePath) =>
        string.Equals(
            Canonicalize(sourceCanonicalPath),
            Canonicalize(candidatePath),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    public static void EnsureExportPathAllowed(string sourceCanonicalPath, string exportPath)
    {
        if (IsSourcePath(sourceCanonicalPath, exportPath))
        {
            throw new InvalidOperationException("Export cannot overwrite the immutable source image.");
        }
    }
}
