using System.Diagnostics;
using StarSimCore.Application.Processing;
using StarSimCore.Domain.Imaging;
using StarSimCore.Interop;
using StarSimCore.Interop.Plugins;

namespace StarSimCore.Application.Plugins;

public sealed record PluginRejection(string FilePath, string Reason);

public sealed class PluginProcessorAdapter : IImageProcessor
{
    private readonly LoadedPlugin plugin;
    private readonly PluginProcessorMetadata processorMetadata;

    public PluginProcessorAdapter(LoadedPlugin plugin, PluginProcessorMetadata processorMetadata)
    {
        this.plugin = plugin ?? throw new ArgumentNullException(nameof(plugin));
        this.processorMetadata = processorMetadata ?? throw new ArgumentNullException(nameof(processorMetadata));

        Id = processorMetadata.Id;
        Name = processorMetadata.Name;
        Category = processorMetadata.Category;
        SemanticVersion = Version.TryParse(plugin.Metadata.Version, out var v) ? v : new Version(1, 0, 0);

        ParameterSchema = processorMetadata.Parameters.Select(p => new ProcessorParameterSchema(
            p.Id,
            p.Name,
            p.MinimumValue,
            p.MaximumValue,
            p.DefaultValue,
            p.Step)).ToList();

        var caps = ProcessorCapabilities.None;
        if (processorMetadata.Capabilities.HasFlag(PluginCapabilities.Grayscale))
            caps |= ProcessorCapabilities.Grayscale;
        if (processorMetadata.Capabilities.HasFlag(PluginCapabilities.Rgb))
            caps |= ProcessorCapabilities.Rgb;
        if (processorMetadata.Capabilities.HasFlag(PluginCapabilities.SupportsPreview))
            caps |= ProcessorCapabilities.InteractivePreview;
        caps |= ProcessorCapabilities.FullResolution;

        Capabilities = caps;
    }

    public string Id { get; }
    public string Name { get; }
    public string Category { get; }
    public Version SemanticVersion { get; }
    public IReadOnlyList<ProcessorParameterSchema> ParameterSchema { get; }
    public bool IsEnabledByDefault => false;
    public ProcessorCapabilities Capabilities { get; }
    public PreviewQualityHint PreviewQualityHint => PreviewQualityHint.Debounced;
    public TilingCapability TilingCapability => TilingCapability.NotSupported;
    public LoadedPlugin Plugin => plugin;
    public string PluginId => plugin.Metadata.PluginId;
    public string PluginName => plugin.Metadata.Name;
    public string PluginVersion => plugin.Metadata.Version;
    public string PluginAuthor => plugin.Metadata.Author;
    public string PluginDescription => plugin.Metadata.Description;

    public void Validate(ProcessorState state, ImageMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(metadata);

        if (state.Parameters.Length != ParameterSchema.Count)
        {
            throw new ArgumentException(
                $"Processor '{Id}' expects {ParameterSchema.Count} parameters, got {state.Parameters.Length}.",
                nameof(state));
        }

        var requiredCapability = metadata.ChannelCount == 1
            ? ProcessorCapabilities.Grayscale
            : metadata.ChannelCount == 3
                ? ProcessorCapabilities.Rgb
                : ProcessorCapabilities.None;
        if (requiredCapability == ProcessorCapabilities.None ||
            !Capabilities.HasFlag(requiredCapability))
        {
            throw new NotSupportedException(
                $"Plugin processor '{Id}' does not support {metadata.ChannelCount}-channel images.");
        }

        for (var i = 0; i < ParameterSchema.Count; i++)
        {
            var val = state.Parameters[i];
            var schema = ParameterSchema[i];
            if (!double.IsFinite(val) || val < schema.Minimum || val > schema.Maximum)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(state),
                    $"Parameter '{schema.Id}' in processor '{Id}' value {val} is outside valid range [{schema.Minimum}, {schema.Maximum}].");
            }
        }
    }

    public NativeImage Process(
        NativeImage input,
        ProcessorState state,
        ProcessorContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);

        cancellationToken.ThrowIfCancellationRequested();

        var nativeQuality = context.Quality == ProcessingQuality.InteractivePreview &&
                            Capabilities.HasFlag(ProcessorCapabilities.InteractivePreview)
            ? NativeProcessingQuality.InteractivePreview
            : NativeProcessingQuality.FullResolution;

        return plugin.Execute(
            processorMetadata.Index,
            input,
            state.Parameters,
            nativeQuality,
            (fraction, stage) =>
            {
                context.ReportProgress?.Invoke(new ProcessorProgress(
                    Id,
                    Name,
                    stage,
                    (uint)(fraction * 100),
                    100,
                    fraction));
            },
            cancellationToken);
    }
}

