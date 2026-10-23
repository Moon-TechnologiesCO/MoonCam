using System.Buffers.Binary;

namespace MoonCam.Core.Protocol;

/// <summary>The 10 bytes that open every MoonLink connection: ASCII "MOONLINK" + u16 version.</summary>
public static class Preamble
{
    public const int Size = 10;
    public const ushort CurrentVersion = 1;

    public static ReadOnlySpan<byte> Magic => "MOONLINK"u8;

    public static void Write(Span<byte> destination, ushort version = CurrentVersion)
    {
        Magic.CopyTo(destination);
        BinaryPrimitives.WriteUInt16BigEndian(destination[8..], version);
    }

    /// <summary>Returns the peer's protocol version, or throws if the bytes are not a MoonLink preamble.</summary>
    public static ushort Parse(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size || !source[..8].SequenceEqual(Magic))
            throw new ProtocolException("Not a MoonLink connection (bad preamble).");
        return BinaryPrimitives.ReadUInt16BigEndian(source[8..]);
    }
}
