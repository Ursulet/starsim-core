using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarSimCore.Application.Localization;
using StarSimCore.Application.Plugins;
using StarSimCore.Application.Processing;

namespace StarSimCore.UI.ViewModels;

public sealed partial class ProcessorParameterViewModel : ObservableObject
{
    private readonly Action<int, double> apply;
    private readonly string processorId;
    private readonly string fallbackName;
    private readonly bool localizeBuiltIn;
    private bool synchronizing;
    private double value;

    public ProcessorParameterViewModel(
        int index,
        string processorId,
        bool localizeBuiltIn,
        ProcessorParameterSchema schema,
        Action<int, double> apply)
    {
        Index = index;
        Id = schema.Id;
        this.processorId = processorId;
        this.localizeBuiltIn = localizeBuiltIn;
        fallbackName = schema.DisplayName;
        Minimum = schema.Minimum;
        Maximum = schema.Maximum;
        DefaultValue = schema.DefaultValue;
        value = schema.DefaultValue;
        this.apply = apply;

        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(CompactName));
        };

        var span = Maximum - Minimum;
        if (schema.RecommendedStep is > 0)
        {
            Step = schema.RecommendedStep.Value;
        }
        else if (Id.Contains("solo", StringComparison.OrdinalIgnoreCase) ||
            Id.Contains("iteration", StringComparison.OrdinalIgnoreCase) ||
            Id.Contains("search", StringComparison.OrdinalIgnoreCase) ||
            (Id.Equals("radius", StringComparison.OrdinalIgnoreCase) && Maximum <= 10))
        {
            Step = 1.0;
        }
        else if (span <= 0.5)
        {
            Step = 0.005;
        }
        else if (span <= 2.5)
        {
            Step = 0.01;
        }
        else if (span <= 12.0)
        {
            Step = 0.1;
        }
        else
        {
            Step = 1.0;
        }
    }

    public int Index { get; }
    public string Id { get; }
    public string Name
    {
        get
        {
            if (!localizeBuiltIn) return fallbackName;
            var generic = LocalizationService.Instance.GetStringOrDefault($"Parameter.{Id}", fallbackName);
            return LocalizationService.Instance.GetStringOrDefault($"Parameter.{processorId}.{Id}", generic);
        }
    }
    public string CompactName => Id switch
    {
        "globalStrength" => LocalizationService.Instance["Wavelets.Compact.Strength"],
        "ultraFineLayerEnhancementFactor" => LocalizationService.Instance["Wavelets.Compact.Layer1"],
        "fineLayerEnhancementFactor" => LocalizationService.Instance["Wavelets.Compact.Layer2"],
        "smallLayerEnhancementFactor" => LocalizationService.Instance["Wavelets.Compact.Layer3"],
        "mediumLayerEnhancementFactor" => LocalizationService.Instance["Wavelets.Compact.Layer4"],
        "largeLayerEnhancementFactor" => LocalizationService.Instance["Wavelets.Compact.Layer5"],
        "structureLayerEnhancementFactor" => LocalizationService.Instance["Wavelets.Compact.Layer6"],
        _ => Name,
    };
    public double Minimum { get; }
    public double Maximum { get; }
    public double DefaultValue { get; }
    public double Step { get; }

    public double Value
    {
        get => value;
        set
        {
            var bounded = Math.Clamp(value, Minimum, Maximum);
            if (!SetProperty(ref this.value, bounded) || synchronizing) return;
            apply(Index, bounded);
        }
    }

    [RelayCommand]
    public void NudgeUp() => Value = Math.Round(Math.Min(Maximum, Value + Step), 4);

    [RelayCommand]
    public void NudgeDown() => Value = Math.Round(Math.Max(Minimum, Value - Step), 4);

    [RelayCommand]
    public void Reset() => Value = DefaultValue;

    internal void Synchronize(double next)
    {
        synchronizing = true;
        try { Value = next; }
        finally { synchronizing = false; }
    }
}

public sealed partial class ProcessorModuleViewModel : ObservableObject
{
    private readonly Action<string, bool> applyEnabled;
    private readonly string? displayNameOverride;
    private readonly string? descriptionOverride;
    private readonly bool usePluginDescriptionFallback;
    private bool synchronizing;
    private bool isEnabled;
    private bool isCompatible = true;

    public event EventHandler? OpenWindowRequested;

