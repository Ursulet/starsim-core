using System.Collections.Immutable;
using System.Globalization;
using StarSimCore.Domain.Imaging;
using StarSimCore.Interop;

namespace StarSimCore.Application.Processing;

public static class BuiltInProcessors
{
    public const string ExposureId = "core.linear-exposure";
    public const string ContrastId = "core.contrast";
    public const string GammaId = "core.gamma";
    public const string RgbBalanceId = "core.rgb-balance";
    public const string SaturationId = "core.saturation";
    public const string WaveletId = "core.wavelet";
    public const string UnsharpMaskId = "core.unsharp-mask";
    public const string MultiScaleSharpenId = "core.multi-scale-sharpen";
    public const string NoiseReductionId = "core.noise-reduction";
    public const string DeringingId = "core.deringing";
    public const string RichardsonLucyId = "core.richardson-lucy";
    public const string RgbAlignId = "core.rgb-align";
    public const string AdvancedColorId = "core.advanced-color";
    public const string AdvancedToneId = "core.advanced-tone";
    public const string LocalDetailId = "core.local-detail";

    public static class Categories
    {
        public const string Alignment = "Alignment";
        public const string Restoration = "Restoration";
        public const string Detail = "Detail";
        public const string AdvancedSharpening = "Advanced Sharpening";
        public const string Color = "Color";
        public const string Tone = "Tone";

        public static readonly IReadOnlyList<string> Ordered =
        [
            Alignment,
            Restoration,
            Detail,
            AdvancedSharpening,
            Color,
            Tone
        ];
    }

    public static IReadOnlySet<string> PresetCoreProcessorIds { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ExposureId, ContrastId, GammaId, RgbBalanceId, SaturationId,
    };

