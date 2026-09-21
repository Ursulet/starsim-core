using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StarSimCore.Domain.Imaging;

namespace StarSimCore.Interop.Plugins;

public enum PluginStatus
{
    Ok = 0,
    InvalidArgument = 1,
    AbiMismatch = 2,
    OutOfMemory = 3,
    Cancelled = 4,
    ProcessingFailed = 5,
    NotSupported = 6,
}

[Flags]
public enum PluginCapabilities : uint
{
    None = 0,
    Grayscale = 1 << 0,
    Rgb = 1 << 1,
    SupportsPreview = 1 << 2,
}

public sealed record PluginParameterMetadata(
    string Id,
    string Name,
    double DefaultValue,
    double MinimumValue,
    double MaximumValue,
    double Step);

public sealed record PluginProcessorMetadata(
    uint Index,
    string Id,
    string Name,
    string Category,
    PluginCapabilities Capabilities,
    IReadOnlyList<PluginParameterMetadata> Parameters);

public sealed record PluginMetadata(
    string PluginId,
    string Name,
    string Version,
    string Author,
    string Description,
    IReadOnlyList<PluginProcessorMetadata> Processors);

public sealed class NativePluginException : Exception
{
    public NativePluginException(string message, Exception? inner = null) : base(message, inner) { }
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawPluginInfo
{
    public uint StructSize;
    public uint AbiVersion;
    public nint PluginId;
    public nint Name;
    public nint Version;
    public nint Author;
    public nint Description;
    public uint ProcessorCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawPluginParameterDef
{
    public uint StructSize;
    public nint Id;
    public nint Name;
    public double DefaultValue;
    public double MinimumValue;
    public double MaximumValue;
    public double Step;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawPluginProcessorDef
{
    public uint StructSize;
    public nint Id;
    public nint Name;
    public nint Category;
    public uint Capabilities;
    public uint ParameterCount;
    public nint Parameters;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct RawPluginImageBuffer
{
    public uint StructSize;
    public uint Width;
    public uint Height;
    public uint ChannelCount;
    public float** Planes;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RawPluginHostCallbacks
{
    public uint StructSize;
    public nint UserData;
    public nint IsCancelled;
    public nint ReportProgress;
    public nint LogMessage;
}

public sealed class LoadedPlugin : IDisposable
{
    private const uint SupportedAbiVersion = 1;
    private const uint MaximumProcessorCount = 256;
    private const uint MaximumParameterCount = 256;
    private const PluginCapabilities KnownCapabilities =
        PluginCapabilities.Grayscale | PluginCapabilities.Rgb | PluginCapabilities.SupportsPreview;
    private readonly nint libraryHandle;
    private readonly PluginMetadata metadata;
    private readonly unsafe delegate* unmanaged[Cdecl]<uint, RawPluginImageBuffer*, RawPluginImageBuffer*, double*, uint, int, RawPluginHostCallbacks*, int> processFn;
    private bool disposed;

    private sealed class PluginExecutionContext
    {
        public CancellationToken CancellationToken;
        public Action<double, string>? ReportProgress;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int NativeIsCancelled(nint userData)
    {
        try
        {
            if (userData == nint.Zero) return 0;
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is PluginExecutionContext ctx)
            {
                return ctx.CancellationToken.IsCancellationRequested ? 1 : 0;
            }
        }
        catch
        {
            // Exceptions must never cross the unmanaged callback boundary.
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NativeReportProgress(nint userData, double fraction, nint stageUtf8)
    {
        try
        {
            if (userData == nint.Zero) return;
            var handle = GCHandle.FromIntPtr(userData);
            if (handle.Target is PluginExecutionContext ctx && ctx.ReportProgress != null)
            {
                var stage = stageUtf8 != nint.Zero ? Marshal.PtrToStringUTF8(stageUtf8) ?? string.Empty : string.Empty;
                ctx.ReportProgress(double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0, stage);
            }
        }
        catch
        {
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NativeLogMessage(nint userData, int level, nint messageUtf8)
    {
        try
        {
            var message = messageUtf8 != nint.Zero
                ? Marshal.PtrToStringUTF8(messageUtf8) ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(message)) return;
            var levelName = level switch
            {
                <= 0 => "INFO",
                1 => "WARNING",
                _ => "ERROR",
            };
            DiagnosticService.Current.RecordPluginEvent(levelName, message);
        }
        catch
        {
            // Exceptions must never cross the unmanaged callback boundary.
        }
    }

    private unsafe LoadedPlugin(
        nint libraryHandle,
        PluginMetadata metadata,
        delegate* unmanaged[Cdecl]<uint, RawPluginImageBuffer*, RawPluginImageBuffer*, double*, uint, int, RawPluginHostCallbacks*, int> processFn)
    {
        this.libraryHandle = libraryHandle;
        this.metadata = metadata;
        this.processFn = processFn;
    }

    public PluginMetadata Metadata => metadata;
    public string Path { get; private set; } = string.Empty;

    public static unsafe LoadedPlugin Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Plugin library file not found: {filePath}", filePath);
        }

        if (!NativeLibrary.TryLoad(filePath, out var handle))
        {
            throw new NativePluginException($"Failed to load native library at '{filePath}'.");
        }

        try
        {
            if (!NativeLibrary.TryGetExport(handle, "ssc_plugin_get_info", out var getInfoPtr) ||
                !NativeLibrary.TryGetExport(handle, "ssc_plugin_get_processor_def", out var getProcPtr) ||
                !NativeLibrary.TryGetExport(handle, "ssc_plugin_process", out var processPtr))
            {
                throw new NativePluginException($"Library '{System.IO.Path.GetFileName(filePath)}' is missing required plugin entry points.");
            }

            var getInfo = (delegate* unmanaged[Cdecl]<RawPluginInfo*, int>)getInfoPtr;
            var getProcessorDef = (delegate* unmanaged[Cdecl]<uint, RawPluginProcessorDef*, int>)getProcPtr;
            var process = (delegate* unmanaged[Cdecl]<uint, RawPluginImageBuffer*, RawPluginImageBuffer*, double*, uint, int, RawPluginHostCallbacks*, int>)processPtr;

            var rawInfo = new RawPluginInfo
            {
                StructSize = (uint)sizeof(RawPluginInfo)
            };

            var status = getInfo(&rawInfo);
            if (status != 0)
            {
                throw new NativePluginException($"Plugin get_info failed with status code {status}.");
            }

            if (rawInfo.StructSize < sizeof(RawPluginInfo))
            {
                throw new NativePluginException(
                    $"Plugin info struct is too small ({rawInfo.StructSize} bytes; expected at least {sizeof(RawPluginInfo)}).");
            }

            if (rawInfo.AbiVersion != SupportedAbiVersion)
            {
                throw new NativePluginException(
                    $"Plugin ABI version {rawInfo.AbiVersion} is unsupported. Expected {SupportedAbiVersion}.");
            }

            var pluginId = Marshal.PtrToStringUTF8(rawInfo.PluginId) ?? string.Empty;
            var name = Marshal.PtrToStringUTF8(rawInfo.Name) ?? string.Empty;
            var version = Marshal.PtrToStringUTF8(rawInfo.Version) ?? "1.0.0";
            var author = Marshal.PtrToStringUTF8(rawInfo.Author) ?? string.Empty;
            var desc = Marshal.PtrToStringUTF8(rawInfo.Description) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(pluginId))
                throw new NativePluginException("Plugin ID must be a non-empty UTF-8 string.");
            if (pluginId.StartsWith("core.", StringComparison.OrdinalIgnoreCase))
                throw new NativePluginException($"Plugin ID '{pluginId}' uses the reserved 'core.' namespace.");
            if (string.IsNullOrWhiteSpace(name))
                throw new NativePluginException($"Plugin '{pluginId}' has no display name.");
            if (!Version.TryParse(version, out _))
                throw new NativePluginException($"Plugin '{pluginId}' has invalid semantic version '{version}'.");
            if (rawInfo.ProcessorCount is 0 or > MaximumProcessorCount)
                throw new NativePluginException(
                    $"Plugin '{pluginId}' declares invalid processor count {rawInfo.ProcessorCount}; expected 1..{MaximumProcessorCount}.");

            var processors = new List<PluginProcessorMetadata>();
            var processorIds = new HashSet<string>(StringComparer.Ordinal);
            for (uint i = 0; i < rawInfo.ProcessorCount; i++)
            {
                var rawDef = new RawPluginProcessorDef
                {
                    StructSize = (uint)sizeof(RawPluginProcessorDef)
                };

                var processorStatus = getProcessorDef(i, &rawDef);
                if (processorStatus != 0)
                {
                    throw new NativePluginException(
                        $"Plugin '{pluginId}' get_processor_def({i}) failed with status code {processorStatus}.");
                }
                if (rawDef.StructSize < sizeof(RawPluginProcessorDef))
                {
                    throw new NativePluginException(
                        $"Plugin '{pluginId}' processor {i} returned a truncated definition struct.");
                }
                if (rawDef.ParameterCount > MaximumParameterCount)
                {
                    throw new NativePluginException(
                        $"Plugin '{pluginId}' processor {i} declares too many parameters ({rawDef.ParameterCount}).");
                }
                if (rawDef.ParameterCount > 0 && rawDef.Parameters == nint.Zero)
                {
                    throw new NativePluginException(
                        $"Plugin '{pluginId}' processor {i} declares parameters but returned a null parameter array.");
                }

                var procId = Marshal.PtrToStringUTF8(rawDef.Id) ?? string.Empty;
                var procName = Marshal.PtrToStringUTF8(rawDef.Name) ?? string.Empty;
                var category = Marshal.PtrToStringUTF8(rawDef.Category) ?? string.Empty;
                if (string.IsNullOrWhiteSpace(procId) || !processorIds.Add(procId))
                {
                    throw new NativePluginException(
                        $"Plugin '{pluginId}' contains an empty or duplicate processor ID '{procId}'.");
                }
                if (procId.StartsWith("core.", StringComparison.OrdinalIgnoreCase))
                    throw new NativePluginException(
                        $"Plugin processor ID '{procId}' uses the reserved 'core.' namespace.");
                if (string.IsNullOrWhiteSpace(procName))
                    throw new NativePluginException($"Plugin processor '{procId}' has no display name.");
                if (string.IsNullOrWhiteSpace(category))
                    throw new NativePluginException($"Plugin processor '{procId}' has no category.");

                var capabilities = (PluginCapabilities)rawDef.Capabilities;
                if ((capabilities & ~KnownCapabilities) != 0 ||
                    (capabilities & (PluginCapabilities.Grayscale | PluginCapabilities.Rgb)) == 0)
                {
                    throw new NativePluginException(
                        $"Plugin processor '{procId}' declares invalid capabilities 0x{rawDef.Capabilities:X8}.");
                }

                var paramList = new List<PluginParameterMetadata>();
                var parameterIds = new HashSet<string>(StringComparer.Ordinal);
                if (rawDef.Parameters != nint.Zero && rawDef.ParameterCount > 0)
                {
                    var paramSize = sizeof(RawPluginParameterDef);
                    for (var p = 0; p < rawDef.ParameterCount; p++)
                    {
                        var paramPtr = (RawPluginParameterDef*)(rawDef.Parameters + (p * paramSize));
                        if (paramPtr->StructSize < paramSize)
                        {
                            throw new NativePluginException(
                                $"Plugin processor '{procId}' parameter {p} returned a truncated definition struct.");
                        }
                        var pId = Marshal.PtrToStringUTF8(paramPtr->Id) ?? string.Empty;
                        var pName = Marshal.PtrToStringUTF8(paramPtr->Name) ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(pId) || !parameterIds.Add(pId))
                            throw new NativePluginException(
                                $"Plugin processor '{procId}' contains an empty or duplicate parameter ID '{pId}'.");
                        if (string.IsNullOrWhiteSpace(pName))
                            throw new NativePluginException(
                                $"Plugin processor '{procId}' parameter '{pId}' has no display name.");
                        if (!double.IsFinite(paramPtr->DefaultValue) ||
                            !double.IsFinite(paramPtr->MinimumValue) ||
                            !double.IsFinite(paramPtr->MaximumValue) ||
                            !double.IsFinite(paramPtr->Step) ||
                            paramPtr->MinimumValue > paramPtr->MaximumValue ||
                            paramPtr->DefaultValue < paramPtr->MinimumValue ||
                            paramPtr->DefaultValue > paramPtr->MaximumValue ||
                            paramPtr->Step <= 0)
                        {
                            throw new NativePluginException(
                                $"Plugin processor '{procId}' parameter '{pId}' has an invalid numeric schema.");
                        }
                        paramList.Add(new PluginParameterMetadata(
                            pId,
                            pName,
                            paramPtr->DefaultValue,
                            paramPtr->MinimumValue,
                            paramPtr->MaximumValue,
                            paramPtr->Step));
                    }
                }

                processors.Add(new PluginProcessorMetadata(
                    i,
                    procId,
                    procName,
                    category,
                    capabilities,
                    paramList));
            }

            var pluginMetadata = new PluginMetadata(pluginId, name, version, author, desc, processors);
            return new LoadedPlugin(handle, pluginMetadata, process) { Path = filePath };
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }

    public unsafe NativeImage Execute(
        uint processorIndex,
        NativeImage source,
        IReadOnlyList<double> parameters,
        NativeProcessingQuality quality,
        Action<double, string>? reportProgress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(parameters);
        cancellationToken.ThrowIfCancellationRequested();

        if (quality is not (NativeProcessingQuality.InteractivePreview or NativeProcessingQuality.FullResolution))
            throw new ArgumentOutOfRangeException(nameof(quality));

        if (processorIndex >= metadata.Processors.Count)
            throw new ArgumentOutOfRangeException(nameof(processorIndex));
        var processor = metadata.Processors[(int)processorIndex];
        if (parameters.Count != processor.Parameters.Count)
        {
            throw new ArgumentException(
                $"Processor '{processor.Id}' expects {processor.Parameters.Count} parameters, got {parameters.Count}.",
                nameof(parameters));
        }
        for (var index = 0; index < parameters.Count; index++)
        {
            var value = parameters[index];
            var schema = processor.Parameters[index];
            if (!double.IsFinite(value) || value < schema.MinimumValue || value > schema.MaximumValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(parameters),
                    $"Parameter '{schema.Id}' value {value} is outside [{schema.MinimumValue}, {schema.MaximumValue}].");
            }
        }

        var meta = source.GetMetadata();
        var requiredCapability = meta.ChannelCount == 1
            ? PluginCapabilities.Grayscale
            : meta.ChannelCount == 3
                ? PluginCapabilities.Rgb
                : PluginCapabilities.None;
        if (requiredCapability == PluginCapabilities.None ||
            !processor.Capabilities.HasFlag(requiredCapability))
        {
            throw new NotSupportedException(
                $"Processor '{processor.Id}' does not support {meta.ChannelCount}-channel images.");
        }
        var pixelCount = checked((int)(meta.Width * meta.Height));
        var channelCount = checked((int)meta.ChannelCount);

        // 1. Get source plane pointers
        var inPlanes = stackalloc nint[channelCount];
        source.GetPlanePointers(new Span<nint>(inPlanes, channelCount));

        // 2. Allocate output planar memory
        var outPlanesAlloc = new float[channelCount][];
        var outPlaneHandles = new GCHandle[channelCount];
        var outPlanes = stackalloc nint[channelCount];

        var executionContext = new PluginExecutionContext
        {
            CancellationToken = cancellationToken,
            ReportProgress = reportProgress
        };
        var contextHandle = GCHandle.Alloc(executionContext);

        try
        {
            for (var c = 0; c < channelCount; c++)
            {
                outPlanesAlloc[c] = new float[pixelCount];
                outPlaneHandles[c] = GCHandle.Alloc(outPlanesAlloc[c], GCHandleType.Pinned);
                outPlanes[c] = outPlaneHandles[c].AddrOfPinnedObject();
            }

            var inBuffer = new RawPluginImageBuffer
            {
                StructSize = (uint)sizeof(RawPluginImageBuffer),
                Width = meta.Width,
                Height = meta.Height,
                ChannelCount = meta.ChannelCount,
                Planes = (float**)inPlanes
            };

            var outBuffer = new RawPluginImageBuffer
            {
                StructSize = (uint)sizeof(RawPluginImageBuffer),
                Width = meta.Width,
                Height = meta.Height,
                ChannelCount = meta.ChannelCount,
                Planes = (float**)outPlanes
            };

            var callbacks = new RawPluginHostCallbacks
            {
                StructSize = (uint)sizeof(RawPluginHostCallbacks),
                UserData = GCHandle.ToIntPtr(contextHandle),
                IsCancelled = (nint)(delegate* unmanaged[Cdecl]<nint, int>)&NativeIsCancelled,
                ReportProgress = (nint)(delegate* unmanaged[Cdecl]<nint, double, nint, void>)&NativeReportProgress,
                LogMessage = (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&NativeLogMessage
            };

            fixed (double* pParams = parameters.ToArray())
            {
                var status = (PluginStatus)processFn(
                    processorIndex,
                    &inBuffer,
                    &outBuffer,
                    pParams,
                    (uint)parameters.Count,
                    quality == NativeProcessingQuality.InteractivePreview ? 0 : 1,
                    &callbacks);

                if (status == PluginStatus.Cancelled)
                {
                    throw new OperationCanceledException();
                }
                if (status != PluginStatus.Ok)
                {
                    throw new NativePluginException($"Plugin execution failed with status: {status}.");
                }
            }

            // 3. Create resulting master planar image
            return NativeImage.CreateMasterPlanar(meta, new ReadOnlySpan<nint>(outPlanes, channelCount));
        }
        finally
        {
            if (contextHandle.IsAllocated)
            {
                contextHandle.Free();
            }

            for (var c = 0; c < channelCount; c++)
            {
                if (outPlaneHandles[c].IsAllocated)
                {
                    outPlaneHandles[c].Free();
                }
            }
        }
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            NativeLibrary.Free(libraryHandle);
        }
    }
}
