using System.Runtime.InteropServices;
using MoonCam.Core.Media;

namespace MoonCam.Platform.Linux.Video;

/// <summary>A v4l2loopback device found on this computer.</summary>
public sealed record LoopbackDevice(string Path, string Card);

/// <summary>
/// Writes I420 frames into a v4l2loopback device, which every Linux video app sees as a webcam.
/// The output format is (re)negotiated whenever the incoming resolution changes.
/// </summary>
public sealed unsafe class V4l2LoopbackCamera : IVirtualCamera
{
    private const string LoopbackDriver = "v4l2 loopback";

    private readonly Lock _lock = new();
    private int _fd;
    private (int Width, int Height) _format;

    private V4l2LoopbackCamera(string devicePath, int fd)
    {
        DevicePath = devicePath;
        _fd = fd;
    }

    public string DevicePath { get; }

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
            if (_format != (frame.Width, frame.Height))
                SetFormat(frame.Width, frame.Height);

            fixed (byte* data = frame.Data)
            {
                // v4l2loopback takes a whole frame per write(); a short write means the device is gone.
                if (V4l2.write(_fd, data, (nuint)frame.Data.Length) != frame.Data.Length)
                    throw new IOException($"Writing to {DevicePath} failed (errno {Marshal.GetLastPInvokeError()}).");
            }
        }
    }

    private void SetFormat(int width, int height)
    {
        var format = V4l2.OutputFormat(width, height);
        fixed (byte* p = format)
        {
            if (V4l2.ioctl(_fd, V4l2.VidiocSetFormat, p) != 0)
                throw new IOException($"{DevicePath} refused {width}x{height} I420 (errno {Marshal.GetLastPInvokeError()}). Close apps using the camera and try again.");
        }
        _format = (width, height);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_fd >= 0)
                V4l2.close(_fd);
            _fd = -1;
        }
    }
}
