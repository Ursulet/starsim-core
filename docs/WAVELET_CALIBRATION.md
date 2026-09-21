# Wavelet Calibration Report

## Scope and current status

StarSim Core exposes a controlled Expert-only calibration surface around its existing production `core.wavelet` processor. The implementation does not copy RegiStax code or create a duplicate processing engine.

A same-source RegiStax combined screen reference was supplied on 2026-09-21 for `23_36_32_lapl4_ap5.tif`. It was sufficient to expose and correct a production defect in Classic gain presentation and to calibrate a useful Gaussian starting profile. Full-resolution RegiStax Layer 1-only through Layer 6-only exports are still required before claiming that every isolated spatial band is finally matched.

## Production decompositions

For each active channel, in float32:

`L0 = source`

`Li = GaussianBlur(L(i-1), width_i)`

`Di = L(i-1) - Li`

`output = L6 + globalStrength * sum(factor_i * processed(Di))`

Gaussian kernels are normalized to sum to one, symmetric and separable. The second backend is recursive à-trous B3-spline with `[1,4,6,4,1]/16` and spacings `1,2,4,8,16,32`; it computes `Ci=Filter(C(i-1))` and `Di=C(i-1)-Ci`. Both backends use reflected boundaries and the same reconstruction/gain controls. No intermediate is clipped. With factors and global strength at 1 and Denoise/Threshold at zero, the sum telescopes to the source within float32 tolerance.

`LayerEnhancementFactor` is the only band-contribution control: 0 removes a band, 0..1 reduces it, 1 is neutral, and values above 1 boost it. Advanced Gaussian width controls construction of that layer; Denoise and Threshold remain independent post-analysis controls. A Classic slider writes only its own factor. Classic now displays that canonical factor directly; the former conversion incorrectly turned a displayed `52.7` into an engine factor of only `1.527`.

## Calibrated default Gaussian spatial-scale map

The starting widths are Gaussian sigma values in pixels. The calibrated default is Linear with `initialWidth=0.25` and `stepIncrement=0.05`. Because the filters are recursive, the cumulative low-pass sigma after a layer is approximately the root-sum-square of all widths through that layer. Structure diameters are calibration guides, not hard band boundaries; adjacent Gaussian-difference bands overlap. Dyadic remains available when a user deliberately needs much broader bands.

| Layer | Sigma (px) | Kernel radius (px) | Cumulative sigma (px) | Approx. structure diameter (px) | Structures to inspect |
|---:|---:|---:|---:|---:|---|
| 1 | 0.25 | 1 | 0.250 | 0.500 | Finest sampled residual; strongly noise-sensitive and intended for restrained use. |
| 2 | 0.30 | 1 | 0.391 | 0.781 | Fine pixel-to-pixel and narrow limb/belt detail. |
| 3 | 0.35 | 2 | 0.524 | 1.049 | Fine planetary texture and narrow edge structure. |
| 4 | 0.40 | 2 | 0.660 | 1.319 | Slightly broader fine structure. |
| 5 | 0.45 | 2 | 0.798 | 1.597 | Fine-to-small structure accumulated through the recursive chain. |
| 6 | 0.50 | 2 | 0.942 | 1.884 | Broadest band in the calibrated Linear starting profile. Use Dyadic for regional/global scales. |

The production convolution bounds kernel radius at 64 pixels; all sampled weights are renormalized. A positive per-layer width override replaces the derived sigma. Linear mode uses `initialWidth + layerIndex * stepIncrement`; Dyadic uses `initialWidth * 2^layerIndex`.

For à-trous, the nominal analysis spacings are 1, 2, 4, 8, 16, and 32 pixels; the finite B3 kernel support radii are 2, 4, 8, 16, 32, and 64 pixels. The B3 base kernel has sigma 1 at spacing 1, so the cumulative equivalent scales are approximately 1.00, 2.24, 4.58, 9.22, 18.47, and 36.95 pixels. Gaussian width overrides and the Linear/Dyadic selector apply only to the Recursive Gaussian backend.

