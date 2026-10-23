using System.Reflection;
using System.Runtime.InteropServices;

namespace MoonCam.Infrastructure.Decoding;

/// <summary>
/// The ~15 FFmpeg functions MoonCam needs, bound with source-generated P/Invoke.
/// Only ABI-stable struct fields are touched (see <see cref="AvFrameHead"/>), so the same code works with
/// FFmpeg 6, 7 and 8 — the library version is picked at runtime.
/// </summary>
internal static unsafe partial class FfmpegNative
{
    private const string AvCodec = "avcodec";
    private const string AvUtil = "avutil";

    public const int CodecIdH264 = 27;
    public const int CodecIdHevc = 173;
    public const int PixelFormatYuv420P = 0;
    public const int PixelFormatYuvj420P = 12;
    public const int ErrorAgain = -11; // AVERROR(EAGAIN) on Linux

    private static readonly Dictionary<string, string[]> Candidates = new()
    {
        [AvCodec] = ["libavcodec.so.62", "libavcodec.so.61", "libavcodec.so.60", "libavcodec.so", "avcodec-62", "avcodec-61"],
        [AvUtil] = ["libavutil.so.60", "libavutil.so.59", "libavutil.so.58", "libavutil.so", "avutil-60", "avutil-59"],
    };

    static FfmpegNative() => NativeLibrary.SetDllImportResolver(typeof(FfmpegNative).Assembly, Resolve);

    /// <summary>True if FFmpeg's libraries can be loaded on this machine.</summary>
    public static bool IsAvailable()
    {
        try
        {
            return avcodec_version() > 0;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!Candidates.TryGetValue(name, out var files))
            return IntPtr.Zero;
        foreach (var file in files)
        {
            if (NativeLibrary.TryLoad(file, assembly, searchPath, out var handle))
                return handle;
        }
        return IntPtr.Zero;
    }

    [LibraryImport(AvCodec)] public static partial uint avcodec_version();
    [LibraryImport(AvCodec)] public static partial IntPtr avcodec_find_decoder(int id);
    [LibraryImport(AvCodec)] public static partial IntPtr avcodec_alloc_context3(IntPtr codec);
    [LibraryImport(AvCodec)] public static partial int avcodec_open2(IntPtr context, IntPtr codec, IntPtr options);
    [LibraryImport(AvCodec)] public static partial void avcodec_free_context(ref IntPtr context);
    [LibraryImport(AvCodec)] public static partial int avcodec_send_packet(IntPtr context, IntPtr packet);
    [LibraryImport(AvCodec)] public static partial int avcodec_receive_frame(IntPtr context, IntPtr frame);
    [LibraryImport(AvCodec)] public static partial IntPtr av_packet_alloc();
    [LibraryImport(AvCodec)] public static partial void av_packet_free(ref IntPtr packet);
    [LibraryImport(AvCodec)] public static partial void av_packet_unref(IntPtr packet);
    [LibraryImport(AvCodec)] public static partial int av_new_packet(IntPtr packet, int size);
    [LibraryImport(AvUtil)] public static partial IntPtr av_frame_alloc();
    [LibraryImport(AvUtil)] public static partial void av_frame_free(ref IntPtr frame);
    [LibraryImport(AvUtil)] public static partial void av_frame_unref(IntPtr frame);
    [LibraryImport(AvUtil, StringMarshalling = StringMarshalling.Utf8)]
    public static partial int av_opt_set(IntPtr obj, string name, string value, int searchFlags);

    /// <summary>The leading fields of AVPacket, unchanged since FFmpeg 4.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AvPacketHead
    {
        public IntPtr Buf;
        public long Pts;
        public long Dts;
        public byte* Data;
        public int Size;
    }

    /// <summary>The leading fields of AVFrame, unchanged since FFmpeg 4.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct AvFrameHead
    {
        public fixed ulong Data[8];
        public fixed int LineSize[8];
        public IntPtr ExtendedData;
        public int Width;
        public int Height;
        public int NbSamples;
        public int Format;
    }
}
