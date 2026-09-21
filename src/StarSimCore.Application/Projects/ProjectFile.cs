using System.Text.Json.Serialization;

namespace StarSimCore.Application.Projects;

public sealed class ProjectFile
{
    public const int CurrentSchemaVersion = 4;
    public const string CurrentAppVersion = "1.0.0";
    public const string DefaultExtension = ".starsim";

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("appVersion")]
    public string AppVersion { get; set; } = CurrentAppVersion;

    [JsonPropertyName("createdAtUtc")]
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("modifiedAtUtc")]
    public DateTimeOffset ModifiedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("source")]
    public ProjectSourceReference Source { get; set; } = new();

    [JsonPropertyName("workflow")]
    public ProjectWorkflowState Workflow { get; set; } = new();

    [JsonPropertyName("viewer")]
    public ProjectViewerState Viewer { get; set; } = new();

    [JsonPropertyName("pipeline")]
    public ProjectPipelineData Pipeline { get; set; } = new();

    [JsonPropertyName("settings")]
    public Dictionary<string, string> Settings { get; set; } = new();
}

public sealed class ProjectSourceReference
{
    [JsonPropertyName("canonicalPath")]
    public string CanonicalPath { get; set; } = string.Empty;

    [JsonPropertyName("fileName")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("fileFormat")]
    public string FileFormat { get; set; } = string.Empty;

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [JsonPropertyName("width")]
    public uint Width { get; set; }

    [JsonPropertyName("height")]
    public uint Height { get; set; }

    [JsonPropertyName("channelCount")]
    public uint ChannelCount { get; set; }

    [JsonPropertyName("sourceBitDepth")]
    public uint SourceBitDepth { get; set; }

    [JsonPropertyName("lastWriteTimeUtc")]
    public DateTimeOffset LastWriteTimeUtc { get; set; }
}

public sealed class ProjectWorkflowState
{
    [JsonPropertyName("isExpertMode")]
    public bool IsExpertMode { get; set; }

    [JsonPropertyName("target")]
    public string Target { get; set; } = "Default";

    [JsonPropertyName("presetId")]
    public string PresetId { get; set; } = "Default";

    [JsonPropertyName("presetVersion")]
    public string PresetVersion { get; set; } = "1.0.0";

    [JsonPropertyName("colorPresetId")]
    public string ColorPresetId { get; set; } = "Natural";

    [JsonPropertyName("presetStrength")]
    public double PresetStrength { get; set; }

    [JsonPropertyName("detail")]
    public double Detail { get; set; } = 50;

    [JsonPropertyName("noiseReduction")]
    public double NoiseReduction { get; set; }

    [JsonPropertyName("color")]
    public double Color { get; set; } = 50;

    [JsonPropertyName("brightness")]
    public double Brightness { get; set; }

    [JsonPropertyName("contrast")]
    public double Contrast { get; set; }
}

public sealed class ProjectViewerState
{
    [JsonPropertyName("isFitToViewer")]
    public bool IsFitToViewer { get; set; } = true;

    [JsonPropertyName("zoomFactor")]
    public double ZoomFactor { get; set; } = 1.0;

    [JsonPropertyName("isComparisonEnabled")]
    public bool IsComparisonEnabled { get; set; } = true;

    [JsonPropertyName("comparisonSplit")]
    public double ComparisonSplit { get; set; } = 0.5;
}

public sealed class ProjectPipelineData
{
    [JsonPropertyName("revision")]
    public long Revision { get; set; }

    [JsonPropertyName("modules")]
    public List<ProjectModuleData> Modules { get; set; } = [];
}

public sealed class ProjectModuleData
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("parameters")]
    public List<double> Parameters { get; set; } = [];
}