    public ProcessorModuleViewModel(
        int order,
        IImageProcessor definition,
        Action<string, int, double> applyParameter,
        Action<string, bool> applyEnabled,
        Action<string> reset)
    {
        Id = definition.Id;
        Name = definition.Name;
        Category = definition.Category;
        OrderLabel = (order + 1).ToString("00");
        isEnabled = definition.IsEnabledByDefault;
        this.applyEnabled = applyEnabled;
        if (definition is PluginProcessorAdapter plugin)
        {
            IsExternalPlugin = true;
            PluginId = plugin.PluginId;
            PluginName = plugin.PluginName;
            PluginVersion = plugin.PluginVersion;
            PluginAuthor = plugin.PluginAuthor;
            displayNameOverride = definition.Name;
            usePluginDescriptionFallback = string.IsNullOrWhiteSpace(plugin.PluginDescription);
            descriptionOverride = usePluginDescriptionFallback ? null : plugin.PluginDescription;
        }
        Icon = ResolveIcon(definition.Id, definition.Category);
        Parameters = definition.ParameterSchema
            .Select((schema, index) => new ProcessorParameterViewModel(
                index,
                Id,
                !IsExternalPlugin,
                schema,
                (parameter, value) => applyParameter(Id, parameter, value)))
            .ToArray();
        InlineParameters = Id == BuiltInProcessors.WaveletId
            ? Parameters.Where(parameter =>
                    parameter.Id == "globalStrength" ||
                    parameter.Id.EndsWith("LayerEnhancementFactor", StringComparison.Ordinal))
                .ToArray()
            : Parameters;
        isExpanded = Id == BuiltInProcessors.WaveletId;
        ResetCommand = new RelayCommand(() => reset(Id));
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(PluginProviderSummary));
            OnPropertyChanged(nameof(UsageHint));
        };
    }

    public string Id { get; }
    public string Name { get; }
    public string DisplayName => displayNameOverride ?? LocalizationService.Instance.GetString(ResolveResourceKey(Id));
    public string Description => descriptionOverride ?? (usePluginDescriptionFallback
        ? LocalizationService.Instance.GetString("ToolWindow.PluginFallbackDescription", PluginName)
        : LocalizationService.Instance.GetString($"{ResolveResourceKey(Id)}.Description"));
    public string Category { get; }
    public string OrderLabel { get; }
    public string Icon { get; }
    public bool IsExternalPlugin { get; }
    public string PluginId { get; } = string.Empty;
    public string PluginName { get; } = string.Empty;
    public string PluginVersion { get; } = string.Empty;
    public string PluginAuthor { get; } = string.Empty;
    public string PluginProviderSummary => IsExternalPlugin
        ? LocalizationService.Instance.GetString("ToolWindow.PluginProvider", PluginName, PluginVersion, PluginAuthor)
        : string.Empty;
    public string UsageHint => IsExternalPlugin
        ? LocalizationService.Instance["ToolWindow.PluginHint"]
        : Id == BuiltInProcessors.NoiseReductionId
            ? LocalizationService.Instance["ToolWindow.NoiseHint"]
            : LocalizationService.Instance["ToolWindow.GenericHint"];
    public IReadOnlyList<ProcessorParameterViewModel> Parameters { get; }
    public IReadOnlyList<ProcessorParameterViewModel> InlineParameters { get; }
    public IRelayCommand ResetCommand { get; }
    public bool IsInitiallyExpanded => Id == BuiltInProcessors.WaveletId;
    public bool IsWavelet => Id == BuiltInProcessors.WaveletId;

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool isPanelVisible = true;

    public bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (!SetProperty(ref isEnabled, value) || synchronizing) return;
            applyEnabled(Id, value);
        }
    }

    public bool IsCompatible
    {
        get => isCompatible;
        private set => SetProperty(ref isCompatible, value);
    }

    [RelayCommand]
    public void OpenWindow() => OpenWindowRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    public void ToggleExpanded() => IsExpanded = !IsExpanded;

    internal void Synchronize(ProcessorState state)
    {
        synchronizing = true;
        try
        {
            IsEnabled = state.Enabled;
            for (var index = 0; index < Parameters.Count; index++)
                Parameters[index].Synchronize(state.Parameters[index]);
        }
        finally { synchronizing = false; }
    }

    internal void SetCompatibility(bool compatible) => IsCompatible = compatible;

    private static string ResolveResourceKey(string id) => id switch
    {
        BuiltInProcessors.WaveletId => "Module.Wavelet",
        BuiltInProcessors.RichardsonLucyId => "Module.RichardsonLucy",
        BuiltInProcessors.NoiseReductionId => "Module.NoiseReduction",
        BuiltInProcessors.DeringingId => "Module.Deringing",
        BuiltInProcessors.RgbAlignId => "Module.RgbAlign",
        BuiltInProcessors.AdvancedColorId => "Module.AdvancedColor",
        BuiltInProcessors.RgbBalanceId => "Module.RgbBalance",
        BuiltInProcessors.SaturationId => "Module.Saturation",
        BuiltInProcessors.AdvancedToneId => "Module.AdvancedTone",
        BuiltInProcessors.ExposureId => "Module.Exposure",
        BuiltInProcessors.ContrastId => "Module.Contrast",
        BuiltInProcessors.GammaId => "Module.Gamma",
        BuiltInProcessors.LocalDetailId => "Module.LocalDetail",
        BuiltInProcessors.UnsharpMaskId => "Module.UnsharpMask",
        BuiltInProcessors.MultiScaleSharpenId => "Module.MultiScaleSharpen",
        _ => id
    };

    private static string ResolveIcon(string id, string category) => id switch
    {
        BuiltInProcessors.WaveletId => "≋",
        BuiltInProcessors.RichardsonLucyId => "◎",
        BuiltInProcessors.NoiseReductionId => "⠿",
        BuiltInProcessors.DeringingId => "◌",
        BuiltInProcessors.RgbAlignId => "⌖",
        BuiltInProcessors.AdvancedColorId => "◉",
        BuiltInProcessors.RgbBalanceId => "◒",
        BuiltInProcessors.SaturationId => "◍",
        BuiltInProcessors.AdvancedToneId => "☼",
        BuiltInProcessors.ExposureId => "◐",
        BuiltInProcessors.ContrastId => "◑",
        BuiltInProcessors.GammaId => "⌁",
        BuiltInProcessors.LocalDetailId => "◔",
        BuiltInProcessors.UnsharpMaskId => "◇",
        BuiltInProcessors.MultiScaleSharpenId => "▥",
        _ => id.StartsWith("core.", StringComparison.Ordinal)
            ? category switch
            {
                "Alignment" or "Geometry" => "🎯",
                "Restoration" or "Noise" => "🛡️",
                "Detail" => "🌌",
                "Advanced Sharpening" or "Sharpen" => "⚡",
                "Color" => "🎨",
                "Tone" => "☀️",
                _ => "⚙️"
            }
            : "♙"
    };
}

