using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using StarSimCore.Application.Imaging;
using StarSimCore.Application.Processing;
using StarSimCore.Domain.Imaging;
using StarSimCore.Interop;

namespace StarSimCore.Application.Projects;

public enum SourceMatchStatus
{
    ExactMatch,
    CompatibleMatch,
    Missing,
    Incompatible,
}

public sealed record SourceVerificationResult(
    SourceMatchStatus Status,
    string Message,
    string? ResolvedPath = null);

public sealed class ProjectService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly ImageOpenService imageOpenService;

    public ProjectService() : this(new ImageOpenService())
    {
    }

    public ProjectService(ImageOpenService imageOpenService)
    {
        this.imageOpenService = imageOpenService;
    }

    public static ProjectFile CreateProject(
        ImageDocument document,
        PipelineSnapshot snapshot,
        WorkspaceState? workspaceState = null,
        ProjectViewerState? viewerState = null,
        string presetVersion = "1.0.0")
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(snapshot);

        var source = document.Source;
        var metadata = source.Metadata;

        var project = new ProjectFile
        {
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ModifiedAtUtc = DateTimeOffset.UtcNow,
            Source = new ProjectSourceReference
            {
                CanonicalPath = source.CanonicalPath,
                FileName = source.FileName,
                FileFormat = source.FileFormat,
                FileSize = source.FileSize,
                Sha256 = source.Sha256,
                Width = metadata.Width,
                Height = metadata.Height,
                ChannelCount = metadata.ChannelCount,
                SourceBitDepth = metadata.SourceBitDepth,
                LastWriteTimeUtc = source.LastWriteTimeUtc,
            },
            Workflow = workspaceState is not null
                ? new ProjectWorkflowState
                {
                    IsExpertMode = workspaceState.IsExpertMode,
                    Target = workspaceState.SelectedTarget,
                    PresetId = workspaceState.SelectedPreset,
                    PresetVersion = presetVersion,
                    ColorPresetId = workspaceState.SelectedColorPreset,
                    PresetStrength = workspaceState.PresetStrength,
                    Detail = workspaceState.Detail,
                    NoiseReduction = workspaceState.NoiseReduction,
                    Color = workspaceState.Color,
                    Brightness = workspaceState.Brightness,
                    Contrast = workspaceState.Contrast,
                }
                : new ProjectWorkflowState(),
            Viewer = viewerState ?? new ProjectViewerState(),
            Pipeline = new ProjectPipelineData
            {
                Revision = snapshot.Revision,
                Modules = snapshot.Modules.Select(m => new ProjectModuleData
                {
                    Id = m.Id,
                    Enabled = m.Enabled,
                    Parameters = m.Parameters.ToList(),
                }).ToList(),
            }
        };

        return project;
    }

    public static void SaveProject(ProjectFile project, string destinationPath)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        project.ModifiedAtUtc = DateTimeOffset.UtcNow;
        var fullPath = Path.GetFullPath(destinationPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(project, SerializerOptions);
        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, json);

        if (File.Exists(fullPath))
        {
            File.Replace(tempPath, fullPath, null);
        }
        else
        {
            File.Move(tempPath, fullPath);
        }
    }

    public static ProjectFile LoadProject(string projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        var fullPath = Path.GetFullPath(projectPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Project file was not found: {fullPath}", fullPath);
        }

        var json = File.ReadAllText(fullPath);
        var project = JsonSerializer.Deserialize<ProjectFile>(json, SerializerOptions)
            ?? throw new InvalidDataException("Project file is empty or invalid.");

        if (project.SchemaVersion is < 1 or > ProjectFile.CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Project schema version {project.SchemaVersion} is not supported. Expected 1..{ProjectFile.CurrentSchemaVersion}.");
        }
        // Older projects are upgraded lazily when positional pipeline arrays are restored.
        project.SchemaVersion = ProjectFile.CurrentSchemaVersion;

        return project;
    }

    public SourceVerificationResult VerifySource(ProjectFile project, string? candidatePath = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var path = candidatePath ?? project.Source.CanonicalPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return new SourceVerificationResult(
                SourceMatchStatus.Missing,
                "Project does not contain a source image path.");
        }

        var canonicalPath = SourceGuard.Canonicalize(path);
        if (!File.Exists(canonicalPath))
        {
            return new SourceVerificationResult(
                SourceMatchStatus.Missing,
                $"Source image was not found at '{canonicalPath}'.",
                canonicalPath);
        }

        // Check hash
        try
        {
            using var stream = File.OpenRead(canonicalPath);
            using var sha = SHA256.Create();
            var hashBytes = sha.ComputeHash(stream);
            var hash = Convert.ToHexString(hashBytes);

            if (string.Equals(hash, project.Source.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new SourceVerificationResult(
                    SourceMatchStatus.ExactMatch,
                    "Source image verified with exact SHA-256 match.",
                    canonicalPath);
            }

            // Probe dimensions to prevent binding an unrelated image
            var loaded = NativeImage.LoadFile(canonicalPath);
            using var loadedImage = loaded.Image;
            var meta = loadedImage.GetMetadata();
            if (meta.Width == project.Source.Width &&
                meta.Height == project.Source.Height &&
                meta.ChannelCount == project.Source.ChannelCount)
            {
                return new SourceVerificationResult(
                    SourceMatchStatus.CompatibleMatch,
                    "Source image matches dimensions and channel count, but file hash differs.",
                    canonicalPath);
            }

            return new SourceVerificationResult(
                SourceMatchStatus.Incompatible,
                $"Incompatible image: expected {project.Source.Width}x{project.Source.Height} ({project.Source.ChannelCount} ch), but found {meta.Width}x{meta.Height} ({meta.ChannelCount} ch). Unrelated images cannot be bound to this project.",
                canonicalPath);
        }
        catch (Exception ex)
        {
            return new SourceVerificationResult(
                SourceMatchStatus.Incompatible,
                $"Failed to verify source image: {ex.Message}",
                canonicalPath);
        }
    }

    public async Task<ImageDocument> OpenProjectDocumentAsync(
        ProjectFile project,
        string? locatedSourcePath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        var path = locatedSourcePath ?? project.Source.CanonicalPath;

        var verification = VerifySource(project, path);
        if (verification.Status == SourceMatchStatus.Missing)
        {
            throw new FileNotFoundException(verification.Message, path);
        }
        if (verification.Status == SourceMatchStatus.CompatibleMatch)
        {
            throw new InvalidOperationException(
                $"Source fingerprint mismatch. {verification.Message} " +
                "StarSim Core will not bind a different image silently; locate the exact original source file.");
        }
        if (verification.Status == SourceMatchStatus.Incompatible)
        {
            throw new InvalidOperationException(verification.Message);
        }

        var document = await imageOpenService.OpenAsync(path, cancellationToken);
        if (locatedSourcePath is not null)
        {
            project.Source.CanonicalPath = document.Source.CanonicalPath;
            project.Source.FileName = document.Source.FileName;
            project.Source.FileFormat = document.Source.FileFormat;
            project.Source.FileSize = document.Source.FileSize;
            project.Source.Sha256 = document.Source.Sha256;
            project.Source.LastWriteTimeUtc = document.Source.LastWriteTimeUtc;
        }

        return document;
    }

    public static PipelineSnapshot RestorePipelineSnapshot(
        ProjectFile project,
        IReadOnlyList<IImageProcessor>? processorDefinitions = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var definitions = processorDefinitions ?? BuiltInProcessors.All;

        // Build modules map from project
        var projectModules = project.Pipeline.Modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var availableIds = definitions.Select(definition => definition.Id).ToHashSet(StringComparer.Ordinal);
        var missingEnabled = project.Pipeline.Modules
            .Where(module => module.Enabled && !availableIds.Contains(module.Id))
            .Select(module => module.Id)
            .ToArray();
        if (missingEnabled.Length > 0)
        {
            throw new InvalidOperationException(
                "This project requires unavailable processor plugin(s): " +
                string.Join(", ", missingEnabled) +
                ". Install and enable the matching plugins, restart StarSim Core, then reopen the project.");
        }
        var modulesBuilder = ImmutableArray.CreateBuilder<ProcessorState>();

        foreach (var definition in definitions)
        {
            if (projectModules.TryGetValue(definition.Id, out var pm))
            {
                var sourceParameters = ProcessorParameterMigration.Upgrade(definition.Id, pm.Parameters);
                var paramsBuilder = ImmutableArray.CreateBuilder<double>();
                for (var i = 0; i < definition.ParameterSchema.Count; i++)
                {
                    var val = i < sourceParameters.Count
                        ? sourceParameters[i]
                        : definition.ParameterSchema[i].DefaultValue;
                    paramsBuilder.Add(Math.Clamp(val, definition.ParameterSchema[i].Minimum, definition.ParameterSchema[i].Maximum));
                }
                modulesBuilder.Add(new ProcessorState(definition.Id, pm.Enabled, paramsBuilder.ToImmutable()));
            }
            else
            {
                modulesBuilder.Add(new ProcessorState(
                    definition.Id,
                    definition.IsEnabledByDefault,
                    definition.ParameterSchema.Select(p => p.DefaultValue).ToImmutableArray()));
            }
        }

        return new PipelineSnapshot(modulesBuilder.ToImmutable(), project.Pipeline.Revision);
    }
}
