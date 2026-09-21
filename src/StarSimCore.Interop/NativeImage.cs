using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StarSimCore.Domain.Imaging;

namespace StarSimCore.Interop;

public sealed class NativeImage : IDisposable
{
    private ImageMetadata? metadata;

    private NativeImage(SscImageSafeHandle handle, ImageMetadata? metadata = null)
    {
        Handle = handle;
        this.metadata = metadata;
    }

    internal SscImageSafeHandle Handle { get; }

    internal static NativeImage FromHandle(nint nativeHandle, ImageMetadata? metadata = null) =>
        new(SscImageSafeHandle.FromNative(nativeHandle), metadata);

    public bool IsDisposed => Handle.IsClosed;

    public static LoadedNativeImage LoadFile(string canonicalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalPath);
        var fileInfo = NativeImageFileInfo.Create();
        var operation = NativeCall.Start(new NativeOperationMetadata(
            "ssc_image_load_file_utf8",
            Algorithm: "Image decoder",
            Parameters: $"path={canonicalPath}"));
        var status = NativeMethods.LoadImageFile(canonicalPath, ref fileInfo, out var nativeHandle);
        operation.Complete(status);
        var image = new NativeImage(SscImageSafeHandle.FromNative(nativeHandle));
        image.metadata = image.GetMetadata();
        return new LoadedNativeImage(
            image,
            fileInfo.FileFormat,
            fileInfo.HadAlpha != 0);
    }

    public static unsafe NativeImage CreateMaster(
        ImageMetadata metadata,
        ReadOnlySpan<float> interleavedPixels,
        uint abiVersion = NativeAbi.ExpectedVersion)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var descriptor = NativeImageDescriptor.FromMetadata(metadata, abiVersion);

        fixed (float* pixelPointer = interleavedPixels)
        {
            var operation = NativeCall.Start(new NativeOperationMetadata(
                "ssc_image_create_master_f32",
                Algorithm: "Master image allocation",
                Parameters: $"channels={metadata.ChannelCount};bitDepth={metadata.SourceBitDepth};values={interleavedPixels.Length}",
                ImageWidth: metadata.Width,
                ImageHeight: metadata.Height));
            var status = NativeMethods.CreateMasterImage(
                in descriptor,
                (nint)pixelPointer,
                (ulong)interleavedPixels.Length,
                out var nativeHandle);
            operation.Complete(status);
            return new NativeImage(SscImageSafeHandle.FromNative(nativeHandle), metadata);
        }
    }

    public static unsafe NativeImage CreateMasterPlanar(
        ImageMetadata metadata,
        ReadOnlySpan<nint> planePointers,
        uint abiVersion = NativeAbi.ExpectedVersion)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (planePointers.Length < metadata.ChannelCount)
            throw new ArgumentException("Plane pointer count is less than channel count.", nameof(planePointers));

        var descriptor = NativeImageDescriptor.FromMetadata(metadata, abiVersion);
        fixed (nint* planes = planePointers)
        {
            var operation = NativeCall.Start(new NativeOperationMetadata(
                "ssc_image_create_master_planar_f32",
                Algorithm: "Master image planar allocation",
                Parameters: $"channels={metadata.ChannelCount};bitDepth={metadata.SourceBitDepth}",
                ImageWidth: metadata.Width,
                ImageHeight: metadata.Height));
            var status = NativeMethods.CreateMasterPlanarImage(
                in descriptor,
                (nint)planes,
                out var nativeHandle);
            operation.Complete(status);
            return new NativeImage(SscImageSafeHandle.FromNative(nativeHandle), metadata);
        }
    }

    public unsafe void GetPlanePointers(Span<nint> outPlanes)
    {
        var imageMetadata = metadata ?? GetMetadata();
        if (outPlanes.Length < imageMetadata.ChannelCount)
            throw new ArgumentException("Buffer is too small for channel count.", nameof(outPlanes));

        fixed (nint* planes = outPlanes)
        {
            var planesPtr = (nint)planes;
            var capacity = (uint)outPlanes.Length;
            UseHandle(pointer =>
            {
                var operation = NativeCall.Start(new NativeOperationMetadata(
                    "ssc_image_get_plane_pointers",
                    Algorithm: "Planar buffer extraction",
                    ImageWidth: imageMetadata.Width,
                    ImageHeight: imageMetadata.Height));
                var status = NativeMethods.GetPlanePointers(pointer, planesPtr, capacity);
                operation.Complete(status);
                return 0;
            });
        }
    }

    public NativeImage CloneWorking()
    {
        var imageMetadata = metadata ?? GetMetadata();
        var nativeHandle = UseHandle(
            pointer =>
            {
                var operation = NativeCall.Start(new NativeOperationMetadata(
                    "ssc_image_clone_working",
                    Algorithm: "Working image clone",
                    ImageWidth: imageMetadata.Width,
                    ImageHeight: imageMetadata.Height));
                var status = NativeMethods.CloneWorkingImage(pointer, out var clone);
                operation.Complete(status);
                return clone;
            });
        return new NativeImage(SscImageSafeHandle.FromNative(nativeHandle), imageMetadata);
    }

    public NativeImage CreateScaledPreview(
        float resolutionScale,
        CancellationToken cancellationToken = default)
    {
        if (!float.IsFinite(resolutionScale) || resolutionScale <= 0 || resolutionScale > 1)
            throw new ArgumentOutOfRangeException(nameof(resolutionScale));
        cancellationToken.ThrowIfCancellationRequested();
        var sourceMetadata = metadata ?? GetMetadata();
        using var cancellation = new NativeCancellationFlag(cancellationToken);
        var nativeHandle = UseHandle(
            pointer =>
            {
                var operation = NativeCall.Start(new NativeOperationMetadata(
                    "ssc_image_create_scaled_preview",
                    Algorithm: "Bilinear preview reduction",
                    Parameters: $"scale={resolutionScale:R}",
                    ImageWidth: sourceMetadata.Width,
                    ImageHeight: sourceMetadata.Height));
                var status = NativeMethods.CreateScaledPreview(
                    pointer,
                    resolutionScale,
                    cancellation.Pointer,
                    out var output);
                operation.Complete(status, cancellationToken);
                return output;
            });
        var scaledMetadata = sourceMetadata with
        {
            Width = Math.Max(1, (uint)MathF.Round(
                sourceMetadata.Width * resolutionScale,
                MidpointRounding.AwayFromZero)),
            Height = Math.Max(1, (uint)MathF.Round(
                sourceMetadata.Height * resolutionScale,
                MidpointRounding.AwayFromZero)),
        };
        return new NativeImage(SscImageSafeHandle.FromNative(nativeHandle), scaledMetadata);
    }

    public ImageMetadata GetMetadata() =>
        UseHandle(
            pointer =>
            {
                var descriptor = NativeImageDescriptor.FromMetadata(
                    new ImageMetadata(1, 1, 1, 8, ImageColorModel.Grayscale),
                    NativeAbi.ExpectedVersion);
                var operation = NativeCall.Start(new NativeOperationMetadata("ssc_image_get_descriptor"));
                var status = NativeMethods.GetImageDescriptor(pointer, ref descriptor);
                operation.Complete(status);
                metadata = descriptor.ToMetadata();
                return metadata;
            });

    public NativeImageIdentity GetIdentity() =>
        UseHandle(
            pointer =>
            {
                var imageMetadata = metadata;
                var operation = NativeCall.Start(new NativeOperationMetadata(
                    "ssc_image_get_identity",
                    Algorithm: "Image identity",
                    ImageWidth: imageMetadata?.Width ?? 0,
                    ImageHeight: imageMetadata?.Height ?? 0));
                var status = NativeMethods.GetImageIdentity(
                    pointer,
                    out var sourceIdentity,
                    out var instanceIdentity,
                    out var isMaster);
                operation.Complete(status);
                return new NativeImageIdentity(
                    sourceIdentity,
                    instanceIdentity,
                    isMaster != 0);
            });

    public unsafe byte[] RenderBgra8()
    {
        var metadata = GetMetadata();
        var stride = checked(metadata.Width * 4);
        var destination = new byte[checked((int)((ulong)stride * metadata.Height))];

        var addedReference = false;
        try
        {
            Handle.DangerousAddRef(ref addedReference);
            fixed (byte* destinationPointer = destination)
            {
                var operation = NativeCall.Start(new NativeOperationMetadata(
                    "ssc_image_render_bgra8",
                    Algorithm: "Display conversion",
                    Parameters: $"stride={stride};destinationBytes={destination.Length}",
                    ImageWidth: metadata.Width,
                    ImageHeight: metadata.Height));
                var status = NativeMethods.RenderImageBgra8(
                    Handle.DangerousGetHandle(),
                    (nint)destinationPointer,
                    (ulong)destination.Length,
                    stride);
                operation.Complete(status);
            }
        }
        finally
        {
            if (addedReference)
            {
                Handle.DangerousRelease();
            }
        }

        return destination;
    }

    public NativeImage Process(
        NativeProcessorRequest request,
        CancellationToken cancellationToken = default,
        NativeOperationMetadata? diagnosticMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var imageMetadata = metadata ?? GetMetadata();
        var parameters = NativeProcessorParameters.FromRequest(request);
        using var cancellation = new NativeCancellationFlag(cancellationToken);
        var nativeHandle = UseHandle(
            pointer =>
            {
                var details = diagnosticMetadata ?? new NativeOperationMetadata(
                    "ssc_image_process",
                    Algorithm: request.Kind.ToString(),
                    Parameters: FormatParameters([request.Value0, request.Value1, request.Value2, request.Value3]),
                    ImageWidth: imageMetadata.Width,
                    ImageHeight: imageMetadata.Height);
                var operation = NativeCall.Start(details);
                var status = NativeMethods.ProcessImage(
                    pointer,
                    in parameters,
                    cancellation.Pointer,
                    out var output);
                operation.Complete(status, cancellationToken);
                return output;
            });
        return new NativeImage(SscImageSafeHandle.FromNative(nativeHandle), imageMetadata);
    }

    public NativeImage ProcessExpert(
        NativeExpertProcessorRequest request,
        CancellationToken cancellationToken = default,
        NativeOperationMetadata? diagnosticMetadata = null,
        Action<NativeProgressUpdate>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var imageMetadata = metadata ?? GetMetadata();
        var parameters = NativeExpertProcessorParameters.FromRequest(request);
        using var cancellation = new NativeCancellationFlag(cancellationToken);
        GCHandle progressHandle = default;
        try
        {
            nint progressPointer = 0;
            nint progressContext = 0;
            if (progress is not null)
            {
                progressHandle = GCHandle.Alloc(progress);
                progressPointer = NativeProgressFunctionPointer;
                progressContext = GCHandle.ToIntPtr(progressHandle);
            }
            var nativeHandle = UseHandle(
                pointer =>
                {
                    var operationName = progress is null
                        ? "ssc_image_process_expert"
                        : "ssc_image_process_expert_with_progress";
                    var details = diagnosticMetadata ?? new NativeOperationMetadata(
                        operationName,
                        Algorithm: request.Kind.ToString(),
                        Parameters: FormatParameters(request.Values),
                        ImageWidth: imageMetadata.Width,
                        ImageHeight: imageMetadata.Height);
                    var operation = NativeCall.Start(details);
                    NativeStatus status;
                    nint output;
                    if (progress is null)
                    {
                        status = NativeMethods.ProcessExpertImage(
                            pointer,
                            in parameters,
                            cancellation.Pointer,
                            out output);
                    }
                    else
                    {
                        status = NativeMethods.ProcessExpertImageWithProgress(
                            pointer,
                            in parameters,
                            cancellation.Pointer,
                            progressPointer,
                            progressContext,
                            out output);
                    }
                    operation.Complete(status, cancellationToken);
                    return output;
                });
            return new NativeImage(SscImageSafeHandle.FromNative(nativeHandle), imageMetadata);
        }
        finally
        {
            if (progressHandle.IsAllocated) progressHandle.Free();
        }
    }

    private static unsafe nint NativeProgressFunctionPointer =>
        (nint)(delegate* unmanaged[Cdecl]<NativeProgressInfo*, nint, void>)&ReportNativeProgress;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void ReportNativeProgress(NativeProgressInfo* progress, nint userContext)
    {
        try
        {
            if (userContext == 0 || progress is null) return;
            if (GCHandle.FromIntPtr(userContext).Target is Action<NativeProgressUpdate> callback)
                callback(progress->ToUpdate());
        }
        catch
        {
            // Exceptions must never unwind through the native callback boundary.
        }
    }

    public unsafe NativeHistogram ComputeHistogram(uint binCount = 256, CancellationToken cancellationToken = default)
    {
        if (binCount < 2) throw new ArgumentOutOfRangeException(nameof(binCount));
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = GetMetadata();
        var red = new ulong[binCount];
        var green = metadata.ChannelCount == 3 ? new ulong[binCount] : [];
        var blue = metadata.ChannelCount == 3 ? new ulong[binCount] : [];
        var stats = NativeHistogramStats.Create(binCount);
        using var cancellation = new NativeCancellationFlag(cancellationToken);
        UseHandle(
            pointer =>
            {
                fixed (ulong* redPointer = red)
                fixed (ulong* greenPointer = green)
                fixed (ulong* bluePointer = blue)
                {
                    var operation = NativeCall.Start(new NativeOperationMetadata(
                        "ssc_image_compute_histogram",
                        Algorithm: "Histogram",
                        Parameters: $"bins={binCount}",
                        ImageWidth: metadata.Width,
                        ImageHeight: metadata.Height));
                    var status = NativeMethods.ComputeHistogram(
                        pointer,
                        (nint)redPointer,
                        green.Length == 0 ? 0 : (nint)greenPointer,
                        blue.Length == 0 ? 0 : (nint)bluePointer,
                        binCount,
                        ref stats,
                        cancellation.Pointer);
                    operation.Complete(status, cancellationToken);
                }
                return 0;
            });
        return stats.ToResult(red, green, blue);
    }

    public unsafe float[] GetPixel(uint x, uint y)
    {
        var metadata = GetMetadata();
        var channels = new float[metadata.ChannelCount];
        UseHandle(
            pointer =>
            {
                fixed (float* channelPointer = channels)
                {
                    var operation = NativeCall.Start(new NativeOperationMetadata(
                        "ssc_image_get_pixel_f32",
                        Algorithm: "Pixel inspection",
                        Parameters: $"x={x};y={y};capacity={channels.Length}",
                        ImageWidth: metadata.Width,
                        ImageHeight: metadata.Height));
                    var status = NativeMethods.GetPixel(
                        pointer,
                        x,
                        y,
                        (nint)channelPointer,
                        (uint)channels.Length);
                    operation.Complete(status);
                }
                return 0;
            });
        return channels;
    }

    /// <summary>Copies the native planar float32 image into a managed planar buffer.</summary>
    public float[] CopyPlanarPixels()
    {
        var imageMetadata = metadata ?? GetMetadata();
        var planeLength = checked((int)((ulong)imageMetadata.Width * imageMetadata.Height));
        var result = new float[checked(planeLength * (int)imageMetadata.ChannelCount)];
        var planes = new nint[imageMetadata.ChannelCount];
        GetPlanePointers(planes);
        for (var channel = 0; channel < planes.Length; channel++)
            Marshal.Copy(planes[channel], result, channel * planeLength, planeLength);
        return result;
    }

    /// <summary>Creates an image by interleaving a managed planar float32 buffer.</summary>
    public static NativeImage CreateMasterFromPlanar(ImageMetadata metadata, ReadOnlySpan<float> planarPixels)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var planeLength = checked((int)((ulong)metadata.Width * metadata.Height));
        var expected = checked(planeLength * (int)metadata.ChannelCount);
        if (planarPixels.Length != expected)
            throw new ArgumentException($"Expected {expected} planar values.", nameof(planarPixels));

        var interleaved = new float[expected];
        for (var pixel = 0; pixel < planeLength; pixel++)
        for (var channel = 0; channel < metadata.ChannelCount; channel++)
            interleaved[pixel * metadata.ChannelCount + channel] = planarPixels[channel * planeLength + pixel];
        return CreateMaster(metadata, interleaved);
    }

    public void ExportFile(
        string destinationPath,
        NativeExportFormat format,
        CancellationToken cancellationToken = default,
        Action<NativeProgressUpdate>? progress = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        cancellationToken.ThrowIfCancellationRequested();
        var imageMetadata = metadata ?? GetMetadata();
        using var cancellation = new NativeCancellationFlag(cancellationToken);
        GCHandle progressHandle = default;
        try
        {
            nint progressPointer = 0;
            nint progressContext = 0;
            if (progress is not null)
            {
                progressHandle = GCHandle.Alloc(progress);
                progressPointer = NativeProgressFunctionPointer;
                progressContext = GCHandle.ToIntPtr(progressHandle);
            }

            UseHandle(
                pointer =>
                {
                    var operation = NativeCall.Start(new NativeOperationMetadata(
                        "ssc_image_export_file_utf8",
                        Algorithm: $"Export {format}",
                        Parameters: $"format={format};destination={destinationPath}",
                        ImageWidth: imageMetadata.Width,
                        ImageHeight: imageMetadata.Height));
                    var status = NativeMethods.ExportImageFile(
                        pointer,
                        destinationPath,
                        format,
                        cancellation.Pointer,
                        progressPointer,
                        progressContext);
                    operation.Complete(status, cancellationToken);
                    return 0;
                });
        }
        finally
        {
            if (progressHandle.IsAllocated)
            {
                progressHandle.Free();
            }
        }
    }

    public void Dispose() => Handle.Dispose();

    private static string FormatParameters(IReadOnlyList<float> values) =>
        string.Join(',', values.Select((value, index) =>
            $"p{index}={value.ToString("R", CultureInfo.InvariantCulture)}"));

    internal T UseHandle<T>(Func<nint, T> action)
    {
        var addedReference = false;
        try
        {
            Handle.DangerousAddRef(ref addedReference);
            return action(Handle.DangerousGetHandle());
        }
        finally
        {
            if (addedReference)
            {
                Handle.DangerousRelease();
            }
        }
    }
}
