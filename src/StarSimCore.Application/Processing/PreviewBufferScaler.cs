namespace StarSimCore.Application.Processing;

internal static class PreviewBufferScaler
{
    internal static byte[] UpscaleBgra(
        byte[] source,
        uint sourceWidth,
        uint sourceHeight,
        uint targetWidth,
        uint targetHeight)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length != checked((int)(sourceWidth * sourceHeight * 4)))
            throw new ArgumentException("Preview buffer dimensions do not match its byte length.", nameof(source));
        if (sourceWidth == targetWidth && sourceHeight == targetHeight) return source;
        var result = GC.AllocateUninitializedArray<byte>(checked((int)(targetWidth * targetHeight * 4)));
        for (var y = 0U; y < targetHeight; y++)
        {
            var sourceY = Math.Min(sourceHeight - 1, (uint)((ulong)y * sourceHeight / targetHeight));
            for (var x = 0U; x < targetWidth; x++)
            {
                var sourceX = Math.Min(sourceWidth - 1, (uint)((ulong)x * sourceWidth / targetWidth));
                var sourceOffset = checked((int)((sourceY * sourceWidth + sourceX) * 4));
                var targetOffset = checked((int)((y * targetWidth + x) * 4));
                result[targetOffset] = source[sourceOffset];
                result[targetOffset + 1] = source[sourceOffset + 1];
                result[targetOffset + 2] = source[sourceOffset + 2];
                result[targetOffset + 3] = source[sourceOffset + 3];
            }
        }
        return result;
    }
}
