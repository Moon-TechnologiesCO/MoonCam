namespace MoonCam.Core.Media;

/// <summary>I420 → BGRA for the on-screen preview (fixed-point BT.601, video range).</summary>
public static class I420Converter
{
    /// <summary>
    /// Writes the picture as BGRA, shrunk by <paramref name="factor"/> in both directions (1 = full size), into
    /// <paramref name="destination"/> with the given row stride. Shrinking averages each 2×2 block of luma, so a
    /// preview drawn at half size costs a quarter of the work and still looks smooth.
    /// </summary>
    public static unsafe void ToBgra(VideoFrame frame, Span<byte> destination, int destinationStride, int factor = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        int width = frame.Width / factor, height = frame.Height / factor, sourceWidth = frame.Width, chromaWidth = frame.Width / 2;
        if (destinationStride < width * 4 || destination.Length < destinationStride * (height - 1) + width * 4)
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));

        fixed (byte* data = frame.Data, output = destination)
        {
            byte* yPlane = data, uPlane = data + sourceWidth * frame.Height, vPlane = uPlane + sourceWidth * frame.Height / 4;
            for (var row = 0; row < height; row++)
            {
                var sourceRow = row * factor;
                byte* y0 = yPlane + sourceRow * sourceWidth, y1 = factor > 1 ? y0 + sourceWidth : y0;
                byte* u = uPlane + (sourceRow >> 1) * chromaWidth, v = vPlane + (sourceRow >> 1) * chromaWidth;
                byte* o = output + row * destinationStride;
                for (var x = 0; x < width; x++, o += 4)
                {
                    var sx = x * factor;
                    var luma = factor > 1 ? (y0[sx] + y0[sx + 1] + y1[sx] + y1[sx + 1] + 2) >> 2 : y0[sx];
                    var c = 298 * (luma - 16);
                    var d = u[sx >> 1] - 128;
                    var e = v[sx >> 1] - 128;
                    o[0] = Clamp((c + 516 * d + 128) >> 8);
                    o[1] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
                    o[2] = Clamp((c + 409 * e + 128) >> 8);
                    o[3] = 255;
                }
            }
        }
    }

    private static byte Clamp(int value) => (byte)(value < 0 ? 0 : value > 255 ? 255 : value);
}
