# StarSim Core native Plugin SDK v1

This document is the complete development guide for native StarSim Core image-processing plugins. The release package also contains a self-contained `sdk/` directory with the public header, a CMake project and a working sample.

> Security: plugins are Windows x64 native DLLs loaded into the StarSim Core process. They are not sandboxed. A defective or malicious plugin can corrupt memory, crash the application or access anything available to the current user. Install and distribute only binaries whose source and publisher you trust.

## 1. What a plugin can add

A single DLL can publish one or more processors. Each processor:

- appears automatically in Expert mode, in the `PLUGINS` group;
- receives normalized planar `float32` grayscale or RGB image data;
- declares its own sliders and numeric fields through metadata;
- can participate in interactive preview and full-resolution export;
- participates in Undo/Redo, custom presets and `.starsim` projects;
- can report progress, observe cancellation and write diagnostic log messages.

V1 plugins cannot add arbitrary Avalonia windows or replace the built-in UI. StarSim Core generates a consistent inline panel and a movable separate panel from the parameter definitions exported by the DLL.

## 2. SDK layout

The packaged `sdk/` directory contains:

```text
sdk/
├── CMakeLists.txt
├── README.md
├── PLUGIN_SDK.md
├── sample_plugin.cpp
└── include/
    └── starsim_core_plugin.h
```

In the source repository, the authoritative ABI header is `native/StarSimCore.Native/include/starsim_core_plugin.h`, and the sample implementation is `native/StarSimCore.SamplePlugin/sample_plugin.cpp`.

## 3. Toolchain

Required on Windows x64:

- Visual Studio 2022 or newer with **Desktop development with C++**;
- current MSVC x64 compiler and Windows SDK;
- CMake 3.24 or newer.

Open an **x64 Native Tools Command Prompt for Visual Studio**, or a PowerShell session in which the MSVC tools are available. Copy the release `sdk/` directory to a writable project directory before changing the sample.

## 4. Build the included sample

From inside `sdk/`:

```powershell
cmake -S . -B build -A x64
cmake --build build --config Release
```

The result is:

```text
build/Release/starsim_diagnostic_plugin.dll
```

The sample exposes two processors:

- **Diagnostic Invert and Tint**, demonstrating four generated parameters;
- **RGB to B&W Mix**, demonstrating luminance conversion with adjustable strength.

The B&W conversion uses Rec. 709 luminance weights and preserves the host buffer layout: an RGB input still produces three channels, with the mixed luminance written to R, G and B.

## 5. Start a plugin project

The shortest route is to copy the SDK directory, rename the CMake target, then edit `sample_plugin.cpp`. Keep the public header unchanged.

Minimal `CMakeLists.txt`:

```cmake
cmake_minimum_required(VERSION 3.24)
project(MyStarSimPlugin LANGUAGES CXX)

add_library(my_starsim_plugin SHARED my_plugin.cpp)
target_compile_features(my_starsim_plugin PRIVATE cxx_std_20)
target_compile_definitions(my_starsim_plugin PRIVATE STARSIM_CORE_PLUGIN_EXPORTS)
target_include_directories(my_starsim_plugin PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/include")
set_target_properties(my_starsim_plugin PROPERTIES CXX_EXTENSIONS OFF)

if(MSVC)
    target_compile_options(my_starsim_plugin PRIVATE /W4 /permissive- /EHsc)
endif()
```

## 6. Required C ABI

Every plugin must export exactly these C-callable entry points:

```c
int32_t ssc_plugin_get_info(SSC_PluginInfo* out_info);
int32_t ssc_plugin_get_processor_def(uint32_t index, SSC_PluginProcessorDef* out_def);
int32_t ssc_plugin_process(
    uint32_t processor_index,
    const SSC_PluginImageBuffer* in_buffer,
    SSC_PluginImageBuffer* out_buffer,
    const double* parameters,
    uint32_t parameter_count,
    SSC_PluginProcessingQuality quality,
    const SSC_PluginHostCallbacks* callbacks);
```

