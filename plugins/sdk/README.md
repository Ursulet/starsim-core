# StarSim Core Plugin SDK — quick start

This directory is copied into every StarSim Core release as `sdk/`. It is a standalone Windows x64 C++ starter project and includes a working demonstration plugin.

## Included files

```text
CMakeLists.txt                  Standalone build project
sample_plugin.cpp               Invert/Tint and RGB-to-B&W example
include/starsim_core_plugin.h   Public native ABI v1
PLUGIN_SDK.md                   Complete development and distribution guide
```

## Build the example

Requirements: Visual Studio with **Desktop development with C++**, the x64 MSVC tools, Windows SDK and CMake 3.24+.

Open an x64 Visual Studio developer PowerShell in this directory and run:

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release
```

The output is `build/Release/starsim_diagnostic_plugin.dll`.

## Install it

1. Open StarSim Core → **Plugins → Plugin Manager**.
2. Select **Install plugin…** and choose the DLL.
3. Enable external native plugins if necessary.
4. Restart StarSim Core.
5. Select Expert mode and expand `PLUGINS`.

The generated panels expose **Diagnostic Invert and Tint** and **RGB to B&W Mix**. Each can also be opened in a separate movable window.

## Turn the sample into your own plugin

1. Copy this entire directory to a writable project folder.
2. Rename the target in `CMakeLists.txt`.
3. Change the plugin ID, processor IDs, metadata and parameters in `sample_plugin.cpp`.
4. Replace the sample pixel operation, preserving input/output ownership and shape.
5. Validate all arguments, poll cancellation, catch exceptions at exports and return an `SSC_PluginStatus`.
6. Build Release x64 and install the resulting DLL through Plugin Manager.

Read `PLUGIN_SDK.md` before publishing. IDs are stored in projects and presets, so they must be globally unique and stable. Native DLLs execute in-process and are not sandboxed; distribute source and binaries only through a trusted location.
