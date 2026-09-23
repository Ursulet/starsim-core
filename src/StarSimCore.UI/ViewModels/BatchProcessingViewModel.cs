using System.Collections.ObjectModel;
using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarSimCore.Application.Batch;
using StarSimCore.Application.Export;
using StarSimCore.Application.Localization;
using StarSimCore.Application.Performance;
using StarSimCore.Application.Processing;

namespace StarSimCore.UI.ViewModels;

public sealed class BatchFileViewModel : ObservableObject, IDisposable
{
    private BatchProcessingStage stage = BatchProcessingStage.Pending;
    private string outputName = string.Empty;
    private string? errorMessage;
    private TimeSpan elapsed;

    public BatchFileViewModel(int index, string sourcePath)
    {
        Index = index;
        SourcePath = sourcePath;
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
    }

    public int Index { get; }
    public string SourcePath { get; }
    public string FileName => Path.GetFileName(SourcePath);
    public string OutputName
    {
        get => outputName;
        private set => SetProperty(ref outputName, value);
    }
    public string StatusText => LocalizationService.Instance[$"Batch.Stage.{stage}"];
    public string DetailText => stage == BatchProcessingStage.Failed && !string.IsNullOrWhiteSpace(errorMessage)
        ? errorMessage
        : elapsed > TimeSpan.Zero
            ? elapsed.ToString(@"mm\:ss\.f")
            : string.Empty;

    public void Prepare(string outputPath)
    {
        OutputName = Path.GetFileName(outputPath);
        stage = BatchProcessingStage.Pending;
        errorMessage = null;
        elapsed = TimeSpan.Zero;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
    }

    public void Update(BatchProcessingStage nextStage, string outputPath, string? error, TimeSpan? duration = null)
    {
        stage = nextStage;
        OutputName = Path.GetFileName(outputPath);
        errorMessage = error;
        if (duration is { } value) elapsed = value;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(DetailText));
    }

    private void OnLanguageChanged(object? sender, EventArgs e) =>
        OnPropertyChanged(nameof(StatusText));

    public void Dispose() => LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
}

public sealed partial class BatchProcessingViewModel : ObservableObject, IDisposable
{
    private readonly PipelineSnapshot frozenSnapshot;
    private readonly BatchProcessingService batchService;
    private CancellationTokenSource? runCancellation;
    private bool disposed;
    private bool destinationWasSuggested;

