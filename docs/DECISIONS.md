# StarSim Core V1 — Decisions

Last updated: 2026-09-21

## D-001 — Master context is authoritative

Status: accepted

The master context, repository state, and approved UI references remain the source of truth.

## D-002 — Enforce inward managed dependencies

Status: accepted

Domain is independent of Avalonia. Application depends on Domain; UI depends on Application; App is the composition root. Native calls are isolated in Interop.

## D-003 — Use a minimal versioned C ABI

Status: accepted

ABI version 1 exposes fixed-width values and opaque handles through `extern "C"` functions. C++ implementation types and exceptions do not cross the boundary.

## D-004 — Pin repository-local native dependencies

Status: accepted

vcpkg is pinned to commit `319504a5326aa870edde46438c5455fa76305a56`. The manifest declares `libpng` and `tiff`; no global vcpkg installation is required.

## D-005 — Centralize managed package versions

Status: accepted

Avalonia 12.1.2 targets .NET 10. Managed package versions are centralized in `Directory.Packages.props`.

## D-006 — Keep the Phase 1 UI honest

Status: accepted

The initial window contains only product/build-shell identification. Editing controls are deferred until implemented, so no dead controls are presented as completed behavior.

## D-007 — Target the installed Visual Studio 2026 generator

Status: accepted

The Windows build preset uses `Visual Studio 18 2026`, matching the installed Visual Studio Community 2026 toolchain. Both x64 configurations were verified with MSVC 14.51 and Windows SDK 10.0.26100.

## D-008 — Centralize the approved visual system

Status: accepted

All approved colors and reusable shell styles live in `StarSimCore.UI/Styles/Theme.axaml`. The shell uses an 8px rhythm, compact desktop density, restrained borders, and the system Segoe UI/Arial fallback without bundling proprietary fonts.

## D-009 — Keep Phase 03 pre-processing UI behavior explicit

Status: accepted

At the Phase 03 boundary, targets, presets, mode switching, and parameter state were interactive while later-phase actions remained disabled. Image I/O, processing, and history were activated only after their implementations were complete and verified.

## D-010 — Keep DPI behavior explicit

Status: accepted

The UI is designed for the documented minimum viewport and Windows display scaling. Layout containers must remain usable without overlapping controls at supported scaling values.

## D-011 — Keep native image ownership explicit

Status: accepted

The version 1 C ABI exposes only opaque image and pipeline handles. Creation, cloning, snapshot creation, and release are explicit. A master image has no mutation entry point, and a working clone or output snapshot owns distinct storage.

## D-012 — Store working pixels as planar float32

Status: accepted

Native working images use one contiguous `float32` plane per channel. A Release benchmark on the verified workstation measured 99.249 ms for planar and 119.201 ms for interleaved storage over twelve per-channel horizontal convolution passes on a 2048×1024 RGB image. Incoming interleaved samples are converted once at the ABI boundary.

## D-013 — Restrict pixel conversion to explicit boundaries

Status: accepted

The image model retains native `float32` samples. BGRA8 is produced only on request into a caller-owned display buffer, without changing the source or working image.

## D-014 — Define ABI failure and concurrency behavior

Status: accepted

All native entry points return stable status values and keep diagnostic UTF-8 text in thread-local storage. Immutable image reads may run concurrently while a handle remains alive. Release must not race an unmanaged call; managed wrappers hold a `DangerousAddRef` lease for each call. Future mutable pipeline operations require external serialization.

## D-015 — Decode through the pinned native libraries

Status: accepted

PNG/TIFF input uses libpng/libtiff already declared in vcpkg. Inputs are opened read-only, normalized once to planar float32, and alpha is detected then discarded. OpenCV is not introduced for file I/O.

## D-016 — Treat source identity as a guarded application concept

Status: accepted

A loaded document records canonical path, file metadata, SHA-256, and native source identity. The hash is verified across loading and `SourceGuard` prevents export to the canonical source path. Original and Processed always use distinct native instances.

## D-017 — Keep processors UI-independent and versioned

Status: accepted

Processor contracts live in Application and have no Avalonia dependency. Stable IDs, semantic versions, schemas, capabilities, validation, enabled state, and preview hints participate in deterministic pipeline state and cache keys.

## D-018 — Use latest-wins cancellable previews

Status: superseded by D-023, then D-024

Interactive requests use a 60 ms debounce. A new request cancels the previous native work, and a monotonically increasing generation is checked immediately before output publication. Stale output is disposed and cannot replace the current UI result.

## D-019 — Store history as parameter snapshots

Status: accepted

Undo/Redo and module/global reset operate on immutable ordered processor-state snapshots. Full pixel buffers are excluded from command history. Intermediate image reuse belongs to the bounded native-image cache instead.

## D-020 — Treat presets as external parameter recipes

Status: accepted

Built-in presets use a strict versioned JSON schema and conservative target-specific engineering defaults. Beginner macros deterministically produce the same editable processor parameters used by Expert mode; no preset bakes pixels or claims universal scientific optimality.

## D-021 — Keep Expert math explicit and reference-testable

Status: accepted

