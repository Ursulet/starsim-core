# Changelog

All user-visible StarSim Core changes are recorded here. Development work is added under **Unreleased** as it is implemented. When a release is prepared, the section is renamed to the final version and release date, then expanded into the bilingual GitHub release notes under `releases/<version>.md`.

## [Unreleased]

No user-visible changes recorded after 1.0.0 yet.

## [1.0.0] — 2026-09-23

### Added

- Added an opt-in clipping warning that overlays processed pixels at display white with red in the preview only; source data and exported pixels are unchanged.
- Added a visual processing-history panel beside Undo/Redo. It lists the reachable immutable pipeline revisions, identifies the affected module/parameter and allows direct navigation while preserving normal Undo/Redo branching.
- Added an opt-in ROI preview workflow: activate ROI, draw a rectangle, then work in a fitted/zoomable view of that independent native float32 crop. **Apply to full image** restores the complete frame and runs the same canonical snapshot on the full source.
- Added complete English/Romanian labels, tooltips and ROI/history status text for the new controls.
- Added working keyboard shortcuts for History, Clipping Warning, ROI selection, applying ROI settings to the full image, panel cancellation and an alternate Redo gesture. The complete bilingual shortcut reference is now shown in Help → About.
- Added bilingual Batch Processing (`Ctrl+Shift+B`) with a prominent toolbar button immediately before Export: it freezes the current canonical pipeline snapshot and processes a deterministic TIFF/PNG folder queue sequentially at full resolution, with per-file/overall progress, cancellation, skip/overwrite policy and TIFF16/PNG16/PNG8 output.
- Batch Processing now supports both intended entry flows: start without an open image to load/edit the first folder image and then **Apply to Entire Batch**, or start after editing a reference image to select a folder and process it immediately. The default output is the source folder's `StarSim_Batch` subfolder.
- Fixed Batch naming preview initialization so an empty `destinationDirectory` can no longer be passed while the window is being constructed.
- Added safe deterministic Batch naming (`<source>_batch_0001`) and transactional output files: incomplete exports remain temporary and never replace a valid result.

### Changed

- Beginner planetary profiles now provide more useful fine-detail headroom. At the nominal preset value, the L1 profile contribution gains exactly 10 points (`47 → 57`), while L2–L6 are scaled proportionally above neutral so broad layers are not over-amplified.
- Multi-scale Sharpen now treats Broad Radius as an absolute target scale instead of accumulating it on top of Fine Radius.
- Deringing / Halo Protection now uses a luminance-guided RGB correction, preserves channel differences, interpolates fractional radii continuously and responds to cancellation during long processing.
- Noise Reduction now removes residual Rec.709 luminance from independently filtered chroma planes, so Chrominance reduction no longer changes brightness detail.
- Local Detail edge protection now observes both the local and micro-detail bands.
- Advanced Tone now bounds shadow/highlight weighting for valid float samples outside the visible range without clipping the float result.
- Long native loops in Unsharp Mask, Multi-scale Sharpen, Richardson–Lucy, RGB Align, Advanced Color and Local Detail now poll cancellation more consistently.
- Full-resolution export now participates in the shared `ResourceGovernor`, so normal and Batch exports obey the same CPU/memory admission policy as processing jobs.

### Documentation

- Added complete Romanian and English non-Wavelet module guides with formulas, parameter behavior, conservative starting ranges and limitations.
- Updated the built-in Romanian/English Help descriptions for the revised processors and stronger Beginner detail mapping.
- Added a documentation index and included the module guides, processing mathematics and Beginner preset specification in release packages.
- Normalized the repository `README.md` as valid UTF-8 Markdown so GitHub no longer treats it as a binary file.
- Added a bilingual Batch Processing guide covering the frozen snapshot, sequential headless pipeline, naming, failure handling and source-protection rules.

### Compatibility

- Existing `.starsim` projects retain their saved canonical parameters and are not silently upgraded. Reapply a Beginner preset to use the stronger mapping.
- Processor parameter identifiers and persisted parameter order remain unchanged.
- The calibrated six-scale Wavelet engine, its decomposition, reconstruction and Expert controls were not modified.
- Clipping Warning and ROI start disabled; with both disabled, the existing full-frame processing and preview route is unchanged.
- ROI selection is impossible while its tool button is inactive, and the History panel is offset below the Original badge so their labels no longer overlap.
- Replaced borderless Full Screen with native Maximize/Restore and explicitly retained the Windows title bar, so Minimize, Maximize/Restore and Close remain available.

### Verification status

- Source changes received static diff review only. Build and image validation remain pending by explicit project-owner request.
- Full bilingual release notes: [`releases/1.0.0.md`](releases/1.0.0.md).

## [0.9.0-beta.1] — 2026-09-21

- First public Windows x64 beta.
- Added Beginner and Expert workflows, six-scale Wavelet processing and diagnostics, the native processing pipeline, projects, presets, export, bilingual interface/help, Plugin Manager, demonstration plugin, SDK, portable package and Inno Setup installer.
- Full bilingual release notes: [`releases/0.9.0-beta.1.md`](releases/0.9.0-beta.1.md).
