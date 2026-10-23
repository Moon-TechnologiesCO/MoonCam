namespace MoonCam.Core.Protocol;

/// <summary>A complete MoonLink message: header fields plus payload.</summary>
public sealed record Frame(FrameType Type, ReadOnlyMemory<byte> Payload, FrameFlags Flags = FrameFlags.None, long TimestampUs = 0)
{
    public FrameHeader Header => new(Type, Flags, Payload.Length, TimestampUs);

    public bool IsKeyFrame => (Flags & FrameFlags.KeyFrame) != 0;
}

/// <summary>Raised when the peer violates the MoonLink protocol. The connection must be closed.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException(string message) : base(message) { }

    public ProtocolException(string message, Exception inner) : base(message, inner) { }
}
