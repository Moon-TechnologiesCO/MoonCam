namespace MoonCam.Core.Media;

/// <summary>Converts I420 to BGRA for the on-screen preview (BT.601, limited range — what phone encoders emit).</summary>
public static class I420Converter
{
    /// <summary>Writes width×height BGRA pixels into <paramref name="destination"/> with the given row stride.</summary>
    public static void ToBgra(VideoFrame frame, Span<byte> destination, int destinationStride)
    {
        int width = frame.Width, height = frame.Height, chromaWidth = width / 2;
        if (destinationStride < width * 4 || destination.Length < destinationStride * (height - 1) + width * 4)
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));

        ReadOnlySpan<byte> yPlane = frame.Y, uPlane = frame.U, vPlane = frame.V;
        for (var row = 0; row < height; row++)
        {
            var yRow = yPlane.Slice(row * width, width);
            var uRow = uPlane.Slice(row / 2 * chromaWidth, chromaWidth);
            var vRow = vPlane.Slice(row / 2 * chromaWidth, chromaWidth);
            var outRow = destination.Slice(row * destinationStride, width * 4);

            for (var x = 0; x < width; x++)
            {
                // Fixed-point BT.601: R = 1.164(Y-16) + 1.596(V-128), etc., scaled by 256.
                var c = 298 * (yRow[x] - 16);
                var d = uRow[x >> 1] - 128;
                var e = vRow[x >> 1] - 128;
                var o = x * 4;
                outRow[o] = Clamp((c + 516 * d + 128) >> 8);
                outRow[o + 1] = Clamp((c - 100 * d - 208 * e + 128) >> 8);
                outRow[o + 2] = Clamp((c + 409 * e + 128) >> 8);
                outRow[o + 3] = 255;
            }
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