Use `STARSIM_PLUGIN_API` and `extern "C"` exactly as the sample does. Do not send C++ classes, STL containers, exceptions, allocator-owned objects or compiler-specific types across the ABI boundary.

Every input/output struct includes `struct_size`. Validate it before accessing the rest of the structure. Set `abi_version` to `STARSIM_CORE_PLUGIN_ABI_VERSION`.

## 7. Describe the plugin and processors

`SSC_PluginInfo` declares the DLL-wide identity:

```cpp
constexpr SSC_PluginInfo kPluginInfo = {
    sizeof(SSC_PluginInfo),
    STARSIM_CORE_PLUGIN_ABI_VERSION,
    "com.example.planetary-tools",
    "Planetary Tools",
    "1.0.0",
    "Example Author",
    "Small demonstrative planetary processors.",
    1
};
```

Rules:

- use a stable, globally unique plugin ID such as a reverse-domain name;
- do not use the reserved `core.` prefix;
- keep IDs stable after publishing, because projects and presets refer to them;
- use semantic version strings such as `1.2.0`;
- keep all returned UTF-8 strings and definition arrays alive until the DLL unloads.

Each `SSC_PluginProcessorDef` declares a globally unique processor ID, a visible name, category, capabilities and an ordered parameter array:

```cpp
constexpr SSC_PluginParameterDef kParameters[] = {
    { sizeof(SSC_PluginParameterDef), "strength", "Strength", 0.50, 0.0, 1.0, 0.01 },
    { sizeof(SSC_PluginParameterDef), "radius",   "Radius",   1.50, 0.1, 8.0, 0.10 }
};

constexpr SSC_PluginProcessorDef kProcessors[] = {
    {
        sizeof(SSC_PluginProcessorDef),
        "com.example.planetary-tools.soft_filter",
        "Soft Planetary Filter",
        "Detail",
        SSC_PLUGIN_CAP_GRAYSCALE | SSC_PLUGIN_CAP_RGB | SSC_PLUGIN_CAP_SUPPORTS_PREVIEW,
        2,
        kParameters
    }
};
```

StarSim Core uses `name`, `minimum_value`, `maximum_value`, `default_value` and `step` to generate the processor controls. The order in `kParameters` is also the order of values received by `ssc_plugin_process`. The host validates definitions, but the plugin must still validate `parameter_count` and every received value.

`SSC_PLUGIN_CAP_SUPPORTS_PREVIEW` means the processor can run during interactive preview. Omit it when an algorithm is unsuitable for frequent low-latency execution; it remains available for definitive/full-resolution processing.

## 8. Image-buffer contract

- Samples are normalized planar `float32` values.
- `channel_count` is 1 for grayscale or 3 for RGB.
- Each plane contains `width * height` consecutive samples.
- V1 output dimensions and channel count must equal input dimensions and channel count.
- The host owns all structs, plane arrays and pixel memory.
- Read input planes and write output planes only during `ssc_plugin_process`.
- Never free, reallocate, retain or write to input pointers.
- Verify multiplication overflow before calculating `width * height`.
- Write finite output. Ordinary processors should keep values in `[0, 1]` unless a documented downstream operation intentionally supports a wider range.

Typical processing skeleton:

```cpp
const size_t count = static_cast<size_t>(in_buffer->width) * in_buffer->height;
for (uint32_t channel = 0; channel < in_buffer->channel_count; ++channel)
{
    const float* src = in_buffer->planes[channel];
    float* dst = out_buffer->planes[channel];
    if (!src || !dst) return SSC_PLUGIN_STATUS_INVALID_ARGUMENT;

    for (size_t i = 0; i < count; ++i)
    {
        if ((i & 0xFFFFu) == 0 && callbacks && callbacks->is_cancelled &&
            callbacks->is_cancelled(callbacks->user_data))
            return SSC_PLUGIN_STATUS_CANCELLED;

        dst[i] = std::clamp(src[i], 0.0f, 1.0f); // replace with the actual operation
    }
}
return SSC_PLUGIN_STATUS_OK;
```

