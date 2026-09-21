using System.Text.Json;
using System.Text.Json.Serialization;
using StarSimCore.Application.Localization;
using StarSimCore.Interop;

namespace StarSimCore.Application.Plugins;

public sealed record InstalledPluginFile(
    string FilePath,
    string FileName,
    bool IsUserManaged);

public sealed record PluginInstallResult(
    bool Success,
    string Message,
    string? InstalledPath = null,
    bool RestartRequired = true);

public sealed class PluginInstallationService
{
    private const int CurrentSettingsVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object gate = new();
    private readonly string settingsPath;
    private PluginSettings settings;

    public PluginInstallationService(string? userPluginDirectory = null, string? settingsFilePath = null)
    {
        var appDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StarSimCore");
        UserPluginDirectory = Path.GetFullPath(
            userPluginDirectory ?? Path.Combine(appDataRoot, "plugins"));
        settingsPath = Path.GetFullPath(
            settingsFilePath ?? Path.Combine(appDataRoot, "plugin-settings.json"));
        settings = LoadSettings();
    }

    public string UserPluginDirectory { get; }
    public string ArchivedPluginDirectory => Path.Combine(UserPluginDirectory, "disabled");
    public string SdkDirectory => Path.Combine(AppContext.BaseDirectory, "sdk");

    public bool ExternalPluginsEnabled
    {
        get
        {
            lock (gate) return settings.ExternalPluginsEnabled;
        }
        set
        {
            lock (gate)
            {
                if (settings.ExternalPluginsEnabled == value) return;
                settings.ExternalPluginsEnabled = value;
                SaveSettings();
            }
        }
    }

