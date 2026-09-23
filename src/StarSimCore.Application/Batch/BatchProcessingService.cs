using System.Collections.Immutable;
using System.Diagnostics;
using StarSimCore.Application.Export;
using StarSimCore.Application.Imaging;
using StarSimCore.Application.Performance;
using StarSimCore.Application.Processing;

namespace StarSimCore.Application.Batch;

public enum BatchProcessingStage
{
    Pending,
    Opening,
    Processing,
    Writing,
    Completed,
    Failed,
    Skipped,
    Cancelled,
}

public sealed record BatchProcessingRequest(
    IReadOnlyList<string> SourcePaths,
    string DestinationDirectory,
    PipelineSnapshot Snapshot,
    ExportFormat Format,
    bool OverwriteExisting = false,
    int StartIndex = 1);

public sealed record BatchProcessingProgress(
    int ItemNumber,
    int TotalItems,
    int CompletedItems,
    int FailedItems,
    int SkippedItems,
    string SourcePath,
    string DestinationPath,
    BatchProcessingStage Stage,
    double? FileFraction,
    double OverallFraction,
    string? Detail = null);

public sealed record BatchFileResult(
    string SourcePath,
    string DestinationPath,
    BatchProcessingStage Stage,
    TimeSpan Elapsed,
    string? ErrorMessage = null);

public sealed record BatchProcessingResult(
    ImmutableArray<BatchFileResult> Files,
    TimeSpan Elapsed)
{
    public int CompletedCount => Files.Count(file => file.Stage == BatchProcessingStage.Completed);
    public int FailedCount => Files.Count(file => file.Stage == BatchProcessingStage.Failed);
    public int SkippedCount => Files.Count(file => file.Stage == BatchProcessingStage.Skipped);
}

/// <summary>
/// Applies one immutable pipeline snapshot to a deterministic, sequential list of
/// source images. No preview frame or histogram is generated. Each source document,
/// native pipeline cache and output image is released before the next item starts.
/// </summary>
public sealed class BatchProcessingService
{
    private static readonly string[] SupportedExtensions = [".tif", ".tiff", ".png"];
    private readonly IReadOnlyList<IImageProcessor> processorDefinitions;
    private readonly ResourceGovernor governor;
    private readonly ImageOpenService imageOpenService = new();

    public BatchProcessingService(
        IReadOnlyList<IImageProcessor>? processorDefinitions = null,
        ResourceGovernor? governor = null)
    {
        this.processorDefinitions = processorDefinitions ?? BuiltInProcessors.All;
        this.governor = governor ?? ResourceGovernor.Shared;
    }

    public static IReadOnlyList<string> EnumerateSources(string sourceDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        var canonicalDirectory = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(canonicalDirectory))
            throw new DirectoryNotFoundException(canonicalDirectory);

        return Directory
            .EnumerateFiles(canonicalDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .Select(Path.GetFullPath)
            .ToArray();
    }

    public async Task<BatchProcessingResult> ProcessAsync(
        BatchProcessingRequest request,
        IProgress<BatchProcessingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationDirectory);
        if (request.SourcePaths.Count == 0)
            throw new ArgumentException("At least one source image is required.", nameof(request));
        if (request.StartIndex < 1)
            throw new ArgumentOutOfRangeException(nameof(request), "The start index must be at least one.");

