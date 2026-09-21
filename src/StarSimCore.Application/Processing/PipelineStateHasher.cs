using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace StarSimCore.Application.Processing;

/// <summary>
/// Produces a stable fingerprint of the complete canonical processing state.
/// The encoding includes revision, module order/identity, enabled flags, parameter
/// counts, and the exact IEEE-754 bit pattern of every parameter.
/// </summary>
public static class PipelineStateHasher
{
    private static readonly byte[] FormatMarker = "StarSimCore.PipelineState.v1\0"u8.ToArray();

    public static string ComputeSha256(PipelineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(FormatMarker);
        AppendInt64(hash, snapshot.Revision);
        AppendInt32(hash, snapshot.Modules.Length);

        foreach (var module in snapshot.Modules)
        {
            AppendString(hash, module.Id);
            hash.AppendData([module.Enabled ? (byte)1 : (byte)0]);
            AppendInt32(hash, module.Parameters.Length);
            foreach (var parameter in module.Parameters)
                AppendInt64(hash, BitConverter.DoubleToInt64Bits(parameter));
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static string ComputeModuleSha256(PipelineSnapshot snapshot, string moduleId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleId);
        var module = snapshot.GetModule(moduleId);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("StarSimCore.ModuleState.v1\0"u8);
        AppendString(hash, module.Id);
        hash.AppendData([module.Enabled ? (byte)1 : (byte)0]);
        AppendInt32(hash, module.Parameters.Length);
        foreach (var parameter in module.Parameters)
            AppendInt64(hash, BitConverter.DoubleToInt64Bits(parameter));
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }

    private static void AppendInt64(IncrementalHash hash, long value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        hash.AppendData(bytes);
    }
}
