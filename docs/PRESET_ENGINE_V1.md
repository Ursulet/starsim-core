# Preset Engine V1

StarSim Core presets are external, versioned JSON recipes. They describe an editable processing state; applying one never writes pixels into the source image.

## Storage and schema

The schema is `presets/schema/preset.schema.json`. Built-ins are stored as:

`presets/builtin/{jupiter,saturn,mars,venus,moon,solar,generic}/{natural,balanced,detailed,strong}.json`

Every recipe declares `schemaVersion`, stable preset and target IDs, display text, `recipeVersion`, an ordered processor list, Beginner macro defaults, a six-value `waveletGains` profile, and compatibility data. The loader rejects unknown fields, missing core processors, invalid IDs, unsupported versions, non-finite values, and values outside the registered processor schema.

The 28 built-ins are practical starting points, not claims of universal scientific optimality. Seeing, sampling, telescope aperture, stacking and source noise still determine how far a source can be pushed.

## Deterministic macro mapping

`Preset Strength` interpolates the recipe's tone/color core from each processor's neutral value. Positive multiplicative quantities use log-space interpolation; signed/additive quantities use linear interpolation. A strength of zero returns the exact neutral value without a floating-point round trip.

Each remaining macro owns a small, explicit part of the canonical pipeline:

- `Detail` directly changes only the six Wavelet band contribution/gain values. At 50 every band is neutral (`1`); below 50 the bands are attenuated; above 50 the selected target profile is introduced progressively. Beginner adds ten gain points to the nominal L1 profile contribution and scales L2–L6 proportionally above neutral, preserving the target-specific profile shape. For example, an L1 profile value of `47` resolves to `57`, while an L6 value of `2` remains close to `2`, not `12`. It never changes Gaussian width, Wavelet Denoise, Threshold, Gamma, Contrast or Local Detail.
- `Noise Reduction` changes and enables only the dedicated Noise Reduction processor. It never changes Wavelet Denoise/Threshold or Contrast.
- `Color` and Natural/Neutral/Warm/Vivid change RGB gains and saturation only for RGB images.
- `Brightness` maps to linear exposure stops.
- `Contrast` maps to midpoint contrast.

Changing a target or preset only stages its visible Beginner values. The canonical image-processing state changes when `Apply Preset` is pressed. After that, moving one Beginner control changes only its owned parameters. The preset menu selects and applies the named level in one action.

## Planetary profiles

Each target has Natural, Balanced, Detailed and Strong profiles. Their `waveletGains` arrays define the direct Classic L1–L6 profile shape. Beginner applies the documented ten-point L1 headroom and proportional contribution scale when mapping that shape into canonical gains; there is no conversion to Sharpen, Denoise, Threshold or any other processor.

| Target | Profile emphasis |
| --- | --- |
| Jupiter | Strongest response in L1–L3 for belts, festoons and compact storms, with rapidly decreasing broad-scale contribution. |
| Saturn | L1–L2 lead for ring edges and fine banding; higher layers remain restrained to avoid broad halos. |
| Mars | L1–L3 balance for albedo boundaries, polar-cap edges and medium surface structure. |
| Venus | Gentler L1 and relatively stronger L2–L3 because useful cloud contrast is commonly broader and lower contrast. |
| Moon | L1–L4 progression for crater rims, rilles and larger relief, with restrained L5–L6. |
| Solar | L1–L3 emphasis for fine surface structure with controlled broad bands. Use only correctly captured, stacked solar data. |
| Generic | Conservative fallback when the object or sampling does not match a named target. |

The profiles were designed from the production StarSim band semantics and official RegiStax documentation describing Layer 1 as the finest scale and successive layers as progressively coarser. They are StarSim parameters, not copied RegiStax settings, and numerical slider equality is neither required nor implied:

- https://www.astronomie.be/registax/previewv6-3.html
- https://www.astronomie.be/registax/linkedwavelets1.html
- https://astronomie.be/registax/denoiseandsharpen.html

Grayscale input disables every RGB-only processor and retains neutral color parameters. Target selection is entirely user controlled; there is no target recognition or AI classification.

## Auto RGB Balance

Auto balance uses the source-channel histogram means and a conservative compressed gray-world correction:

`target = (meanR * meanG * meanB)^(1/3)`

`gainC = clamp((target / meanC)^0.35, 0.90, 1.10)`

The three gains are then divided by their Rec.709 luminance-weighted gain and clamped again to `[0.90, 1.10]`. This performs only 35% of a full gray-world correction and does not force a planetary image to neutral gray. The resulting RGB gains are ordinary pipeline parameters and remain visible and editable in Expert mode.

## Shared state

Beginner and Expert views use the same immutable `PipelineSnapshot` and parameter history. Switching modes does not remap, bake, or approximate the current values. `Apply Preset` commits another parameter snapshot, and Undo/Redo operates on those snapshots rather than pixel buffers. Beginner presentation values are remembered with their committed pipeline hash so Undo/Redo can restore the matching controls; an Expert state that cannot be represented exactly remains `Custom/Modified` and is never reverse-mapped into the pipeline.