    public static IReadOnlyList<IImageProcessor> All { get; } =
    [
        // Canonical production order: alignment -> restoration -> detail -> color -> tone.
        new ExpertNativeProcessor(
            RgbAlignId, "RGB Align", Categories.Alignment, NativeProcessorKind.RgbAlign,
            [new("auto", "Auto", 0, 1, 0), new("redX", "Red X", -10, 10, 0), new("redY", "Red Y", -10, 10, 0), new("greenX", "Green X", -10, 10, 0), new("greenY", "Green Y", -10, 10, 0), new("blueX", "Blue X", -10, 10, 0), new("blueY", "Blue Y", -10, 10, 0), new("search", "Search Radius", 1, 12, 4)],
            colorOnly: true, isEnabledByDefault: false, semanticVersion: new Version(2, 0, 0)),

        new ExpertNativeProcessor(
            NoiseReductionId, "Noise Reduction", Categories.Restoration, NativeProcessorKind.NoiseReduction,
            [new("luminance", "Luminance", 0, 1, 0), new("chrominance", "Chrominance", 0, 1, 0), new("detailProtection", "Fine Detail Protection", 0, 1, 0.7), new("threshold", "Threshold", 0.1, 5, 1)],
            isEnabledByDefault: false, semanticVersion: new Version(2, 0, 0)),
        new ExpertNativeProcessor(
            RichardsonLucyId, "Richardson-Lucy", Categories.Restoration, NativeProcessorKind.RichardsonLucy,
            [new("iterations", "Iterations", 1, 50, 1), new("psfRadius", "PSF Radius", 0.3, 5, 1), new("strength", "Strength", 0, 1, 0), new("damping", "Damping", 0, 1, 0.001)],
            previewHint: PreviewQualityHint.Debounced, isEnabledByDefault: false, semanticVersion: new Version(2, 0, 0)),

        new ExpertNativeProcessor(
            WaveletId, "6-Scale Wavelet", Categories.Detail, NativeProcessorKind.Wavelet,
            CreateWaveletSchema(),
            isEnabledByDefault: true,
            semanticVersion: new Version(4, 0, 0)),
        new ExpertNativeProcessor(
            UnsharpMaskId, "Unsharp Mask", Categories.AdvancedSharpening, NativeProcessorKind.UnsharpMask,
            [new("amount", "Amount", 0, 2, 0), new("radius", "Radius", 0.3, 10, 1), new("threshold", "Threshold", 0, 0.5, 0)],
            isEnabledByDefault: false, semanticVersion: new Version(2, 0, 0)),
        new ExpertNativeProcessor(
            MultiScaleSharpenId, "Multi-scale Sharpen", Categories.AdvancedSharpening, NativeProcessorKind.MultiScaleSharpen,
            [new("fineAmount", "Fine Amount", 0, 2, 0), new("fineRadius", "Fine Radius", 0.3, 3, 0.8), new("broadAmount", "Broad Amount", 0, 2, 0), new("broadRadius", "Broad Radius", 1, 12, 3), new("threshold", "Threshold", 0, 0.5, 0)],
            isEnabledByDefault: false, semanticVersion: new Version(2, 0, 0)),
        new ExpertNativeProcessor(
            DeringingId, "Deringing / Halo Protection", Categories.Restoration, NativeProcessorKind.Deringing,
            [new("strength", "Strength", 0, 1, 0), new("radius", "Radius", 1, 8, 2), new("edgeProtection", "Edge Protection", 0, 1, 0.7)],
            isEnabledByDefault: false, semanticVersion: new Version(2, 0, 0)),
        new ExpertNativeProcessor(
            LocalDetailId, "Local Detail", Categories.Detail, NativeProcessorKind.LocalDetail,
            [new("localAmount", "Local Contrast", 0, 2, 0), new("localRadius", "Local Radius", 1, 32, 8), new("microAmount", "Microcontrast", 0, 2, 0), new("microRadius", "Micro Radius", 0.3, 4, 1), new("edgeProtection", "Edge Protection", 0, 1, 0.8)],
            isEnabledByDefault: false),

        // Color and tone are late display operations. Changing them reuses every
        // expensive upstream stage rather than rebuilding from Source.
        new ExpertNativeProcessor(
            AdvancedColorId, "Advanced Color", Categories.Color, NativeProcessorKind.AdvancedColor,
            [new("whiteRed", "White Balance R", 0, 2, 1), new("whiteGreen", "White Balance G", 0, 2, 1), new("whiteBlue", "White Balance B", 0, 2, 1), new("temperature", "Temperature", -1, 1, 0), new("tint", "Tint", -1, 1, 0), new("vibrance", "Vibrance", -1, 1, 0)],
            colorOnly: true, isEnabledByDefault: true),
        new NativeProcessor(
            RgbBalanceId, "RGB Channel Balance", Categories.Color, NativeProcessorKind.RgbBalance,
            [new("red", "Red", 0, 2, 1), new("green", "Green", 0, 2, 1), new("blue", "Blue", 0, 2, 1)],
            colorOnly: true, isEnabledByDefault: true),
        new NativeProcessor(
            SaturationId, "Saturation", Categories.Color, NativeProcessorKind.Saturation,
            [new("amount", "Saturation", 0, 2, 1)],
            colorOnly: true, isEnabledByDefault: true),

        new ExpertNativeProcessor(
            AdvancedToneId, "Advanced Tone", Categories.Tone, NativeProcessorKind.AdvancedTone,
            [new("brightness", "Brightness", -1, 1, 0), new("blackPoint", "Black Point", 0, 0.49, 0), new("whitePoint", "White Point", 0.51, 2, 1), new("highlights", "Highlights", -1, 1, 0), new("shadows", "Shadows", -1, 1, 0)],
            isEnabledByDefault: true),
        new NativeProcessor(
            ExposureId, "Brightness / Exposure", Categories.Tone, NativeProcessorKind.LinearExposure,
            [new("stops", "Exposure", -4, 4, 0)],
            isEnabledByDefault: true),
        new NativeProcessor(
            ContrastId, "Contrast", Categories.Tone, NativeProcessorKind.Contrast,
            [new("factor", "Contrast", 0, 3, 1)],
            isEnabledByDefault: true),
        new NativeProcessor(
            GammaId, "Gamma", Categories.Tone, NativeProcessorKind.Gamma,
            [new("gamma", "Gamma", 0.1, 4, 1)],
            isEnabledByDefault: true,
            semanticVersion: new Version(2, 0, 0)),
    ];

    private static IReadOnlyList<ProcessorParameterSchema> CreateWaveletSchema()
    {
        var result = new List<ProcessorParameterSchema>
        {
            new("globalStrength", "Global Strength", 0, 2, 1),
            new("initialLayerWidth", "Initial Gaussian Width", 0.15, 8, 0.25),
            new("linked", "Linked Wavelets", 0, 1, 0),
        };
        var layers = new[] { "ultraFine", "fine", "small", "medium", "large", "structure" };
        foreach (var layer in layers)
        {
            result.Add(new($"{layer}LayerEnhancementFactor", $"{layer} Layer Enhancement Factor", 0, 100, 1));
            result.Add(new($"{layer}GaussianWidth", $"{layer} Gaussian Width Override", 0, 64, 0));
            result.Add(new($"{layer}Denoise", $"{layer} Denoise", 0, 1, 0));
            result.Add(new($"{layer}Threshold", $"{layer} Threshold", 0, 0.2, 0));
        }
        result.Add(new("soloLayer", "Solo Layer", -1, 5, -1));
        result.Add(new("scaleScheme", "Scale Scheme (Linear/Dyadic)", 0, 1, (double)WaveletScaleScheme.Linear));
        result.Add(new("stepIncrement", "Linear Step Increment", 0, 16, 0.05));
        result.Add(new("decompositionBackend", "Decomposition Backend (Gaussian/B3 Spline)", 0, 1, (double)WaveletDecompositionBackend.RecursiveGaussian));
        return result;
    }

