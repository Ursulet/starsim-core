using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StarSimCore.Application.Localization;
using StarSimCore.Application.Plugins;
using StarSimCore.Interop;
using StarSimCore.Interop.Plugins;

namespace StarSimCore.UI.ViewModels;

public sealed class PluginListItemViewModel
{
    public PluginListItemViewModel(
        string filePath,
        string fileName,
        string displayName,
        string version,
        string author,
        string pluginId,
        string status,
        string details,
        string processorSummary,
        bool isLoaded,
        bool canRemove,
        Action<PluginListItemViewModel> remove)
    {
        FilePath = filePath;
        FileName = fileName;
        DisplayName = displayName;
        Version = version;
        Author = author;
        PluginId = pluginId;
        Status = status;
        Details = details;
        ProcessorSummary = processorSummary;
        IsLoaded = isLoaded;
        CanRemove = canRemove;
        RemoveCommand = new RelayCommand(() => remove(this), () => CanRemove);
    }

    public string FilePath { get; }
    public string FileName { get; }
    public string DisplayName { get; }
    public string Version { get; }
    public string Author { get; }
    public string PluginId { get; }
    public string Status { get; }
    public string Details { get; }
    public string ProcessorSummary { get; }
    public bool IsLoaded { get; }
    public bool CanRemove { get; }
    public bool HasIdentity => !string.IsNullOrWhiteSpace(PluginId) || !string.IsNullOrWhiteSpace(Author);
    public string IdentitySummary => string.Join(
        " • ",
        new[]
        {
            string.IsNullOrWhiteSpace(PluginId) ? null : LocalizationService.Instance.GetString("Plugin.Identity.Id", PluginId),
            string.IsNullOrWhiteSpace(Author) ? null : LocalizationService.Instance.GetString("Plugin.Identity.Author", Author),
        }.OfType<string>());
    public IRelayCommand RemoveCommand { get; }
}

public sealed partial class PluginManagerViewModel : ObservableObject
{
    private readonly PluginInstallationService installationService;
    private readonly PluginService? runtimeService;

    public PluginManagerViewModel(
        PluginInstallationService installationService,
        PluginService? runtimeService,
        bool openDeveloperGuide = false)
    {
        this.installationService = installationService;
        this.runtimeService = runtimeService;
        externalPluginsEnabled = installationService.ExternalPluginsEnabled;
        selectedTabIndex = openDeveloperGuide ? 1 : 0;
        LocalizationService.Instance.LanguageChanged += (_, _) =>
        {
            StatusText = LocalizationService.Instance["Plugin.Status.Ready"];
            Refresh();
        };
        Refresh();
    }

    public ObservableCollection<PluginListItemViewModel> Plugins { get; } = [];
    public string PluginDirectory => installationService.UserPluginDirectory;
    public string SdkDirectory => installationService.SdkDirectory;
    public bool HasPlugins => Plugins.Count > 0;
    public bool RuntimeDiscoveryActive => runtimeService is not null;
    public string RuntimeStatus => RuntimeDiscoveryActive
        ? LocalizationService.Instance.GetString(
            "Plugin.Runtime.Active",
            runtimeService!.LoadedPlugins.Count,
            runtimeService.PluginProcessors.Count,
            runtimeService.Rejections.Count)
        : LocalizationService.Instance["Plugin.Runtime.Disabled"];

    [ObservableProperty] private bool externalPluginsEnabled;
    [ObservableProperty] private bool restartRequired;
    [ObservableProperty] private int selectedTabIndex;
    [ObservableProperty] private string statusText = LocalizationService.Instance["Plugin.Status.Ready"];

    partial void OnExternalPluginsEnabledChanged(bool value)
    {
        installationService.ExternalPluginsEnabled = value;
        RestartRequired = true;
        StatusText = value
            ? LocalizationService.Instance["Plugin.Status.EnabledRestart"]
            : LocalizationService.Instance["Plugin.Status.DisabledRestart"];
        OnPropertyChanged(nameof(RuntimeStatus));
    }

