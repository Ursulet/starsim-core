using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using StarSimCore.Application.Plugins;
using StarSimCore.Interop;
using StarSimCore.UI;

namespace StarSimCore.App;

public partial class App : Avalonia.Application
{
    private PluginService? pluginService;
    private PluginInstallationService? pluginInstallationService;

    public static bool StartInExpertMode { get; set; }
    public static bool EnableExternalPlugins { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        pluginInstallationService = new PluginInstallationService();
        pluginInstallationService.ApplyPendingRemovals();
        var shouldLoadExternalPlugins =
            EnableExternalPlugins || pluginInstallationService.ExternalPluginsEnabled;
        if (shouldLoadExternalPlugins)
        {
            try
            {
                pluginService = new PluginService();
                pluginService.DiscoverAndLoadPlugins();
            }
            catch (Exception ex)
            {
                DiagnosticService.Current.RecordManagedException("Opt-in plugin discovery", ex);
                pluginService?.Dispose();
                pluginService = null;
            }
        }

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow(
                StartInExpertMode,
                pluginService,
                pluginInstallationService);
            desktop.Exit += (_, _) =>
            {
                pluginService?.Dispose();
                pluginService = null;
                pluginInstallationService = null;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
