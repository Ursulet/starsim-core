#ifndef STARSIM_CORE_NATIVE_H
#define STARSIM_CORE_NATIVE_H

#include <stdint.h>

#define STARSIM_CORE_NATIVE_ABI_VERSION UINT32_C(1)
#define SSC_IMAGE_DESCRIPTOR_RESERVED_COUNT 8
#define SSC_IMAGE_FILE_INFO_RESERVED_COUNT 4
#define SSC_PROCESSOR_PARAMETERS_RESERVED_COUNT 4
#define SSC_HISTOGRAM_STATS_RESERVED_COUNT 4
#define SSC_EXPERT_PARAMETER_VALUE_COUNT 32
#define SSC_PROGRESS_INFO_RESERVED_COUNT 4

#if defined(_WIN32)
  #if defined(STARSIM_CORE_NATIVE_EXPORTS)
    #define STARSIM_CORE_NATIVE_API __declspec(dllexport)
  #else
    #define STARSIM_CORE_NATIVE_API __declspec(dllimport)
  #endif
#else
  #define STARSIM_CORE_NATIVE_API
#endif

#ifdef __cplusplus
  #define SSC_NOEXCEPT noexcept
extern "C" {
#else
  #define SSC_NOEXCEPT
#endif

typedef int32_t SSC_Status;
enum
{
    SSC_STATUS_OK = 0,
    SSC_STATUS_INVALID_ARGUMENT = 1,
    SSC_STATUS_OUT_OF_MEMORY = 2,
    SSC_STATUS_ABI_MISMATCH = 3,
    SSC_STATUS_BUFFER_TOO_SMALL = 4,
    SSC_STATUS_UNSUPPORTED = 5,
    SSC_STATUS_INTERNAL_ERROR = 6,
    SSC_STATUS_IO_ERROR = 7,
    SSC_STATUS_DECODE_ERROR = 8,
    SSC_STATUS_CANCELLED = 9
};

typedef int32_t SSC_ColorModel;
enum
{
    SSC_COLOR_MODEL_GRAYSCALE = 1,
    SSC_COLOR_MODEL_RGB = 2
};

typedef int32_t SSC_PixelFormat;
enum
{
    SSC_PIXEL_FORMAT_FLOAT32_PLANAR = 1,
    SSC_PIXEL_FORMAT_BGRA8 = 2
};

typedef int32_t SSC_ImageFileFormat;
enum
{
    SSC_IMAGE_FILE_FORMAT_PNG = 1,
    SSC_IMAGE_FILE_FORMAT_TIFF = 2
};

typedef int32_t SSC_ProcessorKind;
enum
{
    SSC_PROCESSOR_LINEAR_EXPOSURE = 1,
    SSC_PROCESSOR_CONTRAST = 2,
    SSC_PROCESSOR_GAMMA = 3,
    SSC_PROCESSOR_RGB_BALANCE = 4,
    SSC_PROCESSOR_SATURATION = 5,
    SSC_PROCESSOR_WAVELET = 10,
    SSC_PROCESSOR_UNSHARP_MASK = 11,
    SSC_PROCESSOR_MULTI_SCALE_SHARPEN = 12,
    SSC_PROCESSOR_NOISE_REDUCTION = 13,
    SSC_PROCESSOR_DERINGING = 14,
    SSC_PROCESSOR_RICHARDSON_LUCY = 15,
    SSC_PROCESSOR_RGB_ALIGN = 16,
    SSC_PROCESSOR_ADVANCED_COLOR = 17,
    SSC_PROCESSOR_ADVANCED_TONE = 18,
    SSC_PROCESSOR_LOCAL_DETAIL = 19
};

typedef int32_t SSC_ProcessingQuality;
enum
{
    SSC_PROCESSING_QUALITY_INTERACTIVE_PREVIEW = 1,
    SSC_PROCESSING_QUALITY_FULL_RESOLUTION = 2
};

typedef struct SSC_Image SSC_Image;
typedef struct SSC_Pipeline SSC_Pipeline;
typedef SSC_Image* SSC_ImageHandle;
typedef SSC_Pipeline* SSC_PipelineHandle;

typedef struct SSC_ImageDescriptor
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t width;
    uint32_t height;
    uint32_t channel_count;
    uint32_t source_bit_depth;
    SSC_ColorModel color_model;
    SSC_PixelFormat working_pixel_format;
    uint64_t reserved[SSC_IMAGE_DESCRIPTOR_RESERVED_COUNT];
} SSC_ImageDescriptor;