        var sources = request.SourcePaths
            .Select(SourceGuard.Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var destinationDirectory = Path.GetFullPath(request.DestinationDirectory);
        Directory.CreateDirectory(destinationDirectory);
        var sourceSet = sources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinationPaths = sources
            .Select((sourcePath, itemOffset) => BuildDestinationPath(
                destinationDirectory,
                sourcePath,
                checked(request.StartIndex + itemOffset),
                request.Format))
            .ToArray();
        if (destinationPaths.Any(sourceSet.Contains))
        {
            throw new InvalidOperationException(
                "The batch naming plan would overwrite one of the selected source images. Choose another destination folder.");
        }

        // PipelineSnapshot is already immutable. Rebuilding it makes the session
        // boundary explicit and prevents any future mutable collection regression.
        var frozenSnapshot = new PipelineSnapshot(
            request.Snapshot.Modules
                .Select(module => module with { Parameters = module.Parameters.ToImmutableArray() })
                .ToImmutableArray(),
            request.Snapshot.Revision);

        var results = ImmutableArray.CreateBuilder<BatchFileResult>(sources.Length);
        var batchTimer = Stopwatch.StartNew();
        var completed = 0;
        var failed = 0;
        var skipped = 0;

        for (var itemOffset = 0; itemOffset < sources.Length; itemOffset++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = sources[itemOffset];
            var itemNumber = itemOffset + 1;
            var destinationPath = destinationPaths[itemOffset];

            if (File.Exists(destinationPath) && !request.OverwriteExisting)
            {
                skipped++;
                results.Add(new BatchFileResult(
                    sourcePath,
                    destinationPath,
                    BatchProcessingStage.Skipped,
                    TimeSpan.Zero));
                Report(
                    progress,
                    itemNumber,
                    sources.Length,
                    completed,
                    failed,
                    skipped,
                    sourcePath,
                    destinationPath,
                    BatchProcessingStage.Skipped,
                    1,
                    null);
                continue;
            }

            var extension = Path.GetExtension(destinationPath);
            var temporaryPath = Path.Combine(
                destinationDirectory,
                $".{Path.GetFileNameWithoutExtension(destinationPath)}.{Guid.NewGuid():N}.partial{extension}");
            var itemTimer = Stopwatch.StartNew();

            try
            {
                Report(
                    progress,
                    itemNumber,
                    sources.Length,
                    completed,
                    failed,
                    skipped,
                    sourcePath,
                    destinationPath,
                    BatchProcessingStage.Opening,
                    0,
                    null);

                using var document = await imageOpenService
                    .OpenAsync(sourcePath, cancellationToken)
                    .ConfigureAwait(false);
                using var exportService = new ExportService(processorDefinitions, governor);
                var exportProgress = new InlineProgress<ExportProgress>(update =>
                {
                    var stage = update.Fraction is >= 0.7
                        ? BatchProcessingStage.Writing
                        : BatchProcessingStage.Processing;
                    Report(
                        progress,
                        itemNumber,
                        sources.Length,
                        completed,
                        failed,
                        skipped,
                        sourcePath,
                        destinationPath,
                        stage,
                        update.Fraction,
                        update.Stage);
                });

                await exportService.ExportAsync(
                        document,
                        frozenSnapshot,
                        new ExportOptions(temporaryPath, request.Format),
                        exportProgress,
                        cancellationToken)
                    .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporaryPath, destinationPath, request.OverwriteExisting);
                completed++;
                results.Add(new BatchFileResult(
                    sourcePath,
                    destinationPath,
                    BatchProcessingStage.Completed,
                    itemTimer.Elapsed));
                Report(
                    progress,
                    itemNumber,
                    sources.Length,
                    completed,
                    failed,
                    skipped,
                    sourcePath,
                    destinationPath,
                    BatchProcessingStage.Completed,
                    1,
                    null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDeleteTemporaryFile(temporaryPath);
                Report(
                    progress,
                    itemNumber,
                    sources.Length,
                    completed,
                    failed,
                    skipped,
                    sourcePath,
                    destinationPath,
                    BatchProcessingStage.Cancelled,
                    null,
                    null);
                throw;
            }
            catch (Exception exception)
            {
                TryDeleteTemporaryFile(temporaryPath);
                failed++;
                results.Add(new BatchFileResult(
                    sourcePath,
                    destinationPath,
                    BatchProcessingStage.Failed,
                    itemTimer.Elapsed,
                    exception.Message));
                Report(
                    progress,
                    itemNumber,
                    sources.Length,
                    completed,
                    failed,
                    skipped,
                    sourcePath,
                    destinationPath,
                    BatchProcessingStage.Failed,
                    1,
                    exception.Message);
            }
        }

        return new BatchProcessingResult(results.ToImmutable(), batchTimer.Elapsed);
    }

    public static string BuildDestinationPath(
        string destinationDirectory,
        string sourcePath,
        int index,
        ExportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var extension = format switch
        {
            ExportFormat.Tiff16 => ".tif",
            ExportFormat.Png16 or ExportFormat.Png8 => ".png",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        return Path.Combine(
            Path.GetFullPath(destinationDirectory),
            $"{sourceName}_batch_{index:D4}{extension}");
    }

    private static void Report(
        IProgress<BatchProcessingProgress>? progress,
        int itemNumber,
        int totalItems,
        int completedItems,
        int failedItems,
        int skippedItems,
        string sourcePath,
        string destinationPath,
        BatchProcessingStage stage,
        double? fileFraction,
        string? detail)
    {
        var boundedFileFraction = fileFraction is { } value ? Math.Clamp(value, 0, 1) : 0;
        var overallFraction = Math.Clamp(
            ((itemNumber - 1) + boundedFileFraction) / Math.Max(1, totalItems),
            0,
            1);
        progress?.Report(new BatchProcessingProgress(
            itemNumber,
            totalItems,
            completedItems,
            failedItems,
            skippedItems,
            sourcePath,
            destinationPath,
            stage,
            fileFraction,
            overallFraction,
            detail));
    }

    private static void TryDeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Preserve the primary processing/cancellation failure. Partial files
            // are hidden and uniquely named, so they cannot replace valid output.
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