public sealed class PluginService : IDisposable
{
    private readonly List<LoadedPlugin> loadedPlugins = [];
    private readonly List<PluginRejection> rejections = [];
    private readonly List<IImageProcessor> pluginProcessors = [];
    private readonly string[] scanDirectories;
    private bool disposed;

    public PluginService(params string[]? searchDirectories)
    {
        scanDirectories = searchDirectories is { Length: > 0 }
            ? searchDirectories
            : GetDefaultPluginDirectories();
    }

    public IReadOnlyList<LoadedPlugin> LoadedPlugins => loadedPlugins;
    public IReadOnlyList<PluginRejection> Rejections => rejections;
    public IReadOnlyList<IImageProcessor> PluginProcessors => pluginProcessors;

    public static string[] GetDefaultPluginDirectories()
    {
        var appBase = AppDomain.CurrentDomain.BaseDirectory;
        var appPlugins = Path.Combine(appBase, "plugins");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userPlugins = Path.Combine(appData, "StarSimCore", "plugins");

        return [appPlugins, userPlugins];
    }

    public void DiscoverAndLoadPlugins()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        DiagnosticService.Current.RecordPluginEvent(
            "INFO",
            $"Opt-in discovery started. Directories: {string.Join("; ", scanDirectories)}. Native plugins are not sandboxed.");

        foreach (var dir in scanDirectories)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            string[] candidates;
            try
            {
                candidates = Directory
                    .EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch (Exception ex)
            {
                RecordRejection(dir, $"Plugin directory could not be scanned: {ex.Message}");
                continue;
            }

            foreach (var candidatePath in candidates)
            {
                var dllPath = Path.GetFullPath(candidatePath);
                // Prevent duplicate loads
                if (loadedPlugins.Any(p => string.Equals(p.Path, dllPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                LoadedPlugin? plugin = null;
                try
                {
                    plugin = LoadedPlugin.Load(dllPath);
                    if (loadedPlugins.Any(existing => string.Equals(
                            existing.Metadata.PluginId,
                            plugin.Metadata.PluginId,
                            StringComparison.Ordinal)))
                    {
                        throw new NativePluginException(
                            $"Duplicate plugin ID '{plugin.Metadata.PluginId}'.");
                    }

                    var adapters = new List<IImageProcessor>();
                    foreach (var procDef in plugin.Metadata.Processors)
                    {
                        var adapter = new PluginProcessorAdapter(plugin, procDef);
                        if (BuiltInProcessors.All.Any(processor => string.Equals(processor.Id, adapter.Id, StringComparison.Ordinal)) ||
                            pluginProcessors.Any(processor => string.Equals(processor.Id, adapter.Id, StringComparison.Ordinal)) ||
                            adapters.Any(processor => string.Equals(processor.Id, adapter.Id, StringComparison.Ordinal)))
                        {
                            throw new NativePluginException(
                                $"Processor ID '{adapter.Id}' is already registered.");
                        }
                        adapters.Add(adapter);
                    }

                    loadedPlugins.Add(plugin);
                    pluginProcessors.AddRange(adapters);
                    var loadedMessage = $"Loaded plugin '{plugin.Metadata.Name}' v{plugin.Metadata.Version} " +
                                        $"({plugin.Metadata.PluginId}) from '{dllPath}' with {adapters.Count} processor(s).";
                    Trace.WriteLine($"[PluginService] {loadedMessage}");
                    DiagnosticService.Current.RecordPluginEvent("INFO", loadedMessage);
                }
                catch (Exception ex)
                {
                    plugin?.Dispose();
                    RecordRejection(dllPath, ex.Message);
                }
            }
        }
    }

    private void RecordRejection(string path, string reason)
    {
        var rejection = new PluginRejection(path, reason);
        rejections.Add(rejection);
        var message = $"Rejected '{path}': {reason}";
        Trace.WriteLine($"[PluginService] {message}");
        DiagnosticService.Current.RecordPluginEvent("REJECTED", message);
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            foreach (var plugin in loadedPlugins)
            {
                plugin.Dispose();
            }
            loadedPlugins.Clear();
            pluginProcessors.Clear();
        }
    }
}
