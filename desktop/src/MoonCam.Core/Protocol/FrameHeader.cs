using System.Buffers.Binary;

namespace MoonCam.Core.Protocol;

/// <summary>MoonLink message types (docs/protocol.md §3).</summary>
public enum FrameType : byte
{
    Hello = 0x01,
    Welcome = 0x02,
    Capabilities = 0x03,
    Control = 0x04,
    VideoConfig = 0x10,
    VideoFrame = 0x11,
    AudioConfig = 0x20,
    AudioFrame = 0x21,
    Ping = 0x30,
    Pong = 0x31,
    Bye = 0x3F,
}

[Flags]
public enum FrameFlags : byte
{
    None = 0,
    KeyFrame = 1 << 0,
    EndOfStream = 1 << 1,
}

/// <summary>
/// The fixed 16-byte header that precedes every MoonLink payload. All fields are big-endian.
/// </summary>
public readonly record struct FrameHeader(FrameType Type, FrameFlags Flags, int Length, long TimestampUs)
{
    public const int Size = 16;
    public const int MaxPayloadLength = 16 * 1024 * 1024;

    private const FrameFlags KnownFlags = FrameFlags.KeyFrame | FrameFlags.EndOfStream;

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Need {Size} bytes.", nameof(destination));
        if (Length is < 0 or > MaxPayloadLength)
            throw new ProtocolException($"Payload length {Length} is outside 0..{MaxPayloadLength}.");

        destination[0] = (byte)Type;
        destination[1] = (byte)Flags;
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], (uint)Length);
        BinaryPrimitives.WriteInt64BigEndian(destination[8..], TimestampUs);
    }

    /// <summary>Parses and validates a header. Strict on purpose: anything unexpected is a protocol error.</summary>
    public static FrameHeader Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ProtocolException($"A frame header needs {Size} bytes, got {source.Length}.");

        var type = (FrameType)source[0];
        if (!Enum.IsDefined(type))
            throw new ProtocolException($"Unknown frame type 0x{source[0]:X2}.");

        var flags = (FrameFlags)source[1];
        if ((flags & ~KnownFlags) != 0)
            throw new ProtocolException($"Unknown flags 0x{source[1]:X2}.");

        if (BinaryPrimitives.ReadUInt16BigEndian(source[2..]) != 0)
            throw new ProtocolException("Reserved header bits must be zero.");

        var length = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        if (length > MaxPayloadLength)
            throw new ProtocolException($"Payload length {length} exceeds {MaxPayloadLength}.");

        return new FrameHeader(type, flags, (int)length, BinaryPrimitives.ReadInt64BigEndian(source[8..]));
    }
}
