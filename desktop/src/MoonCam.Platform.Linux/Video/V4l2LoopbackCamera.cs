using System.Runtime.InteropServices;
using MoonCam.Core.Media;

namespace MoonCam.Platform.Linux.Video;

/// <summary>A v4l2loopback device found on this computer.</summary>
public sealed record LoopbackDevice(string Path, string Card);

/// <summary>
/// Writes I420 frames into a v4l2loopback device, which every Linux video app sees as a webcam.
///
/// When the phone's resolution changes, the device format follows it — unless an app (OBS, Zoom…) is reading
/// the camera: Linux then locks the format, so frames are scaled to the locked size instead. The app never
/// sees the camera disappear or change under its feet.
///
/// The camera is listed in apps even before the phone sends video (a first placeholder picture makes it
/// visible), and while an app reads it without live video (the phone's camera is starting, or the phone left)
/// a calm placeholder keeps flowing a few times per second, so apps never time out or freeze.
/// </summary>
public sealed unsafe class V4l2LoopbackCamera : IVirtualCamera
{
    private const string LoopbackDriver = "v4l2 loopback";
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 720;
    private static readonly TimeSpan KeepAliveEvery = TimeSpan.FromMilliseconds(250);
    private const long LiveVideoTimeoutMs = 700;

    private readonly Lock _lock = new();
    private readonly DeviceReaders _readers;
    private readonly Timer _keepAlive;
    private int _fd;
    private (int Width, int Height) _format;
    private long _lastLiveWriteMs;
    private VideoFrame? _placeholder;

    private V4l2LoopbackCamera(string devicePath, int fd)
    {
        DevicePath = devicePath;
        _fd = fd;
        _format = ReadFormat();
        lock (_lock)
        {
            try
            {
                if (_format == (0, 0))
                    TrySetFormat(DefaultWidth, DefaultHeight);
                WritePlaceholder(); // makes the camera appear in apps right away
            }
            catch (IOException)
            {
                // Best effort: the first live frame sets the format instead, as before.
            }
        }
        _readers = new DeviceReaders(devicePath);
        _readers.Changed += () => ReadersChanged?.Invoke();
        _keepAlive = new Timer(_ => KeepAlive(), null, KeepAliveEvery, KeepAliveEvery);
    }

    public string DevicePath { get; }

    public bool HasReaders => _readers.Any;

    public event Action? ReadersChanged;

    /// <summary>Lists v4l2loopback devices, e.g. the one created with card_label="MoonCam".</summary>
    public static IReadOnlyList<LoopbackDevice> FindDevices()
    {
        var devices = new List<LoopbackDevice>();
        foreach (var path in Directory.EnumerateFiles("/dev", "video*").Order(StringComparer.Ordinal))
        {
            var fd = V4l2.open(path, V4l2.OpenWriteOnly | V4l2.OpenCloseOnExec);
            if (fd < 0)
                continue;
            try
            {
                var cap = new byte[V4l2.CapabilitySize];
                fixed (byte* p = cap)
                {
                    if (V4l2.ioctl(fd, V4l2.VidiocQueryCap, p) != 0)
                        continue;
                }
                var (driver, card, _) = V4l2.ParseCapability(cap);
                if (driver == LoopbackDriver)
                    devices.Add(new LoopbackDevice(path, card));
            }
            finally
            {
                V4l2.close(fd);
            }
        }
        return devices;
    }

    public static V4l2LoopbackCamera Open(string devicePath)
    {
        var fd = V4l2.open(devicePath, V4l2.OpenWriteOnly | V4l2.OpenCloseOnExec);
        if (fd < 0)
            throw new IOException($"Cannot open {devicePath} (errno {Marshal.GetLastPInvokeError()}). Is v4l2loopback loaded?");
        return new V4l2LoopbackCamera(devicePath, fd);
    }

    public void Push(VideoFrame frame)
    {
        lock (_lock)
        {
            if (_fd < 0)
                return;
            if (_format != (frame.Width, frame.Height) && !TrySetFormat(frame.Width, frame.Height))
                frame = I420Scaler.Scale(frame, _format.Width, _format.Height);
            Write(frame);
            _lastLiveWriteMs = Environment.TickCount64;
        }
    }

    /// <summary>While an app reads the camera without live video, a placeholder keeps the picture flowing.</summary>
    private void KeepAlive()
    {
        if (!_readers.Any)
            return;
        lock (_lock)
        {
            if (_fd < 0 || Environment.TickCount64 - _lastLiveWriteMs < LiveVideoTimeoutMs)
                return;
            try
            {
                WritePlaceholder();
            }
            catch (IOException)
            {
                // The device went away; Push reports it when live video comes back.
            }
        }
    }

    /// <summary>MoonCam's night blue, in the device's current size.</summary>
    private void WritePlaceholder()
    {
        if (_format is not { Width: > 0, Height: > 0 } format)
            return;
        if (_placeholder is not { } placeholder || (placeholder.Width, placeholder.Height) != format)
        {
            var data = new byte[VideoFrame.SizeOf(format.Width, format.Height)];
            int luma = format.Width * format.Height, chroma = luma / 4;
            data.AsSpan(0, luma).Fill(36);              // #101633 in BT.601 video range
            data.AsSpan(luma, chroma).Fill(142);
            data.AsSpan(luma + chroma, chroma).Fill(123);
            _placeholder = placeholder = new VideoFrame(format.Width, format.Height, data, 0);
        }
        Write(placeholder);
    }

    private void Write(VideoFrame frame)
    {
        fixed (byte* data = frame.Data)
        {
            // v4l2loopback takes a whole frame per write(); a short write means the device is gone.
            if (V4l2.write(_fd, data, (nuint)frame.Data.Length) != frame.Data.Length)
                throw new IOException($"Writing to {DevicePath} failed (errno {Marshal.GetLastPInvokeError()}).");
        }
    }

    /// <summary>Changes the device resolution. False when a reader holds the current format (EBUSY).</summary>
    private bool TrySetFormat(int width, int height)
    {
        var format = V4l2.OutputFormat(width, height);
        fixed (byte* p = format)
        {
            if (V4l2.ioctl(_fd, V4l2.VidiocSetFormat, p) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                if (errno == V4l2.ErrorBusy && _format is { Width: > 0, Height: > 0 })
                    return false;
                throw new IOException($"{DevicePath} refused {width}x{height} I420 (errno {errno}).");
            }
        }
        _format = (width, height);
        return true;
    }

    /// <summary>The format the device already has (another writer or reader may have fixed it), or (0,0).</summary>
    private (int Width, int Height) ReadFormat()
    {
        var format = V4l2.OutputFormatQuery();
        fixed (byte* p = format)
        {
            if (V4l2.ioctl(_fd, V4l2.VidiocGetFormat, p) != 0)
                return (0, 0);
        }
        var (width, height, pixelFormat) = V4l2.ParseFormat(format);
        return pixelFormat == V4l2.PixelFormatYuv420 && width > 0 && height > 0 ? (width, height) : (0, 0);
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        _readers.Dispose();
        lock (_lock)
        {
            if (_fd >= 0)
                V4l2.close(_fd);
            _fd = -1;
        }
    }
}
