using MoonCam.Core.Protocol;

namespace MoonCam.Core.Session;

public enum StreamQuality
{
    Low,
    Balanced,
    High,
}

/// <summary>What the desktop asks the phone to stream.</summary>
public sealed record StreamSettings(string CameraId, int Width, int Height, int Fps, StreamQuality Quality = StreamQuality.Balanced, VideoCodec Codec = VideoCodec.H264)
{
    public static readonly StreamSettings Default = new("0", 1280, 720, 30);

    /// <summary>Common presets offered in the UI.</summary>
    public static IReadOnlyList<(string Name, int Width, int Height, int Fps)> Presets { get; } =
    [
        ("480p · 30 fps", 854, 480, 30),
        ("720p · 30 fps", 1280, 720, 30),
        ("720p · 60 fps", 1280, 720, 60),
        ("1080p · 30 fps", 1920, 1080, 30),
        ("1080p · 60 fps", 1920, 1080, 60),
    ];

    /// <summary>
    /// Bitrate from resolution, frame rate and quality, in bits per second. Bits-per-pixel values are typical
    /// for low-latency H.264 without B-frames: 720p30 "Balanced" ≈ 2.8 Mbit/s, 1080p30 ≈ 6.2 Mbit/s.
    /// </summary>
    public int BitrateBps
    {
        get
        {
            var bitsPerPixel = Quality switch
            {
                StreamQuality.Low => 0.06,
                StreamQuality.High => 0.15,
                _ => 0.10,
            };
            if (Codec == VideoCodec.H265)
                bitsPerPixel *= 0.7;
            return (int)Math.Round(Width * Height * Fps * bitsPerPixel / 100_000) * 100_000;
        }
    }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(CameraId))
            errors.Add("Choose a camera.");
        if (Width is < 160 or > 3840 || Height is < 120 or > 2160)
            errors.Add($"Resolution {Width}x{Height} is outside 160x120..3840x2160.");
        if (Width % 2 != 0 || Height % 2 != 0)
            errors.Add("Width and height must be even numbers.");
        if (Fps is < 5 or > 60)
            errors.Add($"Frame rate {Fps} is outside 5..60.");
        return errors;
    }

    public ControlMessage ToStartCommand() => new(
        ControlOps.Start,
        Camera: CameraId,
        Width: Width,
        Height: Height,
        Fps: Fps,
        BitrateBps: BitrateBps,
        Codec: Codec == VideoCodec.H265 ? "h265" : "h264");
}
