using StarSimCore.Application.Imaging;
using StarSimCore.Domain.Imaging;

namespace StarSimCore.Application.Presets;

public sealed record RgbBalanceResult(double Red, double Green, double Blue, string Method);

public static class AutoRgbBalanceService
{
    public static RgbBalanceResult Analyze(ImageDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Source.Metadata.ColorModel == ImageColorModel.Grayscale)
        {
            throw new InvalidOperationException("Auto RGB Balance is unavailable for grayscale images.");
        }

        var histogram = document.Master.ComputeHistogram(256, cancellationToken);
        var means = histogram.Mean;
        if (means.Take(3).Any(value => !double.IsFinite(value) || value <= 1e-8))
        {
            throw new InvalidOperationException("Auto RGB Balance requires non-empty finite RGB channel means.");
        }

        // Conservative compressed gray-world: only 35% of the full correction, capped at ±10%.
        var target = Math.Pow(means[0] * means[1] * means[2], 1.0 / 3.0);
        var gains = means.Take(3)
            .Select(mean => Math.Clamp(Math.Pow(target / mean, 0.35), 0.90, 1.10))
            .ToArray();
        var luminanceGain = 0.2126 * gains[0] + 0.7152 * gains[1] + 0.0722 * gains[2];
        for (var index = 0; index < gains.Length; index++)
        {
            gains[index] = Math.Clamp(gains[index] / luminanceGain, 0.90, 1.10);
        }

        return new RgbBalanceResult(
            gains[0],
            gains[1],
            gains[2],
            "Compressed gray-world (35% correction, ±10% cap, luminance-normalized)");
    }
}
