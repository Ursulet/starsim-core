using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using StarSimCore.Application.Localization;
using StarSimCore.Application.Plugins;
using StarSimCore.Application.Processing;
using StarSimCore.UI.ViewModels;

namespace StarSimCore.UI;

public partial class MainWindow : Window
{
    private const string PerformanceWindowKey = "$performance";
    private const string HistogramWindowKey = "$histogram";
    private const string PluginManagerWindowKey = "$plugins";
    private const string HelpWindowKey = "$help";
    private readonly Dictionary<string, Window> openToolWindows = new();
    private readonly PluginService? pluginService;
    private readonly PluginInstallationService pluginInstallationService;

    public MainWindow()
        : this(startInExpertMode: false, pluginService: null, pluginInstallationService: null)
    {
    }

    public MainWindow(bool startInExpertMode)
        : this(startInExpertMode, pluginService: null, pluginInstallationService: null)
    {
    }

    public MainWindow(
        bool startInExpertMode,
        PluginService? pluginService,
        PluginInstallationService? pluginInstallationService)
    {
        InitializeComponent();
        this.pluginService = pluginService;
        this.pluginInstallationService = pluginInstallationService ?? new PluginInstallationService();
        var viewModel = new MainWindowViewModel(
            startInExpertMode,
            pluginService?.PluginProcessors);
        DataContext = viewModel;

        foreach (var module in viewModel.PipelineModules)
        {
            module.OpenWindowRequested += (s, e) => OpenProcessorToolWindow((ProcessorModuleViewModel)s!);
        }

        AddHandler(InputElement.PointerPressedEvent, ParameterSlider_PointerPressed, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerReleasedEvent, ParameterSlider_PointerReleased, RoutingStrategies.Tunnel);
        AddHandler(InputElement.PointerCaptureLostEvent, ParameterSlider_PointerCaptureLost, RoutingStrategies.Tunnel);
        Opened += async (_, _) =>
        {
            await Task.Delay(250);
            BeginnerControlsScroll.Offset = new Vector(0, 0);
            ExpertControlsScroll.Offset = new Vector(0, 0);
        };
        Closed += (_, _) =>
        {
            foreach (var win in openToolWindows.Values.ToArray())
            {
                win.Close();
            }
            openToolWindows.Clear();
            (DataContext as IDisposable)?.Dispose();
        };
    }

    public void OpenProcessorToolWindow(ProcessorModuleViewModel module)
    {
        var windowKey = module.Id;
        if (openToolWindows.TryGetValue(windowKey, out var existing))
        {
            existing.Activate();
            return;
        }

        var mainViewModel = DataContext as MainWindowViewModel;
        Window window;
        if (module.Id == BuiltInProcessors.WaveletId)
        {
            var waveletViewModel = new WaveletToolViewModel(
                module,
                mainViewModel?.IsExpertMode == true
                    ? mainViewModel.ExportWaveletDiagnosticsAsync
                    : null);
            if (mainViewModel?.IsExpertMode == true)
                waveletViewModel.SelectAdvancedView();
            window = new WaveletToolWindow(waveletViewModel);
        }
        else
        {
            window = new ProcessorToolWindow(module);
        }

        openToolWindows[windowKey] = window;
        window.Closed += (_, _) => openToolWindows.Remove(windowKey);
        window.Show(this);
    }

