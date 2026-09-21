# StarSim Core V1 — UI Specification

Last updated: 2026-09-21

## Sources of truth

- the current implementation and public screenshots in `docs/screenshots/`;
- the processing and UI architecture documents in this repository;
- the established StarSim Core visual language and product requirements.

Mockup brand names are placeholders. The application name is always **StarSim Core**.

## Visual direction

The application uses a premium dark desktop visual language with near-black and charcoal surfaces, restrained blue-cyan accents, crisp typography, thin borders, and subtle depth. The image viewer remains dominant. The design must avoid a game-like or neon appearance.

## Persistent shell

The current shell follows the approved 1536×960 reference: a native menu, compact command toolbar, dominant image viewer, separate Histogram card, Processing rail, and global status bar. The toolbar exposes Open Image, Save Project, Undo, Redo, Compare, Zoom, Fit, Beginner/Expert mode, Performance, and Export. Compare is the single before/after control and only shows or hides the draggable split divider. Controls must not be presented as completed until their behavior is implemented.

## Beginner mode

Beginner mode uses a calm three-column workflow: numbered workflow on the left, large viewer in the center, and simplified controls on the right. Beginner parameters map to the same processing state used by Expert mode.

## Expert mode

Expert mode keeps the viewer dominant and arranges every production module in the right Processing rail:

- **Detail (8):** Wavelet, Deconvolution, Noise Reduction, Local Contrast, Unsharp Mask, Multi-scale Sharpening, Deringing, and RGB Alignment.
- **Color (3):** Advanced Color, RGB Balance, and Saturation.
- **Tone (4):** Advanced Tone, Exposure, Contrast, and Gamma.

The search box filters localized module names, descriptions, and parameter names. It never removes, disables, resets, or rewrites a processor. Clearing the search restores all 15 modules. Groups and modules are independently collapsible.

Each module row exposes its real enable/bypass state, a compact inline editor, Reset, and **Open separate panel**. The separate panel is one movable native window per module and uses that module's localized name, description, full registered parameter list, numeric editing, and reset action. Exposure, Contrast, and Gamma remain visually grouped under Tone but detach independently. Performance and Histogram also open as independent movable windows instead of workspace overlays.

Wavelet's compact editor shows direct Strength and L1–L6 contribution controls. Its specialized detached window retains Classic/Advanced and Diagnostic functionality. The redesign changes presentation only; it does not change Wavelet parameter mapping, radii, decomposition, gains, reconstruction, or native processing.

Switching modes must preserve all active processing state. Beginner remains a projection over the same canonical snapshot and displays `Custom/Modified` when Expert state does not exactly match a Beginner preset.

## Viewer behavior

The viewer normally displays the latest processed image. Compare activates the draggable original/processed divider; turning Compare off removes the divider and returns to the complete processed view. Moving the divider is viewer-only and never schedules processing. Original/Processed badges appear only during comparison.

The global bottom bar reports source metadata, processing state, zoom, and pixel readout. Histogram remains a separate card above Processing in both modes.

## Phase 1 scope

The Phase 1 window is intentionally only a build-verification shell. The complete shell and visual system belong to Phase 03; no inactive editing controls are shown in Phase 1.