    public BatchProcessingViewModel(
        PipelineSnapshot snapshot,
        IReadOnlyList<IImageProcessor> processorDefinitions,
        ResourceGovernor? governor = null,
        ExportFormat initialFormat = ExportFormat.Tiff16)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        frozenSnapshot = new PipelineSnapshot(
            snapshot.Modules
                .Select(module => module with { Parameters = module.Parameters.ToImmutableArray() })
                .ToImmutableArray(),
            snapshot.Revision);
        batchService = new BatchProcessingService(processorDefinitions, governor);
        selectedFormatOption = FormatOptions.FirstOrDefault(option => option.Value == initialFormat)
            ?? FormatOptions[0];
        SnapshotHash = PipelineStateHasher.ComputeSha256(frozenSnapshot);
        LocalizationService.Instance.LanguageChanged += OnLanguageChanged;
        currentStatusText = LocalizationService.Instance["Batch.Status.SelectSource"];
    }

    public ObservableCollection<BatchFileViewModel> Files { get; } = [];
    public IReadOnlyList<LocalizedChoiceViewModel<ExportFormat>> FormatOptions { get; } =
    [
        new(ExportFormat.Tiff16, "Batch.Format.Tiff16"),
        new(ExportFormat.Png16, "Batch.Format.Png16"),
        new(ExportFormat.Png8, "Batch.Format.Png8"),
    ];

    public string SnapshotHash { get; }
    public long SnapshotRevision => frozenSnapshot.Revision;
    public int EnabledModuleCount => frozenSnapshot.Modules.Count(module => module.Enabled);
    public string SnapshotSummary => LocalizationService.Instance.GetString(
        "Batch.SnapshotSummary",
        SnapshotRevision,
        SnapshotHash[..Math.Min(12, SnapshotHash.Length)],
        EnabledModuleCount);
    public string FileCountText => LocalizationService.Instance.GetString("Batch.FileCount", Files.Count);
    public string CompletedCountText => LocalizationService.Instance.GetString("Batch.Count.Completed", CompletedCount);
    public string FailedCountText => LocalizationService.Instance.GetString("Batch.Count.Failed", FailedCount);
    public string SkippedCountText => LocalizationService.Instance.GetString("Batch.Count.Skipped", SkippedCount);
    public string NamingExample
    {
        get
        {
            var sourcePath = Files.FirstOrDefault()?.SourcePath ?? "planet.tif";
            var sourceDirectory = Path.GetDirectoryName(sourcePath);
            var directory = !string.IsNullOrWhiteSpace(DestinationFolder)
                ? DestinationFolder
                : !string.IsNullOrWhiteSpace(sourceDirectory)
                    ? sourceDirectory
                    : Environment.CurrentDirectory;
            return LocalizationService.Instance.GetString(
                "Batch.NamingExample",
                Path.GetFileName(BatchProcessingService.BuildDestinationPath(
                    directory,
                    sourcePath,
                    1,
                    SelectedFormatOption.Value)));
        }
    }
    public bool HasFiles => Files.Count > 0;
    public bool CanEdit => !IsRunning;
    public bool CanStart => !IsRunning && HasFiles && !string.IsNullOrWhiteSpace(DestinationFolder);
    public bool CanCancel => IsRunning && runCancellation is { IsCancellationRequested: false };
    public bool HasCurrentFile => !string.IsNullOrWhiteSpace(CurrentFileName);

    [ObservableProperty] private string sourceFolder = string.Empty;
    [ObservableProperty] private string destinationFolder = string.Empty;
    [ObservableProperty] private LocalizedChoiceViewModel<ExportFormat> selectedFormatOption;
    [ObservableProperty] private bool overwriteExisting;
    [ObservableProperty] private bool isRunning;
    [ObservableProperty] private bool isCancelling;
    [ObservableProperty] private double overallPercent;
    [ObservableProperty] private double filePercent;
    [ObservableProperty] private string currentFileName = string.Empty;
    [ObservableProperty] private string currentStatusText;
    [ObservableProperty] private int completedCount;
    [ObservableProperty] private int failedCount;
    [ObservableProperty] private int skippedCount;

    partial void OnDestinationFolderChanged(string value)
    {
        RefreshOutputNames();
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(NamingExample));
        StartBatchCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedFormatOptionChanged(LocalizedChoiceViewModel<ExportFormat> value)
    {
        RefreshOutputNames();
        OnPropertyChanged(nameof(NamingExample));
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanCancel));
        StartBatchCommand.NotifyCanExecuteChanged();
        CancelBatchCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentFileNameChanged(string value) =>
        OnPropertyChanged(nameof(HasCurrentFile));

    partial void OnCompletedCountChanged(int value) =>
        OnPropertyChanged(nameof(CompletedCountText));

    partial void OnFailedCountChanged(int value) =>
        OnPropertyChanged(nameof(FailedCountText));

    partial void OnSkippedCountChanged(int value) =>
        OnPropertyChanged(nameof(SkippedCountText));

    public void SetSourceFolder(string folder)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (IsRunning) return;

        try
        {
            var sources = BatchProcessingService.EnumerateSources(folder);
            SetSourceFilesCore(folder, sources);
        }
        catch (Exception exception)
        {
            ClearFiles();
            SourceFolder = string.Empty;
            CurrentStatusText = LocalizationService.Instance.GetString("Batch.Status.SourceError", exception.Message);
        }

        NotifyFilesChanged();
    }

    /// <summary>
    /// Loads an already captured, deterministic source list. This is used by the
    /// two-stage Batch workflow so files added to the folder while the reference
    /// image is being edited do not silently enter the active batch.
    /// </summary>
    public void SetSourceFiles(string folder, IReadOnlyList<string> sourcePaths)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(sourcePaths);
        if (IsRunning) return;

        try
        {
            SetSourceFilesCore(folder, sourcePaths);
        }
        catch (Exception exception)
        {
            ClearFiles();
            SourceFolder = string.Empty;
            CurrentStatusText = LocalizationService.Instance.GetString("Batch.Status.SourceError", exception.Message);
        }

        NotifyFilesChanged();
    }

    public void SetDestinationFolder(string folder)
    {
        if (IsRunning) return;
        destinationWasSuggested = false;
        DestinationFolder = Path.GetFullPath(folder);
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartBatchAsync()
    {
        if (!CanStart) return;
        var cancellation = new CancellationTokenSource();
        runCancellation = cancellation;
        IsRunning = true;
        IsCancelling = false;
        OverallPercent = 0;
        FilePercent = 0;
        CompletedCount = 0;
        FailedCount = 0;
        SkippedCount = 0;
        CurrentFileName = string.Empty;
        CurrentStatusText = LocalizationService.Instance["Batch.Status.Starting"];

        var sources = Files.Select(file => file.SourcePath).ToArray();
        for (var index = 0; index < Files.Count; index++)
        {
            var outputPath = BatchProcessingService.BuildDestinationPath(
                DestinationFolder,
                sources[index],
                index + 1,
                SelectedFormatOption.Value);
            Files[index].Prepare(outputPath);
        }

        try
        {
            var progress = new Progress<BatchProcessingProgress>(UpdateProgress);
            var result = await batchService.ProcessAsync(
                new BatchProcessingRequest(
                    sources,
                    DestinationFolder,
                    frozenSnapshot,
                    SelectedFormatOption.Value,
                    OverwriteExisting),
                progress,
                cancellation.Token);

            OverallPercent = 100;
            CompletedCount = result.CompletedCount;
            FailedCount = result.FailedCount;
            SkippedCount = result.SkippedCount;
            ApplyDurations(result);
            CurrentStatusText = LocalizationService.Instance.GetString(
                "Batch.Status.Finished",
                result.CompletedCount,
                result.FailedCount,
                result.SkippedCount,
                FormatDuration(result.Elapsed));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CurrentStatusText = LocalizationService.Instance.GetString(
                "Batch.Status.CancelledSummary",
                CompletedCount,
                FailedCount,
                SkippedCount);
        }
        catch (Exception exception)
        {
            CurrentStatusText = LocalizationService.Instance.GetString("Batch.Status.FatalError", exception.Message);
        }
        finally
        {
            if (ReferenceEquals(runCancellation, cancellation)) runCancellation = null;
            cancellation.Dispose();
            IsCancelling = false;
            IsRunning = false;
        }
    }

    public Task StartPreparedBatchAsync() => StartBatchAsync();

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelBatch()
    {
        if (runCancellation is not { IsCancellationRequested: false } cancellation) return;
        IsCancelling = true;
        CurrentStatusText = LocalizationService.Instance["Batch.Status.Cancelling"];
        cancellation.Cancel();
        OnPropertyChanged(nameof(CanCancel));
        CancelBatchCommand.NotifyCanExecuteChanged();
    }

    private void UpdateProgress(BatchProcessingProgress progress)
    {
        if (progress.ItemNumber < 1 || progress.ItemNumber > Files.Count) return;
        var item = Files[progress.ItemNumber - 1];
        item.Update(progress.Stage, progress.DestinationPath, progress.Detail);
        CurrentFileName = item.FileName;
        FilePercent = Math.Clamp((progress.FileFraction ?? 0) * 100, 0, 100);
        OverallPercent = Math.Clamp(progress.OverallFraction * 100, 0, 100);
        CompletedCount = progress.CompletedItems;
        FailedCount = progress.FailedItems;
        SkippedCount = progress.SkippedItems;
        if (!IsCancelling)
        {
            CurrentStatusText = LocalizationService.Instance.GetString(
                $"Batch.Status.{progress.Stage}",
                progress.ItemNumber,
                progress.TotalItems,
                item.FileName);
        }
    }

    private void ApplyDurations(BatchProcessingResult result)
    {
        var bySource = result.Files.ToDictionary(file => file.SourcePath, StringComparer.OrdinalIgnoreCase);
        foreach (var item in Files)
        {
            if (bySource.TryGetValue(item.SourcePath, out var resultItem))
            {
                item.Update(
                    resultItem.Stage,
                    resultItem.DestinationPath,
                    resultItem.ErrorMessage,
                    resultItem.Elapsed);
            }
        }
    }

    private void NotifyFilesChanged()
    {
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(NamingExample));
        StartBatchCommand.NotifyCanExecuteChanged();
    }

    private void RefreshOutputNames()
    {
        if (IsRunning || Files.Count == 0 || string.IsNullOrWhiteSpace(DestinationFolder)) return;
        for (var index = 0; index < Files.Count; index++)
        {
            Files[index].Prepare(BatchProcessingService.BuildDestinationPath(
                DestinationFolder,
                Files[index].SourcePath,
                index + 1,
                SelectedFormatOption.Value));
        }
    }

    private void ClearFiles()
    {
        foreach (var file in Files) file.Dispose();
        Files.Clear();
    }

    private void SetSourceFilesCore(string folder, IReadOnlyList<string> sourcePaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        var canonicalFolder = Path.GetFullPath(folder);
        if (!Directory.Exists(canonicalFolder))
            throw new DirectoryNotFoundException(canonicalFolder);

        var sources = sourcePaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ClearFiles();
        foreach (var (source, index) in sources.Select((source, index) => (source, index)))
            Files.Add(new BatchFileViewModel(index + 1, source));

        SourceFolder = canonicalFolder;
        if (string.IsNullOrWhiteSpace(DestinationFolder) || destinationWasSuggested)
        {
            destinationWasSuggested = true;
            DestinationFolder = Path.Combine(SourceFolder, "StarSim_Batch");
        }
        RefreshOutputNames();
        CurrentStatusText = HasFiles
            ? LocalizationService.Instance.GetString("Batch.Status.Ready", Files.Count)
            : LocalizationService.Instance["Batch.Status.NoFiles"];
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SnapshotSummary));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(NamingExample));
        OnPropertyChanged(nameof(CompletedCountText));
        OnPropertyChanged(nameof(FailedCountText));
        OnPropertyChanged(nameof(SkippedCountText));
        if (!IsRunning)
        {
            CurrentStatusText = HasFiles
                ? LocalizationService.Instance.GetString("Batch.Status.Ready", Files.Count)
                : LocalizationService.Instance["Batch.Status.SelectSource"];
        }
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1 ? duration.ToString(@"hh\:mm\:ss") : duration.ToString(@"mm\:ss");

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        LocalizationService.Instance.LanguageChanged -= OnLanguageChanged;
        runCancellation?.Cancel();
        ClearFiles();
        foreach (var option in FormatOptions) option.Dispose();
    }
}
