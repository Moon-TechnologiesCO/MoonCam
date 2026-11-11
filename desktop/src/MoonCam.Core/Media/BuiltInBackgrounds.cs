namespace MoonCam.Core.Media;

/// <summary>A background picture that ships with MoonCam, drawn in code at any size.</summary>
public sealed record BuiltInBackground(string Id, string Name, Func<int, int, VideoFrame> Draw);

/// <summary>Soft, calm backgrounds that keep the attention on the person.</summary>
public static class BuiltInBackgrounds
{
    public static IReadOnlyList<BuiltInBackground> All { get; } =
    [
        new("moonlit", "Moonlit hills", Moonlit),
        new("aurora", "Aurora", Aurora),
        new("sunset", "Sunset", Sunset),
        new("studio", "Studio", Studio),
        new("ocean", "Ocean", Ocean),
        new("lavender", "Lavender", Lavender),
    ];

    private static VideoFrame Moonlit(int w, int h) => Draw(w, h, (x, y) =>
    {
        double fx = (double)x / w, fy = (double)y / h;
        var c = fy < 0.55 ? Mix((14, 16, 52), (78, 52, 140), fy / 0.55) : Mix((78, 52, 140), (236, 140, 120), Math.Min(1, (fy - 0.55) / 0.25));
        double dx = fx - 0.72, dy = (fy - 0.28) * h / w, d = Math.Sqrt(dx * dx + dy * dy);
        c = d < 0.055 ? (246, 242, 228) : Mix(c, (220, 212, 255), Math.Exp(-(d - 0.055) / 0.09) * 0.5);
        if (Hash(x, y) > 0.9988 && fy < 0.6) c = Mix(c, (255, 255, 255), 0.8);
        if (fy > 0.66 + 0.06 * Math.Sin(fx * 7) + 0.03 * Math.Sin(fx * 19)) c = Mix((60, 44, 104), (34, 26, 70), fy);
        if (fy > 0.80 + 0.06 * Math.Sin(fx * 4.2 + 1.3)) c = (18, 15, 40);
        return c;
    });

    private static VideoFrame Aurora(int w, int h) => Draw(w, h, (x, y) =>
    {
        double fx = (double)x / w, fy = (double)y / h;
        var c = Mix((8, 14, 34), (14, 30, 60), fy);
        var band1 = Math.Exp(-Math.Pow((fy - 0.35 - 0.12 * Math.Sin(fx * 5 + 0.5)) / 0.09, 2));
        var band2 = Math.Exp(-Math.Pow((fy - 0.5 - 0.08 * Math.Sin(fx * 7 + 2)) / 0.07, 2));
        c = Mix(c, (60, 230, 170), band1 * 0.7);
        c = Mix(c, (150, 90, 255), band2 * 0.55);
        if (Hash(x, y) > 0.999) c = Mix(c, (255, 255, 255), 0.7);
        return c;
    });

    private static VideoFrame Sunset(int w, int h) => Draw(w, h, (x, y) =>
    {
        double fx = (double)x / w, fy = (double)y / h;
        var c = fy < 0.6 ? Mix((255, 120, 90), (255, 196, 120), fy / 0.6) : Mix((255, 196, 120), (120, 70, 110), (fy - 0.6) / 0.4);
        double dx = fx - 0.3, dy = (fy - 0.62) * h / w, d = Math.Sqrt(dx * dx + dy * dy);
        return d < 0.09 ? Mix((255, 236, 180), c, Math.Clamp((d - 0.08) / 0.01, 0, 1)) : Mix(c, (255, 220, 160), Math.Exp(-d / 0.12) * 0.35);
    });

    private static VideoFrame Studio(int w, int h) => Draw(w, h, (x, y) =>
    {
        double dx = (double)x / w - 0.5, dy = (double)y / h - 0.45, d = Math.Sqrt(dx * dx + dy * dy);
        return Mix((120, 124, 134), (40, 42, 50), Math.Clamp(d / 0.75, 0, 1));
    });

    private static VideoFrame Ocean(int w, int h) => Draw(w, h, (x, y) =>
    {
        double fx = (double)x / w, fy = (double)y / h;
        var c = Mix((18, 120, 160), (8, 40, 80), fy);
        var light = Math.Max(0, Math.Sin(fx * 30 + Math.Sin(fy * 12) * 2) * Math.Sin(fy * 20 + fx * 6)) * (1 - fy) * 0.25;
        return Mix(c, (180, 240, 255), light);
    });

    private static VideoFrame Lavender(int w, int h) => Draw(w, h, (x, y) =>
    {
        double fx = (double)x / w, fy = (double)y / h;
        var c = Mix((190, 170, 240), (120, 140, 230), (fx + fy) / 2);
        double dx = fx - 0.8, dy = fy - 0.2;
        return Mix(c, (255, 210, 235), Math.Exp(-(dx * dx + dy * dy) / 0.08) * 0.6);
    });

    private static (double R, double G, double B) Mix((double R, double G, double B) a, (double R, double G, double B) b, double k)
    {
        k = Math.Clamp(k, 0, 1);
        return (a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k);
    }

    private static double Hash(int x, int y)
    {
        var n = (uint)(x * 374761393 + y * 668265263);
        n = (n ^ (n >> 13)) * 1274126177;
        return (n ^ (n >> 16)) / (double)uint.MaxValue;
    }

    /// <summary>Paints an RGB function into a BT.601 (video range) I420 frame.</summary>
    public static VideoFrame Draw(int width, int height, Func<int, int, (double R, double G, double B)> pixel)
    {
        width &= ~1; height &= ~1;
        var data = new byte[VideoFrame.SizeOf(width, height)];
        int yPlane = width * height, cw = width / 2;
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var (r, g, b) = pixel(x, y);
            data[y * width + x] = Clamp(16 + 0.257 * r + 0.504 * g + 0.098 * b);
            if ((x & 1) == 0 && (y & 1) == 0)
            {
                var i = (y / 2) * cw + x / 2;
                data[yPlane + i] = Clamp(128 - 0.148 * r - 0.291 * g + 0.439 * b);
                data[yPlane + yPlane / 4 + i] = Clamp(128 + 0.439 * r - 0.368 * g - 0.071 * b);
            }
        }
        return new VideoFrame(width, height, data, 0);
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
}
