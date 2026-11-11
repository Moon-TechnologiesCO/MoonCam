namespace MoonCam.Core.Media;

public enum FilterKind
{
    None,
    Vivid,
    Warm,
    Cool,
    Mono,
    Noir,
    Sepia,
    Fade,
}

/// <summary>
/// Colour filters for the picture apps receive, photos and recordings. Each filter is three lookup tables
/// (Y, U, V), so applying one costs a table read per byte. Reuses one output buffer.
/// </summary>
public sealed class FrameFilter
{
    private readonly byte[] _y = new byte[256];
    private readonly byte[] _u = new byte[256];
    private readonly byte[] _v = new byte[256];
    private FilterKind _built = (FilterKind)(-1);
    private byte[] _out = [];

    public VideoFrame Apply(VideoFrame frame, FilterKind kind)
    {
        if (kind == FilterKind.None)
            return frame;
        if (_built != kind)
            Build(kind);
        var size = VideoFrame.SizeOf(frame.Width, frame.Height);
        if (_out.Length != size)
            _out = new byte[size];
        int luma = frame.Width * frame.Height, chroma = luma / 4;
        var src = frame.Data;
        for (var i = 0; i < luma; i++) _out[i] = _y[src[i]];
        for (var i = luma; i < luma + chroma; i++) _out[i] = _u[src[i]];
        for (var i = luma + chroma; i < size; i++) _out[i] = _v[src[i]];
        return new VideoFrame(frame.Width, frame.Height, _out, frame.TimestampUs);
    }

    private void Build(FilterKind kind)
    {
        for (var i = 0; i < 256; i++)
        {
            double y = i, c = i - 128;
            (double Y, double U, double V) r = kind switch
            {
                FilterKind.Vivid => (Contrast(y, 1.12), 128 + c * 1.4, 128 + c * 1.4),
                FilterKind.Warm => (y + 3, 128 + c * 1.05 - 10, 128 + c * 1.05 + 12),
                FilterKind.Cool => (y, 128 + c * 1.05 + 12, 128 + c * 1.05 - 8),
                FilterKind.Mono => (y, 128, 128),
                FilterKind.Noir => (Contrast(y, 1.4), 128, 128),
                FilterKind.Sepia => (Contrast(y, 1.05), 128 - 14, 128 + 16),
                FilterKind.Fade => (36 + (y - 16) * 0.82, 128 + c * 0.75, 128 + c * 0.75),
                _ => (y, i, i),
            };
            _y[i] = Clamp(r.Y);
            _u[i] = Clamp(r.U);
            _v[i] = Clamp(r.V);
        }
        _built = kind;
    }

    private static double Contrast(double y, double amount) => 126 + (y - 126) * amount;

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
