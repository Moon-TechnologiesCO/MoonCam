namespace MoonCam.Core.Media;

public enum BackgroundMode
{
    None,
    Blur,
    Image,
}

/// <summary>
/// Blurs or replaces everything behind the person, using the person mask the phone sends (MASK frames).
/// The mask is small (256x144); it is scaled up with bilinear filtering and given a soft edge. The blur is
/// cheap: the picture is averaged down 8x, blurred, and scaled back up. Without a recent mask the picture
/// passes through untouched, so a phone that stops sending masks never leaves a frozen background.
/// One output buffer is reused: sinks never keep frames.
/// </summary>
public sealed class BackgroundCompositor
{
    private static readonly TimeSpan MaskTimeout = TimeSpan.FromSeconds(1);
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();
    private byte[]? _mask;
    private int _maskWidth;
    private int _maskHeight;
    private long _maskReceived;
    private VideoFrame? _image;          // the chosen background, any size
    private VideoFrame? _imageScaled;    // the same, at the size of the current pictures
    private byte[] _out = [];
    private byte[] _alpha = [];
    private byte[] _small = [];

    public BackgroundCompositor(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public BackgroundMode Mode { get; set; }

    /// <summary>True when a mask arrived recently enough to use.</summary>
    public bool HasMask
    {
        get
        {
            lock (_lock)
                return _mask is not null && _time.GetElapsedTime(_maskReceived) < MaskTimeout;
        }
    }

    /// <summary>Stores the latest mask (one byte per pixel, 255 = person).</summary>
    public void SetMask(ReadOnlySpan<byte> pixels, int width, int height)
    {
        if (width <= 0 || height <= 0 || pixels.Length < width * height)
            return;
        lock (_lock)
        {
            if (_mask is null || _mask.Length != width * height)
                _mask = new byte[width * height];
            pixels[..(width * height)].CopyTo(_mask);
            (_maskWidth, _maskHeight) = (width, height);
            _maskReceived = _time.GetTimestamp();
        }
    }

    /// <summary>Reads a MASK payload: u16 width, u16 height (big-endian), then the pixels.</summary>
    public void SetMaskPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 4)
            return;
        int width = payload[0] << 8 | payload[1], height = payload[2] << 8 | payload[3];
        SetMask(payload[4..], width, height);
    }

    /// <summary>The picture shown behind the person in <see cref="BackgroundMode.Image"/>.</summary>
    public void SetImage(VideoFrame? image)
    {
        lock (_lock)
        {
            _image = image;
            _imageScaled = null;
        }
    }

    public VideoFrame Apply(VideoFrame frame)
    {
        if (Mode == BackgroundMode.None)
            return frame;
        byte[] mask;
        int mw, mh;
        VideoFrame? background = null;
        lock (_lock)
        {
            if (_mask is null || _time.GetElapsedTime(_maskReceived) >= MaskTimeout)
                return frame;
            (mask, mw, mh) = (_mask, _maskWidth, _maskHeight);
            if (Mode == BackgroundMode.Image && _image is { } image)
            {
                if (_imageScaled is not { } scaled || (scaled.Width, scaled.Height) != (frame.Width, frame.Height))
                    _imageScaled = Cover(image, frame.Width, frame.Height);
                background = _imageScaled;
            }
        }
        if (Mode == BackgroundMode.Image && background is null)
            return frame;

        int w = frame.Width, h = frame.Height, size = VideoFrame.SizeOf(w, h);
        if (_out.Length != size) _out = new byte[size];
        if (_alpha.Length != w * h) _alpha = new byte[w * h];
        BuildAlpha(mask, mw, mh, w, h);
        if (background is null)
            background = Blurred(frame);

        var src = frame.Data;
        var bg = background.Data;
        for (var i = 0; i < w * h; i++)
        {
            var a = _alpha[i];
            _out[i] = a == 255 ? src[i] : a == 0 ? bg[i] : (byte)((src[i] * a + bg[i] * (255 - a) + 127) / 255);
        }
        int cw = w / 2, ch = h / 2, uOffset = w * h, vOffset = uOffset + cw * ch;
        for (var y = 0; y < ch; y++)
        for (var x = 0; x < cw; x++)
        {
            var a = _alpha[(y * 2) * w + x * 2];
            int u = uOffset + y * cw + x, v = vOffset + y * cw + x;
            _out[u] = (byte)((src[u] * a + bg[u] * (255 - a) + 127) / 255);
            _out[v] = (byte)((src[v] * a + bg[v] * (255 - a) + 127) / 255);
        }
        return new VideoFrame(w, h, _out, frame.TimestampUs);
    }

    /// <summary>Scales the mask to the picture (bilinear) and turns it into a soft-edged alpha.</summary>
    private void BuildAlpha(byte[] mask, int mw, int mh, int w, int h)
    {
        for (var y = 0; y < h; y++)
        {
            var fy = Math.Max(0, (y + 0.5) * mh / h - 0.5);
            int y0 = Math.Min((int)fy, mh - 1), y1 = Math.Min(y0 + 1, mh - 1);
            var ty = fy - y0;
            for (var x = 0; x < w; x++)
            {
                var fx = Math.Max(0, (x + 0.5) * mw / w - 0.5);
                int x0 = Math.Min((int)fx, mw - 1), x1 = Math.Min(x0 + 1, mw - 1);
                var tx = fx - x0;
                var top = mask[y0 * mw + x0] + (mask[y0 * mw + x1] - mask[y0 * mw + x0]) * tx;
                var bottom = mask[y1 * mw + x0] + (mask[y1 * mw + x1] - mask[y1 * mw + x0]) * tx;
                var value = (top + (bottom - top) * ty) / 255.0;
                // Soft edge: below 0.3 is background, above 0.7 is person, smooth in between.
                var t = Math.Clamp((value - 0.3) / 0.4, 0, 1);
                _alpha[y * w + x] = (byte)(t * t * (3 - 2 * t) * 255);
            }
        }
    }

    /// <summary>A strong blur: average 8x8 blocks, blur the small picture twice, then scale back up.</summary>
    private VideoFrame Blurred(VideoFrame frame)
    {
        const int factor = 8;
        int w = frame.Width, h = frame.Height;
        int sw = Math.Max(2, (w / factor) & ~1), sh = Math.Max(2, (h / factor) & ~1);
        var down = I420Scaler.Scale(frame, sw, sh);
        for (var pass = 0; pass < 2; pass++)
            BoxBlur(down);
        return I420Scaler.Scale(down, w, h);
    }

    private void BoxBlur(VideoFrame f)
    {
        BlurPlane(f.Data, 0, f.Width, f.Height);
        BlurPlane(f.Data, f.Width * f.Height, f.Width / 2, f.Height / 2);
        BlurPlane(f.Data, f.Width * f.Height * 5 / 4, f.Width / 2, f.Height / 2);
    }

    private void BlurPlane(byte[] data, int offset, int w, int h)
    {
        if (_small.Length < w * h) _small = new byte[w * h];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            int sum = 0, count = 0;
            for (var dy = -2; dy <= 2; dy++)
            for (var dx = -2; dx <= 2; dx++)
            {
                int yy = y + dy, xx = x + dx;
                if (yy < 0 || yy >= h || xx < 0 || xx >= w) continue;
                sum += data[offset + yy * w + xx];
                count++;
            }
            _small[y * w + x] = (byte)(sum / count);
        }
        Array.Copy(_small, 0, data, offset, w * h);
    }

    /// <summary>Scales an image to fill the picture, cropping what does not fit (like CSS object-fit: cover).</summary>
    public static VideoFrame Cover(VideoFrame image, int width, int height)
    {
        double scale = Math.Max((double)width / image.Width, (double)height / image.Height);
        int sw = Math.Max(2, (int)Math.Round(image.Width * scale)) & ~1, sh = Math.Max(2, (int)Math.Round(image.Height * scale)) & ~1;
        sw = Math.Max(sw, width); sh = Math.Max(sh, height);
        var scaled = I420Scaler.Scale(image, sw, sh);
        if ((sw, sh) == (width, height))
            return scaled;
        var output = new byte[VideoFrame.SizeOf(width, height)];
        int x0 = ((sw - width) / 2) & ~1, y0 = ((sh - height) / 2) & ~1;
        for (var y = 0; y < height; y++)
            Array.Copy(scaled.Data, (y0 + y) * sw + x0, output, y * width, width);
        int su = sw * sh, sv = su + su / 4, du = width * height, dv = du + du / 4;
        for (var y = 0; y < height / 2; y++)
        {
            Array.Copy(scaled.Data, su + (y0 / 2 + y) * (sw / 2) + x0 / 2, output, du + y * (width / 2), width / 2);
            Array.Copy(scaled.Data, sv + (y0 / 2 + y) * (sw / 2) + x0 / 2, output, dv + y * (width / 2), width / 2);
        }
        return new VideoFrame(width, height, output, 0);
    }
}