    private void OpenModuleById(string id)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            var module = vm.PipelineModules.FirstOrDefault(m => m.Id == id);
            if (module is not null)
            {
                OpenProcessorToolWindow(module);
            }
        }
    }

    private void OpenPerformanceSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            OpenUtilityWindow(PerformanceWindowKey, () => new PerformanceSettingsWindow(viewModel));
    }

    private void OpenHistogramWindow_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
            OpenUtilityWindow(HistogramWindowKey, () => new HistogramToolWindow(viewModel));
    }

    private PluginManagerWindow OpenPluginManager(bool developerGuide)
    {
        if (openToolWindows.TryGetValue(PluginManagerWindowKey, out var existing) &&
            existing is PluginManagerWindow existingManager)
        {
            if (developerGuide) existingManager.SelectDeveloperGuide();
            existingManager.Activate();
            return existingManager;
        }

        var viewModel = new PluginManagerViewModel(
            pluginInstallationService,
            pluginService,
            developerGuide);
        var window = new PluginManagerWindow(viewModel);
        openToolWindows[PluginManagerWindowKey] = window;
        window.Closed += (_, _) => openToolWindows.Remove(PluginManagerWindowKey);
        window.Show(this);
        return window;
    }

    private void OpenPluginManager_Click(object? sender, RoutedEventArgs e) =>
        OpenPluginManager(developerGuide: false);

    private async void InstallPlugin_Click(object? sender, RoutedEventArgs e) =>
        await OpenPluginManager(developerGuide: false).BeginInstallAsync();

    private void OpenPluginDeveloperGuide_Click(object? sender, RoutedEventArgs e) =>
        OpenPluginManager(developerGuide: true);

    private void OpenUtilityWindow(string windowKey, Func<Window> createWindow)
    {
        if (openToolWindows.TryGetValue(windowKey, out var existing))
        {
            existing.Activate();
            return;
        }

        var window = createWindow();
        openToolWindows[windowKey] = window;
        window.Closed += (_, _) => openToolWindows.Remove(windowKey);
        window.Show(this);
    }

    private void OpenWaveletTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.WaveletId);
    private void OpenRichardsonLucyTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.RichardsonLucyId);
    private void OpenNoiseReductionTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.NoiseReductionId);
    private void OpenDeringingTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.DeringingId);
    private void OpenRgbAlignTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.RgbAlignId);
    private void OpenAdvancedColorTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.AdvancedColorId);
    private void OpenRgbBalanceTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.RgbBalanceId);
    private void OpenSaturationTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.SaturationId);
    private void OpenAdvancedToneTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.AdvancedToneId);
    private void OpenExposureTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.ExposureId);
    private void OpenContrastTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.ContrastId);
    private void OpenGammaTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.GammaId);
    private void OpenLocalDetailTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.LocalDetailId);
    private void OpenUnsharpMaskTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.UnsharpMaskId);
    private void OpenMultiScaleSharpenTool_Click(object? sender, RoutedEventArgs e) => OpenModuleById(BuiltInProcessors.MultiScaleSharpenId);
    private void Exit_Click(object? sender, RoutedEventArgs e) => Close();
    private void FullScreen_Click(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
    private void OpenHelpWindow(int selectedTab)
    {
        if (openToolWindows.TryGetValue(HelpWindowKey, out var existing) &&
            existing is HelpWindow existingHelp)
        {
            existingHelp.SelectTab(selectedTab);
            existingHelp.Activate();
            return;
        }

        var window = new HelpWindow(new HelpWindowViewModel(selectedTab));
        openToolWindows[HelpWindowKey] = window;
        window.Closed += (_, _) => openToolWindows.Remove(HelpWindowKey);
        window.Show(this);
    }

    private void Help_Click(object? sender, RoutedEventArgs e) => OpenHelpWindow(selectedTab: 1);
    private void About_Click(object? sender, RoutedEventArgs e) => OpenHelpWindow(selectedTab: 0);

    private void ParameterSlider_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if ((sender is Slider || e.Source is Slider) && DataContext is MainWindowViewModel viewModel)
            viewModel.BeginParameterInteraction();
    }

    private void ParameterSlider_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if ((sender is Slider || e.Source is Slider) && DataContext is MainWindowViewModel viewModel)
            viewModel.EndParameterInteraction();
    }

    private void ParameterSlider_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if ((sender is Slider || e.Source is Slider) && DataContext is MainWindowViewModel viewModel)
            viewModel.EndParameterInteraction();
    }

    private async void OpenImage_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["FilePicker.OpenImage"],
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(LocalizationService.Instance["FilePicker.Images"])
                    {
                        Patterns = ["*.tif", "*.tiff", "*.png"],
                        MimeTypes = ["image/tiff", "image/png"],
                    },
                ],
            });
        var path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (path is not null && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.OpenImageAsync(path);
        }
    }

    private async void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["FilePicker.OpenProject"],
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(LocalizationService.Instance["FilePicker.Projects"])
                    {
                        Patterns = ["*.starsim"],
                    },
                ],
            });
        var path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (path is not null && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.OpenProjectAsync(path);
        }
    }

    private async void SaveProject_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsImageLoaded) return;
        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance["FilePicker.SaveProject"],
                DefaultExtension = "starsim",
                SuggestedFileName = viewModel.CurrentProjectPath is not null
                    ? System.IO.Path.GetFileName(viewModel.CurrentProjectPath)
                    : "project.starsim",
                FileTypeChoices =
                [
                    new FilePickerFileType(LocalizationService.Instance["FilePicker.Projects"])
                    {
                        Patterns = ["*.starsim"],
                    },
                ],
            });
        var path = file?.TryGetLocalPath();
        if (path is not null)
        {
            viewModel.SaveProject(path);
        }
    }

    private async void Export_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsImageLoaded) return;
        var ext = viewModel.SelectedExportFormat == StarSimCore.Application.Export.ExportFormat.Tiff16 ? "tif" : "png";
        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance["FilePicker.ExportImage"],
                DefaultExtension = ext,
                SuggestedFileName = $"export_processed.{ext}",
                FileTypeChoices =
                [
                    new FilePickerFileType(LocalizationService.Instance["FilePicker.Tiff16"]) { Patterns = ["*.tif", "*.tiff"] },
                    new FilePickerFileType(LocalizationService.Instance["FilePicker.Png"]) { Patterns = ["*.png"] },
                ],
            });
        var path = file?.TryGetLocalPath();
        if (path is not null)
        {
            await viewModel.ExportImageAsync(path);
        }
    }

    private async void LocateSource_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["FilePicker.LocateSource"],
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(LocalizationService.Instance["FilePicker.Images"])
                    {
                        Patterns = ["*.tif", "*.tiff", "*.png"],
                    },
                ],
            });
        var path = files.Count == 1 ? files[0].TryGetLocalPath() : null;
        if (path is not null && DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.LocateSourceAsync(path);
        }
    }

    private void DismissLocateSource_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.IsLocateSourceRequired = false;
            viewModel.PendingProject = null;
        }
    }
}