    [RelayCommand]
    public void Refresh()
    {
        Plugins.Clear();
        var loadedPlugins = runtimeService is null
            ? Array.Empty<LoadedPlugin>()
            : runtimeService.LoadedPlugins.ToArray();
        var rejections = runtimeService is null
            ? Array.Empty<PluginRejection>()
            : runtimeService.Rejections.ToArray();
        var loadedByPath = loadedPlugins
            .ToDictionary(plugin => Path.GetFullPath(plugin.Path), StringComparer.OrdinalIgnoreCase);
        var rejectedByPath = rejections
            .GroupBy(rejection => Path.GetFullPath(rejection.FilePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

        foreach (var file in installationService.EnumerateInstalledFiles())
        {
            if (loadedByPath.TryGetValue(file.FilePath, out var loaded))
            {
                var processors = loaded.Metadata.Processors.Count == 0
                    ? LocalizationService.Instance["Plugin.Processors.None"]
                    : string.Join(", ", loaded.Metadata.Processors.Select(processor => processor.Name));
                Plugins.Add(new PluginListItemViewModel(
                    file.FilePath,
                    file.FileName,
                    loaded.Metadata.Name,
                    loaded.Metadata.Version,
                    loaded.Metadata.Author,
                    loaded.Metadata.PluginId,
                    LocalizationService.Instance["Plugin.State.Loaded"],
                    loaded.Metadata.Description,
                    LocalizationService.Instance.GetString("Plugin.Processors", processors),
                    isLoaded: true,
                    canRemove: file.IsUserManaged,
                    remove: Remove));
            }
            else if (rejectedByPath.TryGetValue(file.FilePath, out var rejected))
            {
                Plugins.Add(new PluginListItemViewModel(
                    file.FilePath,
                    file.FileName,
                    Path.GetFileNameWithoutExtension(file.FileName),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    LocalizationService.Instance["Plugin.State.Rejected"],
                    rejected.Reason,
                    LocalizationService.Instance["Plugin.NoProcessorsRegistered"],
                    isLoaded: false,
                    canRemove: file.IsUserManaged,
                    remove: Remove));
            }
            else
            {
                Plugins.Add(new PluginListItemViewModel(
                    file.FilePath,
                    file.FileName,
                    Path.GetFileNameWithoutExtension(file.FileName),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    RuntimeDiscoveryActive
                        ? LocalizationService.Instance["Plugin.State.Restart"]
                        : LocalizationService.Instance["Plugin.State.NotLoaded"],
                    RuntimeDiscoveryActive
                        ? LocalizationService.Instance["Plugin.RestartDetail"]
                        : LocalizationService.Instance["Plugin.DisabledDetail"],
                    LocalizationService.Instance["Plugin.MetadataAfter"],
                    isLoaded: false,
                    canRemove: file.IsUserManaged,
                    remove: Remove));
            }
        }

        OnPropertyChanged(nameof(HasPlugins));
        OnPropertyChanged(nameof(RuntimeDiscoveryActive));
        OnPropertyChanged(nameof(RuntimeStatus));
    }

    public void InstallFile(string sourcePath)
    {
        var result = installationService.Install(sourcePath);
        StatusText = result.Message;
        RestartRequired |= result.Success && result.RestartRequired;
        if (result.Success && !ExternalPluginsEnabled)
            ExternalPluginsEnabled = true;
        Refresh();
    }

    public void SelectDeveloperGuide() => SelectedTabIndex = 1;

    [RelayCommand]
    public void OpenPluginFolder()
    {
        Directory.CreateDirectory(installationService.UserPluginDirectory);
        OpenInExplorer(installationService.UserPluginDirectory);
    }

    [RelayCommand]
    public void OpenSdkFolder()
    {
        if (!Directory.Exists(installationService.SdkDirectory))
        {
            StatusText = LocalizationService.Instance.GetString("Plugin.SdkMissing", installationService.SdkDirectory);
            return;
        }
        OpenInExplorer(installationService.SdkDirectory);
    }

    [RelayCommand]
    public void OpenTutorial()
    {
        var tutorialPath = Path.Combine(installationService.SdkDirectory, "PLUGIN_SDK.md");
        if (!File.Exists(tutorialPath))
        {
            StatusText = LocalizationService.Instance.GetString("Plugin.TutorialMissing", tutorialPath);
            return;
        }

        var start = new ProcessStartInfo(tutorialPath) { UseShellExecute = true };
        Process.Start(start);
    }

    [RelayCommand]
    public void OpenLogFolder()
    {
        Directory.CreateDirectory(DiagnosticService.Current.LogDirectory);
        OpenInExplorer(DiagnosticService.Current.LogDirectory);
    }

    private void Remove(PluginListItemViewModel item)
    {
        var result = installationService.RemoveOrSchedule(item.FilePath);
        StatusText = result.Message;
        RestartRequired |= result.Success && result.RestartRequired;
        Refresh();
    }

    private static void OpenInExplorer(string path)
    {
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
        start.ArgumentList.Add(path);
        Process.Start(start);
    }
}
