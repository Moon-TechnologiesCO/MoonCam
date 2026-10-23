using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace MoonCam.Platform.Linux.Video;

/// <summary>
/// The small part of the Video4Linux2 kernel API that a v4l2loopback writer needs: two ioctls and the
/// layout of their structs on 64-bit Linux (see linux/videodev2.h).
/// </summary>
internal static partial class V4l2
{
    public const uint BufTypeVideoOutput = 2;
    public const uint FieldNone = 1;
    public const uint ColorspaceSrgb = 8;
    public const uint CapVideoOutput = 0x00000002;
    public const uint CapDeviceCaps = 0x80000000;

    /// <summary>FourCC 'YU12': planar I420, the format MoonCam writes.</summary>
    public static readonly uint PixelFormatYuv420 = FourCc('Y', 'U', '1', '2');

    public const int CapabilitySize = 104;      // struct v4l2_capability
    public const int FormatSize = 208;          // struct v4l2_format: u32 type, 4 bytes padding, 200-byte union

    public static readonly ulong VidiocQueryCap = Ioc(Read, 'V', 0, CapabilitySize);
    public static readonly ulong VidiocGetFormat = Ioc(Read | Write, 'V', 4, FormatSize);
    public static readonly ulong VidiocSetFormat = Ioc(Read | Write, 'V', 5, FormatSize);

    private const uint Write = 1;
    private const uint Read = 2;

    /// <summary>Linux _IOC(dir, type, nr, size) encoding.</summary>
    public static ulong Ioc(uint direction, char type, uint number, int size)
        => (direction << 30) | ((uint)size << 16) | ((uint)type << 8) | number;

    public static uint FourCc(char a, char b, char c, char d) => a | ((uint)b << 8) | ((uint)c << 16) | ((uint)d << 24);

    /// <summary>Builds a v4l2_format describing an I420 output of the given size.</summary>
    public static byte[] OutputFormat(int width, int height)
    {
        var format = new byte[FormatSize];
        var pix = format.AsSpan(8); // the union starts after the 4-byte type and 4 bytes of alignment
        BinaryPrimitives.WriteUInt32LittleEndian(format, BufTypeVideoOutput);
        BinaryPrimitives.WriteUInt32LittleEndian(pix[0..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(pix[4..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(pix[8..], PixelFormatYuv420);
        BinaryPrimitives.WriteUInt32LittleEndian(pix[12..], FieldNone);
        BinaryPrimitives.WriteUInt32LittleEndian(pix[16..], (uint)width);                 // bytesperline (Y plane)
        BinaryPrimitives.WriteUInt32LittleEndian(pix[20..], (uint)(width * height * 3 / 2)); // sizeimage
        BinaryPrimitives.WriteUInt32LittleEndian(pix[24..], ColorspaceSrgb);
        return format;
    }

    /// <summary>An empty v4l2_format asking for the current output format (VIDIOC_G_FMT).</summary>
    public static byte[] OutputFormatQuery()
    {
        var format = new byte[FormatSize];
        BinaryPrimitives.WriteUInt32LittleEndian(format, BufTypeVideoOutput);
        return format;
    }

    /// <summary>Reads width, height and FourCC from a v4l2_format filled by the driver.</summary>
    public static (int Width, int Height, uint PixelFormat) ParseFormat(ReadOnlySpan<byte> format)
    {
        var pix = format[8..];
        return ((int)BinaryPrimitives.ReadUInt32LittleEndian(pix), (int)BinaryPrimitives.ReadUInt32LittleEndian(pix[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(pix[8..]));
    }

    public const int ErrorBusy = 16; // EBUSY

    /// <summary>Reads driver and card names from a v4l2_capability buffer.</summary>
    public static (string Driver, string Card, uint DeviceCaps) ParseCapability(ReadOnlySpan<byte> capability)
    {
        static string Text(ReadOnlySpan<byte> s) => System.Text.Encoding.UTF8.GetString(s[..(s.IndexOf((byte)0) is var i and >= 0 ? i : s.Length)]);
        var capabilities = BinaryPrimitives.ReadUInt32LittleEndian(capability[84..]);
        var deviceCaps = BinaryPrimitives.ReadUInt32LittleEndian(capability[88..]);
        return (Text(capability[..16]), Text(capability[16..48]), (capabilities & CapDeviceCaps) != 0 ? deviceCaps : capabilities);
    }

    // libc

    public const int OpenWriteOnly = 0x1;
    public const int OpenCloseOnExec = 0x80000;

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true)]
    public static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    public static unsafe partial int ioctl(int fd, ulong request, byte* argument);

    [LibraryImport("libc", SetLastError = true)]
    public static unsafe partial nint write(int fd, byte* buffer, nuint count);
}