    public static PipelineSnapshot CreateDefaultSnapshot() =>
        new(
            All.Select(
                    processor => new ProcessorState(
                        processor.Id,
                        processor.IsEnabledByDefault,
                        processor.ParameterSchema.Select(parameter => parameter.DefaultValue).ToImmutableArray()))
                .ToImmutableArray(),
            Revision: 0);

    /// <summary>
    /// Creates the canonical untouched-image state. Parameters retain their documented
    /// defaults, but every processor is bypassed so opening/resetting an image is a
    /// byte-for-byte source view rather than a trip through nominally neutral stages.
    /// </summary>
    public static PipelineSnapshot CreateNeutralBypassSnapshot() =>
        new(
            All.Select(
                    processor => new ProcessorState(
                        processor.Id,
                        false,
                        processor.ParameterSchema.Select(parameter => parameter.DefaultValue).ToImmutableArray()))
                .ToImmutableArray(),
            Revision: 0);

    public static PipelineSnapshot ResetModule(PipelineSnapshot snapshot, string id)
    {
        var definition = All.First(processor => processor.Id == id);
        var modules = snapshot.Modules.ToBuilder();
        var index = -1;
        for (var candidate = 0; candidate < snapshot.Modules.Length; candidate++)
        {
            if (snapshot.Modules[candidate].Id == id)
            {
                index = candidate;
                break;
            }
        }
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(id));
        modules[index] = new ProcessorState(
            id,
            definition.IsEnabledByDefault,
            definition.ParameterSchema.Select(parameter => parameter.DefaultValue).ToImmutableArray());
        return new PipelineSnapshot(modules.ToImmutable(), snapshot.Revision + 1);
    }

    public static PipelineSnapshot ResetCategory(PipelineSnapshot snapshot, string category)
    {
        var categoryProcessors = All.Where(p => string.Equals(p.Category, category, StringComparison.OrdinalIgnoreCase)).ToList();
        if (categoryProcessors.Count == 0) return snapshot;

        var builder = snapshot.Modules.ToBuilder();
        foreach (var definition in categoryProcessors)
        {
            var index = -1;
            for (var candidate = 0; candidate < builder.Count; candidate++)
            {
                if (builder[candidate].Id == definition.Id)
                {
                    index = candidate;
                    break;
                }
            }
            if (index >= 0)
            {
                builder[index] = new ProcessorState(
                    definition.Id,
                    definition.IsEnabledByDefault,
                    definition.ParameterSchema.Select(parameter => parameter.DefaultValue).ToImmutableArray());
            }
        }
        return new PipelineSnapshot(builder.ToImmutable(), snapshot.Revision + 1);
    }

    private sealed class NativeProcessor(
        string id,
        string name,
        string category,
        NativeProcessorKind kind,
        IReadOnlyList<ProcessorParameterSchema> schema,
        bool colorOnly = false,
        bool isEnabledByDefault = true,
        Version? semanticVersion = null) : IImageProcessor
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Category { get; } = category;
        public Version SemanticVersion { get; } = semanticVersion ?? new(1, 0, 0);
        public IReadOnlyList<ProcessorParameterSchema> ParameterSchema { get; } = schema;
        public bool IsEnabledByDefault { get; } = isEnabledByDefault;
        public ProcessorCapabilities Capabilities =>
            (colorOnly ? ProcessorCapabilities.Rgb : ProcessorCapabilities.Grayscale | ProcessorCapabilities.Rgb) |
            ProcessorCapabilities.InteractivePreview |
            ProcessorCapabilities.FullResolution;
        public PreviewQualityHint PreviewQualityHint => PreviewQualityHint.Immediate;
        public TilingCapability TilingCapability => TilingCapability.IndependentTiles;

        public void Validate(ProcessorState state, ImageMetadata metadata)
        {
            if (state.Id != Id || state.Parameters.Length != ParameterSchema.Count)
            {
                throw new ArgumentException($"State does not match processor {Id}.", nameof(state));
            }
            for (var index = 0; index < ParameterSchema.Count; index++)
            {
                var parameter = ParameterSchema[index];
                var value = state.Parameters[index];
                if (!double.IsFinite(value) || value < parameter.Minimum || value > parameter.Maximum)
                {
                    throw new ArgumentOutOfRangeException(parameter.Id, value, $"{parameter.DisplayName} is outside its valid range.");
                }
            }
            if (colorOnly && metadata.ColorModel == ImageColorModel.Grayscale)
            {
                return;
            }
        }

        public NativeImage Process(
            NativeImage input,
            ProcessorState state,
            ProcessorContext context,
            CancellationToken cancellationToken)
        {
            Validate(state, context.Metadata);
            var values = state.Parameters.AddRange(Enumerable.Repeat(0D, 4 - state.Parameters.Length));
            return input.Process(
                new NativeProcessorRequest(
                    kind,
                    state.Enabled,
                    context.Quality == ProcessingQuality.InteractivePreview
                        ? NativeProcessingQuality.InteractivePreview
                        : NativeProcessingQuality.FullResolution,
                    context.ResolutionScale,
                    (float)values[0],
                    (float)values[1],
                    (float)values[2],
                    (float)values[3]),
                cancellationToken,
                CreateDiagnosticMetadata(Id, Name, ParameterSchema, state, context, "ssc_image_process"));
        }
    }

    private sealed class ExpertNativeProcessor(
        string id,
        string name,
        string category,
        NativeProcessorKind kind,
        IReadOnlyList<ProcessorParameterSchema> schema,
        bool colorOnly = false,
        PreviewQualityHint previewHint = PreviewQualityHint.Debounced,
        bool isEnabledByDefault = true,
        Version? semanticVersion = null) : IImageProcessor
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public string Category { get; } = category;
        public Version SemanticVersion { get; } = semanticVersion ?? new(1, 0, 0);
        public IReadOnlyList<ProcessorParameterSchema> ParameterSchema { get; } = schema;
        public bool IsEnabledByDefault { get; } = isEnabledByDefault;
        public ProcessorCapabilities Capabilities =>
            (colorOnly ? ProcessorCapabilities.Rgb : ProcessorCapabilities.Grayscale | ProcessorCapabilities.Rgb) |
            ProcessorCapabilities.InteractivePreview |
            ProcessorCapabilities.FullResolution;
        public PreviewQualityHint PreviewQualityHint { get; } = previewHint;
        public TilingCapability TilingCapability => kind switch
        {
            NativeProcessorKind.AdvancedColor or NativeProcessorKind.AdvancedTone =>
                TilingCapability.IndependentTiles,
            NativeProcessorKind.RgbAlign => TilingCapability.NotSupported,
            _ => TilingCapability.RequiresHalo,
        };

        public void Validate(ProcessorState state, ImageMetadata metadata)
        {
            if (state.Id != Id || state.Parameters.Length != ParameterSchema.Count)
                throw new ArgumentException($"State does not match processor {Id}.", nameof(state));
            for (var index = 0; index < ParameterSchema.Count; index++)
            {
                var parameter = ParameterSchema[index];
                var value = state.Parameters[index];
                if (!double.IsFinite(value) || value < parameter.Minimum || value > parameter.Maximum)
                    throw new ArgumentOutOfRangeException(parameter.Id, value, $"{parameter.DisplayName} is outside its valid range.");
            }
            if (Id == AdvancedToneId && state.Parameters[2] <= state.Parameters[1])
                throw new ArgumentException("White Point must exceed Black Point.", nameof(state));
        }

        public NativeImage Process(
            NativeImage input,
            ProcessorState state,
            ProcessorContext context,
            CancellationToken cancellationToken)
        {
            Validate(state, context.Metadata);
            return input.ProcessExpert(
                new NativeExpertProcessorRequest(
                    kind,
                    state.Enabled,
                    context.Quality == ProcessingQuality.InteractivePreview
                        ? NativeProcessingQuality.InteractivePreview
                        : NativeProcessingQuality.FullResolution,
                    context.ResolutionScale,
                    state.Parameters.Select(value => (float)value).ToArray()),
                cancellationToken,
                CreateDiagnosticMetadata(Id, Name, ParameterSchema, state, context, "ssc_image_process_expert_with_progress"),
                progress => context.ReportProgress?.Invoke(new ProcessorProgress(
                    Id,
                    Name,
                    progress.Stage switch
                    {
                        NativeProgressStage.WaveletLayer => "Wavelet layer",
                        NativeProgressStage.RichardsonLucyIteration => "Richardson-Lucy iteration",
                        _ => Name,
                    },
                    progress.CurrentStep,
                    progress.TotalSteps,
                    progress.Fraction)));
        }
    }

    private static NativeOperationMetadata CreateDiagnosticMetadata(
        string id,
        string name,
        IReadOnlyList<ProcessorParameterSchema> schema,
        ProcessorState state,
        ProcessorContext context,
        string operation) =>
        new(
            operation,
            Module: id,
            Algorithm: name,
            Parameters: string.Join(';', state.Parameters.Select((value, index) =>
                $"{schema[index].Id}={value.ToString("R", CultureInfo.InvariantCulture)}")),
            ImageWidth: context.Metadata.Width,
            ImageHeight: context.Metadata.Height);
}