The calibration target remains ordered spatial responsibility: Layer 1 contains the finest useful planetary detail, Layers 2–3 progressively larger detail, and Layers 4–6 progressively broader structure. The supplied combined reference establishes that Classic gain is now effective; isolated layer references are still needed to decide whether the Linear starting profile should be broadened further without confusing gain strength with band placement.

### Same-source Saturn defect reproduction and correction

The supplied comparison used the exact 440×440 16-bit RGB TIFF `23_36_32_lapl4_ap5.tif`. The visible RegiStax state was Gaussian/Linear, Denoise `0`, per-layer Sharpen/width `0.100`, and layer values `52.7, 24.5, 46.6, 23.9, 46.0, 25.1`. The production StarSim state was reconstructed with only `core.wavelet` active and the same six displayed layer values.

The original StarSim conversion stored only `1 + displayedValue/100`, so the first requested value `52.7` became `1.527`. Combined with the old default Dyadic widths `1,2,4,8,16,32`, this produced the visibly soft result in the supplied screenshot. Version 4 changes Classic to direct factor display/storage and uses the calibrated Linear Gaussian starting widths listed above.

For a reproducible screen-reference check, the 1:1 RegiStax image area was registered by its canvas offset only; no geometric resampling was applied. Because RegiStax had an intensity display stretch, one global display multiplier was fitted before comparing the 100×150 planet ROI. The production native output measured Pearson correlation `0.998955` and RMS error `2.716/255` against the supplied RegiStax screen preview. This measurement validates the corrected combined response on this source; it is not a substitute for full-resolution single-layer TIFF references.

The v4 diagnostic dump on that exact TIFF recorded the following raw signed-band values. Timings are Debug observations on this workstation and are not acceptance limits.

| Layer | Effective sigma (px) | Cumulative sigma (px) | RMS energy | Minimum | Maximum | Processing interval (ms) |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 0.25 | 0.250 | 0.000000283 | -0.000006080 | 0.000005662 | 62.83 |
| 2 | 0.30 | 0.391 | 0.000003245 | -0.000069261 | 0.000064731 | 34.07 |
| 3 | 0.35 | 0.524 | 0.000013778 | -0.000294149 | 0.000275016 | 65.41 |
| 4 | 0.40 | 0.660 | 0.000033874 | -0.000721097 | 0.000675678 | 69.98 |
| 5 | 0.45 | 0.798 | 0.000059981 | -0.001267910 | 0.001192808 | 73.87 |
| 6 | 0.50 | 0.942 | 0.000086790 | -0.001808137 | 0.001714647 | 74.43 |

The neutral full reconstruction took 400.21 ms in that instrumented Debug dump. The diagnostic output contains source, D1–D6, L6, reconstruction, deterministic single-layer gain states, and the fixed L1+L2+L3 result.

### Recorded synthetic baseline

The following is the earlier 2026-09-21 Debug x64 run stored at `artifacts/wavelet-calibration/current` on the deterministic 96×96 grayscale synthetic-Jupiter source, at neutral factors and the former Dyadic defaults. It is retained as a historical engine baseline, not the current default calibration. The current v4 real-source dump is stored at `artifacts/wavelet-calibration/saturn-23_36_32-v4` and identifies processor version `4.0.0`, Linear widths `0.25..0.50`, and the production source used above.

| Layer | Band RMS | Minimum | Maximum | Production request (ms) |
|---:|---:|---:|---:|---:|
| 1 | 0.029735 | -0.164429 | 0.194680 | 1.11 |
| 2 | 0.027947 | -0.082971 | 0.104915 | 1.76 |
| 3 | 0.031465 | -0.070695 | 0.081644 | 3.10 |
| 4 | 0.055882 | -0.092251 | 0.085164 | 5.75 |
| 5 | 0.109358 | -0.137426 | 0.172900 | 11.28 |
| 6 | 0.116766 | -0.186617 | 0.261406 | 16.85 |

