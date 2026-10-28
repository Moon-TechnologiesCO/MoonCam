namespace MoonCam.Core.Media;

/// <summary>
/// A decoded picture in planar I420 (YUV 4:2:0): the full-size Y plane, then the quarter-size U and V planes,
/// tightly packed. It is the format v4l2loopback consumers expect, so it goes to the virtual camera as-is.
/// </summary>
public sealed class VideoFrame
{
    public VideoFrame(int width, int height, byte[] data, long timestampUs)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
            throw new ArgumentException($"Invalid I420 size {width}x{height}.");
        if (data.Length != SizeOf(width, height))
            throw new ArgumentException($"I420 {width}x{height} needs {SizeOf(width, height)} bytes, got {data.Length}.");
        Width = width;
        Height = height;
        Data = data;
        TimestampUs = timestampUs;
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Data { get; }
    public long TimestampUs { get; }

    public ReadOnlySpan<byte> Y => Data.AsSpan(0, Width * Height);
    public ReadOnlySpan<byte> U => Data.AsSpan(Width * Height, Width * Height / 4);
    public ReadOnlySpan<byte> V => Data.AsSpan(Width * Height * 5 / 4, Width * Height / 4);

    public static int SizeOf(int width, int height) => width * height * 3 / 2;
}

/// <summary>
/// Anything that consumes decoded video: the preview, the virtual camera. <see cref="Push"/> must not keep the
/// frame after it returns: its buffer is reused for later pictures (copy what you need to keep).
/// </summary>
public interface IVideoSink
{
    void Push(VideoFrame frame);
}

/// <summary>A system-wide virtual camera device (v4l2loopback on Linux).</summary>
public interface IVirtualCamera : IVideoSink, IDisposable
{
    string DevicePath { get; }

    /// <summary>True while another program (OBS, a browser, a call app) has the camera open.</summary>
    bool HasReaders => false;

    /// <summary>Raised when the first program opens the camera or the last one closes it.</summary>
    event Action? ReadersChanged
    {
        add { }
        remove { }
    }
}
