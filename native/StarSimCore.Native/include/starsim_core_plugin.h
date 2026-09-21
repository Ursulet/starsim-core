#ifndef STARSIM_CORE_PLUGIN_H
#define STARSIM_CORE_PLUGIN_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define STARSIM_CORE_PLUGIN_ABI_VERSION 1
#define STARSIM_CORE_PLUGIN_HOST_CALLBACKS_VERSION 1

#if defined(_WIN32) || defined(__CYGWIN__)
#  if defined(STARSIM_CORE_PLUGIN_EXPORTS)
#    define STARSIM_PLUGIN_API __declspec(dllexport)
#  else
#    define STARSIM_PLUGIN_API __declspec(dllimport)
#  endif
#else
#  define STARSIM_PLUGIN_API __attribute__((visibility("default")))
#endif

/* Status codes */
typedef enum SSC_PluginStatus {
    SSC_PLUGIN_STATUS_OK = 0,
    SSC_PLUGIN_STATUS_INVALID_ARGUMENT = 1,
    SSC_PLUGIN_STATUS_ABI_MISMATCH = 2,
    SSC_PLUGIN_STATUS_OUT_OF_MEMORY = 3,
    SSC_PLUGIN_STATUS_CANCELLED = 4,
    SSC_PLUGIN_STATUS_PROCESSING_FAILED = 5,
    SSC_PLUGIN_STATUS_NOT_SUPPORTED = 6
} SSC_PluginStatus;

/* Capabilities */
typedef enum SSC_PluginCapabilities {
    SSC_PLUGIN_CAP_NONE = 0,
    SSC_PLUGIN_CAP_GRAYSCALE = (1 << 0),
    SSC_PLUGIN_CAP_RGB = (1 << 1),
    SSC_PLUGIN_CAP_SUPPORTS_PREVIEW = (1 << 2)
} SSC_PluginCapabilities;

/* Quality mode */
typedef enum SSC_PluginProcessingQuality {
    SSC_PLUGIN_QUALITY_INTERACTIVE_PREVIEW = 0,
    SSC_PLUGIN_QUALITY_FULL_RESOLUTION = 1
} SSC_PluginProcessingQuality;

/* Image buffer ownership:
 * - The host owns the buffer structs, plane-pointer arrays and pixel memory.
 * - The plugin may read input pixels and write output pixels only for the duration
 *   of ssc_plugin_process. It must not retain or free any host pointer. */
typedef struct SSC_PluginImageBuffer {
    uint32_t struct_size;       /* sizeof(SSC_PluginImageBuffer) */
    uint32_t width;             /* Pixel width */
    uint32_t height;            /* Pixel height */
    uint32_t channel_count;     /* 1 for Grayscale, 3 for RGB */
    float* const* planes;       /* Array of channel pointers: float32 planar [channel_count][width * height] */
} SSC_PluginImageBuffer;

/* Parameter definition. Strings and definition storage are plugin-owned UTF-8 data
 * that remain valid until the plugin library is unloaded. */
typedef struct SSC_PluginParameterDef {
    uint32_t struct_size;       /* sizeof(SSC_PluginParameterDef) */
    const char* id;             /* Unique parameter ID within processor (UTF-8) */
    const char* name;           /* Display name (UTF-8) */
    double default_value;       /* Default value */
    double minimum_value;       /* Minimum allowable value */
    double maximum_value;       /* Maximum allowable value */
    double step;                /* Recommended step/increment */
} SSC_PluginParameterDef;

/* Processor definition */
typedef struct SSC_PluginProcessorDef {
    uint32_t struct_size;       /* sizeof(SSC_PluginProcessorDef) */
    const char* id;             /* Unique processor ID, e.g. "diagnostic.tint" (UTF-8) */
    const char* name;           /* Display name, e.g. "Diagnostic Color Tint" (UTF-8) */
    const char* category;       /* Category, e.g. "Diagnostic" or "Filter" (UTF-8) */
    uint32_t capabilities;      /* Bitwise OR of SSC_PluginCapabilities */
    uint32_t parameter_count;   /* Number of parameters */
    const SSC_PluginParameterDef* parameters; /* Array of parameter definitions */
} SSC_PluginProcessorDef;

/* Plugin info */
typedef struct SSC_PluginInfo {
    uint32_t struct_size;       /* sizeof(SSC_PluginInfo) */
    uint32_t abi_version;       /* STARSIM_CORE_PLUGIN_ABI_VERSION */
    const char* plugin_id;      /* Unique plugin identifier (UTF-8) */
    const char* name;           /* Human-readable plugin name (UTF-8) */
    const char* version;        /* Semantic version (UTF-8), e.g. "1.0.0" */
    const char* author;         /* Author or organization (UTF-8) */
    const char* description;    /* Short description (UTF-8) */
    uint32_t processor_count;   /* Number of processors exported */
} SSC_PluginInfo;

/* Host callbacks passed by the host during processing. This layout is versioned by
 * STARSIM_CORE_PLUGIN_HOST_CALLBACKS_VERSION together with struct_size and the
 * enclosing plugin ABI version. Callback pointers and user_data are host-owned,
 * valid only during ssc_plugin_process, and must never be retained by the plugin. */
typedef struct SSC_PluginHostCallbacks {
    uint32_t struct_size;       /* sizeof(SSC_PluginHostCallbacks) */
    void* user_data;            /* Host-provided contextual pointer */
    int (*is_cancelled)(void* user_data);
    void (*report_progress)(void* user_data, double fraction, const char* stage_utf8);
    void (*log_message)(void* user_data, int level, const char* message_utf8);
} SSC_PluginHostCallbacks;

/* Required exported function prototypes */
STARSIM_PLUGIN_API int32_t ssc_plugin_get_info(SSC_PluginInfo* out_info);
STARSIM_PLUGIN_API int32_t ssc_plugin_get_processor_def(uint32_t index, SSC_PluginProcessorDef* out_def);
STARSIM_PLUGIN_API int32_t ssc_plugin_process(
    uint32_t processor_index,
    const SSC_PluginImageBuffer* in_buffer,
    SSC_PluginImageBuffer* out_buffer,
    const double* parameters,             /* May be NULL only when parameter_count is zero */
    uint32_t parameter_count,
    SSC_PluginProcessingQuality quality,
    const SSC_PluginHostCallbacks* callbacks);

/* Standard export names */
#define SSC_PLUGIN_FN_GET_INFO "ssc_plugin_get_info"
#define SSC_PLUGIN_FN_GET_PROCESSOR_DEF "ssc_plugin_get_processor_def"
#define SSC_PLUGIN_FN_PROCESS "ssc_plugin_process"

#ifdef __cplusplus
}
#endif

#endif /* STARSIM_CORE_PLUGIN_H */
