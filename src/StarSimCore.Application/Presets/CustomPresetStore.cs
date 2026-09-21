using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using StarSimCore.Application.Processing;

namespace StarSimCore.Application.Presets;

public sealed class CustomPreset
{
    public const int CurrentSchemaVersion = 3;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("targetId")]
    public string TargetId { get; set; } = "Custom";

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("modules")]
    public List<CustomPresetModule> Modules { get; set; } = [];
}

public sealed class CustomPresetModule
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("parameters")]
    public List<double> Parameters { get; set; } = [];
}

public sealed class CustomPresetStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string directoryPath;
    private readonly IReadOnlyList<IImageProcessor> processorDefinitions;

    public CustomPresetStore(
        string? directoryPath = null,
        IReadOnlyList<IImageProcessor>? processorDefinitions = null)
    {
        this.directoryPath = directoryPath ?? GetDefaultStorageDirectory();
        this.processorDefinitions = processorDefinitions ?? BuiltInProcessors.All;
    }

    public string DirectoryPath => directoryPath;

    public static string GetDefaultStorageDirectory()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "StarSimCore", "presets", "custom");
    }

    public static void Validate(CustomPreset preset)
        => Validate(preset, BuiltInProcessors.All);

    public static void Validate(
        CustomPreset preset,
        IReadOnlyList<IImageProcessor> processorDefinitions)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(processorDefinitions);
        UpgradeLegacyInPlace(preset);
        if (preset.SchemaVersion != CustomPreset.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Unsupported schema version: {preset.SchemaVersion}. Expected {CustomPreset.CurrentSchemaVersion}.");
        }
        if (string.IsNullOrWhiteSpace(preset.Name))
        {
            throw new InvalidDataException("Preset name is required.");
        }
        if (string.IsNullOrWhiteSpace(preset.Id))
        {
            throw new InvalidDataException("Preset ID is required.");
        }

        var knownProcessors = processorDefinitions.ToDictionary(p => p.Id, StringComparer.Ordinal);
        foreach (var module in preset.Modules)
        {
            if (!knownProcessors.TryGetValue(module.Id, out var processor))
            {
                throw new InvalidDataException($"Unknown processor '{module.Id}'.");
            }
            if (module.Parameters.Count != processor.ParameterSchema.Count)
            {
                throw new InvalidDataException($"Processor '{module.Id}' parameter count mismatch: expected {processor.ParameterSchema.Count}, got {module.Parameters.Count}.");
            }
            for (var i = 0; i < processor.ParameterSchema.Count; i++)
            {
                var val = module.Parameters[i];
                var schema = processor.ParameterSchema[i];
                if (!double.IsFinite(val) || val < schema.Minimum || val > schema.Maximum)
                {
                    throw new InvalidDataException($"Parameter '{schema.Id}' in processor '{module.Id}' is out of range [{schema.Minimum}, {schema.Maximum}]: {val}.");
                }
            }
        }
    }

    public void Save(CustomPreset preset)
    {
        Validate(preset, processorDefinitions);
        if (!Directory.Exists(directoryPath))
        {
            Directory.CreateDirectory(directoryPath);
        }

        var safeFileName = SanitizeFileName(preset.Id) + ".json";
        var targetFile = Path.Combine(directoryPath, safeFileName);
        var tempFile = targetFile + ".tmp";

        var json = JsonSerializer.Serialize(preset, SerializerOptions);
        File.WriteAllText(tempFile, json);

        if (File.Exists(targetFile))
        {
            File.Replace(tempFile, targetFile, null);
        }
        else
        {
            File.Move(tempFile, targetFile);
        }
    }

    public IReadOnlyList<CustomPreset> LoadAll()
    {
        if (!Directory.Exists(directoryPath))
        {
            return [];
        }

        var results = new List<CustomPreset>();
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            try
            {
                var json = File.ReadAllText(file);
                var preset = JsonSerializer.Deserialize<CustomPreset>(json, SerializerOptions);
                if (preset is not null)
                {
                    Validate(preset, processorDefinitions);
                    results.Add(preset);
                }
            }
            catch
            {
                // Skip invalid or corrupt preset files gracefully
            }
        }

        return results;
    }

    public bool Delete(string presetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(presetId);
        var safeFileName = SanitizeFileName(presetId) + ".json";
        var targetFile = Path.Combine(directoryPath, safeFileName);
        if (File.Exists(targetFile))
        {
            File.Delete(targetFile);
            return true;
        }
        return false;
    }

    public static CustomPreset CreateFromSnapshot(
        string name,
        string description,
        PipelineSnapshot snapshot,
        string targetId = "Custom",
        IReadOnlyList<IImageProcessor>? processorDefinitions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(snapshot);

        var id = "custom." + SanitizeFileName(name.ToLowerInvariant().Replace(' ', '_'));

        var preset = new CustomPreset
        {
            Id = id,
            Name = name.Trim(),
            Description = description?.Trim() ?? string.Empty,
            TargetId = targetId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Modules = snapshot.Modules.Select(m => new CustomPresetModule
            {
                Id = m.Id,
                Enabled = m.Enabled,
                Parameters = m.Parameters.ToList(),
            }).ToList(),
        };

        Validate(preset, processorDefinitions ?? BuiltInProcessors.All);
        return preset;
    }

    public static PipelineSnapshot ApplyToSnapshot(
        CustomPreset preset,
        PipelineSnapshot current,
        IReadOnlyList<IImageProcessor>? processorDefinitions = null)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(current);
        var definitions = processorDefinitions ?? BuiltInProcessors.All;
        Validate(preset, definitions);

        var presetModules = preset.Modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var modulesBuilder = ImmutableArray.CreateBuilder<ProcessorState>();

        foreach (var definition in definitions)
        {
            if (presetModules.TryGetValue(definition.Id, out var pm))
            {
                var paramsBuilder = ImmutableArray.CreateBuilder<double>();
                for (var i = 0; i < definition.ParameterSchema.Count; i++)
                {
                    var val = i < pm.Parameters.Count
                        ? pm.Parameters[i]
                        : definition.ParameterSchema[i].DefaultValue;
                    paramsBuilder.Add(Math.Clamp(val, definition.ParameterSchema[i].Minimum, definition.ParameterSchema[i].Maximum));
                }
                modulesBuilder.Add(new ProcessorState(definition.Id, pm.Enabled, paramsBuilder.ToImmutable()));
            }
            else
            {
                modulesBuilder.Add(current.GetModule(definition.Id));
            }
        }

        return new PipelineSnapshot(modulesBuilder.ToImmutable(), current.Revision + 1);
    }

    private static void UpgradeLegacyInPlace(CustomPreset preset)
    {
        if (preset.SchemaVersion is not (1 or 2)) return;
        foreach (var module in preset.Modules)
        {
            module.Parameters = ProcessorParameterMigration.Upgrade(module.Id, module.Parameters).ToList();
        }
        preset.SchemaVersion = CustomPreset.CurrentSchemaVersion;
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(input.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
