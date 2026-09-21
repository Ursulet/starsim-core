using StarSimCore.Application.Imaging;
using StarSimCore.Application.Processing;
using StarSimCore.Interop;

namespace StarSimCore.Application.Export;

public sealed class ExportService
{
    private readonly PipelineEngine engine;

    public ExportService(IReadOnlyList<IImageProcessor>? processorDefinitions = null)
        : this(new PipelineEngine(processorDefinitions: processorDefinitions))
    {
    }

    internal ExportService(PipelineEngine engine)
    {
        this.engine = engine;
    }

    public static string GetDefaultExportPath(string sourcePath, ExportFormat format)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var directory = Path.GetDirectoryName(sourcePath) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = format switch
        {
            ExportFormat.Tiff16 => ".tif",
            ExportFormat.Png16 => ".png",
            ExportFormat.Png8 => ".png",
            _ => ".tif"
        };
        return Path.Combine(directory, $"{fileNameWithoutExtension}_processed{extension}");
    }

    public Task ExportAsync(
        ImageDocument document,
        PipelineSnapshot snapshot,
        ExportOptions options,
        IProgress<ExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DestinationPath);

        cancellationToken.ThrowIfCancellationRequested();

        // 1. Block source path overwrite (strict canonical comparison)
        SourceGuard.EnsureExportPathAllowed(document.Source.CanonicalPath, options.DestinationPath);

        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(options.DestinationPath));
        if (!string.IsNullOrEmpty(destinationDirectory) && !Directory.Exists(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new ExportProgress("Preparing full-resolution pipeline...", 0.0, 0, 100));

            // 2. Full-resolution processing context
            var context = new ProcessorContext(
                Quality: ProcessingQuality.FullResolution,
                ResolutionScale: 1.0f,
                Metadata: document.Master.GetMetadata(),
                ReportProgress: procProgress =>
                {
                    progress?.Report(new ExportProgress(
                        $"Processing {procProgress.ModuleName}...",
                        procProgress.Fraction * 0.7, // 0 - 70% is processing
                        procProgress.CurrentStep,
                        procProgress.TotalSteps));
                });

            NativeImage? processedImage = null;
            try
            {
                processedImage = engine.Execute(document.Master, snapshot, context, cancellationToken);
                if (processedImage is null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new InvalidOperationException("Pipeline processing failed to produce output.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new ExportProgress("Encoding and writing to disk...", 0.7, 70, 100));

                var nativeFormat = options.Format switch
                {
                    ExportFormat.Tiff16 => NativeExportFormat.Tiff16,
                    ExportFormat.Png16 => NativeExportFormat.Png16,
                    ExportFormat.Png8 => NativeExportFormat.Png8,
                    _ => throw new ArgumentOutOfRangeException(nameof(options.Format))
                };

                processedImage.ExportFile(
                    options.DestinationPath,
                    nativeFormat,
                    cancellationToken,
                    nativeProgress =>
                    {
                        var fraction = nativeProgress.Fraction.HasValue
                            ? 0.7 + (nativeProgress.Fraction.Value * 0.3)
                            : 0.7;
                        progress?.Report(new ExportProgress(
                            $"Writing export file... ({nativeProgress.CurrentStep}/{nativeProgress.TotalSteps})",
                            fraction,
                            nativeProgress.CurrentStep,
                            nativeProgress.TotalSteps));
                    });

                progress?.Report(new ExportProgress("Export complete.", 1.0, 100, 100));
            }
            finally
            {
                processedImage?.Dispose();
            }
        }, cancellationToken);
    }
}
