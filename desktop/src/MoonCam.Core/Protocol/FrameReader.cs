namespace MoonCam.Core.Protocol;

/// <summary>Reads MoonLink preambles and frames from any stream (TCP, TLS, or in-memory in tests).</summary>
public sealed class FrameReader(Stream stream)
{
    private readonly byte[] _header = new byte[FrameHeader.Size];

    public async ValueTask<ushort> ReadPreambleAsync(CancellationToken cancellationToken = default)
    {
        var buffer = new byte[Preamble.Size];
        if (!await FillAsync(buffer, cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException("The peer closed the connection before sending its preamble.");
        return Preamble.Parse(buffer);
    }

    /// <summary>
    /// Reads the next frame. Returns <c>null</c> when the peer closes the connection cleanly between frames;
    /// throws if it closes in the middle of one.
    /// </summary>
    public async ValueTask<Frame?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!await FillAsync(_header, cancellationToken).ConfigureAwait(false))
            return null;

        var header = FrameHeader.Parse(_header);
        var payload = header.Length == 0 ? [] : new byte[header.Length];
        if (header.Length > 0 && !await FillAsync(payload, cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException($"Connection closed inside a {header.Type} payload.");

        return new Frame(header.Type, payload, header.Flags, header.TimestampUs);
    }

    /// <summary>Fills the buffer completely. False if the stream ended before the first byte.</summary>
    private async ValueTask<bool> FillAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                if (read == 0)
                    return false;
                throw new EndOfStreamException($"Connection closed after {read} of {buffer.Length} bytes.");
            }
            read += n;
        }
        return true;
    }
}
