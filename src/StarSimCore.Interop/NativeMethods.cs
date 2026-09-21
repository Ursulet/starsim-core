using System.Runtime.InteropServices;

namespace StarSimCore.Interop;

internal static partial class NativeMethods
{
    internal const string LibraryName = "StarSimCore.Native";

    [LibraryImport(LibraryName, EntryPoint = "starsim_core_native_abi_version")]
    internal static partial uint GetAbiVersion();

    [LibraryImport(LibraryName, EntryPoint = "starsim_core_context_create")]
    internal static partial NativeStatus CreateContext(out nint context);

    [LibraryImport(LibraryName, EntryPoint = "starsim_core_context_destroy")]
    internal static partial void DestroyContext(nint context);

    [LibraryImport(LibraryName, EntryPoint = "ssc_buffer_pool_configure")]
    internal static partial void ConfigureBufferPool(ulong maximumRetainedBytes);

    [LibraryImport(LibraryName, EntryPoint = "ssc_buffer_pool_trim")]
    internal static partial void TrimBufferPool();

    [LibraryImport(LibraryName, EntryPoint = "ssc_buffer_pool_get_metrics")]
    internal static partial void GetBufferPoolMetrics(
        out ulong retainedBytes,
        out uint retainedBufferCount,
        out ulong reuseCount,
        out ulong allocationCount);

    [LibraryImport(LibraryName, EntryPoint = "ssc_get_last_error_utf8")]
    internal static partial NativeStatus GetLastErrorUtf8(
        nint buffer,
        uint bufferSize,
        out uint requiredSize);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_create_master_f32")]
    internal static partial NativeStatus CreateMasterImage(
        in NativeImageDescriptor descriptor,
        nint interleavedPixels,
        ulong pixelValueCount,
        out nint image);

    [LibraryImport(
        LibraryName,
        EntryPoint = "ssc_image_load_file_utf8",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial NativeStatus LoadImageFile(
        string canonicalPath,
        ref NativeImageFileInfo fileInfo,
        out nint image);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_clone_working")]
    internal static partial NativeStatus CloneWorkingImage(
        nint source,
        out nint image);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_create_scaled_preview")]
    internal static partial NativeStatus CreateScaledPreview(
        nint source,
        float resolutionScale,
        nint cancellationFlag,
        out nint image);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_release")]
    internal static partial void ReleaseImage(nint image);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_get_descriptor")]
    internal static partial NativeStatus GetImageDescriptor(
        nint image,
        ref NativeImageDescriptor descriptor);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_get_identity")]
    internal static partial NativeStatus GetImageIdentity(
        nint image,
        out ulong sourceIdentity,
        out ulong instanceIdentity,
        out byte isMaster);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_render_bgra8")]
    internal static partial NativeStatus RenderImageBgra8(
        nint image,
        nint destination,
        ulong destinationSize,
        uint destinationStride);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_process")]
    internal static partial NativeStatus ProcessImage(
        nint input,
        in NativeProcessorParameters parameters,
        nint cancellationFlag,
        out nint image);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_process_expert")]
    internal static partial NativeStatus ProcessExpertImage(
        nint input,
        in NativeExpertProcessorParameters parameters,
        nint cancellationFlag,
        out nint image);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_process_expert_with_progress")]
    internal static partial NativeStatus ProcessExpertImageWithProgress(
        nint input,
        in NativeExpertProcessorParameters parameters,
        nint cancellationFlag,
        nint progressCallback,
        nint progressContext,
        out nint image);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_compute_histogram")]
    internal static partial NativeStatus ComputeHistogram(
        nint image,
        nint binsRedOrGray,
        nint binsGreen,
        nint binsBlue,
        uint binCount,
        ref NativeHistogramStats stats,
        nint cancellationFlag);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_get_pixel_f32")]
    internal static partial NativeStatus GetPixel(
        nint image,
        uint x,
        uint y,
        nint channels,
        uint channelCapacity);

    [LibraryImport(LibraryName, EntryPoint = "ssc_pipeline_create")]
    internal static partial NativeStatus CreatePipeline(
        nint immutableMaster,
        out nint pipeline);

    [LibraryImport(LibraryName, EntryPoint = "ssc_pipeline_get_output_snapshot")]
    internal static partial NativeStatus GetPipelineOutputSnapshot(
        nint pipeline,
        out nint image);

    [LibraryImport(LibraryName, EntryPoint = "ssc_pipeline_release")]
    internal static partial void ReleasePipeline(nint pipeline);

    [LibraryImport(
        LibraryName,
        EntryPoint = "ssc_image_export_file_utf8",
        StringMarshalling = StringMarshalling.Utf8)]
    internal static partial NativeStatus ExportImageFile(
        nint image,
        string destinationPathUtf8,
        NativeExportFormat format,
        nint cancellationFlag,
        nint progressCallback,
        nint progressContext);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_get_plane_pointers")]
    internal static partial NativeStatus GetPlanePointers(
        nint image,
        nint outPlanes,
        uint planeCapacity);

    [LibraryImport(LibraryName, EntryPoint = "ssc_image_create_master_planar_f32")]
    internal static partial NativeStatus CreateMasterPlanarImage(
        in NativeImageDescriptor descriptor,
        nint planes,
        out nint image);
}
