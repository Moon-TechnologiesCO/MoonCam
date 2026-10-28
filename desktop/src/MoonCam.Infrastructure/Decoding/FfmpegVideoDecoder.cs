using MoonCam.Core.Media;
using MoonCam.Core.Protocol;
using static MoonCam.Infrastructure.Decoding.FfmpegNative;

namespace MoonCam.Infrastructure.Decoding;

/// <summary>
/// H.264/H.265 software decoding with FFmpeg's libavcodec, tuned for latency: low-delay mode and slice
/// threads (frame threads would add one frame of delay per thread).
/// </summary>
public sealed unsafe class FfmpegVideoDecoder : IVideoDecoder
{
    private IntPtr _context;
    private IntPtr _packet;
    private IntPtr _frame;
    private byte[] _codecData = [];
    private readonly Queue<long> _timestamps = new();
    private readonly Stack<byte[]> _freeBuffers = new(); // a 1080p picture is 3 MB: reuse instead of allocating 30 per second

    public static bool IsAvailable => FfmpegNative.IsAvailable();

    public void Configure(VideoConfig config)
    {
        Release();
        var codec = avcodec_find_decoder(config.Codec == VideoCodec.H265 ? CodecIdHevc : CodecIdH264);
        if (codec == IntPtr.Zero)
            throw new InvalidOperationException($"FFmpeg has no {config.Codec} decoder.");

        _context = avcodec_alloc_context3(codec);
        _packet = av_packet_alloc();
        _frame = av_frame_alloc();
        if (_context == IntPtr.Zero || _packet == IntPtr.Zero || _frame == IntPtr.Zero)
            throw new OutOfMemoryException("FFmpeg could not allocate the decoder.");

        // Best effort: an older FFmpeg without one of these options still decodes correctly.
        av_opt_set(_context, "flags", "+low_delay", 0);
        av_opt_set(_context, "threads", "auto", 0);
        av_opt_set(_context, "thread_type", "slice", 0);

        Check(avcodec_open2(_context, codec, IntPtr.Zero), "open the decoder");
        _codecData = config.CodecData.ToArray();
        _timestamps.Clear();
    }

    public IReadOnlyList<VideoFrame> Decode(ReadOnlyMemory<byte> accessUnit, bool isKeyFrame, long timestampUs)
    {
        if (_context == IntPtr.Zero)
            throw new InvalidOperationException("Call Configure before Decode.");

        // MediaCodec sends SPS/PPS once, separately. Prepending them to each key frame lets the decoder
        // recover from any reset or lost frame at the next key frame.
        var prefix = isKeyFrame ? _codecData : [];
        Check(av_new_packet(_packet, prefix.Length + accessUnit.Length), "allocate a packet");
        var head = (AvPacketHead*)_packet;
        prefix.CopyTo(new Span<byte>(head->Data, prefix.Length));
        accessUnit.Span.CopyTo(new Span<byte>(head->Data + prefix.Length, accessUnit.Length));

        var sent = avcodec_send_packet(_context, _packet);
        av_packet_unref(_packet);
        if (sent < 0)
            return []; // corrupt data: skip this unit; the next key frame resynchronises

        _timestamps.Enqueue(timestampUs);
        while (_timestamps.Count > 16)
            _timestamps.Dequeue();

        var pictures = new List<VideoFrame>(1);
        while (avcodec_receive_frame(_context, _frame) == 0)
        {
            pictures.Add(CopyToI420((AvFrameHead*)_frame, _timestamps.Count > 0 ? _timestamps.Dequeue() : timestampUs));
            av_frame_unref(_frame);
        }
        return pictures;
    }

    public void Recycle(VideoFrame frame)
    {
        if (_freeBuffers.Count < MaxFreeBuffers)
            _freeBuffers.Push(frame.Data);
    }

    private const int MaxFreeBuffers = 3;

    private byte[] RentBuffer(int size)
    {
        while (_freeBuffers.TryPop(out var buffer))
        {
            if (buffer.Length == size)
                return buffer;
        }
        return GC.AllocateUninitializedArray<byte>(size); // every byte is written below
    }

    private VideoFrame CopyToI420(AvFrameHead* frame, long timestampUs)
    {
        if (frame->Format is not (PixelFormatYuv420P or PixelFormatYuvj420P))
            throw new NotSupportedException($"Unsupported decoder pixel format {frame->Format}.");

        int width = frame->Width & ~1, height = frame->Height & ~1;
        var data = RentBuffer(VideoFrame.SizeOf(width, height));
        var offset = 0;
        for (var plane = 0; plane < 3; plane++)
        {
            int planeWidth = plane == 0 ? width : width / 2, planeHeight = plane == 0 ? height : height / 2;
            var source = (byte*)frame->Data[plane];
            var stride = frame->LineSize[plane];
            for (var row = 0; row < planeHeight; row++)
            {
                new ReadOnlySpan<byte>(source + (long)row * stride, planeWidth).CopyTo(data.AsSpan(offset));
                offset += planeWidth;
            }
        }
        return new VideoFrame(width, height, data, timestampUs);
    }

    private static void Check(int result, string action)
    {
        if (result < 0)
            throw new InvalidOperationException($"FFmpeg could not {action} (error {result}).");
    }

    private void Release()
    {
        if (_frame != IntPtr.Zero) av_frame_free(ref _frame);
        if (_packet != IntPtr.Zero) av_packet_free(ref _packet);
        if (_context != IntPtr.Zero) avcodec_free_context(ref _context);
    }

    public void Dispose() => Release();
}
