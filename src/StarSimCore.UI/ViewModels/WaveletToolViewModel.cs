using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarSimCore.Application.Localization;
using StarSimCore.Application.Processing;

namespace StarSimCore.UI.ViewModels;

public sealed partial class WaveletClassicLayerViewModel : ObservableObject
{
    private readonly WaveletToolViewModel parent;
    private readonly ProcessorParameterViewModel enhancementFactorParam;
    private bool updating;

    public WaveletClassicLayerViewModel(
        WaveletToolViewModel parent,
        int layerIndex,
        ProcessorParameterViewModel enhancementFactor)
    {
        this.parent = parent;
        LayerIndex = layerIndex;
        LayerNumber = layerIndex + 1;
        enhancementFactorParam = enhancementFactor;

        enhancementFactorParam.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProcessorParameterViewModel.Value) && !updating)
            {
                OnPropertyChanged(nameof(SliderValue));
            }
        };

        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(LayerName));
            OnPropertyChanged(nameof(LayerHelp));
        };
    }

    public int LayerIndex { get; }
    public int LayerNumber { get; }

    public string LayerName => LocalizationService.Instance[$"Wavelets.Layer{LayerNumber}.Name"];
    public string LayerHelp => LocalizationService.Instance[$"Wavelets.Layer{LayerNumber}.Help"];

    public double SliderValue
    {
        get => Math.Round(WaveletMacroMapper.InvertToClassicSlider(enhancementFactorParam.Value), 1);
        set
        {
            var clamped = Math.Clamp(value, 0.0, 100.0);
            if (Math.Abs(SliderValue - clamped) < 0.05) return;

            var oldVal = SliderValue;
            var delta = clamped - oldVal;

            ApplyClassicValue(clamped);

            if (parent.IsLinked && Math.Abs(delta) > 0.01)
            {
                parent.ApplyLinkedDelta(LayerIndex, delta);
            }
        }
    }

    internal void ApplyClassicValue(double val)
    {
        updating = true;
        try
        {
            var p = WaveletMacroMapper.MapClassicSlider(val);
            enhancementFactorParam.Value = p.EnhancementFactor;
            OnPropertyChanged(nameof(SliderValue));
        }
        finally
        {
            updating = false;
        }
    }

    public bool IsSolo
    {
        get => parent.SoloLayerIndex == LayerIndex;
        set
        {
            if (value)
            {
                parent.SoloLayerIndex = LayerIndex;
            }
            else if (parent.SoloLayerIndex == LayerIndex)
            {
                parent.SoloLayerIndex = -1;
            }
            OnPropertyChanged(nameof(IsSolo));
        }
    }

    internal void NotifySoloChanged() => OnPropertyChanged(nameof(IsSolo));

    [RelayCommand]
    public void NudgeUp() => SliderValue = Math.Min(100.0, SliderValue + 1.0);

    [RelayCommand]
    public void NudgeDown() => SliderValue = Math.Max(0.0, SliderValue - 1.0);

    [RelayCommand]
    public void Reset() => SliderValue = 1.0;
}

public sealed partial class WaveletAdvancedLayerViewModel : ObservableObject
{
    public WaveletAdvancedLayerViewModel(
        WaveletToolViewModel parent,
        int layerIndex,
        ProcessorParameterViewModel enhancementFactor,
        ProcessorParameterViewModel gaussianWidth,
        ProcessorParameterViewModel denoise,
        ProcessorParameterViewModel threshold)
    {
        LayerIndex = layerIndex;
        LayerNumber = layerIndex + 1;
        Parent = parent;
        EnhancementFactor = enhancementFactor;
        GaussianWidth = gaussianWidth;
        Denoise = denoise;
        Threshold = threshold;

        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(LayerName));
        };
    }

    public int LayerIndex { get; }
    public int LayerNumber { get; }
    public WaveletToolViewModel Parent { get; }
    public string LayerName => LocalizationService.Instance[$"Wavelets.Layer{LayerNumber}.Name"];

    public ProcessorParameterViewModel EnhancementFactor { get; }
    public ProcessorParameterViewModel GaussianWidth { get; }
    public ProcessorParameterViewModel Denoise { get; }
    public ProcessorParameterViewModel Threshold { get; }

    public bool IsSolo
    {
        get => Parent.SoloLayerIndex == LayerIndex;
        set
        {
            if (value)
            {
                Parent.SoloLayerIndex = LayerIndex;
            }
            else if (Parent.SoloLayerIndex == LayerIndex)
            {
                Parent.SoloLayerIndex = -1;
            }
            OnPropertyChanged(nameof(IsSolo));
        }
    }

    internal void NotifySoloChanged() => OnPropertyChanged(nameof(IsSolo));

    [RelayCommand]
    public void ResetLayer()
    {
        EnhancementFactor.Reset();
        GaussianWidth.Reset();
        Denoise.Reset();
        Threshold.Reset();
    }
}