`quality` is either `SSC_PLUGIN_QUALITY_INTERACTIVE_PREVIEW` or `SSC_PLUGIN_QUALITY_FULL_RESOLUTION`. It is valid to use a faster approximation for preview, but both paths must honor the same visible parameter meaning and must remain deterministic for identical inputs.

## 9. Cancellation, progress, logging and errors

Long loops must poll `callbacks->is_cancelled` often enough to stop promptly. Report progress with a finite fraction from 0 to 1 and a short UTF-8 stage description:

```cpp
if (callbacks && callbacks->report_progress)
    callbacks->report_progress(callbacks->user_data, 0.5, "Processing second pass");
```

Use `log_message` for diagnostic information, not per-pixel messages. StarSim Core writes these messages to `logs/starsim-core-YYYYMMDD.log`.

Return the most specific `SSC_PluginStatus`: `OK`, `INVALID_ARGUMENT`, `ABI_MISMATCH`, `OUT_OF_MEMORY`, `CANCELLED`, `PROCESSING_FAILED` or `NOT_SUPPORTED`. Catch all C++ exceptions inside every export. No exception may cross into the host.

## 10. Install and find the generated panel

1. Start StarSim Core.
2. Open **Plugins → Plugin Manager**.
3. Choose **Install plugin…** and select the Release x64 DLL.
4. Put required dependency DLLs beside it in `%AppData%\StarSimCore\plugins`.
5. Enable external native plugins if disabled.
6. Restart StarSim Core so the DLL can be validated and loaded.
7. Switch to Expert mode and expand the `PLUGINS` group.
8. Expand the processor inline, or use its separate-panel button for a movable window.

The manager reports `Loaded`, `Rejected`, `Not loaded` or `Restart required`. Rejection details appear in the manager and diagnostic log. Remove/disable is recoverable: user plugins are moved to the disabled archive rather than permanently deleted.

Built-in order is unchanged. External processors are appended after built-in Tone processors in discovery order.

## 11. Projects, presets and compatibility

The processor ID and parameter IDs are persistence contracts. After releasing a plugin:

- do not reuse an existing ID for a different operation;
- do not reorder parameters without a compatibility plan;
- preserve parameter meaning and units across patch releases;
- prefer adding a new processor ID for a mathematically incompatible algorithm;
- treat a missing plugin as a dependency failure, never as permission to substitute another processor.

Plugin enabled state and parameters are captured by Undo/Redo, custom presets and `.starsim` projects. Full-resolution export reruns the same configured processor in the canonical pipeline.

## 12. Distribution checklist

Before publishing a plugin DLL:

- build Release x64, not Win32 or ARM64;
- verify all three required exports exist and use the exact names;
- verify `abi_version`, every `struct_size` and all counts;
- use unique, stable plugin/processor/parameter IDs;
- validate dimensions, channels, pointers, parameter values and quality;
- support cancellation in long operations;
- keep outputs finite and document any intentional out-of-range values;
- catch exceptions at the ABI boundary;
- list any dependency DLLs and their licenses;
- include a README with author, version, algorithm, parameters and trusted download/source location.

## 13. Troubleshooting

**The plugin is Rejected**  
Open Plugin Manager and the diagnostic log. Common causes are a missing export, incompatible ABI, invalid metadata, a duplicated ID or missing dependency DLL.

**The plugin installs but has no panel**  
Restart the application, enable external plugins, switch to Expert mode, clear the module search field and expand `PLUGINS`.

**The application cannot load the DLL**  
Confirm the plugin and every dependency are Windows x64 Release binaries. Use the same supported MSVC runtime family as the application package.

**The image is unchanged**  
Enable the processor, check parameter order/ranges, confirm the advertised channel capability matches the loaded image and inspect the log for a non-OK status.

**Interactive preview is slow**  
Honor `SSC_PLUGIN_QUALITY_INTERACTIVE_PREVIEW`, avoid allocations inside pixel loops and report `SSC_PLUGIN_CAP_SUPPORTS_PREVIEW` only when the path is appropriate for responsive use.
