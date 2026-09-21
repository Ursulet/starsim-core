# StarSim Core native plugins

StarSim Core V1 exposes a native C plugin SDK and an integrated Plugin Manager. Native DLLs execute inside the StarSim Core process and are **not sandboxed**; install only code you trust.

## Install and enable

Open **Plugins → Plugin Manager** or **Plugins → Install Plugin…**. User-installed DLLs are copied to `%AppData%\StarSimCore\plugins`, external loading is enabled, and the manager requests an application restart. The startup preference is persisted in `%AppData%\StarSimCore\plugin-settings.json`.

Developer overrides remain available:

- command line: `StarSimCore.App.exe --enable-plugins`
- environment: `STARSIMCORE_ENABLE_PLUGINS=1`

At startup, discovery scans:

1. `plugins` beside the application executable;
2. `%AppData%\StarSimCore\plugins`.

Invalid or incompatible libraries are rejected individually without aborting startup. Loaded and rejected DLLs, processor names and diagnostic details are shown in Plugin Manager and written to `logs/starsim-core-YYYYMMDD.log` (or `STARSIMCORE_LOG_DIR`). Remove/disable archives user DLLs under `plugins\disabled` instead of deleting them permanently.

## Pipeline and generated UI

Every accepted processor becomes a real `IImageProcessor`, is appended after the built-in Tone stages, and appears only in Expert mode under the `PLUGINS` group. The host generates its sliders and separate movable panel from `SSC_PluginParameterDef`; plugins do not create or own application UI.

Plugin enabled state and parameters participate in live preview, Undo/Redo, full-resolution export, custom presets and `.starsim` projects. A project that requires an unavailable enabled plugin reports the missing processor dependency instead of silently substituting or rewriting its pipeline.

The ABI contract is `native/StarSimCore.Native/include/starsim_core_plugin.h`. The packaged `sdk/` folder contains the contract, CMake template, documentation and `native/StarSimCore.SamplePlugin/sample_plugin.cpp`, which demonstrates Invert/Tint and an RGB-to-B&W strength control. See `docs/PLUGIN_SDK.md`.