V1 uses documented CPU reference algorithms: recursive six-level Gaussian wavelet decomposition, separable Gaussian filtering, robust difference-based noise estimation, local-envelope deringing, Gaussian-PSF Richardson–Lucy, deterministic green-reference alignment, and explicit color/tone curves. Float output remains unclipped until display conversion. Correctness and invariants precede SIMD or GPU optimization.

## D-022 — Preserve native failure and cancellation provenance

Status: accepted

Every status-returning ABI call uses one diagnostic boundary that captures the C++ thread-local error immediately, assigns operation/request context, and logs before throwing. Scheduler cancellation state is shared with native worker calls so supersession, user action, shutdown, resource governance, and unexpected native cancellation cannot collapse into an ambiguous `OperationCanceledException`.

## D-023 — Queue interactive processing in strict FIFO order

Status: superseded by D-024

Every slider change captures an immutable pipeline snapshot and appends it to one background FIFO queue. Requests execute and publish in invocation order; rapid interaction never cancels a processing request and never blocks the UI thread. The queue publishes completed/total, pending, stage, revision, and stage-weighted percentage progress for a determinate UI indicator. Cancellation is reserved for explicit caller action, source-image replacement, shutdown, or resource governance. This intentionally supersedes the Phase 06 latest-wins/debounce policy.

## D-024 — Use single-flight, latest-pending scheduling and late display stages

Status: accepted

Interactive authority is bounded to one running request plus one replaceable latest-pending snapshot. Replaced pending states never enter native processing and are counted as coalesced, not cancelled. Cheap color/tone changes render immediately from the latest completed preview buffer during pointer interaction, then receive an authoritative full-resolution commit on release or short idle. Geometry, restoration, and detail precede color/tone in the default order. A per-stage LRU content cache therefore reuses unchanged expensive upstream outputs and invalidates only the first changed stage and its downstream dependants. Debug instrumentation records the scheduler depth invariant and each processor's elapsed time, cache disposition, quality, and request ID.

## D-025 — Validate every completion at the publication gate

Status: accepted

Each request receives a monotonic numeric RequestId and captures source identity, immutable state revision, and document context. Native output, histogram output, and UI publication are rejected if any captured value is no longer current. Opening a new image cancels the old context. Scheduler exceptions complete only their originating task and cannot terminate the worker loop.

## D-026 — Use honest delayed processing feedback

Status: accepted

Processing feedback appears only after exactly 200 ms of continued activity. Native progress is delivered through an additive versioned callback structure. Wavelet layers and Richardson–Lucy iterations report real completed/total steps; unknown work remains indeterminate. Cancel signals the native flag, retains the last valid preview, and intentional cancellation is not presented as an error.

## D-027 — Separate view interaction from processing quality

Status: accepted

The Before/After divider, pan, zoom, and pixel inspection only alter viewer state. Processed display buffers are replaced as complete references on the UI thread. Expensive drag previews may use dynamically selected half/quarter resolution based on real image dimensions and memory admission, while cheap tone/color transforms use the full latest display buffer. Definitive work runs on release or 120 ms idle.

## D-028 — Govern CPU, memory, caches, and reusable buffers centrally

Status: accepted

Eco, Balanced, and Maximum resolve shared worker permits without per-algorithm thread pools. Automatic memory budgets use currently available RAM, retain `max(1 GiB, 15%)` for the OS, and allocate 18%, 35%, or 65% by effective profile within that safe remainder. Battery policy lowers one profile by default. Operations are estimated before admission; interactive work may reduce resolution, while definitive work refuses safely. Native separable-filter temporary arrays use a size-aware, retention-bounded pool; stage caching is LRU- and byte-bounded.

## D-029 — Export format, bit-depth quantization, and source guard

Status: accepted

Export supports 16-bit TIFF (uncompressed), 16-bit PNG, and 8-bit PNG via `ssc_image_export_file_utf8` backed by pinned `libtiff` and `libpng`. Native export converts internal `float32` samples via linear mapping `std::clamp(v, 0.0f, 1.0f)` with round-to-nearest (`* 65535.0f + 0.5f` for 16-bit, `* 255.0f + 0.5f` for 8-bit). Export writes initially to a `.starsim_tmp` file and atomically moves/replaces it upon completion (`MoveFileExW` with `MOVEFILE_REPLACE_EXISTING`), ensuring no incomplete or corrupt files exist if cancelled. Export to the canonical source image path is strictly blocked by `SourceGuard`.

## D-030 — Project file format, schema versioning, and missing source relocation

Status: accepted

Projects use an external versioned JSON format (`.starsim`, current schema version 4) storing schema version, app version, creation/modification timestamps, source reference (canonical path, file name, size, SHA-256 fingerprint, dimensions, channels, bit depth), target, preset ID and recipe version, workflow mode/state, viewer state, ordered pipeline module states/parameters, and user settings. Raw pixel data is excluded from `.starsim` files to keep projects lightweight and fast to save/load. Missing source files are detected on open and prompt user relocation. Dimensions and channel count are useful diagnostics, but only an exact SHA-256 match is accepted automatically; a same-size image with a different fingerprint is rejected rather than silently rebound.

