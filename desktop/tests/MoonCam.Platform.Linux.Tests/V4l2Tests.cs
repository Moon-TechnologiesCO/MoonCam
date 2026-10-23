using System.Buffers.Binary;
using MoonCam.Platform.Linux.Video;

namespace MoonCam.Platform.Linux.Tests;

public class V4l2Tests
{
    [Fact]
    public void Ioctl_numbers_match_the_kernel_headers()
    {
        Assert.Equal(0x80685600UL, V4l2.VidiocQueryCap);   // VIDIOC_QUERYCAP
        Assert.Equal(0xC0D05605UL, V4l2.VidiocSetFormat);  // VIDIOC_S_FMT
    }

    [Fact]
    public void Output_format_describes_i420()
    {
        var format = V4l2.OutputFormat(1280, 720);

        Assert.Equal(V4l2.FormatSize, format.Length);
        Assert.Equal(V4l2.BufTypeVideoOutput, BinaryPrimitives.ReadUInt32LittleEndian(format));
        Assert.Equal(1280u, BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(8)));
        Assert.Equal(720u, BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(12)));
        Assert.Equal(0x32315559u, BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(16))); // 'YU12'
        Assert.Equal(1280u * 720 * 3 / 2, BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(28)));
    }

    [Fact]
    public void Parses_capability_names()
    {
        var cap = new byte[V4l2.CapabilitySize];
        "v4l2 loopback"u8.CopyTo(cap);
        "MoonCam"u8.CopyTo(cap.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(cap.AsSpan(84), V4l2.CapDeviceCaps | 0x3);
        BinaryPrimitives.WriteUInt32LittleEndian(cap.AsSpan(88), V4l2.CapVideoOutput);

        var (driver, card, caps) = V4l2.ParseCapability(cap);

        Assert.Equal(("v4l2 loopback", "MoonCam", V4l2.CapVideoOutput), (driver, card, caps));
    }

    [Fact]
    public void Finding_devices_never_throws()
    {
        // On this machine v4l2loopback may not be installed yet; the call must simply return what exists.
        var devices = V4l2LoopbackCamera.FindDevices();

        Assert.All(devices, d => Assert.StartsWith("/dev/video", d.Path));
    }
}
