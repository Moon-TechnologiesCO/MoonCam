namespace MoonCam.Core.Media;

/// <summary>
/// Resizes I420 pictures with bilinear filtering (fixed-point, no allocations besides the output).
/// Used when a consumer has locked the virtual camera to a different resolution than the phone sends.
/// </summary>
public static class I420Scaler
{
    private const int Shift = 16;
    private const int One = 1 << Shift;

    public static VideoFrame Scale(VideoFrame source, int width, int height)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException($"Invalid target size {width}x{height}.");
        if (source.Width == width && source.Height == height)
            return source;

        var data = new byte[VideoFrame.SizeOf(width, height)];
        int lumaSize = width * height, chromaSize = lumaSize / 4;
        ScalePlane(source.Y, source.Width, source.Height, data.AsSpan(0, lumaSize), width, height);
        ScalePlane(source.U, source.Width / 2, source.Height / 2, data.AsSpan(lumaSize, chromaSize), width / 2, height / 2);
        ScalePlane(source.V, source.Width / 2, source.Height / 2, data.AsSpan(lumaSize + chromaSize, chromaSize), width / 2, height / 2);
        return new VideoFrame(width, height, data, source.TimestampUs);
    }

    private static void ScalePlane(ReadOnlySpan<byte> src, int srcWidth, int srcHeight, Span<byte> dst, int dstWidth, int dstHeight)
    {
        // Sample at pixel centres so the picture does not drift by half a pixel.
        long stepX = ((long)srcWidth << Shift) / dstWidth, stepY = ((long)srcHeight << Shift) / dstHeight;
        for (var y = 0; y < dstHeight; y++)
        {
            var sy = Math.Max(0, (y * stepY) + (stepY >> 1) - (One >> 1));
            var y0 = Math.Min((int)(sy >> Shift), srcHeight - 1);
            var y1 = Math.Min(y0 + 1, srcHeight - 1);
            var fy = (int)(sy & (One - 1));
            var row0 = src.Slice(y0 * srcWidth, srcWidth);
            var row1 = src.Slice(y1 * srcWidth, srcWidth);
            var outRow = dst.Slice(y * dstWidth, dstWidth);

            for (var x = 0; x < dstWidth; x++)
            {
                var sx = Math.Max(0, (x * stepX) + (stepX >> 1) - (One >> 1));
                var x0 = Math.Min((int)(sx >> Shift), srcWidth - 1);
                var x1 = Math.Min(x0 + 1, srcWidth - 1);
                var fx = (int)(sx & (One - 1));
                long top = (row0[x0] * (long)(One - fx)) + (row0[x1] * (long)fx);
                long bottom = (row1[x0] * (long)(One - fx)) + (row1[x1] * (long)fx);
                outRow[x] = (byte)(((top * (One - fy)) + (bottom * fy) + (1L << (2 * Shift - 1))) >> (2 * Shift));
            }
        }
    }
}
