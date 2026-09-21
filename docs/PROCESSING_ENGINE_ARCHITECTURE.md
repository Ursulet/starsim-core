# StarSim Core Production Processing Architecture

Last updated: 2026-09-21

## Single production engine

The refactor changes the registered `BuiltInProcessors` and `SSC_PROCESSOR_*` implementations in place. It creates no parallel project, replacement pipeline, duplicate Wavelet processor, or UI-side image algorithm. Avalonia edits canonical parameter state; Application owns orchestration; Interop owns the fixed C ABI; C++ owns pixels and math.

## State and module isolation

The immutable ordered `PipelineSnapshot` is the only processing state. Each module receives the previous module's image plus its own enabled flag and parameters. It cannot address another module's parameters. Disabled modules are skipped and therefore exact pass-throughs.

Beginner and Expert are projections over the same snapshot. Presentation-mode switching records a SHA-256 of the full ordered pipeline, a SHA-256 of all Wavelet values, and the canonical write counter before/after. Any difference throws. Expert changes that no longer exactly match the selected recipe appear as `Custom/Modified`; reverse mapping never rewrites state.

New images reset once to Target `Default`, Preset `Default`, neutral macros, and a neutral pipeline. Explicit target or preset selection stages one complete recipe and updates the visible macro controls without processing writes; `Apply Preset` commits it once. Project restoration suppresses target/preset callbacks while restoring the saved canonical snapshot.

## Scheduling and caching

The existing single-flight/latest-pending scheduler, ResourceGovernor, cancellation flag, and publication gates are unchanged. The stage cache key includes source identity, the preceding key, processor ID and semantic version, enabled state, exact parameters, and resolution scale. Algorithm changes increment semantic versions. Changing a stage reuses valid upstream entries and invalidates that stage plus downstream stages only.

## Schemas and compatibility

The Wavelet schema is 31 values, adding a backend selector without changing the 32-float ABI payload. Project and custom-preset schema version 3 migrate:

- Wavelet 28→31 by folding the old Amount/Sharpen pair into one effective gain and appending scheme/step/Gaussian backend;
- Wavelet 30→31 by appending Recursive Gaussian;
- Multi-scale Sharpen 4→5 by preserving the old 65%/35% split as independent fine/broad amounts;
- Richardson–Lucy 5→4 by dropping the former embedded Deringing value.

The native entry point temporarily accepts the old positional counts for direct ABI compatibility. Managed canonical state always uses current schemas.

## Wavelet Diagnostic mode

The Advanced Wavelet window exposes the backend and scale scheme. In Expert mode, its diagnostic exporter clones the immutable master, acquires a full-resolution ResourceGovernor admission/worker lease, and calls the registered production `core.wavelet` processor while bypassing every other stage. It exports source, D1–D6, L6, final reconstruction, a fixed L1+L2+L3 result, and a JSON manifest as float32 or 16-bit TIFF. It does not contain a second decomposition implementation.

## Boundaries

All working buffers remain planar float32. Viewer BGRA8 and file export are explicit conversion boundaries. Source handles remain immutable, and diagnostic/export destinations never replace the source. Native operations retain cancellation and diagnostic provenance through the existing ABI.
