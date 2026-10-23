using System.Buffers.Binary;

namespace MoonCam.Core.Protocol;

public enum VideoCodec : byte
{
    H264 = 1,
    H265 = 2,
}

public enum AudioCodec : byte
{
    Opus = 1,
    PcmS16Le = 2,
}

/// <summary>Describes the video stream that follows. <see cref="CodecData"/> holds Annex-B SPS/PPS (and VPS for H.265).</summary>
public sealed record VideoConfig(VideoCodec Codec, int Width, int Height, int Fps, ReadOnlyMemory<byte> CodecData)
{
    private const int FixedSize = 6;

    public Frame ToFrame()
    {
        var payload = new byte[FixedSize + CodecData.Length];
        payload[0] = (byte)Codec;
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(1), checked((ushort)Width));
        BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(3), checked((ushort)Height));
        payload[5] = checked((byte)Fps);
        CodecData.Span.CopyTo(payload.AsSpan(FixedSize));
        return new Frame(FrameType.VideoConfig, payload);
    }

    public static VideoConfig FromFrame(Frame frame)
    {
        var p = frame.Payload.Span;
        if (frame.Type != FrameType.VideoConfig || p.Length < FixedSize)
            throw new ProtocolException("Malformed VIDEO_CONFIG.");
        var codec = (VideoCodec)p[0];
        if (!Enum.IsDefined(codec))
            throw new ProtocolException($"Unknown video codec {p[0]}.");
        int width = BinaryPrimitives.ReadUInt16BigEndian(p[1..]), height = BinaryPrimitives.ReadUInt16BigEndian(p[3..]);
        if (width == 0 || height == 0 || width % 2 != 0 || height % 2 != 0)
            throw new ProtocolException($"Invalid video size {width}x{height}.");
        return new VideoConfig(codec, width, height, p[5], frame.Payload[FixedSize..]);
    }
}

/// <summary>Describes the audio stream that follows. <see cref="CodecData"/> holds OpusHead for Opus.</summary>
public sealed record AudioConfig(AudioCodec Codec, int SampleRate, int Channels, ReadOnlyMemory<byte> CodecData)
{
    private const int FixedSize = 6;

    public Frame ToFrame()
    {
        var payload = new byte[FixedSize + CodecData.Length];
        payload[0] = (byte)Codec;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), checked((uint)SampleRate));
        payload[5] = checked((byte)Channels);
        CodecData.Span.CopyTo(payload.AsSpan(FixedSize));
        return new Frame(FrameType.AudioConfig, payload);
    }

    public static AudioConfig FromFrame(Frame frame)
    {
        var p = frame.Payload.Span;
        if (frame.Type != FrameType.AudioConfig || p.Length < FixedSize)
            throw new ProtocolException("Malformed AUDIO_CONFIG.");
        var codec = (AudioCodec)p[0];
        if (!Enum.IsDefined(codec))
            throw new ProtocolException($"Unknown audio codec {p[0]}.");
        var channels = p[5];
        if (channels is 0 or > 2)
            throw new ProtocolException($"Unsupported channel count {channels}.");
        return new AudioConfig(codec, (int)BinaryPrimitives.ReadUInt32BigEndian(p[1..]), channels, frame.Payload[FixedSize..]);
    }
}

/// <summary>PING and PONG carry an 8-byte nonce so round-trip time can be measured.</summary>
public static class Heartbeat
{
    public static Frame Ping(ulong nonce, long timestampUs) => new(FrameType.Ping, Encode(nonce), TimestampUs: timestampUs);

    public static Frame Pong(Frame ping) => new(FrameType.Pong, ping.Payload, TimestampUs: ping.TimestampUs);

    public static ulong ReadNonce(Frame frame)
    {
        if (frame.Type is not (FrameType.Ping or FrameType.Pong) || frame.Payload.Length != 8)
            throw new ProtocolException("Malformed PING/PONG.");
        return BinaryPrimitives.ReadUInt64BigEndian(frame.Payload.Span);
    }

    private static byte[] Encode(ulong nonce)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(payload, nonce);
        return payload;
    }
}