typedef struct SSC_ImageFileInfo
{
    uint32_t struct_size;
    uint32_t abi_version;
    SSC_ImageFileFormat file_format;
    uint32_t had_alpha;
    uint64_t reserved[SSC_IMAGE_FILE_INFO_RESERVED_COUNT];
} SSC_ImageFileInfo;

typedef struct SSC_ProcessorParameters
{
    uint32_t struct_size;
    uint32_t abi_version;
    SSC_ProcessorKind processor_kind;
    uint32_t enabled;
    SSC_ProcessingQuality quality;
    float resolution_scale;
    float value0;
    float value1;
    float value2;
    float value3;
    uint64_t reserved[SSC_PROCESSOR_PARAMETERS_RESERVED_COUNT];
} SSC_ProcessorParameters;

typedef struct SSC_ExpertProcessorParameters
{
    uint32_t struct_size;
    uint32_t abi_version;
    SSC_ProcessorKind processor_kind;
    uint32_t enabled;
    SSC_ProcessingQuality quality;
    float resolution_scale;
    uint32_t value_count;
    uint32_t reserved0;
    float values[SSC_EXPERT_PARAMETER_VALUE_COUNT];
    uint64_t reserved[4];
} SSC_ExpertProcessorParameters;

typedef struct SSC_HistogramStats
{
    uint32_t struct_size;
    uint32_t abi_version;
    uint32_t channel_count;
    uint32_t bin_count;
    float minimum[3];
    float maximum[3];
    double mean[3];
    uint64_t reserved[SSC_HISTOGRAM_STATS_RESERVED_COUNT];
} SSC_HistogramStats;

typedef int32_t SSC_ProgressStage;
enum
{
    SSC_PROGRESS_STAGE_PROCESSOR = 1,
    SSC_PROGRESS_STAGE_WAVELET_LAYER = 2,
    SSC_PROGRESS_STAGE_RICHARDSON_LUCY_ITERATION = 3
};

typedef struct SSC_ProgressInfo
{
    uint32_t struct_size;
    uint32_t abi_version;
    SSC_ProgressStage stage;
    uint32_t current_step;
    uint32_t total_steps;
    float fraction;
    uint64_t reserved[SSC_PROGRESS_INFO_RESERVED_COUNT];
} SSC_ProgressInfo;

typedef void (*SSC_ProgressCallback)(
    const SSC_ProgressInfo* progress,
    void* user_context);

/* Phase 1 compatibility probe/context. */
typedef struct starsim_core_context starsim_core_context;

STARSIM_CORE_NATIVE_API uint32_t starsim_core_native_abi_version(void) SSC_NOEXCEPT;
STARSIM_CORE_NATIVE_API int32_t starsim_core_context_create(
    starsim_core_context** out_context) SSC_NOEXCEPT;
STARSIM_CORE_NATIVE_API void starsim_core_context_destroy(
    starsim_core_context* context) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API void ssc_buffer_pool_configure(
    uint64_t maximum_retained_bytes) SSC_NOEXCEPT;
STARSIM_CORE_NATIVE_API void ssc_buffer_pool_trim(void) SSC_NOEXCEPT;
STARSIM_CORE_NATIVE_API void ssc_buffer_pool_get_metrics(
    uint64_t* retained_bytes,
    uint32_t* retained_buffer_count,
    uint64_t* reuse_count,
    uint64_t* allocation_count) SSC_NOEXCEPT;

/* Thread-local UTF-8 error detail for the most recent failing call. */
STARSIM_CORE_NATIVE_API SSC_Status ssc_get_last_error_utf8(
    char* buffer,
    uint32_t buffer_size,
    uint32_t* required_size) SSC_NOEXCEPT;

/* Copies decoded interleaved float32 pixels into an immutable planar master. */
STARSIM_CORE_NATIVE_API SSC_Status ssc_image_create_master_f32(
    const SSC_ImageDescriptor* descriptor,
    const float* interleaved_pixels,
    uint64_t pixel_value_count,
    SSC_ImageHandle* out_image) SSC_NOEXCEPT;

/* Opens PNG/TIFF read-only. Alpha is detected and discarded, never a color channel. */
STARSIM_CORE_NATIVE_API SSC_Status ssc_image_load_file_utf8(
    const char* canonical_path_utf8,
    SSC_ImageFileInfo* out_file_info,
    SSC_ImageHandle* out_image) SSC_NOEXCEPT;

/* Creates a distinct derived working image; no mutation API is exposed. */
STARSIM_CORE_NATIVE_API SSC_Status ssc_image_clone_working(
    SSC_ImageHandle source,
    SSC_ImageHandle* out_image) SSC_NOEXCEPT;