The neutral final reconstruction request took 43.93 ms. The fixed `L1-L2-L3-gain-1.5` output measured RMS 0.338337, minimum 0.022161, maximum 0.739983, and 46.47 ms. The manifest contains 30 single-layer/gain results plus that combined result. Gain-zero states agree because each is the same residual, while increasing gain expands only the selected band's contribution.

The Release x64 440×440 grayscale guardrail measured 160 ms for Recursive Gaussian and 38 ms for À-trous B3-spline on this workstation. Both are below the two-second regression guardrail and remain processed off the UI thread; these figures are machine-specific, not product guarantees.

## Expert Wavelet Diagnostic export and measurements

Open Wavelet while the main workspace is in Expert mode, choose Advanced, then use **Export diagnostics…**. The exporter calls the registered production processor and bypasses every other pipeline stage. Its TIFF encoding can be `Float32` or `UInt16`.

Each run exports:

- immutable source;
- signed D1 through D6;
- L6 residual;
- final reconstruction from the supplied canonical wavelet state;
- fixed L1+L2+L3 gain-1.5 calibration result;
- `wavelet-debug-dump.json`.

Float32 band TIFFs contain raw signed coefficients. UInt16 cannot store signed coefficients, so band zero is encoded at 0.5 for visual inspection; the JSON statistics always use the raw signed values.

For every layer, the manifest records backend, analysis spacing/sigma, kernel support radius, cumulative scale, approximate structure diameter, raw RMS energy, raw minimum/maximum, and observed production time. It executes deterministic `L1-only` through `L6-only` states at gains `0.0, 0.5, 1.0, 1.5, 2.0`, with every other layer factor zero, and records output RMS/min/max/time. Timing is machine- and source-dependent; state definitions and pixels are deterministic.

Existing per-module timing events remain unchanged and continue to distinguish cache hits from executed stages.

## Mode-state proof

Beginner/Expert is presentation state only. Every transition logs:

- SHA-256 of the ordered canonical pipeline, including revision, module identities, enable flags, counts, and exact IEEE-754 parameter bits;
- SHA-256 of the wavelet module and all 31 values;
- canonical parameter-write count before/after.

Both hashes must remain byte-identical and the write delta must be zero for Beginner→Expert and Expert→Beginner transitions. If the Expert state does not exactly equal the selected Beginner mapping, Beginner displays `Custom/Modified` without touching processing state.

## Same-source RegiStax procedure

1. Start with one untouched Saturn/Jupiter source for both applications. Compare full-resolution files, not screenshots or rescaled previews.
2. Generate RegiStax Layer 1-only through Layer 6-only references and one known-good L1+L2+L3 result. Record settings only as provenance.
3. Run the Expert Wavelet Diagnostic export on that exact source with Denoise and Threshold neutral. Confirm dimensions and registration.
4. Compare D1/L1-only first at 100% view. Tune widths only when the spatial footprint is misplaced; tune gain only when the footprint is correct but contribution is weak/strong.
5. Repeat sequentially. Preserve monotonic progression from fine to broad instead of making one layer mimic multiple unrelated structures.
6. Compare the deterministic gain series to tune response perceptually. Do not force numeric equality with RegiStax.
7. Validate the chosen widths/gains against the known-good L1+L2+L3 reference, then repeat the identity/reconstruction checks.
8. Evaluate Advanced Denoise and Threshold only after scale placement and direct gain are accepted.

## Validation coverage

The validation criteria cover both decomposition backends, constant-image zero bands, reconstruction, neutral identity, removal of one band, Layer-1 boost direction, impulse sign/surround, step-edge acutance, recursive ancestry, RGB neutrality, finite output, wavelet-only Source→Wavelet→Viewer integration, mode-switch hash/write invariance, legacy migration, and cache invalidation boundaries.
