# Processing Math V1

All processing operates on planar `float32` samples. Except for Richardson–Lucy's explicitly non-negative estimate and Deringing's defined local envelope, processor output is not clipped to `[0, 1]`; clipping occurs only at viewer/integer-export conversion. Disabled modules copy their input unchanged. The current consolidated specification is [PROCESSING_ENGINE_MATH.md](PROCESSING_ENGINE_MATH.md).

Convolutions and RGB resampling use reflected boundaries, never wraparound. Gaussian filters are normalized and separable; cancellation is polled during long operations.

## Basic tone and color

- Linear Exposure: `y = x * 2^stops`.
- Contrast: `y = (x - 0.5) * factor + 0.5`.
- Gamma: `y = sign(x) * abs(x)^(1/gamma)`.
- RGB Balance: each channel is multiplied by its own gain. It is a no-op for grayscale.
- Saturation: with Rec.709 luminance `L = 0.2126R + 0.7152G + 0.0722B`, each channel becomes `L + amount * (C - L)`. It is a no-op for grayscale.

Neutral values are respectively `0`, `1`, `1`, `(1,1,1)`, and `1`.

## Six-scale Wavelets

The existing processor supports recursive Gaussian and recursive à-trous B3-spline analysis. Gaussian starts with `L0 = input`, then `Li = GaussianBlur(L(i-1), width_i)` and `Di = L(i-1) - Li`. À-trous uses `[1,4,6,4,1]/16` with hole spacings `1,2,4,8,16,32`, then the same signed difference and reconstruction formulas.

Dyadic mode derives widths `initialWidth * 2^layerIndex`. Linear mode uses `initialWidth + layerIndex * stepIncrement`. The calibrated default is Linear at `0.25 + layerIndex * 0.05` pixels; Dyadic is an explicit broad-scale option. A positive per-layer Gaussian width overrides the derived width.

Each of Ultra Fine, Fine, Small, Medium, Large, and Structure has:

- Layer Enhancement Factor: the direct contribution of that band; 0 removes it and 1 is neutral.
- Gaussian Width: optional construction-width override, not a post-sharpen multiplier.
- Denoise: independent soft shrinkage `sign(d) * max(0, abs(d) - denoise)`.
- Threshold: independently removes the remaining coefficient when its magnitude is below Threshold.

Global Strength multiplies reconstructed detail. Linked Wavelets retains its existing flag/API; with Linked off every layer reads its independent group. Solo Layer displays `0.5 + selected detail`; `-1` restores normal reconstruction. With all factors and global strength at 1 and Denoise/Threshold at 0, `L6 + sum(Di)` telescopes to the input within float32 tolerance. Intermediate and final wavelet values remain unclipped.

Classic displays and writes the actual canonical factor: 0 removes a band and 1 is neutral. This fixes the former conversion that displayed values such as 52.7 while sending only 1.527 to the engine. Similar displayed values are still not a promise that every independent advanced setting is numerically equivalent to RegiStax.

## Sharpening

Unsharp Mask uses a separable Gaussian and adds `amount * sign(d) * max(abs(d)-threshold,0)`, where `d=source-blur`.

Multi-scale Sharpen has independent fine and broad bands:

`D1 = source - fineBlur`

`D2 = fineBlur - broadBlur`

It adds `fineAmount*soft(D1) + broadAmount*soft(D2)`. Zero amounts are exact neutral.

## Noise reduction

Noise Reduction uses three recursive Gaussian detail bands. Noise sigma is `median(abs(W1-median(W1)))/0.67448975`; every band uses `T=Strength*ThresholdScale*sigmaNoise` for soft shrinkage. RGB Rec.709 luminance and signed chroma components use independent strengths. Fine Detail Protection reduces shrinkage for coefficients above the estimated noise. Zero strengths are exact neutral.

## Deringing and halo protection

The processor uses a documented self-reference fallback. For each pixel, a circular reflected neighborhood supplies `min/max`; the allowed extension is `EdgeProtection*(max-min)`. The center is clamped to the extended envelope and blended by Strength. It has no implicit access to another stage.

## Richardson–Lucy deconvolution

V1 uses a normalized separable Gaussian PSF with the selected radius. Starting from `estimate = max(0, source)`, each iteration computes:

`candidate = max(0, estimate * PSF(max(source,0) / (PSF(estimate) + epsilon)))`

`estimateNext = lerp(estimate, candidate, 1-damping)`

Iterations are limited to `1..50`; `epsilon=1e-6` protects division. Strength blends the final estimate with the original. Deringing is a separate downstream processor. Strength zero is exact bypass.

## RGB alignment

Green is the fixed reference channel. Auto uses zero-padded 2-D FFT phase correlation and parabolic subpixel peak refinement. Manual offsets are added to the automatic Red/Blue results. Every channel uses the same normalized separable Lanczos-3 resampler with reflected boundaries. RGB Align is disabled for grayscale.

## Advanced color

White Balance multiplies R/G/B independently. Temperature and Tint derive additional bounded exponential gains:

- `R *= 2^(0.25*temperature - 0.075*tint)`
- `G *= 2^(0.15*tint)`
- `B *= 2^(-0.25*temperature - 0.075*tint)`

Vibrance uses `adaptive = 1 + vibrance * (1 - saturation)` and scales channel distance from Rec.709 luminance, affecting less-saturated pixels more. Combined with the basic RGB Balance and Saturation modules, this provides the required R/G/B, white balance, temperature, tint, saturation, and vibrance controls. All advanced color operations are no-ops for mono data.

## Advanced tone

Advanced Tone applies before the independent Exposure, Contrast, and Gamma stages:

`n = (x - blackPoint) / (whitePoint - blackPoint) + brightness`

`y = n + 0.25*shadows*(1-n)^2 + 0.25*highlights*n^2`

White Point must be greater than Black Point. The curve deliberately does not clamp its result, so out-of-range float detail remains available to later processors.

## Local detail

Local Contrast uses `source - Gaussian(source, localRadius)` and Microcontrast uses the same difference at `microRadius`. Their weighted sum is attenuated by the edge gate:

`gate = 1 / (1 + edgeProtection * abs(localDetail) * 20)`

`y = source + gate * (localAmount*localDetail + microAmount*microDetail)`

Zero amounts are neutral defaults. The gate suppresses strong-edge amplification to reduce halos.

## Parameter state and UI

All 15 modules appear in the ordered Expert pipeline. Each has a real enable/bypass control, and all 10 advanced modules expose their registered values through a slider and editable numeric field. Module Reset restores its schema defaults. Wavelet Linked and Solo are ordinary parameters, so presets, Undo/Redo, Beginner-to-Expert switching, cache keys, and native processing all observe the same values.
