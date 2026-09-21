# StarSim Core V1 — Architecture

Last updated: 2026-09-21

The production-refactor addendum and current canonical pipeline are documented in [PROCESSING_ENGINE_ARCHITECTURE.md](PROCESSING_ENGINE_ARCHITECTURE.md).

## Architectural goals

StarSim Core is a Windows x64 desktop application for mathematical, non-destructive post-processing of already stacked astronomical images. The immutable source identity and source pixels remain separate from mutable processing state. The internal processing representation will be float32 per channel.

## Managed boundaries

- `StarSimCore.Domain` contains platform-independent product concepts and processing state. It has no Avalonia dependency.
- `StarSimCore.Application` contains use cases, processor contracts, pipeline orchestration, caching, and history. It depends on Domain and Interop, never on UI technology.
- `StarSimCore.Interop` owns the source-generated `LibraryImport` declarations and native lifetime wrappers.
- `StarSimCore.UI` contains Avalonia views and view models. It depends on Application and must not run heavy processing on the UI thread.
- `StarSimCore.App` is the composition root and desktop executable.

Dependencies flow inward:

```text
StarSimCore.App -> StarSimCore.UI -> StarSimCore.Application -> StarSimCore.Domain
       |                              |
       +------------------------------+-> StarSimCore.Interop -> StarSimCore.Native.dll
```

## Native boundary

`StarSimCore.Native.dll` is implemented in C++20 and exposes a stable C ABI. ABI version 1 uses:

- `extern "C"` exported functions;
- fixed-width integer types;
- opaque native handles;
- explicit status codes;
- no C++ strings, containers, classes, or exceptions across the DLL boundary.

Managed native objects are owned through `SafeHandle`. Built-in processing modules and native plugins share the versioned processor abstraction; plugins declare metadata and numeric parameters while the host generates and owns their application UI.

### ABI v1 image ownership

`SSC_ImageHandle` and `SSC_PipelineHandle` are opaque. `SSC_ImageDescriptor` begins with `struct_size` and `abi_version`, uses fixed-width fields, and reserves eight 64-bit slots for compatible extension. Status codes are explicit, while detailed errors are retrieved as thread-local UTF-8 text.

The decode/import boundary performs one interleaved-to-planar copy into native-owned float32 storage. The immutable master, working clones, and pipeline output have distinct instance identities while retaining a shared source identity. No source/master mutation function exists. Managed wrappers retain handles only through deterministic `SafeHandle` ownership.

### Internal pixel layout

ABI v1 uses planar float32 (`RRR… GGG… BBB…`). A Release x64 microbenchmark on the verified MSVC 14.51 system applied twelve per-channel horizontal convolution passes to a 2048×1024 RGB buffer:

- planar: 99.249 ms;
- interleaved: 119.201 ms;
- interleaved/planar ratio: 1.201.

The approximately 20% result, contiguous per-channel SIMD access, and the V1 emphasis on wavelets/convolution justify planar storage. The result is advisory and machine-specific. UI and managed application layers never depend on this layout.

### Display conversion

The native core can render an image snapshot to caller-provided BGRA8 storage. This is an explicit display copy; BGRA8 never aliases or replaces the float32 master/working representation.

### Image input and source identity

PNG and TIFF decoding occurs in the native DLL through pinned libpng and libtiff dependencies. Supported inputs are 8/16-bit grayscale and RGB scanline images. Declared alpha is detected and discarded explicitly; it never becomes a processing channel. TIFF planar-separate and unsupported photometric layouts fail with an explicit status rather than being guessed.

The application canonicalizes the path, records size/time/SHA-256 metadata, opens only for reading, and verifies the source hash before publishing the document. `SourceGuard` rejects a future export destination that resolves to the canonical source path. The native master and derived processed image retain a common source identity but distinct instance identities.

### Processing pipeline

