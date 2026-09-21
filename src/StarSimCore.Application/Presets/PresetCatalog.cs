using System.Text.Json;
using StarSimCore.Application.Processing;

namespace StarSimCore.Application.Presets;

public sealed class PresetCatalog
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    private readonly IReadOnlyDictionary<string, PresetRecipe> recipes;

    private PresetCatalog(IReadOnlyDictionary<string, PresetRecipe> recipes) => this.recipes = recipes;

    public IReadOnlyCollection<PresetRecipe> Recipes => recipes.Values.ToArray();

    public PresetRecipe Get(string targetId, string name)
    {
        var id = $"{targetId.ToLowerInvariant()}.{name.ToLowerInvariant()}";
        return recipes.TryGetValue(id, out var recipe)
            ? recipe
            : throw new KeyNotFoundException($"Preset '{id}' was not found.");
    }

    public static PresetCatalog LoadBuiltIns(string? root = null)
    {
        root ??= Path.Combine(AppContext.BaseDirectory, "presets", "builtin");
        return LoadDirectory(root);
    }

    public static PresetCatalog LoadDirectory(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"Preset directory was not found: {root}");
        var loaded = new Dictionary<string, PresetRecipe>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            PresetRecipe recipe;
            try
            {
                recipe = JsonSerializer.Deserialize<PresetRecipe>(File.ReadAllText(file), SerializerOptions)
                    ?? throw new InvalidDataException("Preset document is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException($"Preset JSON is invalid: {file}", exception);
            }
            Validate(recipe, file);
            if (!loaded.TryAdd(recipe.PresetId, recipe))
            {
                throw new InvalidDataException($"Duplicate preset ID '{recipe.PresetId}'.");
            }
        }
        if (loaded.Count == 0) throw new InvalidDataException("The preset directory contains no recipes.");
        return new PresetCatalog(loaded);
    }

    public static void Validate(PresetRecipe recipe, string source = "preset")
    {
        if (recipe.SchemaVersion != 1) throw new InvalidDataException($"{source}: unsupported schemaVersion.");
        if (string.IsNullOrWhiteSpace(recipe.PresetId) || recipe.PresetId != $"{recipe.TargetId}.{recipe.Name.ToLowerInvariant()}")
            throw new InvalidDataException($"{source}: presetId must match targetId and name.");
        if (!Version.TryParse(recipe.RecipeVersion, out _)) throw new InvalidDataException($"{source}: invalid recipeVersion.");
        if (string.IsNullOrWhiteSpace(recipe.Description)) throw new InvalidDataException($"{source}: description is required.");
        var known = BuiltInProcessors.All.ToDictionary(processor => processor.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var configuration in recipe.Processors)
        {
            if (!known.TryGetValue(configuration.Id, out var processor))
                throw new InvalidDataException($"{source}: unknown processor '{configuration.Id}'.");
            if (!seen.Add(configuration.Id)) throw new InvalidDataException($"{source}: duplicate processor '{configuration.Id}'.");
            foreach (var parameter in processor.ParameterSchema)
            {
                if (!configuration.Parameters.TryGetValue(parameter.Id, out var value) ||
                    !double.IsFinite(value) || value < parameter.Minimum || value > parameter.Maximum)
                    throw new InvalidDataException($"{source}: parameter '{configuration.Id}.{parameter.Id}' is missing or out of range.");
            }
            if (configuration.Parameters.Count != processor.ParameterSchema.Count)
                throw new InvalidDataException($"{source}: unexpected parameters for '{configuration.Id}'.");
        }
        if (!BuiltInProcessors.PresetCoreProcessorIds.All(seen.Contains))
            throw new InvalidDataException($"{source}: recipe must configure every core preset processor.");
        if (!recipe.Compatibility.RequiredProcessors.All(seen.Contains))
            throw new InvalidDataException($"{source}: required processor list is inconsistent.");
        var clamped = recipe.Macros.Clamp();
        if (clamped != recipe.Macros) throw new InvalidDataException($"{source}: macro default is out of range.");
        if (recipe.Macros.WaveletGains is { } gains &&
            (gains.Count != WaveletMacroMapper.LayerCount || gains.Any(gain => !double.IsFinite(gain) || gain < 0 || gain > 100)))
        {
            throw new InvalidDataException($"{source}: waveletGains must contain six finite values in the 0..100 range.");
        }
    }
}
