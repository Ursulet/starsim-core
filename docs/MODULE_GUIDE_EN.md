# Non-Wavelet module guide

Updated: 2026-09-22

This document describes the native functions, parameters and practical use of every processing module except Wavelet. The calibrated Wavelet engine is intentionally unchanged. Exact formulas and canonical stage order are defined in [PROCESSING_ENGINE_MATH.md](PROCESSING_ENGINE_MATH.md).

## General rules

- Processing is planar `float32`; values are not automatically clipped to `[0,1]`. The viewer and integer export perform final conversion.
- A disabled module is an exact bypass. Unsharp Mask, Multi-scale Sharpen, Noise Reduction, Richardson–Lucy, Deringing and Local Detail can be enabled yet neutral because their primary amount defaults to zero.
- Judge detail at 100% zoom. A scaled preview can hide grain, ringing and double edges.
- Prefer one primary sharpening method. Strong Wavelet, Unsharp, Multi-scale and Richardson–Lucy settings do not combine into more real detail.
- Stage order is RGB Align → Noise Reduction → Richardson–Lucy → Wavelet → Unsharp → Multi-scale → Deringing → Local Detail → Color → Tone.

## Module quick reference

| Module | Native function | Conservative starting point | Main risk |
|---|---|---|---|
| RGB Align | FFT phase correlation + subpixel Lanczos-3 shift | Auto, Search 2–6 | False match on noisy/ambiguous structure |
| Noise Reduction | Three-band Gaussian shrinkage with MAD noise estimate | Luma 0.10–0.30; Chroma 0.20–0.50; Protection 0.65–0.85 | Smoothed fine texture |
| Richardson–Lucy | Iterative Gaussian-PSF deconvolution | 3–8 iterations; PSF 0.7–1.5; Strength 0.10–0.35 | Grain and ringing |
| Unsharp Mask | Soft-threshold Gaussian high-pass | Amount 0.05–0.35; Radius 0.5–1.5 | Limb halos |
| Multi-scale Sharpen | Fine and broad complementary Gaussian bands | Fine 0.05–0.30; Broad 0.02–0.15 | Artificial local contrast |
| Deringing | Luminance-guided local-envelope correction | Strength 0.10–0.35; Radius 1.5–3.5; Protection 0.60–0.85 | Flattened legitimate extrema |
| Local Detail | Protected local + micro Gaussian differences | Local 0.05–0.25; Micro 0.02–0.15 | Mottled background, harsh limb |

Starting points are intentionally conservative and are not universal presets.

## Sharpening details

Unsharp Mask uses:

`Output = Input + Amount · soft(Input - Gaussian(Input, Radius), Threshold)`

Threshold suppresses weak differences and therefore reduces noise amplification. It does not prevent high Amount or Radius values from producing halos.

Multi-scale Sharpen forms two complementary bands:

`Dfine = Input - Gaussian(Input, FineRadius)`

`Dbroad = Gaussian(Input, FineRadius) - Gaussian(Input, BroadRadius)`

Broad Radius is the effective target Gaussian scale. The engine keeps it at least `0.15` above Fine Radius when an old or custom preset supplies reversed scales. Use Multi-scale as the main sharpener or only very lightly after Wavelet.

## Noise Reduction

Luminance controls brightness grain. Chrominance controls colored speckle and is reconstructed with zero residual Rec.709 luminance, so it cannot silently change brightness detail. Fine Detail Protection retains strong coefficients, while Threshold scales the robust noise estimate. Both strength controls at zero are exact identity.

## Richardson–Lucy

Iterations control restoration depth, PSF Radius approximates the blur kernel, Strength blends the estimate with the source, and Damping reduces each iterative update. Damping is a stability control rather than a separate anti-halo algorithm. Stop when granular artifacts, doubled edges or ringing appear.

## Deringing / Halo Protection

The module limits local overshoot against a neighbor envelope. RGB decisions are made from Rec.709 luminance and the same additive correction is applied to R, G and B, preserving channel differences and avoiding independently generated colored edges. Fractional Radius values interpolate between adjacent integer envelopes.

Strength sets the correction blend. Radius approximates artifact width. Higher Edge Protection widens the allowed envelope and therefore preserves more contrast while applying less correction. This is a corrective local-extrema method; it cannot semantically identify every broad halo and it does not create detail.

## Local Detail

Local and Micro amounts add separate Gaussian differences. Edge Protection observes the stronger response of both bands, so fine edges are protected as well as broad transitions. Use it for belts, cloud structure and lunar relief, but watch the background and planetary limb at 100%.

## Color and tone

Advanced Color owns white-balance gains, temperature, tint and adaptive vibrance. RGB Balance directly multiplies channels; neutral is `1`. Saturation scales channel distance from Rec.709 luminance; neutral is `1`. Correct balance before increasing saturation.

Advanced Tone normalizes Black/White Point, adds Brightness and shapes Shadows/Highlights. Its weights are bounded to the visible range even when valid float samples lie below black or above white. Exposure is `Input·2^Stops`; Contrast is `0.5+Factor·(Input-0.5)`; Gamma is the signed curve `sign(Input)·|Input|^(1/Gamma)`.

A practical order is Black/White Point → overall Exposure → midtone Gamma → small final Contrast. Monitor the histogram and exported result.

## Scope of the guarantee

Every module above has a real native implementation and direct UI-to-pipeline parameter mapping. This does not imply that every setting is visually useful or that a local mathematical operator can recognize every real-world artifact. Final quality still requires validation on representative planetary images.