The application layer defines UI-independent processor metadata: stable ID, name, category, semantic version, parameter schema, default enabled state, capabilities, validation, and preview hint. The default mathematical order is geometry, noise/restoration, detail/sharpening, advanced color, RGB balance/saturation, advanced tone, then exposure/contrast/gamma. Keeping display color and tone late prevents ordinary visual adjustments from invalidating expensive restoration stages.

The first uncached stage starts from the immutable master. Every enabled stage output is cached with a key containing the source identity, complete preceding enabled state, processor version, parameters, and resolution scale. Processing intent is excluded because the native math is identical at the same resolution. Disabled parameters do not invalidate downstream output. The cache is bounded by both entry count and one third of the active ResourceGovernor memory budget, with LRU eviction. Cache hits return owned clones so cache lifetime cannot escape into the UI.

Authoritative requests use strict single-flight scheduling: at most one request runs and one immutable latest-pending snapshot is retained. Further parameter changes atomically replace that pending snapshot; they do not create FIFO work items. A higher-priority interactive request may cancel lower-priority definitive work, and source replacement, explicit Cancel, shutdown, and resource governance always propagate native cancellation. Every completion passes RequestId, source identity, state revision, document context, and cancellation gates before publication. Scheduler metrics and Debug traces expose running, pending, current depth, maximum observed depth, submitted, completed, coalesced, and discarded counts; `depth <= 2` is asserted and stress-tested.

Exposure, contrast, gamma, RGB balance, and saturation also have a fast display path. During slider drag, the UI transforms the latest completed BGRA preview buffer directly without entering the native pipeline. Pointer release or 120 ms input idle schedules the authoritative full-resolution snapshot in the single-flight worker. The fast result is temporary; the cached float32 pipeline result replaces it when ready.

Expensive interactive work is admitted at full, half, or quarter internal resolution from the actual image metadata and configured preview policy; there is no fixed image size. The resulting display buffer is upscaled atomically for the viewer and is explicitly marked `InteractivePreview`. Release/idle schedules `DefinitivePreview` at full preview resolution.

`ResourceGovernor` is the shared admission and permit boundary. Balanced is the default, CPU permits are resolved from the logical processor count, memory decisions use currently available physical memory with `max(1 GiB, 15%)` reserved for the system, and per-profile automatic budgets are 18%/35%/65% of available memory capped by the safe remainder. The same governor controls the native reusable float-buffer retention limit and the byte-bounded stage cache.

Explicit source replacement, caller cancellation, shutdown, and resource governance can still cancel pending or in-flight work. Slider movement itself is coalescence, not cancellation.

Histogram computation is native and asynchronous. It produces 256 RGB or grayscale bins plus per-channel minimum, maximum, and mean. Undo/Redo stores immutable pipeline parameter snapshots only; bitmap data is never placed in command history.

### Native thread-safety contract

- Immutable image handles support concurrent metadata, identity, and BGRA8 read calls while the handle remains alive.
- Cloning and pipeline creation allocate independent storage and are background-callable.
- Error detail is thread-local.
- Release must not race another call using the same raw handle; managed wrappers prevent this with `SafeHandle.DangerousAddRef` for call duration.
- Pipeline orchestration serializes publication of results. Processor inputs and cached outputs remain immutable after creation.

## Threading and cancellation

Image decoding, native processing, and histogram work run through asynchronous application services. Native loops poll a managed-owned cancellation flag. The UI thread is reserved for short state and bitmap presentation updates.

## Dependency and build model

- .NET dependencies use Central Package Management.
- Native dependencies use a pinned repository-local vcpkg checkout in manifest mode.
- CMake configures and builds x64 Debug and Release variants.
- Build scripts stage the native DLL and its libpng/libtiff runtime dependencies beside the application.
- Release packaging produces a self-contained Windows x64 payload before invoking Inno Setup.

## Source safety contract

Image loading opens the source read-only and verifies its SHA-256 before publishing a document. Processing operates on separate native working images. Export must pass `SourceGuard`, reject the canonical source path, and never overwrite the source file.
