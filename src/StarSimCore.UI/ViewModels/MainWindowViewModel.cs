using System.Collections.ObjectModel;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarSimCore.Application;
using StarSimCore.Application.Batch;
using StarSimCore.Application.Export;
using StarSimCore.Application.Imaging;
using StarSimCore.Application.Presets;
using StarSimCore.Application.Processing;
using StarSimCore.Application.Projects;
using StarSimCore.Application.Localization;
using StarSimCore.Application.Performance;
using StarSimCore.Domain.Imaging;
using StarSimCore.Interop;

namespace StarSimCore.UI.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly double[] ZoomStops = [0.25, 0.5, 1, 2, 4, 8, 16];
    private readonly WorkspaceState state;
    private readonly IReadOnlyList<IImageProcessor> processorDefinitions;
    private readonly ImageOpenService imageOpenService = new();
    private readonly ResourceGovernor resourceGovernor = ResourceGovernor.Shared;
    private readonly ImageProcessingService processingService;
    private readonly ExportService exportService;
    private readonly ProjectService projectService = new();
    private readonly CustomPresetStore customPresetStore;
    private readonly PresetCatalog presetCatalog;
    private readonly ParameterHistory history;
    private PresetRecipe activePreset;
    private CancellationTokenSource? openCancellation;
    private CancellationTokenSource? exportCancellation;
    private ImageDocument? document;
    private ImageDocument? roiDocument;
    private byte[]? fullOriginalPixelsBeforeRoi;
    private byte[]? fullProcessedPixelsBeforeRoi;
    private Rect activeRoiRect;
    private long roiGeneration;
    private Task roiActivationTask = Task.CompletedTask;
    private bool suppressRoiChange;
    private long openGeneration;
    private bool suppressProcessing;
    private bool isParameterInteractionActive;
    private readonly DispatcherTimer authoritativeIdleTimer;
    private readonly DispatcherTimer interactivePreviewTimer;
    private readonly DispatcherTimer progressVisibilityTimer;
    private bool interactionHasExpensiveChange;
    private byte[]? authoritativeProcessedPixels;
    private PipelineSnapshot? authoritativeSnapshot;
    private PipelineSnapshot? lastScheduledSnapshot;
    private ImageDocument? lastScheduledDocument;
    private ProcessingQuality? lastScheduledQuality;
    private long latestDisplayedRequestSequence;
    private long latestProgressRequestId;
    private string? modeSwitchBeforeHash;
    private string? modeSwitchWaveletBeforeHash;
    private long modeSwitchWritesBefore;
    private string modeSwitchFrom = "Beginner";
    private long canonicalParameterWriteCount;
    private readonly Dictionary<string, BeginnerControlState> beginnerStatesByHash = new(StringComparer.Ordinal);
    private string? stagedBatchSourceFolder;
    private IReadOnlyList<string> stagedBatchSourcePaths = Array.Empty<string>();

    private sealed record BeginnerControlState(
        string Target,
        string Preset,
        string ColorPreset,
        double PresetStrength,
        double Detail,
        double NoiseReduction,
        double Color,
        double Brightness,
        double Contrast);

    public MainWindowViewModel(
        bool startInExpertMode = false,
        IReadOnlyList<IImageProcessor>? pluginProcessors = null)
    {
        var externalProcessors = pluginProcessors ?? [];
        processorDefinitions = BuiltInProcessors.All.Concat(externalProcessors).ToArray();
        if (processorDefinitions.Select(processor => processor.Id).Distinct(StringComparer.Ordinal).Count() !=
            processorDefinitions.Count)
        {
            throw new InvalidOperationException("The active processor registry contains duplicate IDs.");
        }
        processingService = new ImageProcessingService(resourceGovernor, processorDefinitions);
        exportService = new ExportService(processorDefinitions, resourceGovernor);
        customPresetStore = new CustomPresetStore(processorDefinitions: processorDefinitions);
        state = new WorkspaceState { IsExpertMode = startInExpertMode };
        isExpertMode = state.IsExpertMode;
        selectedTarget = state.SelectedTarget;
        selectedPreset = state.SelectedPreset;
        selectedColorPreset = state.SelectedColorPreset;
        presetStrength = state.PresetStrength;
        detail = state.Detail;
        noiseReduction = state.NoiseReduction;
        color = state.Color;
        brightness = state.Brightness;
        contrast = state.Contrast;
        gamma = 1;
        redBalance = 1;
        greenBalance = 1;
        blueBalance = 1;
        expertBrightness = 0;
        expertContrast = 0;
        expertSaturation = 50;
        presetCatalog = PresetCatalog.LoadBuiltIns();
        activePreset = presetCatalog.Get("generic", "Balanced");
        history = new ParameterHistory(
            CreateBeginnerSnapshot(revision: 0),
            processorDefinitions: processorDefinitions);
        processingService.ProgressChanged += OnProcessingProgressChanged;
        authoritativeIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        authoritativeIdleTimer.Tick += OnAuthoritativeIdle;
        interactivePreviewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(32) };
        interactivePreviewTimer.Tick += OnInteractivePreviewTimer;
        progressVisibilityTimer = new DispatcherTimer { Interval = ProcessingFeedbackPolicy.VisibilityDelay };
        progressVisibilityTimer.Tick += OnProgressVisibilityTimer;
        resourceGovernor.SnapshotChanged += OnResourceSnapshotChanged;
        var resourceSettings = resourceGovernor.Settings;
        selectedPerformanceProfile = resourceSettings.Profile;
        selectedInteractivePreviewQuality = resourceSettings.PreviewQuality;
        prioritizeUiResponsiveness = resourceSettings.PrioritizeUiResponsiveness;
        reduceCpuOnBattery = resourceSettings.ReduceCpuOnBattery;
        manualMaxThreads = resourceSettings.ManualMaxThreads ?? resourceGovernor.GetLimits().WorkerBudget;
        manualMemoryBudgetGb = (resourceSettings.ManualMemoryBudgetBytes ?? resourceGovernor.GetLimits().MemoryBudgetBytes) / (1024D * 1024 * 1024);
        PipelineModules = processorDefinitions
            .Select((definition, index) => new ProcessorModuleViewModel(
                index,
                definition,
                ApplyParameter,
                ApplyEnabled,
                id => ResetModule(id)))
            .ToArray();
        ExpertModules = PipelineModules
            .Where(module => !BuiltInProcessors.PresetCoreProcessorIds.Contains(module.Id))
            .ToArray();
        var orderedCategories = BuiltInProcessors.Categories.Ordered
            .Concat(externalProcessors.Select(processor => processor.Category))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        CategoryGroups = orderedCategories
            .Select(category => new ProcessorCategoryGroupViewModel(
                category,
                PipelineModules.Where(m => string.Equals(m.Category, category, StringComparison.OrdinalIgnoreCase)).ToArray(),
                categoryName => ResetCategory(categoryName)))
            .Where(group => group.Modules.Count > 0)
            .ToArray();
        ProcessorModuleViewModel Module(string id) => PipelineModules.Single(module => module.Id == id);
        var processingGroups = new List<ProcessingPanelGroupViewModel>
        {
            new ProcessingPanelGroupViewModel(
                "DETAIL",
                "☰",
                [
                    Module(BuiltInProcessors.WaveletId),
                    Module(BuiltInProcessors.RichardsonLucyId),
                    Module(BuiltInProcessors.NoiseReductionId),
                    Module(BuiltInProcessors.LocalDetailId),
                    Module(BuiltInProcessors.UnsharpMaskId),
                    Module(BuiltInProcessors.MultiScaleSharpenId),
                    Module(BuiltInProcessors.DeringingId),
                    Module(BuiltInProcessors.RgbAlignId),
                ],
                isExpanded: true),
            new ProcessingPanelGroupViewModel(
                "COLOR",
                "◉",
                [
                    Module(BuiltInProcessors.AdvancedColorId),
                    Module(BuiltInProcessors.RgbBalanceId),
                    Module(BuiltInProcessors.SaturationId),
                ],
                isExpanded: false),
            new ProcessingPanelGroupViewModel(
                "TONE",
                "☼",
                [
                    Module(BuiltInProcessors.AdvancedToneId),
                    Module(BuiltInProcessors.ExposureId),
                    Module(BuiltInProcessors.ContrastId),
                    Module(BuiltInProcessors.GammaId),
                ],
                isExpanded: false),
        };
        var pluginModules = PipelineModules.Where(module => module.IsExternalPlugin).ToArray();
        if (pluginModules.Length > 0)
        {
            processingGroups.Add(new ProcessingPanelGroupViewModel(
                "PLUGINS",
                "♙",
                pluginModules,
                isExpanded: true));
        }
        ProcessingGroups = processingGroups;
        ApplySnapshotToControls(history.Current);
        RememberBeginnerState(history.Current);
        RefreshCustomPresets();
        UpdateMultiSharpeningWarning();
        RefreshHistoryStates();
    }

    public IReadOnlyList<ExportFormat> AvailableExportFormats { get; } = [ExportFormat.Tiff16, ExportFormat.Png16, ExportFormat.Png8];
    [ObservableProperty] private ExportFormat selectedExportFormat = ExportFormat.Tiff16;
    [ObservableProperty] private bool isExporting;
    [ObservableProperty] private double exportPercent;
    [ObservableProperty] private string exportStatusText = string.Empty;
    [ObservableProperty] private string? currentProjectPath;
    [ObservableProperty] private bool isLocateSourceRequired;
    [ObservableProperty] private string? locateSourceMessage;
    [ObservableProperty] private ProjectFile? pendingProject;
    [ObservableProperty] private ObservableCollection<CustomPreset> customPresets = [];
    [ObservableProperty] private CustomPreset? selectedCustomPreset;
    [ObservableProperty] private string newCustomPresetName = string.Empty;
    [ObservableProperty] private string newCustomPresetDescription = string.Empty;
    [ObservableProperty] private bool isSaveCustomPresetDialogVisible;

    public IReadOnlyList<string> Targets { get; } = ["Default", "Jupiter", "Saturn", "Mars", "Venus", "Moon", "Solar", "Generic"];
    public IReadOnlyList<string> Presets { get; } = ["Default", "Natural", "Balanced", "Detailed", "Strong"];
    public IReadOnlyList<string> ColorPresets { get; } = ["Natural", "Neutral", "Warm", "Vivid"];
    public IReadOnlyList<PerformanceProfile> PerformanceProfiles { get; } = Enum.GetValues<PerformanceProfile>();
    public IReadOnlyList<InteractivePreviewQuality> InteractivePreviewQualities { get; } = Enum.GetValues<InteractivePreviewQuality>();
    public IReadOnlyList<LocalizedChoiceViewModel<string>> TargetOptions { get; } =
    [
        new("Default", "Target.Default"),
        new("Jupiter", "Target.Jupiter"),
        new("Saturn", "Target.Saturn"),
        new("Mars", "Target.Mars"),
        new("Venus", "Target.Venus"),
        new("Moon", "Target.Moon"),
        new("Solar", "Target.Solar"),
        new("Generic", "Target.Generic"),
    ];
    public IReadOnlyList<LocalizedChoiceViewModel<string>> PresetOptions { get; } =
    [
        new("Default", "Preset.Default"),
        new("Natural", "Preset.Natural"),
        new("Balanced", "Preset.Balanced"),
        new("Detailed", "Preset.Detailed"),
        new("Strong", "Preset.Strong"),
    ];
    public IReadOnlyList<LocalizedChoiceViewModel<string>> ColorPresetOptions { get; } =
    [
        new("Natural", "ColorPreset.Natural"),
        new("Neutral", "ColorPreset.Neutral"),
        new("Warm", "ColorPreset.Warm"),
        new("Vivid", "ColorPreset.Vivid"),
    ];
    public IReadOnlyList<LocalizedChoiceViewModel<PerformanceProfile>> PerformanceProfileOptions { get; } =
    [
        new(PerformanceProfile.Eco, "Performance.Profile.Eco"),
        new(PerformanceProfile.Balanced, "Performance.Profile.Balanced"),
        new(PerformanceProfile.Maximum, "Performance.Profile.Maximum"),
    ];
    public IReadOnlyList<LocalizedChoiceViewModel<InteractivePreviewQuality>> InteractivePreviewQualityOptions { get; } =
    [
        new(InteractivePreviewQuality.Auto, "Performance.Preview.Auto"),
        new(InteractivePreviewQuality.Full, "Performance.Preview.Full"),
        new(InteractivePreviewQuality.Half, "Performance.Preview.Half"),
        new(InteractivePreviewQuality.Quarter, "Performance.Preview.Quarter"),
    ];

    public LocalizedChoiceViewModel<string>? SelectedTargetOption
    {
        get => TargetOptions.FirstOrDefault(option => option.Value == SelectedTarget);
        set { if (value is not null) SelectedTarget = value.Value; }
    }
    public LocalizedChoiceViewModel<string>? SelectedPresetOption
    {
        get => PresetOptions.FirstOrDefault(option => option.Value == SelectedPreset);
        set { if (value is not null) SelectedPreset = value.Value; }
    }
    public LocalizedChoiceViewModel<string>? SelectedColorPresetOption
    {
        get => ColorPresetOptions.FirstOrDefault(option => option.Value == SelectedColorPreset);
        set { if (value is not null) SelectedColorPreset = value.Value; }
    }
    public LocalizedChoiceViewModel<PerformanceProfile>? SelectedPerformanceProfileOption
    {
        get => PerformanceProfileOptions.FirstOrDefault(option => option.Value == SelectedPerformanceProfile);
        set { if (value is not null) SelectedPerformanceProfile = value.Value; }
    }
    public LocalizedChoiceViewModel<InteractivePreviewQuality>? SelectedInteractivePreviewQualityOption
    {
        get => InteractivePreviewQualityOptions.FirstOrDefault(option => option.Value == SelectedInteractivePreviewQuality);
        set { if (value is not null) SelectedInteractivePreviewQuality = value.Value; }
    }
    public IReadOnlyList<ProcessorModuleViewModel> PipelineModules { get; }
    internal IReadOnlyList<IImageProcessor> ProcessorDefinitions => processorDefinitions;
    public IReadOnlyList<ProcessorModuleViewModel> ExpertModules { get; }
    public IReadOnlyList<ProcessorCategoryGroupViewModel> CategoryGroups { get; }
    public IReadOnlyList<ProcessingPanelGroupViewModel> ProcessingGroups { get; }
    public bool IsBatchReferenceSessionActive => stagedBatchSourcePaths.Count > 0;
    public IReadOnlyList<string> StagedBatchSourcePaths => stagedBatchSourcePaths;
    public string? StagedBatchSourceFolder => stagedBatchSourceFolder;
    public string BatchToolbarText => LocalizationService.Instance[
        IsBatchReferenceSessionActive ? "Toolbar.Batch.Apply" : "Toolbar.Batch"];
    public string BatchToolbarTip => LocalizationService.Instance[
        IsBatchReferenceSessionActive ? "Toolbar.Batch.ApplyTip" : "Toolbar.Batch.Tip"];

    [ObservableProperty] private bool isMultiSharpeningWarningVisible;
    public string MultiSharpeningWarningText => LocalizationService.Instance["Warning.MultiSharpening"];

    public IReadOnlyList<LocalizationService.LanguageOption> AvailableLanguages =>
        LocalizationService.SupportedLanguageOptions;

    public string CurrentLanguageCode => LocalizationService.Instance.CurrentLanguage;
    public bool IsEnglishLanguage => CurrentLanguageCode == LocalizationService.English;
    public bool IsRomanianLanguage => CurrentLanguageCode == LocalizationService.Romanian;

    [RelayCommand]
    public void SetLanguage(string languageCode)
    {
        LocalizationService.Instance.CurrentLanguage = languageCode;
        OnPropertyChanged(nameof(CurrentLanguageCode));
        OnPropertyChanged(nameof(IsEnglishLanguage));
        OnPropertyChanged(nameof(IsRomanianLanguage));
        OnPropertyChanged(nameof(MultiSharpeningWarningText));
        OnPropertyChanged(nameof(BeginnerPresetDisplayName));
        OnPropertyChanged(nameof(ActivePresetDescription));
        OnPropertyChanged(nameof(ModeDescription));
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ZoomStatusText));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(MetadataSummary));
        OnPropertyChanged(nameof(AlphaSummary));
        OnPropertyChanged(nameof(HistogramStats));
        OnPropertyChanged(nameof(ResolvedCpuThreads));
        OnPropertyChanged(nameof(ResolvedMemoryLimit));
        OnPropertyChanged(nameof(ResourceDiagnostics));
        OnPropertyChanged(nameof(RoiSummary));
        OnPropertyChanged(nameof(SelectedTargetOption));
        OnPropertyChanged(nameof(SelectedPresetOption));
        OnPropertyChanged(nameof(SelectedColorPresetOption));
        OnPropertyChanged(nameof(SelectedPerformanceProfileOption));
        OnPropertyChanged(nameof(SelectedInteractivePreviewQualityOption));
        OnPropertyChanged(nameof(BatchToolbarText));
        OnPropertyChanged(nameof(BatchToolbarTip));
        if (!IsProcessing && !IsExporting)
        {
            ProcessingStatusText = IsImageLoaded
                ? LocalizationService.Instance["Status.ProcessingComplete"]
                : LocalizationService.Instance["Status.Ready"];
        }
        PixelReadout = LocalizationService.Instance["Viewer.PixelHint"];
        ApplyModuleFilter();
        RefreshHistoryStates();
    }

    public bool IsBeginnerMode => !IsExpertMode;
    public bool IsImageLoaded => document is not null;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);
    public bool ShowTechnicalDiagnostics
    {
        get
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }
    }
    public bool IsColorImage => document?.Source.Metadata.ChannelCount == 3;
    public bool CanUndo => history.CanUndo;
    public bool CanRedo => history.CanRedo;
    public PipelineSnapshot ActivePipelineSnapshot => history.Current;
    public string CanonicalPipelineHash => PipelineStateHasher.ComputeSha256(history.Current);
    public long CanonicalParameterWriteCount => canonicalParameterWriteCount;
    public bool IsBeginnerPresetModified => !ParameterHistory.ModulesEqual(
        history.Current,
        CreateBeginnerSnapshot(history.Current.Revision));
    public string BeginnerPresetDisplayName => IsBeginnerPresetModified
        ? LocalizationService.Instance["Beginner.CustomModified"]
        : LocalizationService.Instance.GetStringOrDefault($"Preset.{SelectedPreset}", SelectedPreset);
    public ProcessingSchedulerMetrics ProcessingMetrics => processingService.SchedulerMetrics;
    public string ActivePresetDescription => IsDefaultSelection
        ? LocalizationService.Instance["Beginner.NeutralDescription"]
        : LocalizationService.Instance.GetString(
            $"Beginner.PresetDescription.{SelectedPreset}",
            LocalizationService.Instance.GetStringOrDefault($"Target.{SelectedTarget}", SelectedTarget).ToLowerInvariant());
    public string ModeDescription => IsExpertMode
        ? LocalizationService.Instance["Mode.ExpertWorkspace"]
        : LocalizationService.Instance["Mode.BeginnerWorkspace"];
    public string ZoomText => IsFitToViewer ? LocalizationService.Instance["Viewer.Fit"] : $"{ZoomFactor * 100:0}%";
    public string ZoomStatusText => $"{LocalizationService.Instance["Viewer.ZoomLabel"]} {ZoomText}";
    public string SourceSummary => document is null
        ? LocalizationService.Instance["Summary.SourceNone"]
        : LocalizationService.Instance.GetString(
            "Summary.Source",
            document.Source.FileName,
            document.Source.Metadata.Width,
            document.Source.Metadata.Height,
            document.Source.Metadata.SourceBitDepth,
            document.Source.FileFormat);
    public string MetadataSummary => document is null
        ? LocalizationService.Instance["Summary.NoImage"]
        : LocalizationService.Instance.GetString(
            "Summary.Image",
            document.Source.Metadata.Width,
            document.Source.Metadata.Height,
            document.Source.Metadata.ChannelCount);
    public string AlphaSummary => document?.Source.HadAlpha == true
        ? LocalizationService.Instance["Summary.AlphaIgnored"]
        : LocalizationService.Instance["Summary.SourceImmutable"];
    public string HistogramStats => Histogram is null
        ? LocalizationService.Instance["Summary.NoHistogram"]
        : LocalizationService.Instance.GetString(
            "Summary.Histogram",
            Histogram.Minimum[0],
            Histogram.Mean[0],
            Histogram.Maximum[0]);
    public string ResolvedCpuThreads =>
        LocalizationService.Instance.GetString(
            "Performance.Resolved",
            LocalizationService.Instance[UseAutomaticCpuThreads ? "Common.Auto" : "Common.Manual"],
            resourceGovernor.GetLimits().WorkerBudget);
    public string ResolvedMemoryLimit =>
        LocalizationService.Instance.GetString(
            "Performance.Resolved",
            LocalizationService.Instance[UseAutomaticMemoryBudget ? "Common.Auto" : "Common.Manual"],
            $"{resourceGovernor.GetLimits().MemoryBudgetBytes / (1024D * 1024 * 1024):0.0} GB");
    public string ResourceDiagnostics
    {
        get
        {
            var snapshot = resourceGovernor.GetSnapshot();
            return LocalizationService.Instance.GetString(
                "Performance.Diagnostics",
                snapshot.Limits.LogicalCpuCount,
                snapshot.Limits.WorkerBudget,
                snapshot.Limits.AvailableMemoryBytes / (1024D * 1024 * 1024),
                snapshot.Limits.MemoryBudgetBytes / (1024D * 1024 * 1024),
                snapshot.TrackedAllocationBytes / (1024D * 1024),
                snapshot.BufferPoolRetainedBytes / (1024D * 1024),
                snapshot.BufferPoolRetainedCount,
                snapshot.BufferPoolReuseCount,
                snapshot.BufferPoolAllocationCount,
                snapshot.ActiveJobs);
        }
    }

    [ObservableProperty] private bool isExpertMode;
    [ObservableProperty] private string selectedTarget;
    [ObservableProperty] private string selectedPreset;
    [ObservableProperty] private string selectedColorPreset;
    [ObservableProperty] private double presetStrength;
    [ObservableProperty] private double detail;
    [ObservableProperty] private double noiseReduction;
    [ObservableProperty] private double color;
    [ObservableProperty] private double brightness;
    [ObservableProperty] private double contrast;
    [ObservableProperty] private double gamma;
    [ObservableProperty] private double redBalance;
    [ObservableProperty] private double greenBalance;
    [ObservableProperty] private double blueBalance;
    [ObservableProperty] private double expertBrightness;
    [ObservableProperty] private double expertContrast;
    [ObservableProperty] private double expertSaturation;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isProcessing;
    [ObservableProperty] private bool isProcessingFeedbackVisible;
    [ObservableProperty] private bool isProcessingIndeterminate = true;
    [ObservableProperty] private bool canCancelProcessing;
    [ObservableProperty] private double processingPercent;
    [ObservableProperty] private int processingPendingCount;
    [ObservableProperty] private string processingStatusText = LocalizationService.Instance["Status.Ready"];
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private bool isProcessingErrorVisible;
    [ObservableProperty] private string processingErrorMessage = string.Empty;
    [ObservableProperty] private string processingErrorCode = string.Empty;
    [ObservableProperty] private string processingErrorTechnicalDetails = string.Empty;
    [ObservableProperty] private Bitmap? originalBitmap;
    [ObservableProperty] private Bitmap? processedBitmap;
    [ObservableProperty] private byte[]? originalPixels;
    [ObservableProperty] private byte[]? processedPixels;
    [ObservableProperty] private uint imageWidth;
    [ObservableProperty] private uint imageHeight;
    [ObservableProperty] private uint imageChannelCount;
    [ObservableProperty] private bool isFitToViewer = true;
    [ObservableProperty] private double zoomFactor = 1;
    [ObservableProperty] private bool isComparisonEnabled;
    [ObservableProperty] private bool isPreviewEnabled = true;
    [ObservableProperty] private double comparisonSplit = 0.5;
    [ObservableProperty] private string pixelReadout = LocalizationService.Instance["Viewer.PixelHint"];
    [ObservableProperty] private bool isClippingWarningEnabled;
    [ObservableProperty] private bool isRoiSelectionEnabled;
    [ObservableProperty] private Rect roiImageRect;
    [ObservableProperty] private bool isHistoryPanelVisible;
    [ObservableProperty] private HistogramData? histogram;
    [ObservableProperty] private bool isPerformanceSettingsVisible;
    [ObservableProperty] private PerformanceProfile selectedPerformanceProfile;
    [ObservableProperty] private InteractivePreviewQuality selectedInteractivePreviewQuality;
    [ObservableProperty] private bool prioritizeUiResponsiveness;
    [ObservableProperty] private bool reduceCpuOnBattery;
    [ObservableProperty] private bool useAutomaticCpuThreads = true;
    [ObservableProperty] private bool useAutomaticMemoryBudget = true;
    [ObservableProperty] private int manualMaxThreads;
    [ObservableProperty] private double manualMemoryBudgetGb;
    [ObservableProperty] private string moduleSearchText = string.Empty;
    public ObservableCollection<HistoryStateViewModel> HistoryStates { get; } = [];
    public bool IsRoiActive => roiDocument is not null;
    public string RoiSummary => IsRoiActive
        ? LocalizationService.Instance.GetString(
            "Roi.ActiveSummary",
            (int)activeRoiRect.X,
            (int)activeRoiRect.Y,
            (int)activeRoiRect.Width,
            (int)activeRoiRect.Height)
        : LocalizationService.Instance["Roi.Inactive"];

    partial void OnModuleSearchTextChanged(string value) => ApplyModuleFilter();

    partial void OnIsExpertModeChanging(bool value)
    {
        modeSwitchFrom = IsExpertMode ? "Expert" : "Beginner";
        modeSwitchBeforeHash = CanonicalPipelineHash;
        modeSwitchWaveletBeforeHash = PipelineStateHasher.ComputeModuleSha256(
            history.Current,
            BuiltInProcessors.WaveletId);
        modeSwitchWritesBefore = canonicalParameterWriteCount;
    }

    partial void OnIsExpertModeChanged(bool value)
    {
        state.IsExpertMode = value;
        OnPropertyChanged(nameof(IsBeginnerMode));
        OnPropertyChanged(nameof(ModeDescription));
        RefreshBeginnerPresetMatch();

        var afterHash = CanonicalPipelineHash;
        var beforeHash = modeSwitchBeforeHash ?? afterHash;
        var waveletAfterHash = PipelineStateHasher.ComputeModuleSha256(
            history.Current,
            BuiltInProcessors.WaveletId);
        var waveletBeforeHash = modeSwitchWaveletBeforeHash ?? waveletAfterHash;
        DiagnosticService.Current.RecordModeSwitchAudit(
            modeSwitchFrom,
            value ? "Expert" : "Beginner",
            beforeHash,
            afterHash,
            waveletBeforeHash,
            waveletAfterHash,
            modeSwitchWritesBefore,
            canonicalParameterWriteCount);
        if (!string.Equals(beforeHash, afterHash, StringComparison.Ordinal) ||
            !string.Equals(waveletBeforeHash, waveletAfterHash, StringComparison.Ordinal) ||
            modeSwitchWritesBefore != canonicalParameterWriteCount)
        {
            throw new InvalidOperationException(
                "Beginner/Expert switching changed canonical processing state. " +
                $"before={beforeHash}; after={afterHash}; writes={canonicalParameterWriteCount - modeSwitchWritesBefore}.");
        }
    }

    partial void OnSelectedTargetChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedTargetOption));
        OnPropertyChanged(nameof(ActivePresetDescription));
        state.SelectedTarget = value;
        if (!suppressProcessing &&
            !string.Equals(value, "Default", StringComparison.Ordinal) &&
            string.Equals(SelectedPreset, "Default", StringComparison.Ordinal))
        {
            suppressProcessing = true;
            SelectedPreset = "Balanced";
            state.SelectedPreset = SelectedPreset;
            suppressProcessing = false;
        }
        SelectPreset(useRecipeDefaults: true);
    }

    partial void OnSelectedPresetChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedPresetOption));
        OnPropertyChanged(nameof(BeginnerPresetDisplayName));
        OnPropertyChanged(nameof(ActivePresetDescription));
        state.SelectedPreset = value;
        if (!suppressProcessing &&
            !string.Equals(value, "Default", StringComparison.Ordinal) &&
            string.Equals(SelectedTarget, "Default", StringComparison.Ordinal))
        {
            suppressProcessing = true;
            SelectedTarget = "Generic";
            state.SelectedTarget = SelectedTarget;
            suppressProcessing = false;
        }
        SelectPreset(useRecipeDefaults: true);
    }

    partial void OnSelectedColorPresetChanged(string value)
    {
        OnPropertyChanged(nameof(SelectedColorPresetOption));
        state.SelectedColorPreset = value;
        ApplyBeginnerControl(BeginnerMacroControl.ColorStyle);
    }

    partial void OnPresetStrengthChanged(double value)
    {
        state.PresetStrength = value;
        ApplyBeginnerControl(BeginnerMacroControl.PresetStrength);
    }

    partial void OnDetailChanged(double value)
    {
        state.Detail = value;
        ApplyBeginnerControl(BeginnerMacroControl.Detail);
    }

    partial void OnNoiseReductionChanged(double value)
    {
        state.NoiseReduction = value;
        ApplyBeginnerControl(BeginnerMacroControl.NoiseReduction);
    }
    partial void OnColorChanged(double value)
    {
        state.Color = value;
        ApplyBeginnerControl(BeginnerMacroControl.Color);
    }

    partial void OnBrightnessChanged(double value)
    {
        state.Brightness = value;
        ApplyBeginnerControl(BeginnerMacroControl.Brightness);
    }

    partial void OnContrastChanged(double value)
    {
        state.Contrast = value;
        ApplyBeginnerControl(BeginnerMacroControl.Contrast);
    }

    partial void OnGammaChanged(double value) => ApplyParameter(BuiltInProcessors.GammaId, 0, value);
    partial void OnRedBalanceChanged(double value) => ApplyParameter(BuiltInProcessors.RgbBalanceId, 0, value);
    partial void OnGreenBalanceChanged(double value) => ApplyParameter(BuiltInProcessors.RgbBalanceId, 1, value);
    partial void OnBlueBalanceChanged(double value) => ApplyParameter(BuiltInProcessors.RgbBalanceId, 2, value);
    partial void OnExpertBrightnessChanged(double value) => ApplyParameter(BuiltInProcessors.ExposureId, 0, value / 50.0);
    partial void OnExpertContrastChanged(double value) => ApplyParameter(BuiltInProcessors.ContrastId, 0, 1 + value / 100.0);
    partial void OnExpertSaturationChanged(double value) => ApplyParameter(BuiltInProcessors.SaturationId, 0, value / 50.0);
    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));
    partial void OnZoomFactorChanged(double value)
    {
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ZoomStatusText));
    }

    partial void OnIsFitToViewerChanged(bool value)
    {
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ZoomStatusText));
    }
    partial void OnSelectedPerformanceProfileChanged(PerformanceProfile value)
    {
        OnPropertyChanged(nameof(SelectedPerformanceProfileOption));
        ApplyResourceSettings();
    }
    partial void OnSelectedInteractivePreviewQualityChanged(InteractivePreviewQuality value)
    {
        OnPropertyChanged(nameof(SelectedInteractivePreviewQualityOption));
        ApplyResourceSettings();
    }
    partial void OnPrioritizeUiResponsivenessChanged(bool value) => ApplyResourceSettings();
    partial void OnReduceCpuOnBatteryChanged(bool value) => ApplyResourceSettings();
    partial void OnUseAutomaticCpuThreadsChanged(bool value) => ApplyResourceSettings();
    partial void OnUseAutomaticMemoryBudgetChanged(bool value) => ApplyResourceSettings();
    partial void OnManualMaxThreadsChanged(int value) => ApplyResourceSettings();
    partial void OnManualMemoryBudgetGbChanged(double value) => ApplyResourceSettings();

    partial void OnRoiImageRectChanged(Rect value)
    {
        if (suppressRoiChange || document is null || value.Width < 1 || value.Height < 1) return;
        roiActivationTask = ActivateRoiAsync(value);
    }

    public async Task OpenImageAsync(string path)
    {
        ClearBatchReferenceSession();
        var generation = Interlocked.Increment(ref openGeneration);
        Interlocked.Increment(ref roiGeneration);
        StopPendingParameterInteraction();
        openCancellation?.Cancel();
        openCancellation?.Dispose();
        openCancellation = new CancellationTokenSource();
        var token = openCancellation.Token;
        IsBusy = true;
        ErrorMessage = null;
        IsProcessingErrorVisible = false;
        ImageDocument? pendingDocument = null;
        Bitmap? pendingOriginalBitmap = null;
        Bitmap? pendingProcessedBitmap = null;

        try
        {
            pendingDocument = await imageOpenService.OpenAsync(path, token);
            if (generation != Volatile.Read(ref openGeneration) || token.IsCancellationRequested)
                return;

            await roiActivationTask;
            if (generation != Volatile.Read(ref openGeneration) || token.IsCancellationRequested)
                return;

            var nextOriginalPixels = pendingDocument.RenderOriginalBgra8();
            var nextProcessedPixels = pendingDocument.RenderProcessedBgra8();
            var metadata = pendingDocument.Source.Metadata;
            pendingOriginalBitmap = CreateBitmap(nextOriginalPixels, metadata.Width, metadata.Height);
            pendingProcessedBitmap = CreateBitmap(nextProcessedPixels, metadata.Width, metadata.Height);

            // The worker owns handles from the current document. Do not dispose that
            // document until cancellation has reached the scheduler's idle boundary.
            await processingService.CancelAndWaitForIdleAsync(
                NativeCancellationReason.RequestSuperseded,
                token);
            if (generation != Volatile.Read(ref openGeneration) || token.IsCancellationRequested)
                return;

            ResetRoiStateAfterSchedulerIdle();
            document?.Dispose();
            OriginalBitmap?.Dispose();
            ProcessedBitmap?.Dispose();
            document = pendingDocument;
            pendingDocument = null;
            authoritativeProcessedPixels = null;
            authoritativeSnapshot = null;
            lastScheduledSnapshot = null;
            lastScheduledDocument = null;
            lastScheduledQuality = null;
            latestDisplayedRequestSequence = 0;
            latestProgressRequestId = 0;
            OriginalPixels = nextOriginalPixels;
            ProcessedPixels = nextProcessedPixels;
            OriginalBitmap = pendingOriginalBitmap;
            ProcessedBitmap = pendingProcessedBitmap;
            pendingOriginalBitmap = null;
            pendingProcessedBitmap = null;
            ImageWidth = metadata.Width;
            ImageHeight = metadata.Height;
            ImageChannelCount = metadata.ChannelCount;
            IsFitToViewer = true;
            IsComparisonEnabled = true;
            ComparisonSplit = 0.5;
            PixelReadout = LocalizationService.Instance["Viewer.PixelHint"];
            ResetNewImageToDefault(metadata);
            NotifyDocumentChanged();
            await ProcessCurrentAsync(ProcessingQuality.FullResolution, history.Current);
        }
        catch (NativeOperationCanceledException exception) when (!exception.IsIntentional)
        {
            ShowProcessingError(exception);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowProcessingError(exception);
        }
        finally
        {
            pendingDocument?.Dispose();
            pendingOriginalBitmap?.Dispose();
            pendingProcessedBitmap?.Dispose();
            if (generation == Volatile.Read(ref openGeneration))
            {
                IsBusy = false;
            }
        }
    }

    public async Task<bool> BeginBatchReferenceSessionAsync(string sourceFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceFolder);
        ClearBatchReferenceSession();

        IReadOnlyList<string> sources;
        try
        {
            sources = BatchProcessingService.EnumerateSources(sourceFolder);
        }
        catch (Exception exception)
        {
            ProcessingStatusText = LocalizationService.Instance.GetString(
                "Batch.Status.SourceError",
                exception.Message);
            return false;
        }

        if (sources.Count == 0)
        {
            ProcessingStatusText = LocalizationService.Instance["Batch.Status.NoFiles"];
            return false;
        }

        var firstSource = sources[0];
        await OpenImageAsync(firstSource);
        if (document is null ||
            !string.Equals(
                document.Source.CanonicalPath,
                Path.GetFullPath(firstSource),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        stagedBatchSourceFolder = Path.GetFullPath(sourceFolder);
        stagedBatchSourcePaths = sources.ToArray();
        NotifyBatchReferenceChanged();
        ProcessingStatusText = LocalizationService.Instance.GetString(
            "Batch.Reference.Ready",
            Path.GetFileName(firstSource),
            sources.Count);
        return true;
    }

    public void ClearBatchReferenceSession()
    {
        if (!IsBatchReferenceSessionActive && string.IsNullOrWhiteSpace(stagedBatchSourceFolder)) return;
        stagedBatchSourceFolder = null;
        stagedBatchSourcePaths = Array.Empty<string>();
        NotifyBatchReferenceChanged();
    }

    private void NotifyBatchReferenceChanged()
    {
        OnPropertyChanged(nameof(IsBatchReferenceSessionActive));
        OnPropertyChanged(nameof(StagedBatchSourcePaths));
        OnPropertyChanged(nameof(StagedBatchSourceFolder));
        OnPropertyChanged(nameof(BatchToolbarText));
        OnPropertyChanged(nameof(BatchToolbarTip));
    }

    [RelayCommand] private void ShowBeginner() => IsExpertMode = false;
    [RelayCommand] private void ShowExpert() => IsExpertMode = true;
    [RelayCommand] private void TogglePerformanceSettings() => IsPerformanceSettingsVisible = !IsPerformanceSettingsVisible;
    [RelayCommand] private void ClosePerformanceSettings() => IsPerformanceSettingsVisible = false;
    [RelayCommand(CanExecute = nameof(IsImageLoaded))]
    private void ToggleHistoryPanel()
    {
        IsHistoryPanelVisible = !IsHistoryPanelVisible;
        if (IsHistoryPanelVisible) RefreshHistoryStates();
    }
    [RelayCommand] private void CloseHistoryPanel() => IsHistoryPanelVisible = false;
    [RelayCommand]
    private void CancelTransientUi()
    {
        if (IsRoiSelectionEnabled)
        {
            IsRoiSelectionEnabled = false;
            return;
        }
        if (IsHistoryPanelVisible)
        {
            IsHistoryPanelVisible = false;
            return;
        }
        if (IsPerformanceSettingsVisible)
            IsPerformanceSettingsVisible = false;
    }
    [RelayCommand(CanExecute = nameof(IsImageLoaded))]
    private void ToggleClippingWarning() => IsClippingWarningEnabled = !IsClippingWarningEnabled;
    [RelayCommand(CanExecute = nameof(CanSelectRoi))]
    private void ToggleRoiSelection() => IsRoiSelectionEnabled = !IsRoiSelectionEnabled;
    [RelayCommand] private void ClearModuleSearch() => ModuleSearchText = string.Empty;
    [RelayCommand]
    private void CancelProcessing()
    {
        exportCancellation?.Cancel();
        processingService.CancelActive(NativeCancellationReason.UserRequested);
    }
    [RelayCommand(CanExecute = nameof(IsImageLoaded))] private void ToggleComparison() => IsComparisonEnabled = !IsComparisonEnabled;

    private bool CanSelectRoi() => IsImageLoaded && !IsRoiActive;

    [RelayCommand(CanExecute = nameof(IsImageLoaded))]
    private void Fit()
    {
        IsFitToViewer = true;
        OnPropertyChanged(nameof(ZoomText));
        OnPropertyChanged(nameof(ZoomStatusText));
    }

    [RelayCommand(CanExecute = nameof(IsImageLoaded))] private void ZoomIn() => SetAdjacentZoom(1);
    [RelayCommand(CanExecute = nameof(IsImageLoaded))] private void ZoomOut() => SetAdjacentZoom(-1);

    [RelayCommand]
    private void ApplyPreset() => ApplyBeginnerMapping();

    [RelayCommand]
    private void ApplyNamedPreset(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !Presets.Contains(name, StringComparer.Ordinal) ||
            string.Equals(name, "Default", StringComparison.Ordinal))
            return;

        suppressProcessing = true;
        try
        {
            if (string.Equals(SelectedTarget, "Default", StringComparison.Ordinal))
                SelectedTarget = "Generic";
            SelectedPreset = name;
            state.SelectedTarget = SelectedTarget;
            state.SelectedPreset = SelectedPreset;
        }
        finally
        {
            suppressProcessing = false;
        }

        SelectPreset(useRecipeDefaults: true);
        ApplyBeginnerMapping();
    }

    [RelayCommand(CanExecute = nameof(IsColorImage))]
    private void AutoRgbBalance()
    {
        if (document is null) return;
        try
        {
            var result = AutoRgbBalanceService.Analyze(document);
            var snapshot = history.Current
                .WithParameter(BuiltInProcessors.RgbBalanceId, 0, result.Red)
                .WithParameter(BuiltInProcessors.RgbBalanceId, 1, result.Green)
                .WithParameter(BuiltInProcessors.RgbBalanceId, 2, result.Blue);
            history.Apply(snapshot);
            ApplySnapshotToControls(snapshot);
            NotifyHistoryChanged();
            QueueProcessing();
        }
        catch (Exception exception)
        {
            ShowProcessingError(exception);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (history.Undo())
        {
            TryRestoreBeginnerState(history.Current);
            ApplySnapshotToControls(history.Current);
            NotifyHistoryChanged();
            QueueProcessing();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (history.Redo())
        {
            TryRestoreBeginnerState(history.Current);
            ApplySnapshotToControls(history.Current);
            NotifyHistoryChanged();
            QueueProcessing();
        }
    }

    private void SelectHistoryState(int index)
    {
        if (!history.JumpToTimelineIndex(index)) return;
        TryRestoreBeginnerState(history.Current);
        ApplySnapshotToControls(history.Current);
        NotifyHistoryChanged();
        QueueProcessing();
    }

    [RelayCommand(CanExecute = nameof(IsRoiActive))]
    private async Task ApplyRoiToFullImage()
    {
        var generation = Interlocked.Increment(ref roiGeneration);
        StopPendingParameterInteraction();
        await processingService.CancelAndWaitForIdleAsync(NativeCancellationReason.RequestSuperseded);
        if (generation != Volatile.Read(ref roiGeneration)) return;

        var sourceDocument = document;
        if (sourceDocument is null) return;
        var restoredOriginal = fullOriginalPixelsBeforeRoi ?? sourceDocument.RenderOriginalBgra8();
        var restoredProcessed = fullProcessedPixelsBeforeRoi ?? restoredOriginal.ToArray();
        var metadata = sourceDocument.Source.Metadata;
        var restoredOriginalBitmap = CreateBitmap(restoredOriginal, metadata.Width, metadata.Height);
        var restoredProcessedBitmap = CreateBitmap(restoredProcessed, metadata.Width, metadata.Height);

        roiDocument?.Dispose();
        roiDocument = null;
        fullOriginalPixelsBeforeRoi = null;
        fullProcessedPixelsBeforeRoi = null;
        activeRoiRect = default;
        suppressRoiChange = true;
        try { RoiImageRect = default; }
        finally { suppressRoiChange = false; }
        IsRoiSelectionEnabled = false;
        OriginalBitmap?.Dispose();
        ProcessedBitmap?.Dispose();
        OriginalPixels = restoredOriginal;
        ProcessedPixels = restoredProcessed;
        OriginalBitmap = restoredOriginalBitmap;
        ProcessedBitmap = restoredProcessedBitmap;
        authoritativeProcessedPixels = null;
        authoritativeSnapshot = null;
        IsFitToViewer = true;
        lastScheduledDocument = null;
        lastScheduledSnapshot = null;
        lastScheduledQuality = null;
        OnPropertyChanged(nameof(IsRoiActive));
        OnPropertyChanged(nameof(RoiSummary));
        ApplyRoiToFullImageCommand.NotifyCanExecuteChanged();
        ToggleRoiSelectionCommand.NotifyCanExecuteChanged();
        QueueProcessing(ProcessingQuality.DefinitivePreview);
    }

    [RelayCommand]
    private void ResetAll()
    {
        ResetBeginnerControlsToNeutral();
        history.ResetAll();
        RememberBeginnerState(history.Current);
        ApplySnapshotToControls(history.Current);
        NotifyHistoryChanged();
        QueueProcessing();
        UpdateMultiSharpeningWarning();
    }

    [RelayCommand]
    private void ResetModule(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        history.ResetModule(id);
        ApplySnapshotToControls(history.Current);
        NotifyHistoryChanged();
        QueueProcessing();
        UpdateMultiSharpeningWarning();
    }

    [RelayCommand]
    private void ResetCategory(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return;
        history.ResetCategory(category);
        ApplySnapshotToControls(history.Current);
        NotifyHistoryChanged();
        QueueProcessing();
        UpdateMultiSharpeningWarning();
    }

    [RelayCommand]
    private void DismissProcessingError() => IsProcessingErrorVisible = false;

    [RelayCommand]
    private async Task CopyErrorDetails()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop ||
            desktop.MainWindow?.Clipboard is null)
            return;
        await desktop.MainWindow.Clipboard.SetTextAsync(ProcessingErrorTechnicalDetails);
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        Directory.CreateDirectory(DiagnosticService.Current.LogDirectory);
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(DiagnosticService.Current.LogDirectory);
        Process.Start(start);
    }

    [RelayCommand(CanExecute = nameof(IsImageLoaded))]
    public async Task ExportImageAsync(string? destinationPath = null)
    {
        if (document is null || IsExporting) return;
        var exportPath = destinationPath ?? ExportService.GetDefaultExportPath(document.Source.CanonicalPath, SelectedExportFormat);
        var cancellation = new CancellationTokenSource();
        exportCancellation = cancellation;

        try
        {
            IsExporting = true;
            ExportPercent = 0;
            ExportStatusText = LocalizationService.Instance["Status.ExportStarting"];
            ProcessingPercent = 0;
            ProcessingStatusText = ExportStatusText;
            IsProcessing = true;
            IsProcessingIndeterminate = false;
            CanCancelProcessing = true;
            IsProcessingFeedbackVisible = true;
            var progress = new Progress<ExportProgress>(p =>
            {
                var localizedStage = LocalizeExportStage(p);
                ExportStatusText = localizedStage;
                ExportPercent = (p.Fraction ?? 0) * 100;
                ProcessingStatusText = localizedStage;
                ProcessingPercent = ExportPercent;
                IsProcessing = true;
                IsProcessingIndeterminate = p.Fraction is null;
                CanCancelProcessing = true;
                IsProcessingFeedbackVisible = true;
            });

            await exportService.ExportAsync(
                document,
                history.Current,
                new ExportOptions(exportPath, SelectedExportFormat),
                progress,
                cancellation.Token);

            ExportStatusText = LocalizationService.Instance.GetString(
                "Status.ExportComplete",
                Path.GetFileName(exportPath));
            ProcessingStatusText = ExportStatusText;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ExportStatusText = LocalizationService.Instance["Status.ExportCancelled"];
            ProcessingStatusText = ExportStatusText;
        }
        catch (Exception ex)
        {
            ShowProcessingError(ex);
            ExportStatusText = LocalizationService.Instance.GetString("Status.ExportFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(exportCancellation, cancellation))
                exportCancellation = null;
            cancellation.Dispose();
            IsExporting = false;
            if (processingService.SchedulerMetrics.CurrentDepth == 0)
            {
                IsProcessing = false;
                CanCancelProcessing = false;
                IsProcessingFeedbackVisible = false;
            }
        }
    }

    public async Task ExportWaveletDiagnosticsAsync(
        string destinationDirectory,
        WaveletDebugDump.TiffEncoding encoding)
    {
        if (!IsExpertMode)
            throw new InvalidOperationException(LocalizationService.Instance["Error.WaveletExpertOnly"]);
        if (document is null)
            throw new InvalidOperationException(LocalizationService.Instance["Error.OpenImageForWavelet"]);

        processingService.CancelActive(NativeCancellationReason.RequestSuperseded);
        var waveletState = history.Current.GetModule(BuiltInProcessors.WaveletId);
        var diagnosticSnapshot = BuiltInProcessors.CreateDefaultSnapshot();
        foreach (var module in diagnosticSnapshot.Modules)
            diagnosticSnapshot = diagnosticSnapshot.WithEnabled(module.Id, module.Id == BuiltInProcessors.WaveletId);
        var waveletIndex = -1;
        for (var index = 0; index < diagnosticSnapshot.Modules.Length; index++)
        {
            if (diagnosticSnapshot.Modules[index].Id == BuiltInProcessors.WaveletId)
            {
                waveletIndex = index;
                break;
            }
        }
        if (waveletIndex < 0)
            throw new InvalidOperationException(LocalizationService.Instance["Error.WaveletMissing"]);
        diagnosticSnapshot = diagnosticSnapshot with
        {
            Modules = diagnosticSnapshot.Modules.SetItem(waveletIndex, waveletState with { Enabled = true }),
        };
        var admission = resourceGovernor.Admit(
            document.Source.Metadata,
            diagnosticSnapshot,
            ProcessingQuality.FullResolution);
        using var diagnosticSource = document.CloneImmutableSource();
        ProcessingStatusText = LocalizationService.Instance["Wavelets.Diagnostic.Exporting"];
        await Task.Run(() =>
        {
            using var lease = resourceGovernor.Acquire(admission, CancellationToken.None);
            WaveletDebugDump.Dump(
                diagnosticSource,
                waveletState,
                destinationDirectory,
                encoding);
        });
        ProcessingStatusText = LocalizationService.Instance.GetString(
            "Status.WaveletExported",
            destinationDirectory);
    }

    [RelayCommand(CanExecute = nameof(IsImageLoaded))]
    public void SaveProject(string? projectPath = null)
    {
        if (document is null) return;
        var path = projectPath ?? Path.ChangeExtension(document.Source.CanonicalPath, ProjectFile.DefaultExtension);

        try
        {
            var viewerState = new ProjectViewerState
            {
                IsFitToViewer = IsFitToViewer,
                ZoomFactor = ZoomFactor,
                IsComparisonEnabled = IsComparisonEnabled,
                ComparisonSplit = ComparisonSplit
            };

            var project = ProjectService.CreateProject(
                document,
                history.Current,
                state,
                viewerState,
                activePreset.RecipeVersion);
            ProjectService.SaveProject(project, path);
            CurrentProjectPath = path;
            ProcessingStatusText = LocalizationService.Instance.GetString(
                "Status.ProjectSaved",
                Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ShowProcessingError(ex);
        }
    }

    [RelayCommand]
    public async Task OpenProjectAsync(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ClearBatchReferenceSession();
        try
        {
            IsBusy = true;
            var project = ProjectService.LoadProject(projectPath);
            var verification = projectService.VerifySource(project);

            if (verification.Status == SourceMatchStatus.Missing)
            {
                PendingProject = project;
                CurrentProjectPath = projectPath;
                IsLocateSourceRequired = true;
                LocateSourceMessage = LocalizationService.Instance.GetString(
                    "Project.SourceMissing",
                    project.Source.CanonicalPath);
                return;
            }

            if (verification.Status == SourceMatchStatus.CompatibleMatch)
            {
                PendingProject = project;
                CurrentProjectPath = projectPath;
                IsLocateSourceRequired = true;
                LocateSourceMessage = LocalizationService.Instance.GetString(
                    "Project.SourceMismatch",
                    project.Source.CanonicalPath);
                return;
            }

            if (verification.Status == SourceMatchStatus.Incompatible)
            {
                throw new InvalidOperationException(
                    LocalizationService.Instance.GetString("Project.IncompatibleSource", verification.Message));
            }

            await LoadProjectDocumentAndState(project);
            CurrentProjectPath = projectPath;
            IsLocateSourceRequired = false;
            PendingProject = null;
        }
        catch (Exception ex)
        {
            ShowProcessingError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    public async Task LocateSourceAsync(string relocatedPath)
    {
        if (PendingProject is null) return;
        try
        {
            IsBusy = true;
            var verification = projectService.VerifySource(PendingProject, relocatedPath);
            if (verification.Status == SourceMatchStatus.CompatibleMatch)
            {
                throw new InvalidOperationException(
                    LocalizationService.Instance.GetString(
                        "Project.SourceMismatchLocated",
                        verification.Message));
            }
            if (verification.Status == SourceMatchStatus.Incompatible)
            {
                throw new InvalidOperationException(verification.Message);
            }
            if (verification.Status == SourceMatchStatus.Missing)
            {
                throw new FileNotFoundException(verification.Message, relocatedPath);
            }

            await LoadProjectDocumentAndState(PendingProject, relocatedPath);
            IsLocateSourceRequired = false;
            PendingProject = null;
        }
        catch (Exception ex)
        {
            ShowProcessingError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadProjectDocumentAndState(ProjectFile project, string? overridePath = null)
    {
        Interlocked.Increment(ref roiGeneration);
        var snapshot = ProjectService.RestorePipelineSnapshot(project, processorDefinitions);
        var doc = await projectService.OpenProjectDocumentAsync(project, overridePath);

        await roiActivationTask;
        await processingService.CancelAndWaitForIdleAsync(NativeCancellationReason.RequestSuperseded);
        ResetRoiStateAfterSchedulerIdle();

        var nextOriginalPixels = doc.RenderOriginalBgra8();
        var nextProcessedPixels = doc.RenderProcessedBgra8();
        var nextOriginalBitmap = CreateBitmap(nextOriginalPixels, doc.Source.Metadata.Width, doc.Source.Metadata.Height);
        var nextProcessedBitmap = CreateBitmap(nextProcessedPixels, doc.Source.Metadata.Width, doc.Source.Metadata.Height);

        var previousDoc = Interlocked.Exchange(ref document, doc);
        previousDoc?.Dispose();

        OriginalBitmap?.Dispose();
        ProcessedBitmap?.Dispose();
        OriginalPixels = nextOriginalPixels;
        ProcessedPixels = nextProcessedPixels;
        OriginalBitmap = nextOriginalBitmap;
        ProcessedBitmap = nextProcessedBitmap;
        ImageWidth = doc.Source.Metadata.Width;
        ImageHeight = doc.Source.Metadata.Height;
        ImageChannelCount = doc.Source.Metadata.ChannelCount;

        IsFitToViewer = project.Viewer.IsFitToViewer;
        ZoomFactor = project.Viewer.ZoomFactor;
        IsComparisonEnabled = project.Viewer.IsComparisonEnabled;
        ComparisonSplit = project.Viewer.ComparisonSplit;

        suppressProcessing = true;
        try
        {
            IsExpertMode = project.Workflow.IsExpertMode;
            SelectedTarget = project.Workflow.Target;
            SelectedPreset = project.Workflow.PresetId;
            SelectedColorPreset = project.Workflow.ColorPresetId;
            PresetStrength = project.Workflow.PresetStrength;
            Detail = project.Workflow.Detail;
            NoiseReduction = project.Workflow.NoiseReduction;
            Color = project.Workflow.Color;
            Brightness = project.Workflow.Brightness;
            Contrast = project.Workflow.Contrast;
            activePreset = ResolveActivePreset();
        }
        finally
        {
            suppressProcessing = false;
        }

        history.Clear(snapshot, "Open Project");
        beginnerStatesByHash.Clear();
        RememberBeginnerState(snapshot);
        ApplySnapshotToControls(snapshot);
        OnPropertyChanged(nameof(ActivePresetDescription));
        NotifyDocumentChanged();
        NotifyHistoryChanged();

        await ProcessCurrentAsync(ProcessingQuality.FullResolution, snapshot);
    }

    [RelayCommand]
    public void RefreshCustomPresets()
    {
        CustomPresets.Clear();
        foreach (var preset in customPresetStore.LoadAll())
        {
            CustomPresets.Add(preset);
        }
    }

    [RelayCommand]
    public void ShowSaveCustomPreset() => IsSaveCustomPresetDialogVisible = true;

    [RelayCommand]
    public void DismissSaveCustomPreset() => IsSaveCustomPresetDialogVisible = false;

    [RelayCommand]
    public void SaveCustomPreset()
    {
        var presetName = NewCustomPresetName;
        if (string.IsNullOrWhiteSpace(presetName)) return;

        var preset = CustomPresetStore.CreateFromSnapshot(
            presetName,
            NewCustomPresetDescription,
            history.Current,
            SelectedTarget,
            processorDefinitions);

        customPresetStore.Save(preset);
        RefreshCustomPresets();
        NewCustomPresetName = string.Empty;
        NewCustomPresetDescription = string.Empty;
        IsSaveCustomPresetDialogVisible = false;
        ProcessingStatusText = LocalizationService.Instance.GetString(
            "Status.CustomPresetSaved",
            preset.Name);
    }

    public void SaveCustomPreset(string name, string description)
    {
        NewCustomPresetName = name;
        NewCustomPresetDescription = description;
        SaveCustomPreset();
    }

    [RelayCommand]
    public void ApplyCustomPreset(CustomPreset? preset)
    {
        if (preset is null) return;
        var next = CustomPresetStore.ApplyToSnapshot(preset, history.Current, processorDefinitions);
        history.Apply(next, $"Custom Preset: {preset.Name}");
        ApplySnapshotToControls(next);
        NotifyHistoryChanged();
        QueueProcessing();
    }

    [RelayCommand]
    public void DeleteCustomPreset(string? presetId)
    {
        if (string.IsNullOrWhiteSpace(presetId)) return;
        customPresetStore.Delete(presetId);
        RefreshCustomPresets();
    }

    private void SetAdjacentZoom(int direction)
    {
        var current = IsFitToViewer ? 1 : ZoomFactor;
        var index = direction > 0
            ? Array.FindIndex(ZoomStops, value => value > current + 0.001)
            : Array.FindLastIndex(ZoomStops, value => value < current - 0.001);
        if (index < 0)
        {
            index = direction > 0 ? ZoomStops.Length - 1 : 0;
        }
        ZoomFactor = ZoomStops[index];
        IsFitToViewer = false;
    }

    private void NotifyDocumentChanged()
    {
        OnPropertyChanged(nameof(IsImageLoaded));
        OnPropertyChanged(nameof(IsColorImage));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(MetadataSummary));
        OnPropertyChanged(nameof(AlphaSummary));
        ToggleComparisonCommand.NotifyCanExecuteChanged();
        ToggleHistoryPanelCommand.NotifyCanExecuteChanged();
        ToggleClippingWarningCommand.NotifyCanExecuteChanged();
        ToggleRoiSelectionCommand.NotifyCanExecuteChanged();
        ApplyRoiToFullImageCommand.NotifyCanExecuteChanged();
        FitCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();
        ZoomOutCommand.NotifyCanExecuteChanged();
        AutoRgbBalanceCommand.NotifyCanExecuteChanged();
        ExportImageCommand.NotifyCanExecuteChanged();
        SaveProjectCommand.NotifyCanExecuteChanged();
        foreach (var module in PipelineModules)
        {
            var definition = processorDefinitions.First(processor => processor.Id == module.Id);
            var requiredCapability = IsColorImage
                ? ProcessorCapabilities.Rgb
                : ProcessorCapabilities.Grayscale;
            module.SetCompatibility(definition.Capabilities.HasFlag(requiredCapability));
        }
    }

    private PipelineSnapshot CreateBeginnerSnapshot(long revision)
    {
        var metadata = document?.Source.Metadata ?? new ImageMetadata(1, 1, 3, 16, ImageColorModel.Rgb);
        if (IsDefaultSelection)
            return CreateNeutralSnapshot(metadata, revision);
        var builtIn = BeginnerMacroMapper.Map(
            activePreset,
            CurrentBeginnerMacros(),
            ParseColorStyle(),
            metadata,
            revision);
        return AppendPluginDefaults(builtIn, revision);
    }

    private PipelineSnapshot AppendPluginDefaults(PipelineSnapshot builtIn, long revision)
    {
        var existingIds = builtIn.Modules.Select(module => module.Id).ToHashSet(StringComparer.Ordinal);
        var modules = builtIn.Modules.ToBuilder();
        foreach (var definition in processorDefinitions)
        {
            if (existingIds.Contains(definition.Id)) continue;
            modules.Add(new ProcessorState(
                definition.Id,
                false,
                definition.ParameterSchema.Select(parameter => parameter.DefaultValue).ToImmutableArray()));
        }
        return new PipelineSnapshot(modules.ToImmutable(), revision);
    }

    private static PipelineSnapshot PreservePluginStates(
        PipelineSnapshot mapped,
        PipelineSnapshot previous)
    {
        var builtInIds = BuiltInProcessors.All
            .Select(processor => processor.Id)
            .ToHashSet(StringComparer.Ordinal);
        var previousById = previous.Modules.ToDictionary(module => module.Id, StringComparer.Ordinal);
        var modules = mapped.Modules.ToBuilder();
        for (var index = 0; index < modules.Count; index++)
        {
            var module = modules[index];
            if (!builtInIds.Contains(module.Id) && previousById.TryGetValue(module.Id, out var prior))
                modules[index] = prior;
        }
        return new PipelineSnapshot(modules.ToImmutable(), mapped.Revision);
    }

    private PresetRecipe ResolveActivePreset()
    {
        if (IsDefaultSelection)
            return presetCatalog.Get("generic", "Balanced");
        try
        {
            return presetCatalog.Get(SelectedTarget.ToLowerInvariant(), SelectedPreset);
        }
        catch (KeyNotFoundException)
        {
            return presetCatalog.Get("generic", "Balanced");
        }
    }

    private void SelectPreset(bool useRecipeDefaults)
    {
        if (suppressProcessing || presetCatalog is null) return;
        if (IsDefaultSelection)
        {
            ResetBeginnerControlsToNeutral();
            OnPropertyChanged(nameof(ActivePresetDescription));
            RefreshBeginnerPresetMatch();
            return;
        }
        activePreset = presetCatalog.Get(SelectedTarget.ToLowerInvariant(), SelectedPreset);
        OnPropertyChanged(nameof(ActivePresetDescription));
        if (useRecipeDefaults)
        {
            suppressProcessing = true;
            try
            {
                PresetStrength = activePreset.Macros.PresetStrength;
                Detail = activePreset.Macros.Detail;
                NoiseReduction = activePreset.Macros.NoiseReduction;
                Color = activePreset.Macros.Color;
                Brightness = activePreset.Macros.Brightness;
                Contrast = activePreset.Macros.Contrast;
                state.PresetStrength = PresetStrength;
                state.Detail = Detail;
                state.NoiseReduction = NoiseReduction;
                state.Color = Color;
                state.Brightness = Brightness;
                state.Contrast = Contrast;
            }
            finally
            {
                suppressProcessing = false;
            }
        }
        RefreshBeginnerPresetMatch();
    }

    private bool IsDefaultSelection =>
        string.Equals(SelectedTarget, "Default", StringComparison.Ordinal) ||
        string.Equals(SelectedPreset, "Default", StringComparison.Ordinal);

    private PipelineSnapshot CreateNeutralSnapshot(ImageMetadata metadata, long revision)
    {
        var modules = processorDefinitions.Select(definition => new ProcessorState(
            definition.Id,
            false,
            definition.ParameterSchema.Select(parameter => parameter.DefaultValue).ToImmutableArray()));
        return new PipelineSnapshot(modules.ToImmutableArray(), revision);
    }

    private void ResetNewImageToDefault(ImageMetadata metadata)
    {
        suppressProcessing = true;
        try
        {
            SelectedTarget = "Default";
            SelectedPreset = "Default";
            SelectedColorPreset = "Natural";
            PresetStrength = 0;
            Detail = 50;
            NoiseReduction = 0;
            Color = 50;
            Brightness = 0;
            Contrast = 0;
            state.SelectedTarget = SelectedTarget;
            state.SelectedPreset = SelectedPreset;
            state.SelectedColorPreset = SelectedColorPreset;
            state.PresetStrength = PresetStrength;
            state.Detail = Detail;
            state.NoiseReduction = NoiseReduction;
            state.Color = Color;
            state.Brightness = Brightness;
            state.Contrast = Contrast;
            activePreset = presetCatalog.Get("generic", "Balanced");
            canonicalParameterWriteCount = 0;
            var neutral = CreateNeutralSnapshot(metadata, history.Current.Revision + 1);
            history.Clear(neutral, "Open Image: Default neutral state");
            beginnerStatesByHash.Clear();
            RememberBeginnerState(neutral);
            ApplySnapshotToControls(neutral);
        }
        finally
        {
            suppressProcessing = false;
        }
        OnPropertyChanged(nameof(ActivePresetDescription));
        NotifyHistoryChanged();
    }

    private void ApplyBeginnerMapping(bool queueProcessing = true)
    {
        if (suppressProcessing || history is null) return;
        var previous = history.Current;
        var mapped = PreservePluginStates(
            CreateBeginnerSnapshot(history.Current.Revision + 1),
            previous);
        CommitBeginnerSnapshot(mapped, previous, "Apply Beginner preset", "beginner.preset", queueProcessing);
    }

    private void ApplyBeginnerControl(BeginnerMacroControl control, bool queueProcessing = true)
    {
        if (suppressProcessing || history is null) return;
        var previous = history.Current;
        var metadata = document?.Source.Metadata ?? new ImageMetadata(1, 1, 3, 16, ImageColorModel.Rgb);
        var mapped = BeginnerMacroMapper.ApplyControl(
            previous,
            activePreset,
            CurrentBeginnerMacros(),
            ParseColorStyle(),
            metadata,
            control,
            previous.Revision + 1);
        CommitBeginnerSnapshot(
            mapped,
            previous,
            $"Beginner {control}",
            $"beginner.{control}",
            queueProcessing);
    }

    private void CommitBeginnerSnapshot(
        PipelineSnapshot mapped,
        PipelineSnapshot previous,
        string description,
        string coalesceKey,
        bool queueProcessing)
    {
        if (ParameterHistory.ModulesEqual(previous, mapped))
        {
            RememberBeginnerState(previous);
            RefreshBeginnerPresetMatch();
            return;
        }

        canonicalParameterWriteCount += CountCanonicalWrites(previous, mapped);
        history.Apply(
            mapped,
            description,
            coalesce: isParameterInteractionActive,
            coalesceKey: coalesceKey);
        ApplySnapshotToControls(mapped);
        RememberBeginnerState(mapped);
        RefreshBeginnerPresetMatch();
        NotifyHistoryChanged();
        if (!queueProcessing) return;

        TryApplyFastPreview(mapped);
        interactionHasExpensiveChange |= !FastInteractivePreview.HasOnlyCheapChanges(
            previous,
            mapped,
            ImageChannelCount == 0 ? 3U : ImageChannelCount);
        ScheduleInteractiveCommit();
    }

    private BeginnerMacroValues CurrentBeginnerMacros() =>
        new(PresetStrength, Detail, NoiseReduction, Color, Brightness, Contrast);

    private ColorStyle ParseColorStyle() =>
        Enum.TryParse<ColorStyle>(SelectedColorPreset, out var colorStyle)
            ? colorStyle
            : ColorStyle.Natural;

    private BeginnerControlState CaptureBeginnerState() => new(
        SelectedTarget,
        SelectedPreset,
        SelectedColorPreset,
        PresetStrength,
        Detail,
        NoiseReduction,
        Color,
        Brightness,
        Contrast);

    private void RememberBeginnerState(PipelineSnapshot snapshot) =>
        beginnerStatesByHash[PipelineStateHasher.ComputeSha256(snapshot)] = CaptureBeginnerState();

    private bool TryRestoreBeginnerState(PipelineSnapshot snapshot)
    {
        if (!beginnerStatesByHash.TryGetValue(PipelineStateHasher.ComputeSha256(snapshot), out var saved))
            return false;

        var wasSuppressed = suppressProcessing;
        suppressProcessing = true;
        try
        {
            SelectedTarget = saved.Target;
            SelectedPreset = saved.Preset;
            SelectedColorPreset = saved.ColorPreset;
            PresetStrength = saved.PresetStrength;
            Detail = saved.Detail;
            NoiseReduction = saved.NoiseReduction;
            Color = saved.Color;
            Brightness = saved.Brightness;
            Contrast = saved.Contrast;
            state.SelectedTarget = saved.Target;
            state.SelectedPreset = saved.Preset;
            state.SelectedColorPreset = saved.ColorPreset;
            state.PresetStrength = saved.PresetStrength;
            state.Detail = saved.Detail;
            state.NoiseReduction = saved.NoiseReduction;
            state.Color = saved.Color;
            state.Brightness = saved.Brightness;
            state.Contrast = saved.Contrast;
            activePreset = ResolveActivePreset();
        }
        finally
        {
            suppressProcessing = wasSuppressed;
        }

        OnPropertyChanged(nameof(ActivePresetDescription));
        RefreshBeginnerPresetMatch();
        return true;
    }

    private void ResetBeginnerControlsToNeutral()
    {
        var wasSuppressed = suppressProcessing;
        suppressProcessing = true;
        try
        {
            SelectedTarget = "Default";
            SelectedPreset = "Default";
            SelectedColorPreset = "Natural";
            PresetStrength = 0;
            Detail = 50;
            NoiseReduction = 0;
            Color = 50;
            Brightness = 0;
            Contrast = 0;
            state.SelectedTarget = SelectedTarget;
            state.SelectedPreset = SelectedPreset;
            state.SelectedColorPreset = SelectedColorPreset;
            state.PresetStrength = PresetStrength;
            state.Detail = Detail;
            state.NoiseReduction = NoiseReduction;
            state.Color = Color;
            state.Brightness = Brightness;
            state.Contrast = Contrast;
            activePreset = presetCatalog.Get("generic", "Balanced");
        }
        finally
        {
            suppressProcessing = wasSuppressed;
        }
        OnPropertyChanged(nameof(ActivePresetDescription));
    }

    private void ApplyGrayscaleRules()
    {
        var snapshot = history.Current;
        foreach (var definition in processorDefinitions.Where(
                     processor => !processor.Capabilities.HasFlag(ProcessorCapabilities.Grayscale)))
            snapshot = snapshot.WithEnabled(definition.Id, false);
        history.Apply(snapshot, description: "Grayscale rules");
        ApplySnapshotToControls(snapshot);
        NotifyHistoryChanged();
    }

    private void ApplyParameter(string processorId, int parameterIndex, double value)
    {
        if (suppressProcessing) return;
        var currentModule = history.Current.GetModule(processorId);
        var valueChanged = BitConverter.DoubleToInt64Bits(currentModule.Parameters[parameterIndex]) !=
            BitConverter.DoubleToInt64Bits(value);
        var moduleViewModel = PipelineModules.First(module => module.Id == processorId);
        var shouldEnable = !currentModule.Enabled && moduleViewModel.IsCompatible;
        if (!valueChanged && !shouldEnable) return;

        var next = valueChanged
            ? history.Current.WithParameter(processorId, parameterIndex, value)
            : history.Current;
        if (shouldEnable)
            next = next.WithEnabled(processorId, true);
        canonicalParameterWriteCount += CountCanonicalWrites(history.Current, next);
        history.Apply(
            next,
            description: $"{processorId} parameter {parameterIndex}",
            coalesce: isParameterInteractionActive,
            coalesceKey: $"{processorId}.{parameterIndex}");
        if (shouldEnable)
            moduleViewModel.Synchronize(next.GetModule(processorId));
        NotifyHistoryChanged();
        TryApplyFastPreview(next);
        interactionHasExpensiveChange |= !IsCheapInteractiveProcessor(processorId);
        ScheduleInteractiveCommit();
        UpdateMultiSharpeningWarning();
        RefreshBeginnerPresetMatch();
    }

    private void StopPendingParameterInteraction()
    {
        history.EndCoalescing();
        isParameterInteractionActive = false;
        interactionHasExpensiveChange = false;
        interactivePreviewTimer.Stop();
        authoritativeIdleTimer.Stop();
    }

    private void ApplyEnabled(string processorId, bool enabled)
    {
        if (suppressProcessing) return;
        if (history.Current.GetModule(processorId).Enabled == enabled) return;
        history.EndCoalescing();
        canonicalParameterWriteCount++;
        history.Apply(
            history.Current.WithEnabled(processorId, enabled),
            description: $"{(enabled ? "Enable" : "Disable")} {processorId}");
        NotifyHistoryChanged();
        QueueProcessing(ProcessingQuality.DefinitivePreview);
        UpdateMultiSharpeningWarning();
        RefreshBeginnerPresetMatch();
    }

    private void ApplySnapshotToControls(PipelineSnapshot snapshot)
    {
        suppressProcessing = true;
        try
        {
            ExpertBrightness = snapshot.GetModule(BuiltInProcessors.ExposureId).Parameters[0] * 50;
            ExpertContrast = (snapshot.GetModule(BuiltInProcessors.ContrastId).Parameters[0] - 1) * 100;
            Gamma = snapshot.GetModule(BuiltInProcessors.GammaId).Parameters[0];
            var balance = snapshot.GetModule(BuiltInProcessors.RgbBalanceId).Parameters;
            RedBalance = balance[0];
            GreenBalance = balance[1];
            BlueBalance = balance[2];
            ExpertSaturation = snapshot.GetModule(BuiltInProcessors.SaturationId).Parameters[0] * 50;
            foreach (var module in PipelineModules)
                module.Synchronize(snapshot.GetModule(module.Id));
        }
        finally
        {
            suppressProcessing = false;
        }
        UpdateMultiSharpeningWarning();
        RefreshBeginnerPresetMatch();
    }

    private void RefreshBeginnerPresetMatch()
    {
        if (history is null || activePreset is null) return;
        OnPropertyChanged(nameof(IsBeginnerPresetModified));
        OnPropertyChanged(nameof(BeginnerPresetDisplayName));
        OnPropertyChanged(nameof(CanonicalPipelineHash));
        OnPropertyChanged(nameof(CanonicalParameterWriteCount));
    }

    private static int CountCanonicalWrites(PipelineSnapshot before, PipelineSnapshot after)
    {
        var writes = 0;
        for (var moduleIndex = 0; moduleIndex < before.Modules.Length; moduleIndex++)
        {
            var left = before.Modules[moduleIndex];
            var right = after.Modules[moduleIndex];
            if (left.Enabled != right.Enabled) writes++;
            for (var parameterIndex = 0; parameterIndex < left.Parameters.Length; parameterIndex++)
            {
                if (BitConverter.DoubleToInt64Bits(left.Parameters[parameterIndex]) !=
                    BitConverter.DoubleToInt64Bits(right.Parameters[parameterIndex]))
                    writes++;
            }
        }
        return writes;
    }

    private void UpdateMultiSharpeningWarning()
    {
        var wavelet = PipelineModules.FirstOrDefault(m => m.Id == BuiltInProcessors.WaveletId);
        var isWaveletBoosted = wavelet != null && wavelet.IsEnabled &&
            wavelet.Parameters.Any(p => p.Id.EndsWith("LayerEnhancementFactor", StringComparison.Ordinal) && p.Value > 1.05);

        var isOtherSharpeningActive = PipelineModules.Any(m =>
            m.IsEnabled && (m.Id == BuiltInProcessors.RichardsonLucyId ||
                            m.Id == BuiltInProcessors.UnsharpMaskId ||
                            m.Id == BuiltInProcessors.MultiScaleSharpenId));

        IsMultiSharpeningWarningVisible = isWaveletBoosted && isOtherSharpeningActive;
    }

    private void RefreshHistoryStates()
    {
        if (history is null || PipelineModules is null) return;
        var timeline = history.Timeline;
        HistoryStates.Clear();
        for (var index = 0; index < timeline.Count; index++)
        {
            var entry = timeline[index];
            var title = index == 0
                ? LocalizationService.Instance["History.InitialState"]
                : DescribeHistoryChange(timeline[index - 1].Snapshot, entry.Snapshot, entry.Description);
            var details = LocalizationService.Instance.GetString(
                "History.EntryDetails",
                entry.Snapshot.Revision,
                entry.Timestamp.ToLocalTime().ToString("HH:mm:ss"));
            HistoryStates.Add(new HistoryStateViewModel(
                index,
                title,
                details,
                index == history.CurrentTimelineIndex,
                SelectHistoryState));
        }
    }

    private string DescribeHistoryChange(PipelineSnapshot before, PipelineSnapshot after, string fallback)
    {
        var changes = new List<(ProcessorState Before, ProcessorState After)>();
        for (var index = 0; index < Math.Min(before.Modules.Length, after.Modules.Length); index++)
        {
            if (!ProcessorStatesEqual(before.Modules[index], after.Modules[index]))
                changes.Add((before.Modules[index], after.Modules[index]));
        }

        if (changes.Count == 1)
        {
            var change = changes[0];
            var module = PipelineModules.FirstOrDefault(candidate => candidate.Id == change.After.Id);
            var moduleName = module?.DisplayName ?? change.After.Id;
            if (change.Before.Enabled != change.After.Enabled &&
                ParametersEqual(change.Before.Parameters, change.After.Parameters))
            {
                return LocalizationService.Instance.GetString(
                    change.After.Enabled ? "History.EnabledModule" : "History.DisabledModule",
                    moduleName);
            }

            var changedParameters = Enumerable.Range(0, Math.Min(change.Before.Parameters.Length, change.After.Parameters.Length))
                .Where(parameter => BitConverter.DoubleToInt64Bits(change.Before.Parameters[parameter]) !=
                    BitConverter.DoubleToInt64Bits(change.After.Parameters[parameter]))
                .ToArray();
            if (changedParameters.Length == 1 && module is not null)
            {
                var parameter = module.Parameters[changedParameters[0]];
                return LocalizationService.Instance.GetString(
                    "History.AdjustedParameter",
                    moduleName,
                    parameter.CompactName);
            }

            return LocalizationService.Instance.GetString("History.ModifiedModule", moduleName);
        }

        if (fallback.StartsWith("Reset All", StringComparison.OrdinalIgnoreCase))
            return LocalizationService.Instance["History.ResetAll"];
        if (fallback.StartsWith("Custom Preset", StringComparison.OrdinalIgnoreCase))
            return LocalizationService.Instance["History.AppliedPreset"];
        if (changes.Count > 1)
            return LocalizationService.Instance.GetString("History.ModifiedModules", changes.Count);
        return LocalizationService.Instance.GetStringOrDefault("History.ParameterChange", fallback);
    }

    private static bool ProcessorStatesEqual(ProcessorState left, ProcessorState right) =>
        left.Id == right.Id && left.Enabled == right.Enabled && ParametersEqual(left.Parameters, right.Parameters);

    private static bool ParametersEqual(ImmutableArray<double> left, ImmutableArray<double> right)
    {
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
        {
            if (BitConverter.DoubleToInt64Bits(left[index]) != BitConverter.DoubleToInt64Bits(right[index]))
                return false;
        }
        return true;
    }

    private void NotifyHistoryChanged()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        if (IsHistoryPanelVisible) RefreshHistoryStates();
    }

    public void BeginParameterInteraction()
    {
        isParameterInteractionActive = true;
        interactionHasExpensiveChange = false;
        interactivePreviewTimer.Stop();
        authoritativeIdleTimer.Stop();
    }

    public void EndParameterInteraction()
    {
        history.EndCoalescing();
        isParameterInteractionActive = false;
        interactivePreviewTimer.Stop();
        authoritativeIdleTimer.Stop();
        QueueProcessing(ProcessingQuality.DefinitivePreview);
        interactionHasExpensiveChange = false;
    }

    private void ScheduleInteractiveCommit()
    {
        if (interactionHasExpensiveChange)
        {
            interactivePreviewTimer.Stop();
            interactivePreviewTimer.Start();
        }

        authoritativeIdleTimer.Stop();
        authoritativeIdleTimer.Start();
    }

    private void OnAuthoritativeIdle(object? sender, EventArgs e)
    {
        history.EndCoalescing();
        authoritativeIdleTimer.Stop();
        QueueProcessing(ProcessingQuality.DefinitivePreview);
        if (!isParameterInteractionActive) interactionHasExpensiveChange = false;
    }

    private void OnInteractivePreviewTimer(object? sender, EventArgs e)
    {
        interactivePreviewTimer.Stop();
        QueueProcessing(ProcessingQuality.InteractivePreview);
    }

    private bool TryApplyFastPreview(PipelineSnapshot target)
    {
        if (document is null || roiDocument is not null || authoritativeProcessedPixels is null || authoritativeSnapshot is null)
            return false;
        if (!FastInteractivePreview.TryRender(
                authoritativeProcessedPixels,
                ImageWidth,
                ImageHeight,
                ImageChannelCount,
                authoritativeSnapshot,
                target,
                out var preview,
                out var elapsed))
            return false;

        var bitmap = CreateBitmap(preview, ImageWidth, ImageHeight);
        ProcessedBitmap?.Dispose();
        ProcessedPixels = preview;
        ProcessedBitmap = bitmap;
        ProcessingStatusText = LocalizationService.Instance.GetString(
            "Status.FastPreview",
            elapsed.TotalMilliseconds);
        DiagnosticService.Current.RecordFastPreviewTiming(elapsed, target.Revision, ImageWidth, ImageHeight);
        return true;
    }

    private async void QueueProcessing(ProcessingQuality quality = ProcessingQuality.DefinitivePreview)
    {
        var currentDocument = roiDocument ?? document;
        if (currentDocument is null) return;
        var snapshot = history.Current;
        if (ReferenceEquals(lastScheduledDocument, currentDocument) &&
            Equals(lastScheduledSnapshot, snapshot) &&
            lastScheduledQuality == quality)
            return;
        lastScheduledDocument = currentDocument;
        lastScheduledSnapshot = snapshot;
        lastScheduledQuality = quality;
        await ProcessCurrentAsync(quality, snapshot);
    }

    private async Task ActivateRoiAsync(Rect requestedRect)
    {
        var sourceDocument = document;
        if (sourceDocument is null) return;
        var normalized = NormalizeRoi(requestedRect, sourceDocument.Source.Metadata.Width, sourceDocument.Source.Metadata.Height);
        if (normalized.Width < 2 || normalized.Height < 2) return;

        var generation = Interlocked.Increment(ref roiGeneration);
        StopPendingParameterInteraction();
        ImageDocument? pendingRegion = null;
        Bitmap? pendingOriginalBitmap = null;
        Bitmap? pendingProcessedBitmap = null;
        try
        {
            await processingService.CancelAndWaitForIdleAsync(NativeCancellationReason.RequestSuperseded);
            if (generation != Volatile.Read(ref roiGeneration) || !ReferenceEquals(sourceDocument, document)) return;

            pendingRegion = await Task.Run(() => sourceDocument.CreateRegion(
                normalized.X,
                normalized.Y,
                normalized.Width,
                normalized.Height));
            if (generation != Volatile.Read(ref roiGeneration) || !ReferenceEquals(sourceDocument, document)) return;

            var regionOriginalPixels = pendingRegion.RenderOriginalBgra8();
            var regionProcessedPixels = pendingRegion.RenderProcessedBgra8();
            var regionMetadata = pendingRegion.Source.Metadata;
            pendingOriginalBitmap = CreateBitmap(regionOriginalPixels, regionMetadata.Width, regionMetadata.Height);
            pendingProcessedBitmap = CreateBitmap(regionProcessedPixels, regionMetadata.Width, regionMetadata.Height);

            fullOriginalPixelsBeforeRoi ??= OriginalPixels?.ToArray();
            fullProcessedPixelsBeforeRoi ??= ProcessedPixels?.ToArray();
            roiDocument?.Dispose();
            roiDocument = pendingRegion;
            pendingRegion = null;
            activeRoiRect = new Rect(normalized.X, normalized.Y, normalized.Width, normalized.Height);
            suppressRoiChange = true;
            try { RoiImageRect = default; }
            finally { suppressRoiChange = false; }
            IsRoiSelectionEnabled = false;
            OriginalBitmap?.Dispose();
            ProcessedBitmap?.Dispose();
            OriginalPixels = regionOriginalPixels;
            ProcessedPixels = regionProcessedPixels;
            OriginalBitmap = pendingOriginalBitmap;
            ProcessedBitmap = pendingProcessedBitmap;
            pendingOriginalBitmap = null;
            pendingProcessedBitmap = null;
            authoritativeProcessedPixels = null;
            authoritativeSnapshot = null;
            IsFitToViewer = true;
            lastScheduledDocument = null;
            lastScheduledSnapshot = null;
            lastScheduledQuality = null;
            OnPropertyChanged(nameof(IsRoiActive));
            OnPropertyChanged(nameof(RoiSummary));
            ApplyRoiToFullImageCommand.NotifyCanExecuteChanged();
            ToggleRoiSelectionCommand.NotifyCanExecuteChanged();
            QueueProcessing(ProcessingQuality.DefinitivePreview);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowProcessingError(exception);
        }
        finally
        {
            pendingOriginalBitmap?.Dispose();
            pendingProcessedBitmap?.Dispose();
            pendingRegion?.Dispose();
        }
    }

    private static (uint X, uint Y, uint Width, uint Height) NormalizeRoi(Rect rect, uint imageWidth, uint imageHeight)
    {
        var x = (uint)Math.Clamp(Math.Floor(rect.X), 0D, Math.Max(0D, imageWidth - 1D));
        var y = (uint)Math.Clamp(Math.Floor(rect.Y), 0D, Math.Max(0D, imageHeight - 1D));
        var right = (uint)Math.Clamp(Math.Ceiling(rect.Right), x + 1D, imageWidth);
        var bottom = (uint)Math.Clamp(Math.Ceiling(rect.Bottom), y + 1D, imageHeight);
        return (x, y, right - x, bottom - y);
    }

    private void ResetRoiStateAfterSchedulerIdle()
    {
        Interlocked.Increment(ref roiGeneration);
        roiDocument?.Dispose();
        roiDocument = null;
        fullOriginalPixelsBeforeRoi = null;
        fullProcessedPixelsBeforeRoi = null;
        activeRoiRect = default;
        suppressRoiChange = true;
        try { RoiImageRect = default; }
        finally { suppressRoiChange = false; }
        IsRoiSelectionEnabled = false;
        OnPropertyChanged(nameof(IsRoiActive));
        OnPropertyChanged(nameof(RoiSummary));
        ApplyRoiToFullImageCommand.NotifyCanExecuteChanged();
        ToggleRoiSelectionCommand.NotifyCanExecuteChanged();
    }

    private void OnProcessingProgressChanged(object? sender, ProcessingSchedulerProgress progress)
    {
        void Apply()
        {
            if (progress.RequestId < latestProgressRequestId) return;
            latestProgressRequestId = progress.RequestId;
            ProcessingPercent = (progress.Fraction ?? 0) * 100;
            ProcessingPendingCount = progress.Pending;
            IsProcessing = progress.CurrentDepth > 0;
            IsProcessingIndeterminate = progress.Fraction is null;
            CanCancelProcessing = progress.CanCancel;
            var localizedStage = LocalizeProcessingStage(progress);
            ProcessingStatusText = progress.CurrentDepth > 0
                ? progress.Pending == 1
                    ? LocalizationService.Instance.GetString("Status.LatestPending", localizedStage)
                    : localizedStage
                : LocalizationService.Instance["Status.ProcessingComplete"];
            if (IsProcessing)
            {
                if (!IsProcessingFeedbackVisible && !progressVisibilityTimer.IsEnabled)
                    progressVisibilityTimer.Start();
            }
            else
            {
                progressVisibilityTimer.Stop();
                IsProcessingFeedbackVisible = false;
            }
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private string LocalizeProcessingStage(ProcessingSchedulerProgress progress)
    {
        string LocalizeModule(string name) =>
            PipelineModules.FirstOrDefault(module =>
                string.Equals(module.Name, name, StringComparison.Ordinal))?.DisplayName ?? name;

        if (progress.TotalSteps > 0)
        {
            var separator = progress.Stage.IndexOf(':');
            var moduleName = separator > 0 ? progress.Stage[..separator] : progress.Stage;
            return $"{LocalizeModule(moduleName)}: {progress.CurrentStep} / {progress.TotalSteps}";
        }

        const string processingPrefix = "Processing ";
        if (progress.Stage.StartsWith(processingPrefix, StringComparison.Ordinal))
        {
            var moduleName = progress.Stage[processingPrefix.Length..].TrimEnd('…', '.');
            return LocalizationService.Instance.GetString(
                "Processing.Stage.ProcessingModule",
                LocalizeModule(moduleName));
        }

        var key = progress.Stage switch
        {
            "Scheduled" => "Processing.Stage.Scheduled",
            "Latest pending updated" => "Processing.Stage.LatestPendingUpdated",
            "Processing latest pending" => "Processing.Stage.ProcessingLatestPending",
            "Preparing pipeline" => "Processing.Stage.PreparingPipeline",
            "Computing histogram" => "Processing.Stage.ComputingHistogram",
            "Publishing preview" => "Processing.Stage.PublishingPreview",
            "Processor pipeline" => "Processing.Stage.ProcessorPipeline",
            "Histogram" => "Processing.Stage.Histogram",
            "Publication gate" => "Processing.Stage.PublicationGate",
            "Complete" => "Processing.Stage.Complete",
            "Wavelet layer" => "Processing.Stage.WaveletLayer",
            "Richardson-Lucy iteration" => "Processing.Stage.RichardsonLucyIteration",
            _ => string.Empty,
        };
        return key.Length == 0 ? progress.Stage : LocalizationService.Instance[key];
    }

    private string LocalizeExportStage(ExportProgress progress)
    {
        string LocalizeModule(string name) =>
            PipelineModules.FirstOrDefault(module =>
                string.Equals(module.Name, name, StringComparison.Ordinal))?.DisplayName ?? name;

        const string processingPrefix = "Processing ";
        if (progress.Stage.StartsWith(processingPrefix, StringComparison.Ordinal))
        {
            var moduleName = progress.Stage[processingPrefix.Length..].TrimEnd('…', '.');
            return LocalizationService.Instance.GetString(
                "Export.Stage.ProcessingModule",
                LocalizeModule(moduleName));
        }

        const string writingPrefix = "Writing export file";
        if (progress.Stage.StartsWith(writingPrefix, StringComparison.Ordinal))
        {
            return LocalizationService.Instance.GetString(
                "Export.Stage.Writing",
                progress.CurrentStep,
                progress.TotalSteps);
        }

        var key = progress.Stage switch
        {
            "Preparing full-resolution pipeline..." => "Export.Stage.Preparing",
            "Encoding and writing to disk..." => "Export.Stage.Encoding",
            "Export complete." => "Export.Stage.Complete",
            _ => string.Empty,
        };
        return key.Length == 0 ? progress.Stage : LocalizationService.Instance[key];
    }

    private void OnProgressVisibilityTimer(object? sender, EventArgs e)
    {
        progressVisibilityTimer.Stop();
        IsProcessingFeedbackVisible = IsProcessing;
    }

    private async Task ProcessCurrentAsync(ProcessingQuality quality, PipelineSnapshot snapshot)
    {
        var currentDocument = roiDocument ?? document;
        if (currentDocument is null) return;
        var isRoiRequest = ReferenceEquals(currentDocument, roiDocument);
        IsProcessing = true;
        try
        {
            var frame = await processingService.EnqueueAsync(currentDocument, snapshot, quality);
            if (frame is null) return;
            if (isRoiRequest
                    ? !ReferenceEquals(currentDocument, roiDocument)
                    : !ReferenceEquals(currentDocument, document))
                return;
            if (frame.DocumentContext != currentDocument.ContextId || frame.SourceIdentity != currentDocument.SourceIdentity) return;
            if (frame.RequestSequence != latestProgressRequestId || snapshot.Revision != history.Current.Revision) return;
            if (frame.RequestSequence < latestDisplayedRequestSequence) return;
            latestDisplayedRequestSequence = frame.RequestSequence;
            var displayPixels = frame.BgraPixels;
            authoritativeProcessedPixels = displayPixels;
            authoritativeSnapshot = snapshot;
            if (!isRoiRequest && !Equals(snapshot, history.Current) && FastInteractivePreview.TryRender(
                    frame.BgraPixels,
                    ImageWidth,
                    ImageHeight,
                    ImageChannelCount,
                    snapshot,
                    history.Current,
                    out var latestPreview,
                    out var elapsed))
            {
                displayPixels = latestPreview;
                DiagnosticService.Current.RecordFastPreviewTiming(elapsed, history.Current.Revision, ImageWidth, ImageHeight);
            }
            var displayMetadata = currentDocument.Source.Metadata;
            var bitmap = CreateBitmap(displayPixels, displayMetadata.Width, displayMetadata.Height);
            ProcessedBitmap?.Dispose();
            ProcessedPixels = displayPixels;
            ProcessedBitmap = bitmap;
            Histogram = frame.Histogram;
            OnPropertyChanged(nameof(HistogramStats));
        }
        catch (NativeOperationCanceledException exception) when (!exception.IsIntentional)
        {
            ShowProcessingError(exception);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowProcessingError(exception);
        }
        finally
        {
            if (processingService.SchedulerMetrics.CurrentDepth == 0)
            {
                IsProcessing = false;
                CanCancelProcessing = false;
                progressVisibilityTimer.Stop();
                IsProcessingFeedbackVisible = false;
            }
        }
    }

    private static bool IsCheapInteractiveProcessor(string processorId) => processorId is
        BuiltInProcessors.ExposureId or
        BuiltInProcessors.ContrastId or
        BuiltInProcessors.GammaId or
        BuiltInProcessors.RgbBalanceId or
        BuiltInProcessors.SaturationId;

    private void ApplyModuleFilter()
    {
        if (ProcessingGroups is null) return;
        foreach (var group in ProcessingGroups)
            group.ApplyFilter(ModuleSearchText);
    }

    private void ApplyResourceSettings()
    {
        if (resourceGovernor is null) return;
        try
        {
            var logical = Math.Max(1, Environment.ProcessorCount);
            int? threads = UseAutomaticCpuThreads ? null : Math.Clamp(ManualMaxThreads, 1, logical);
            ulong? memoryBytes = UseAutomaticMemoryBudget
                ? null
                : (ulong)Math.Clamp(
                    ManualMemoryBudgetGb * 1024D * 1024 * 1024,
                    64D * 1024 * 1024,
                    (double)ulong.MaxValue);
            resourceGovernor.UpdateSettings(new ResourceGovernorSettings(
                SelectedPerformanceProfile,
                SelectedInteractivePreviewQuality,
                PrioritizeUiResponsiveness,
                ReduceCpuOnBattery,
                threads,
                memoryBytes));
            OnPropertyChanged(nameof(ResolvedCpuThreads));
            OnPropertyChanged(nameof(ResolvedMemoryLimit));
            OnPropertyChanged(nameof(ResourceDiagnostics));
        }
        catch (Exception exception)
        {
            ShowProcessingError(exception);
        }
    }

    private void OnResourceSnapshotChanged(object? sender, ResourceGovernorSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            OnPropertyChanged(nameof(ResolvedCpuThreads));
            OnPropertyChanged(nameof(ResolvedMemoryLimit));
            OnPropertyChanged(nameof(ResourceDiagnostics));
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(ResolvedCpuThreads));
                OnPropertyChanged(nameof(ResolvedMemoryLimit));
                OnPropertyChanged(nameof(ResourceDiagnostics));
            });
        }
    }

    private static WriteableBitmap CreateBitmap(byte[] pixels, uint width, uint height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(checked((int)width), checked((int)height)),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Opaque);
        using var framebuffer = bitmap.Lock();
        for (var y = 0; y < height; y++)
        {
            Marshal.Copy(
                pixels,
                checked((int)(y * width * 4)),
                framebuffer.Address + checked((int)y * framebuffer.RowBytes),
                checked((int)width * 4));
        }
        return bitmap;
    }

    private void ShowProcessingError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        switch (exception)
        {
            case NativeOperationException native:
                ProcessingErrorMessage = native.FriendlyMessage;
                ProcessingErrorCode = native.Diagnostic.ErrorCode;
                ProcessingErrorTechnicalDetails = native.Diagnostic.ToTechnicalDetails() + Environment.NewLine + native;
                break;
            case NativeOperationCanceledException cancelled:
                ProcessingErrorMessage = LocalizationService.Instance["Error.NativeStopped"];
                ProcessingErrorCode = cancelled.Diagnostic.ErrorCode;
                ProcessingErrorTechnicalDetails = cancelled.Diagnostic.ToTechnicalDetails() + Environment.NewLine + cancelled;
                break;
            default:
                ProcessingErrorMessage = exception.Message;
                ProcessingErrorCode = "SSC-MANAGED";
                ProcessingErrorTechnicalDetails = exception.ToString();
                DiagnosticService.Current.RecordManagedException("UI processing boundary", exception);
                break;
        }
        ErrorMessage = $"{ProcessingErrorMessage} [{ProcessingErrorCode}]";
        IsProcessingErrorVisible = true;
    }

    public void Dispose()
    {
        Interlocked.Increment(ref roiGeneration);
        authoritativeIdleTimer.Stop();
        authoritativeIdleTimer.Tick -= OnAuthoritativeIdle;
        interactivePreviewTimer.Stop();
        interactivePreviewTimer.Tick -= OnInteractivePreviewTimer;
        progressVisibilityTimer.Stop();
        progressVisibilityTimer.Tick -= OnProgressVisibilityTimer;
        resourceGovernor.SnapshotChanged -= OnResourceSnapshotChanged;
        openCancellation?.Cancel();
        openCancellation?.Dispose();
        exportCancellation?.Cancel();
        exportCancellation?.Dispose();
        processingService.ProgressChanged -= OnProcessingProgressChanged;
        processingService.Dispose();
        var regionToDispose = roiDocument;
        var documentToDispose = document;
        roiDocument = null;
        document = null;
        if (roiActivationTask.IsCompleted)
        {
            regionToDispose?.Dispose();
            documentToDispose?.Dispose();
        }
        else
        {
            _ = roiActivationTask.ContinueWith(
                _ =>
                {
                    regionToDispose?.Dispose();
                    documentToDispose?.Dispose();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        OriginalBitmap?.Dispose();
        ProcessedBitmap?.Dispose();
    }
}