public sealed partial class WaveletToolViewModel : ObservableObject
{
    private readonly ProcessorModuleViewModel module;
    private readonly Func<string, WaveletDebugDump.TiffEncoding, Task>? exportDiagnostics;
    private readonly ProcessorParameterViewModel globalStrengthParam;
    private readonly ProcessorParameterViewModel initialLayerWidthParam;
    private readonly ProcessorParameterViewModel linkedParam;
    private readonly ProcessorParameterViewModel soloLayerParam;
    private readonly ProcessorParameterViewModel scaleSchemeParam;
    private readonly ProcessorParameterViewModel stepIncrementParam;
    private readonly ProcessorParameterViewModel backendParam;

    [ObservableProperty]
    private bool isClassicView = true;

    [ObservableProperty]
    private WaveletDebugDump.TiffEncoding diagnosticEncoding = WaveletDebugDump.TiffEncoding.Float32;

    [ObservableProperty]
    private bool isExportingDiagnostics;

    [ObservableProperty]
    private string diagnosticStatus = LocalizationService.Instance["Wavelets.Diagnostic.InitialStatus"];

    public WaveletToolViewModel(
        ProcessorModuleViewModel module,
        Func<string, WaveletDebugDump.TiffEncoding, Task>? exportDiagnostics = null)
    {
        this.module = module;
        this.exportDiagnostics = exportDiagnostics;
        globalStrengthParam = module.Parameters[WaveletMacroMapper.GlobalStrengthIndex];
        initialLayerWidthParam = module.Parameters[WaveletMacroMapper.InitialLayerWidthIndex];
        linkedParam = module.Parameters[WaveletMacroMapper.LinkedIndex];
        soloLayerParam = module.Parameters[WaveletMacroMapper.SoloLayerIndex];
        scaleSchemeParam = module.Parameters[WaveletMacroMapper.ScaleSchemeIndex];
        stepIncrementParam = module.Parameters[WaveletMacroMapper.StepIncrementIndex];
        backendParam = module.Parameters[WaveletMacroMapper.BackendIndex];

        var classicList = new List<WaveletClassicLayerViewModel>(6);
        var advList = new List<WaveletAdvancedLayerViewModel>(6);

        for (var i = 0; i < WaveletMacroMapper.LayerCount; i++)
        {
            var offset = WaveletMacroMapper.GetScaleParamOffset(i);
            var enhancementFactor = module.Parameters[offset];
            var gaussianWidth = module.Parameters[offset + 1];
            var denoise = module.Parameters[offset + 2];
            var threshold = module.Parameters[offset + 3];

            classicList.Add(new WaveletClassicLayerViewModel(this, i, enhancementFactor));
            advList.Add(new WaveletAdvancedLayerViewModel(this, i, enhancementFactor, gaussianWidth, denoise, threshold));
        }

        ClassicLayers = classicList;
        AdvancedLayers = advList;

        soloLayerParam.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProcessorParameterViewModel.Value))
            {
                OnPropertyChanged(nameof(SoloLayerIndex));
                foreach (var layer in ClassicLayers) layer.NotifySoloChanged();
                foreach (var layer in AdvancedLayers) layer.NotifySoloChanged();
            }
        };

        linkedParam.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProcessorParameterViewModel.Value))
            {
                OnPropertyChanged(nameof(IsLinked));
            }
        };

        scaleSchemeParam.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ProcessorParameterViewModel.Value)) return;
            OnPropertyChanged(nameof(ScaleScheme));
            OnPropertyChanged(nameof(SelectedScaleSchemeOption));
        };

        backendParam.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ProcessorParameterViewModel.Value)) return;
            OnPropertyChanged(nameof(DecompositionBackend));
            OnPropertyChanged(nameof(SelectedBackendOption));
        };

        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            if (!IsExportingDiagnostics)
                DiagnosticStatus = LocalizationService.Instance["Wavelets.Diagnostic.InitialStatus"];
        };
    }

    public ProcessorModuleViewModel Module => module;
    public IReadOnlyList<WaveletClassicLayerViewModel> ClassicLayers { get; }
    public IReadOnlyList<WaveletAdvancedLayerViewModel> AdvancedLayers { get; }
    public IReadOnlyList<WaveletScaleScheme> AvailableScaleSchemes { get; } = Enum.GetValues<WaveletScaleScheme>();
    public IReadOnlyList<WaveletDecompositionBackend> AvailableBackends { get; } = Enum.GetValues<WaveletDecompositionBackend>();
    public IReadOnlyList<WaveletDebugDump.TiffEncoding> DiagnosticEncodings { get; } = Enum.GetValues<WaveletDebugDump.TiffEncoding>();
    public IReadOnlyList<LocalizedChoiceViewModel<WaveletScaleScheme>> ScaleSchemeOptions { get; } =
    [
        new(WaveletScaleScheme.Linear, "Wavelets.Scale.Linear"),
        new(WaveletScaleScheme.Dyadic, "Wavelets.Scale.Dyadic"),
    ];
    public IReadOnlyList<LocalizedChoiceViewModel<WaveletDecompositionBackend>> BackendOptions { get; } =
    [
        new(WaveletDecompositionBackend.RecursiveGaussian, "Wavelets.Backend.RecursiveGaussian"),
        new(WaveletDecompositionBackend.AtrousB3Spline, "Wavelets.Backend.AtrousB3Spline"),
    ];
    public IReadOnlyList<LocalizedChoiceViewModel<WaveletDebugDump.TiffEncoding>> DiagnosticEncodingOptions { get; } =
    [
        new(WaveletDebugDump.TiffEncoding.Float32, "Wavelets.Encoding.Float32"),
        new(WaveletDebugDump.TiffEncoding.UInt16, "Wavelets.Encoding.UInt16"),
    ];
    public bool CanExportDiagnostics => exportDiagnostics is not null;

    public LocalizedChoiceViewModel<WaveletScaleScheme>? SelectedScaleSchemeOption
    {
        get => ScaleSchemeOptions.FirstOrDefault(option => option.Value == ScaleScheme);
        set { if (value is not null) ScaleScheme = value.Value; }
    }

    public LocalizedChoiceViewModel<WaveletDecompositionBackend>? SelectedBackendOption
    {
        get => BackendOptions.FirstOrDefault(option => option.Value == DecompositionBackend);
        set { if (value is not null) DecompositionBackend = value.Value; }
    }

    public LocalizedChoiceViewModel<WaveletDebugDump.TiffEncoding>? SelectedDiagnosticEncodingOption
    {
        get => DiagnosticEncodingOptions.FirstOrDefault(option => option.Value == DiagnosticEncoding);
        set { if (value is not null) DiagnosticEncoding = value.Value; }
    }

    public bool IsAdvancedView => !IsClassicView;

    partial void OnDiagnosticEncodingChanged(WaveletDebugDump.TiffEncoding value) =>
        OnPropertyChanged(nameof(SelectedDiagnosticEncodingOption));

    [RelayCommand]
    public void SelectClassicView()
    {
        IsClassicView = true;
        OnPropertyChanged(nameof(IsAdvancedView));
    }

    [RelayCommand]
    public void SelectAdvancedView()
    {
        IsClassicView = false;
        OnPropertyChanged(nameof(IsAdvancedView));
    }

    public double GlobalStrength
    {
        get => globalStrengthParam.Value;
        set => globalStrengthParam.Value = value;
    }

    public double InitialLayerWidth
    {
        get => initialLayerWidthParam.Value;
        set => initialLayerWidthParam.Value = value;
    }

    public double InitialLayerWidthMinimum => initialLayerWidthParam.Minimum;
    public double InitialLayerWidthMaximum => initialLayerWidthParam.Maximum;

    public WaveletScaleScheme ScaleScheme
    {
        get => (WaveletScaleScheme)Math.Round(scaleSchemeParam.Value);
        set => scaleSchemeParam.Value = (double)value;
    }

    public double LinearStepIncrement
    {
        get => stepIncrementParam.Value;
        set => stepIncrementParam.Value = value;
    }

    public WaveletDecompositionBackend DecompositionBackend
    {
        get => (WaveletDecompositionBackend)Math.Round(backendParam.Value);
        set => backendParam.Value = (double)value;
    }

    public bool IsLinked
    {
        get => linkedParam.Value >= 0.5;
        set => linkedParam.Value = value ? 1.0 : 0.0;
    }

    public int SoloLayerIndex
    {
        get => (int)Math.Round(soloLayerParam.Value);
        set
        {
            soloLayerParam.Value = value;
            OnPropertyChanged(nameof(SoloLayerIndex));
            foreach (var layer in ClassicLayers) layer.NotifySoloChanged();
            foreach (var layer in AdvancedLayers) layer.NotifySoloChanged();
        }
    }

    internal void ApplyLinkedDelta(int originIndex, double delta)
    {
        for (var i = 0; i < ClassicLayers.Count; i++)
        {
            if (i == originIndex) continue;
            var current = ClassicLayers[i].SliderValue;
            ClassicLayers[i].ApplyClassicValue(Math.Clamp(current + delta, 0.0, 100.0));
        }
    }

    public async Task ExportDiagnosticsAsync(string destinationDirectory)
    {
        if (exportDiagnostics is null) return;
        IsExportingDiagnostics = true;
        DiagnosticStatus = LocalizationService.Instance["Wavelets.Diagnostic.Exporting"];
        try
        {
            await exportDiagnostics(destinationDirectory, DiagnosticEncoding);
            DiagnosticStatus = LocalizationService.Instance.GetString("Wavelets.Diagnostic.Exported", destinationDirectory);
        }
        catch (Exception exception)
        {
            DiagnosticStatus = LocalizationService.Instance.GetString("Wavelets.Diagnostic.Failed", exception.Message);
        }
        finally
        {
            IsExportingDiagnostics = false;
        }
    }

    [RelayCommand]
    public void ResetWavelets()
    {
        SoloLayerIndex = -1;
        IsLinked = false;
        globalStrengthParam.Reset();
        initialLayerWidthParam.Reset();
        scaleSchemeParam.Reset();
        stepIncrementParam.Reset();
        backendParam.Reset();
        foreach (var layer in AdvancedLayers)
        {
            layer.ResetLayer();
        }
    }
}
