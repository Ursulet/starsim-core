using CommunityToolkit.Mvvm.ComponentModel;
using StarSimCore.Application.Localization;

namespace StarSimCore.UI.ViewModels;

public sealed class HelpModuleGuideViewModel : ObservableObject
{
    public HelpModuleGuideViewModel(string order, string icon, string resourceKey, string usageKey)
    {
        Order = order;
        Icon = icon;
        ResourceKey = resourceKey;
        UsageKey = usageKey;
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(Usage));
        };
    }

    public string Order { get; }
    public string Icon { get; }
    public string ResourceKey { get; }
    public string UsageKey { get; }
    public string Name => LocalizationService.Instance[ResourceKey];
    public string Description => LocalizationService.Instance[$"{ResourceKey}.Description"];
    public string Usage => LocalizationService.Instance[UsageKey];
}

public sealed class HelpShortcutViewModel : ObservableObject
{
    public HelpShortcutViewModel(string gesture, string resourceKey)
    {
        Gesture = gesture;
        ResourceKey = resourceKey;
        LocalizationService.Instance.LanguageChanged += (_, _) => OnPropertyChanged(nameof(Action));
    }

    public string Gesture { get; }
    public string ResourceKey { get; }
    public string Action => LocalizationService.Instance[ResourceKey];
}

public sealed partial class HelpWindowViewModel : ObservableObject
{
    public HelpWindowViewModel(int selectedTabIndex = 0)
    {
        this.selectedTabIndex = selectedTabIndex;
        var version = typeof(HelpWindowViewModel).Assembly.GetName().Version;
        VersionText = LocalizationService.Instance.GetString(
            "Help.Version",
            version is null ? "1.0" : $"{version.Major}.{version.Minor}.{version.Build}");
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            var current = typeof(HelpWindowViewModel).Assembly.GetName().Version;
            VersionText = LocalizationService.Instance.GetString(
                "Help.Version",
                current is null ? "1.0" : $"{current.Major}.{current.Minor}.{current.Build}");
        };
    }

    [ObservableProperty] private int selectedTabIndex;
    [ObservableProperty] private string versionText = string.Empty;

    public IReadOnlyList<HelpShortcutViewModel> Shortcuts { get; } =
    [
        new("Ctrl+O", "Help.Shortcut.OpenImage"),
        new("Ctrl+P", "Help.Shortcut.OpenProject"),
        new("Ctrl+S", "Help.Shortcut.SaveProject"),
        new("Ctrl+E", "Help.Shortcut.Export"),
        new("Ctrl+Z", "Help.Shortcut.Undo"),
        new("Ctrl+Y / Ctrl+Shift+Z", "Help.Shortcut.Redo"),
        new("Ctrl+B", "Help.Shortcut.Compare"),
        new("Ctrl+Shift+B", "Help.Shortcut.Batch"),
        new("Ctrl+H", "Help.Shortcut.History"),
        new("Ctrl+Shift+C", "Help.Shortcut.Clipping"),
        new("Ctrl+R", "Help.Shortcut.Roi"),
        new("Ctrl+Enter", "Help.Shortcut.ApplyRoi"),
        new("Ctrl+0", "Help.Shortcut.Fit"),
        new("Ctrl++ / Ctrl+-", "Help.Shortcut.Zoom"),
        new("F1 / F2", "Help.Shortcut.Mode"),
        new("Ctrl+F1", "Help.Shortcut.Help"),
        new("F11", "Help.Shortcut.Maximize"),
        new("Esc", "Help.Shortcut.Escape"),
        new("Alt+F4", "Help.Shortcut.Close"),
    ];

    public IReadOnlyList<HelpModuleGuideViewModel> Modules { get; } =
    [
        new("01", "⌖", "Module.RgbAlign", "Help.Module.RgbAlign.Usage"),
        new("02", "⠿", "Module.NoiseReduction", "Help.Module.NoiseReduction.Usage"),
        new("03", "◎", "Module.RichardsonLucy", "Help.Module.RichardsonLucy.Usage"),
        new("04", "≋", "Module.Wavelet", "Help.Module.Wavelet.Usage"),
        new("05", "◇", "Module.UnsharpMask", "Help.Module.UnsharpMask.Usage"),
        new("06", "▥", "Module.MultiScaleSharpen", "Help.Module.MultiScaleSharpen.Usage"),
        new("07", "◌", "Module.Deringing", "Help.Module.Deringing.Usage"),
        new("08", "◔", "Module.LocalDetail", "Help.Module.LocalDetail.Usage"),
        new("09", "◉", "Module.AdvancedColor", "Help.Module.AdvancedColor.Usage"),
        new("10", "◒", "Module.RgbBalance", "Help.Module.RgbBalance.Usage"),
        new("11", "◍", "Module.Saturation", "Help.Module.Saturation.Usage"),
        new("12", "☼", "Module.AdvancedTone", "Help.Module.AdvancedTone.Usage"),
        new("13", "◐", "Module.Exposure", "Help.Module.Exposure.Usage"),
        new("14", "◑", "Module.Contrast", "Help.Module.Contrast.Usage"),
        new("15", "⌁", "Module.Gamma", "Help.Module.Gamma.Usage"),
    ];
}