    public IReadOnlyList<InstalledPluginFile> EnumerateInstalledFiles()
    {
        var results = new List<InstalledPluginFile>();
        var applicationPluginDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
        foreach (var directory in new[] { applicationPluginDirectory, UserPluginDirectory }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (!Directory.Exists(directory)) continue;
                var isUserManaged = PathsEqual(directory, UserPluginDirectory);
                results.AddRange(Directory
                    .EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(path => new InstalledPluginFile(
                        Path.GetFullPath(path),
                        Path.GetFileName(path),
                        isUserManaged)));
            }
            catch (Exception ex)
            {
                DiagnosticService.Current.RecordPluginEvent(
                    "WARNING",
                    $"Could not enumerate plugin directory '{directory}': {ex.Message}");
            }
        }
        return results;
    }

    public PluginInstallResult Install(string sourceDllPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDllPath);
        var source = Path.GetFullPath(sourceDllPath);
        if (!File.Exists(source))
            return new PluginInstallResult(false, LocalizationService.Instance.GetString("Plugin.Install.NotFound", source));
        if (!string.Equals(Path.GetExtension(source), ".dll", StringComparison.OrdinalIgnoreCase))
            return new PluginInstallResult(false, LocalizationService.Instance["Plugin.Install.InvalidExtension"]);

        try
        {
            Directory.CreateDirectory(UserPluginDirectory);
            var baseName = Path.GetFileNameWithoutExtension(source);
            var destination = Path.Combine(UserPluginDirectory, Path.GetFileName(source));
            string? replacementToArchiveAtRestart = null;
            if (File.Exists(destination))
            {
                if (PathsEqual(source, destination))
                {
                    return new PluginInstallResult(
                        false,
                        LocalizationService.Instance["Plugin.Install.AlreadyPresent"],
                        destination,
                        RestartRequired: false);
                }

                if (!TryArchive(destination, out _, out _))
                {
                    replacementToArchiveAtRestart = Path.GetFileName(destination);
                    destination = Path.Combine(
                        UserPluginDirectory,
                        $"{baseName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.dll");
                }
            }

            var temporary = destination + $".{Guid.NewGuid():N}.installing";
            try
            {
                File.Copy(source, temporary, overwrite: false);
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }

            if (replacementToArchiveAtRestart is not null)
                ScheduleRemoval(replacementToArchiveAtRestart);
            ExternalPluginsEnabled = true;
            var message = LocalizationService.Instance.GetString(
                "Plugin.Install.Success",
                Path.GetFileName(destination));
            DiagnosticService.Current.RecordPluginEvent("INFO", message);
            return new PluginInstallResult(true, message, destination);
        }
        catch (Exception ex)
        {
            DiagnosticService.Current.RecordPluginEvent(
                "ERROR",
                $"Installation failed for '{source}': {ex.Message}");
            return new PluginInstallResult(
                false,
                LocalizationService.Instance.GetString("Plugin.Install.Failed", ex.Message));
        }
    }

    public PluginInstallResult RemoveOrSchedule(string pluginPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginPath);
        var fullPath = Path.GetFullPath(pluginPath);
        if (!IsInsideDirectory(fullPath, UserPluginDirectory))
        {
            return new PluginInstallResult(
                false,
                LocalizationService.Instance["Plugin.Remove.Bundled"],
                RestartRequired: false);
        }

        if (!File.Exists(fullPath))
            return new PluginInstallResult(
                false,
                LocalizationService.Instance["Plugin.Remove.Missing"],
                RestartRequired: false);

        if (TryArchive(fullPath, out var archivePath, out var archiveError))
        {
            var message = LocalizationService.Instance.GetString("Plugin.Remove.Success", archivePath);
            DiagnosticService.Current.RecordPluginEvent("INFO", message);
            return new PluginInstallResult(true, message, archivePath);
        }

        lock (gate)
        {
            ScheduleRemovalCore(Path.GetFileName(fullPath));
        }
        var scheduled = LocalizationService.Instance.GetString("Plugin.Remove.Scheduled", archiveError ?? string.Empty);
        DiagnosticService.Current.RecordPluginEvent("INFO", scheduled);
        return new PluginInstallResult(true, scheduled, fullPath);
    }

    public void ApplyPendingRemovals()
    {
        lock (gate)
        {
            if (settings.PendingRemovalFiles.Count == 0) return;
            foreach (var fileName in settings.PendingRemovalFiles.ToArray())
            {
                var safeName = Path.GetFileName(fileName);
                var fullPath = Path.Combine(UserPluginDirectory, safeName);
                if (!File.Exists(fullPath) || TryArchive(fullPath, out _, out _))
                    settings.PendingRemovalFiles.Remove(fileName);
            }
            SaveSettings();
        }
    }

    private bool TryArchive(string sourcePath, out string archivePath, out string error)
    {
        archivePath = string.Empty;
        error = string.Empty;
        try
        {
            Directory.CreateDirectory(ArchivedPluginDirectory);
            archivePath = Path.Combine(
                ArchivedPluginDirectory,
                $"{Path.GetFileName(sourcePath)}.{DateTime.UtcNow:yyyyMMdd_HHmmss}.disabled");
            File.Move(sourcePath, archivePath);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private PluginSettings LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return new PluginSettings();
            var loaded = JsonSerializer.Deserialize<PluginSettings>(
                File.ReadAllText(settingsPath),
                SerializerOptions);
            if (loaded is null || loaded.SchemaVersion != CurrentSettingsVersion)
                return new PluginSettings();
            loaded.PendingRemovalFiles ??= [];
            return loaded;
        }
        catch (Exception ex)
        {
            DiagnosticService.Current.RecordPluginEvent(
                "WARNING",
                $"Plugin settings could not be read and defaults will be used: {ex.Message}");
            return new PluginSettings();
        }
    }

    private void ScheduleRemoval(string fileName)
    {
        lock (gate)
        {
            ScheduleRemovalCore(fileName);
        }
    }

    private void ScheduleRemovalCore(string fileName)
    {
        if (!settings.PendingRemovalFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
            settings.PendingRemovalFiles.Add(fileName);
        SaveSettings();
    }

    private void SaveSettings()
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = settingsPath + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings, SerializerOptions));
            if (File.Exists(settingsPath)) File.Replace(temporary, settingsPath, null);
            else File.Move(temporary, settingsPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsInsideDirectory(string path, string directory)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) &&
               relative != ".." &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private sealed class PluginSettings
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; } = CurrentSettingsVersion;

        [JsonPropertyName("externalPluginsEnabled")]
        public bool ExternalPluginsEnabled { get; set; }

        [JsonPropertyName("pendingRemovalFiles")]
        public List<string> PendingRemovalFiles { get; set; } = [];
    }
}