/* Creates a bilinear reduced-resolution preview while preserving source identity. */
STARSIM_CORE_NATIVE_API SSC_Status ssc_image_create_scaled_preview(
    SSC_ImageHandle source,
    float resolution_scale,
    const volatile uint32_t* cancellation_flag,
    SSC_ImageHandle* out_image) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API void ssc_image_release(SSC_ImageHandle image) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API SSC_Status ssc_image_get_descriptor(
    SSC_ImageHandle image,
    SSC_ImageDescriptor* out_descriptor) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API SSC_Status ssc_image_get_identity(
    SSC_ImageHandle image,
    uint64_t* out_source_identity,
    uint64_t* out_instance_identity,
    uint8_t* out_is_master) SSC_NOEXCEPT;

/* Display conversion only. BGRA8 never aliases or replaces float32 master data. */
STARSIM_CORE_NATIVE_API SSC_Status ssc_image_render_bgra8(
    SSC_ImageHandle image,
    uint8_t* destination,
    uint64_t destination_size,
    uint32_t destination_stride) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API SSC_Status ssc_image_process(
    SSC_ImageHandle input,
    const SSC_ProcessorParameters* parameters,
    const volatile uint32_t* cancellation_flag,
    SSC_ImageHandle* out_image) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API SSC_Status ssc_image_process_expert(
    SSC_ImageHandle input,
    const SSC_ExpertProcessorParameters* parameters,
    const volatile uint32_t* cancellation_flag,
    SSC_ImageHandle* out_image) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API SSC_Status ssc_image_process_expert_with_progress(
    SSC_ImageHandle input,
    const SSC_ExpertProcessorParameters* parameters,
    const volatile uint32_t* cancellation_flag,
    SSC_ProgressCallback progress_callback,
    void* progress_context,
    SSC_ImageHandle* out_image) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API SSC_Status ssc_image_compute_histogram(
    SSC_ImageHandle image,
    uint64_t* bins_red_or_gray,
    uint64_t* bins_green,
    uint64_t* bins_blue,
    uint32_t bin_count,
    SSC_HistogramStats* out_stats,
    const volatile uint32_t* cancellation_flag) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API SSC_Status ssc_image_get_pixel_f32(
    SSC_ImageHandle image,
    uint32_t x,
    uint32_t y,
    float* out_channels,
    uint32_t channel_capacity) SSC_NOEXCEPT;

/* Retrieves pointers to contiguous planar channel buffers. Valid as long as image is alive. */
STARSIM_CORE_NATIVE_API SSC_Status ssc_image_get_plane_pointers(
    SSC_ImageHandle image,
    const float** out_planes,
    uint32_t plane_capacity) SSC_NOEXCEPT;

/* Creates an immutable master image from planar float32 channel buffers. */
STARSIM_CORE_NATIVE_API SSC_Status ssc_image_create_master_planar_f32(
    const SSC_ImageDescriptor* descriptor,
    const float* const* planes,
    SSC_ImageHandle* out_image) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API SSC_Status ssc_pipeline_create(
    SSC_ImageHandle immutable_master,
    SSC_PipelineHandle* out_pipeline) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API SSC_Status ssc_pipeline_get_output_snapshot(
    SSC_PipelineHandle pipeline,
    SSC_ImageHandle* out_image) SSC_NOEXCEPT;

STARSIM_CORE_NATIVE_API void ssc_pipeline_release(
    SSC_PipelineHandle pipeline) SSC_NOEXCEPT;

typedef int32_t SSC_ExportFormat;
enum
{
    SSC_EXPORT_FORMAT_TIFF_16 = 1,
    SSC_EXPORT_FORMAT_PNG_16 = 2,
    SSC_EXPORT_FORMAT_PNG_8 = 3,
    SSC_EXPORT_FORMAT_TIFF_FLOAT_32 = 4
};

/* Exports the processed float32 image to disk.
   Integer formats clamp values to [0,1]. TIFF float32 preserves signed values exactly.
   The destination path must not reference the immutable source. */
STARSIM_CORE_NATIVE_API SSC_Status ssc_image_export_file_utf8(
    SSC_ImageHandle image,
    const char* destination_path_utf8,
    SSC_ExportFormat format,
    const volatile uint32_t* cancellation_flag,
    SSC_ProgressCallback progress_callback,
    void* progress_context) SSC_NOEXCEPT;

#ifdef __cplusplus
}
#endif

#endif