public sealed partial class ProcessingPanelGroupViewModel : ObservableObject
{
    public ProcessingPanelGroupViewModel(
        string name,
        string icon,
        IReadOnlyList<ProcessorModuleViewModel> modules,
        bool isExpanded)
    {
        Name = name;
        Icon = icon;
        Modules = modules;
        this.isExpanded = isExpanded;
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(ModuleCountText));
        };
    }

    public string Name { get; }
    public string DisplayName => LocalizationService.Instance.GetStringOrDefault($"Group.{Name}", Name);
    public string ModuleCountText => LocalizationService.Instance.GetString("Panel.ModuleCount", Modules.Count);
    public string Icon { get; }
    public IReadOnlyList<ProcessorModuleViewModel> Modules { get; }

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool isPanelVisible = true;

    public void ApplyFilter(string? query)
    {
        var normalized = query?.Trim() ?? string.Empty;
        foreach (var module in Modules)
        {
            module.IsPanelVisible = normalized.Length == 0 ||
                module.DisplayName.Contains(normalized, StringComparison.CurrentCultureIgnoreCase) ||
                module.Description.Contains(normalized, StringComparison.CurrentCultureIgnoreCase) ||
                module.Parameters.Any(parameter =>
                    parameter.Name.Contains(normalized, StringComparison.CurrentCultureIgnoreCase));
        }

        IsPanelVisible = Modules.Any(module => module.IsPanelVisible);
        if (normalized.Length > 0 && IsPanelVisible)
            IsExpanded = true;
    }
}

public sealed partial class ProcessorCategoryGroupViewModel : ObservableObject
{
    public ProcessorCategoryGroupViewModel(
        string category,
        IReadOnlyList<ProcessorModuleViewModel> modules,
        Action<string> resetCategory)
    {
        Category = category;
        Modules = modules;
        ResetCategoryCommand = new RelayCommand(() => resetCategory(category));

        foreach (var module in modules)
        {
            module.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ProcessorModuleViewModel.IsEnabled))
                {
                    OnPropertyChanged(nameof(EnabledCount));
                    OnPropertyChanged(nameof(CountSummary));
                }
            };
        }

        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DisplayName));
        };
    }

    public string Category { get; }
    public string DisplayName => LocalizationService.Instance.GetStringOrDefault(
        $"Category.{Category.Replace(" ", "")}",
        Category);
    public IReadOnlyList<ProcessorModuleViewModel> Modules { get; }
    public int TotalCount => Modules.Count;
    public int EnabledCount => Modules.Count(m => m.IsEnabled);
    public string CountSummary => $"{EnabledCount}/{TotalCount}";

    [ObservableProperty]
    private bool isExpanded = true;

    public IRelayCommand ResetCategoryCommand { get; }
}
