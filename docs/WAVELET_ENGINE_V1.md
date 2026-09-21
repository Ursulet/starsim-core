# StarSim Core — Wavelet Engine V1

## Architecture

`core.wavelet` is the single production six-scale engine. The Avalonia Classic/Advanced/Diagnostic views, projects, presets, history, cache keys, and native request all use the same canonical processor state. Samples and signed coefficients remain float32; clipping occurs only in display/integer-export paths.

## Analysis

For each channel independently:

`L0 = input`

`Li = GaussianBlur(L(i-1), width_i)`

`Di = L(i-1) - Li`, for `i = 1..6`

The Gaussian kernel is symmetric and normalized, is evaluated as mathematically equivalent separable horizontal/vertical passes, and uses reflected boundary indices. This is recursive: D2 is derived from L1, D3 from L2, and so forth.

Dyadic width derivation is `initialWidth * 2^layerIndex`. Linear derivation is `initialWidth + layerIndex * stepIncrement`. A positive per-layer Gaussian width overrides the derived value. The calibrated Gaussian default is Linear with `initialWidth=0.25` and `stepIncrement=0.05`, producing analysis sigmas `0.25,0.30,0.35,0.40,0.45,0.50` pixels. Dyadic remains available explicitly for much broader structures.

The optional `AtrousB3Spline` backend recursively filters with `[1,4,6,4,1]/16` and hole spacings `1,2,4,8,16,32`. It computes the same signed adjacent-low-pass differences and uses the same gain/reconstruction semantics. Gaussian width/scheme controls are analysis settings for the Recursive Gaussian backend only.

## Band processing and synthesis

Independent Denoise first applies:

`denoised(Di) = sign(Di) * max(0, abs(Di) - Denoise_i)`

Threshold then sets a remaining coefficient to zero only when `abs(coefficient) < Threshold_i`.

The explicit contribution is:

`band_i = LayerEnhancementFactor_i * processed(Di)`

Normal reconstruction is:

`output = L6 + GlobalStrength * sum(band_i)`

No intermediate or reconstructed value is clamped. At neutral values—six factors of 1, Denoise/Threshold zero, Global Strength 1—the Gaussian differences telescope to L0 within float32 tolerance. Solo preview returns `0.5 + GlobalStrength * band_i` for signed visualization.

## Controls

Classic Layer 1..6 each controls exactly one `LayerEnhancementFactor`. The centralized mapping is direct: the number displayed in Classic is the number stored and sent to native processing. Thus 0 removes the band, 1 is neutral, 52.7 means a 52.7 band multiplier, and 100 means 100. Classic does not write Gaussian Width, Denoise, Threshold, scheme, or global settings.

Advanced exposes the canonical factor, Gaussian construction-width override, Denoise, and Threshold independently. Factor semantics are: 0 removes the band, 0..1 reduces it, 1 is neutral, and values above 1 increase its contribution. Gaussian Width is not a second gain or post-sharpen pass.

Linked Wavelets retains its established API. Linked=false is the normal calibration path and uses each layer's own group. Linked=true continues to reuse Layer 1's group; no new linked algorithm is defined here.

## State compatibility

The current managed wavelet schema has 31 values and processor semantic version 4.0.0. The unchanged expert C ABI payload has capacity for 32 values. Version 4 corrects the Classic presentation conversion and calibrated defaults; canonical parameters remain explicit enhancement factors, so no project-format migration is required.

Legacy 28-value projects/presets migrate their old effective multiplier `Amount * (1 + Sharpen)` into `LayerEnhancementFactor`, retain compatible values, and append Dyadic/step-1/Recursive-Gaussian defaults. Previous 30-value state appends Recursive Gaussian. The native entry point accepts 28/30 for compatibility.

## Calibration and artifacts

Layer 1 is intended for the finest useful planetary detail; Layers 2–3 progressively cover larger fine/medium structures; Layers 4–6 cover progressively broader structures. These are overlapping bands, not disjoint frequency bins. See `WAVELET_CALIBRATION.md` for spatial tables, Expert diagnostic exports, measurements, and the same-source RegiStax procedure.

Excessive factors can amplify noise in fine layers and introduce edge overshoot around hard limbs in broader layers. Denoise/Threshold, Deringing, and other processors remain explicit independent choices. The UI's compound-sharpening warning is based on factors above neutral when another sharpening/restoration module is active.