## D-031 — Custom preset store and validation

Status: accepted

Custom user presets are stored in a dedicated user directory separate from built-in recipes (`%AppData%/StarSimCore/presets/custom`). Each preset is validated against `BuiltInProcessors` schemas on save and load to ensure valid module IDs, parameter counts, and numeric bounds. Corrupt or unparseable preset files are skipped gracefully without terminating the application.

## D-032 — Coalesced slider drag history

Status: accepted

Parameter history supports slider drag coalescing (`isCoalescing` and `coalesceKey`). During active pointer dragging, intermediate adjustments update the current snapshot without creating intermediate undo entries, recording exactly one undo step when dragging begins. Reverting (Undo) restores the state immediately preceding the drag gesture. No-op parameter changes (`ModulesEqual`) are pruned automatically.

## D-033 — Native Plugin C ABI and Non-Sandboxed Security Model

Status: accepted

The native plugin architecture exposes a versioned, extern "C" ABI (`starsim_core_plugin.h`, version 1) with explicit `struct_size` prefixes, fixed-width types (`uint32_t`, `float`, etc.), UTF-8 strings, explicit host/plugin memory ownership, and no C++ types, standard library containers, or name-mangling crossing the boundary. The host callback layout is identified by `STARSIM_CORE_PLUGIN_HOST_CALLBACKS_VERSION` and guarded by `struct_size`; it provides cancellation checks, progressive step reporting, and persistent logging. In StarSim Core V1, native plugins run directly in the host process memory space and are NOT sandboxed (developer-oriented, opt-in). This limitation is explicitly documented and surfaced.

## D-034 — Dynamic Plugin Discovery and Safe Rejection Logging

Status: accepted

Plugin discovery is disabled until the user enables it persistently in Plugin Manager or opts in with `--enable-plugins` / `STARSIMCORE_ENABLE_PLUGINS=1`; discovery then scans the application and per-user plugin directories. The loader uses `NativeLibrary.TryLoad` and validates ABI version, returned struct sizes, entry points, identifiers, capability bits, parameter schemas and duplicate processor IDs. Incompatible, corrupt, inaccessible or missing-export libraries are rejected individually, recorded as structured `PluginRejection` values and persisted to the diagnostic log without aborting application startup. Valid processors are wrapped as `IImageProcessor` instances in `PluginService.PluginProcessors`.

## D-035 — Native Planar Pointer Access and Buffer Pooling Invariants

Status: accepted

Native image buffers use a 32-bit floating-point planar representation (`SSC_PIXEL_FORMAT_FLOAT32_PLANAR`). `NativeImage` exposes `GetPlanePointers` and `CreateMasterPlanar` to enable zero-copy planar data exchange between native host routines, plugin processors, and managed buffers without interleaved shuffling. Reusable scratch buffers for multi-scale wavelets, separable gaussian filters, and deconvolution iteration ratios are managed through `NativeBufferPool`, which enforces a strict retention limit and guarantees zero steady-state heap allocations during continuous interactive scrubbing.

## D-036 — Independent, mathematically defined production processors

Status: accepted

The existing registered processors are the sole production implementations and compose strictly in canonical order. Each stage owns only its parameters; disabled stages are exact pass-throughs. Wavelet supports Recursive Gaussian and À-trous B3-spline inside `core.wavelet`, sharing direct per-band gain semantics. Noise Reduction uses MAD wavelet shrinkage, Richardson–Lucy damping is an update blend, Deringing is separate with documented self-reference fallback, RGB Align uses phase correlation plus Lanczos-3, and Gamma is signed. Semantic-version changes invalidate affected cache stages without discarding valid upstream results. Beginner/Expert switching is processing-state read-only and is enforced by deterministic hashes and write-count audits.

## D-037 — Classic Wavelet displays the real canonical band factor

Status: accepted

Classic Layer values are the actual `LayerEnhancementFactor`: 0 removes a band, 1 is neutral, and a displayed 52.7 sends 52.7 to the native reconstruction. The former presentation conversion (`1 + value/100`) was rejected after a same-source Saturn comparison proved that it attenuated requested values by tens of times. The Gaussian starting profile is Linear `0.25..0.50` pixels (`initial=0.25`, `step=0.05`); Dyadic remains an explicit advanced selection. Reset restores factor 1. Advanced Gaussian width, Denoise, and Threshold stay independent.

## D-038 — Managed Plugin Catalog, Generated Panels and Pipeline Integration

Status: accepted

Accepted external processors are appended to the canonical runtime registry after all built-in processors; built-in ordering and implementation are unchanged. Expert mode presents them in a dedicated `PLUGINS` group and generates both inline controls and a separate movable panel from the validated numeric parameter schema. Plugin state participates in history, processing, export, custom presets and projects. Beginner mapping preserves plugin state and mode switching remains state-read-only. User installation is staged in `%AppData%\StarSimCore\plugins`; changes take effect after restart, and removal is recoverable through the `disabled` archive. Projects with enabled unavailable processors fail explicitly as missing plugin dependencies.
