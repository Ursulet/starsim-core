# StarSim Core V1 — Known Issues

Last updated: 2026-09-21

## Open issues

- A same-source combined RegiStax screen reference is now available for `23_36_32_lapl4_ap5.tif` and exposed the corrected Classic-factor defect. Full-resolution Layer 1-only through Layer 6-only RegiStax exports are still needed to refine each band's independent spatial footprint; this no longer blocks direct Classic gain operation.

## Resolved

### KI-001 — Build toolchain was unavailable during installation

Status: resolved 2026-09-20

Visual Studio Community 2026 exposes MSVC x64, CMake, and Windows SDK 10.0.26100. The .NET 10.0.401 SDK is available and the Debug/Release toolchain has been verified.

## Baseline limitations

- RGB automatic registration now uses subpixel phase correlation; automatic and manual shifts both use reflected Lanczos-3 resampling.
- The CPU reference algorithms prioritize correctness, deterministic output, cancellation, and separable kernels; SIMD/GPU acceleration is deferred to future releases.
- Native Plugin SDK (`starsim_core_plugin.h`, `PluginService`, Plugin Manager and diagnostic sample plugin `starsim_diagnostic_plugin.dll`) uses a versioned C ABI. Loading remains opt-in through the persisted manager setting or developer CLI/environment overrides. Valid processors receive generated Expert panels and join the runtime pipeline after built-ins. Native plugins run directly in the host process memory space and are NOT sandboxed.
- `package.ps1` creates the self-contained Windows x64 portable archive; `make-installer.ps1` creates the Inno Setup installer.
- Export (TIFF 16-bit, PNG 16-bit, PNG 8-bit), Projects (`.starsim`), and Custom Presets are implemented with source overwrite protection, progress reporting, and cancellation.
- V1 uses a shared managed admission/permit pool and single-threaded native reference kernels. SIMD and internal parallel kernels remain future optimizations; any such work must consume the same shared permits and must not introduce nested oversubscription.
- Processor metadata now declares independent-tile, halo-required, or non-tileable safety. V1 does not yet execute the halo-aware convolution processors as tiles; memory admission therefore reduces interactive resolution or rejects definitive work instead of risking seam-producing naive tiles.
