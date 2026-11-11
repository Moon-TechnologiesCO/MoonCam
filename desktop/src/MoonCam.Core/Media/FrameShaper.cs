namespace MoonCam.Core.Media;

/// <summary>The shape of the picture apps receive.</summary>
public enum FrameShape
{
    /// <summary>16:9, as the phone sends it.</summary>
    Wide,
    /// <summary>4:3, the classic webcam shape.</summary>
    Standard,
    /// <summary>1:1.</summary>
    Square,
    /// <summary>1:1 with everything outside a circle painted black.</summary>
    Circle,
    /// <summary>9:16, for vertical streams and stories.</summary>
    Vertical,
    /// <summary>16:9 with rounded corners.</summary>
    RoundedWide,
    /// <summary>1:1 with rounded corners, like an app icon.</summary>
    RoundedSquare,
    /// <summary>21:9, a cinema-wide strip.</summary>
    Cinema,
}

/// <summary>
/// Crops the picture to a shape around its centre and, for <see cref="FrameShape.Circle"/>, paints the corners
/// black. Reuses one output buffer, which is safe because sinks never keep a frame after Push returns.
/// </summary>
public sealed class FrameShaper
{
    private byte[] _buffer = [];
    private byte[] _mask = [];
    private (int, int, FrameShape) _maskKey;

    public static (int Width, int Height) SizeFor(FrameShape shape, int width, int height)
    {
        var (w, h) = shape switch
        {
            FrameShape.Standard => Fit(width, height, 4, 3),
            FrameShape.Square or FrameShape.Circle or FrameShape.RoundedSquare => Fit(width, height, 1, 1),
            FrameShape.Vertical => Fit(width, height, 9, 16),
            FrameShape.Cinema => Fit(width, height, 21, 9),
            _ => (width, height),
        };
        return (w & ~1, h & ~1); // I420 needs even sizes

        static (int, int) Fit(int width, int height, int aw, int ah) =>
            width * ah > height * aw ? (height * aw / ah, height) : (width, width * ah / aw);
    }

    public VideoFrame Apply(VideoFrame frame, FrameShape shape)
    {
        if (shape == FrameShape.Wide)
            return frame;
        var (w, h) = SizeFor(shape, frame.Width, frame.Height);
        var masked = shape is FrameShape.Circle or FrameShape.RoundedWide or FrameShape.RoundedSquare;
        if ((w, h) == (frame.Width, frame.Height) && !masked)
            return frame;

        var size = VideoFrame.SizeOf(w, h);
        if (_buffer.Length != size)
            _buffer = new byte[size];
        int x0 = ((frame.Width - w) / 2) & ~1, y0 = ((frame.Height - h) / 2) & ~1;

        var src = frame.Data;
        for (var y = 0; y < h; y++)
            Array.Copy(src, (y0 + y) * frame.Width + x0, _buffer, y * w, w);
        int srcChroma = frame.Width / 2, dstChroma = w / 2;
        int srcU = frame.Width * frame.Height, srcV = srcU + srcU / 4;
        int dstU = w * h, dstV = dstU + dstU / 4;
        for (var y = 0; y < h / 2; y++)
        {
            Array.Copy(src, srcU + (y0 / 2 + y) * srcChroma + x0 / 2, _buffer, dstU + y * dstChroma, dstChroma);
            Array.Copy(src, srcV + (y0 / 2 + y) * srcChroma + x0 / 2, _buffer, dstV + y * dstChroma, dstChroma);
        }

        if (masked)
            PaintCorners(w, h, shape);
        return new VideoFrame(w, h, _buffer, frame.TimestampUs);
    }

    /// <summary>
    /// Black outside the shape (a circle, or a rectangle with rounded corners), with a one-pixel soft edge so
    /// the rim does not look jagged.
    /// </summary>
    private void PaintCorners(int w, int h, FrameShape shape)
    {
        var key = (w, h, shape);
        if (_maskKey != key)
        {
            _mask = new byte[w * h];
            // A circle is a square whose corner radius is half its side.
            var radius = shape == FrameShape.Circle ? w / 2.0 : Math.Min(w, h) * 0.12;
            double halfW = w / 2.0, halfH = h / 2.0;
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                // Signed distance to a rounded rectangle (negative inside).
                var qx = Math.Abs(x + 0.5 - halfW) - (halfW - radius);
                var qy = Math.Abs(y + 0.5 - halfH) - (halfH - radius);
                var outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0))
                              + Math.Min(Math.Max(qx, qy), 0) - radius;
                _mask[y * w + x] = (byte)Math.Clamp((0.5 - outside) * 255, 0, 255); // 255 inside, 0 outside
            }
            _maskKey = key;
        }
        for (var i = 0; i < w * h; i++)
        {
            var keep = _mask[i];
            if (keep != 255)
                _buffer[i] = (byte)(16 + (_buffer[i] - 16) * keep / 255);
        }
        int dstU = w * h, cw = w / 2;
        for (var y = 0; y < h / 2; y++)
        for (var x = 0; x < cw; x++)
        {
            var keep = _mask[(y * 2) * w + x * 2];
            if (keep == 255)
                continue;
            var u = dstU + y * cw + x;
            var v = u + dstU / 4;
            _buffer[u] = (byte)(128 + (_buffer[u] - 128) * keep / 255);
            _buffer[v] = (byte)(128 + (_buffer[v] - 128) * keep / 255);
        }
    }
}
