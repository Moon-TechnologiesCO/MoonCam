using System.Buffers;

namespace MoonCam.Core.Protocol;

/// <summary>
/// Writes MoonLink preambles and frames. Safe to call from several tasks at once: each frame is written
/// atomically, so a heartbeat never lands in the middle of a video frame.
/// </summary>
public sealed class FrameWriter(Stream stream) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async ValueTask WritePreambleAsync(ushort version = Preamble.CurrentVersion, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[Preamble.Size];
        Preamble.Write(buffer, version);
        await WriteLockedAsync(buffer, ReadOnlyMemory<byte>.Empty, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask WriteAsync(Frame frame, CancellationToken cancellationToken = default)
    {
        var header = ArrayPool<byte>.Shared.Rent(FrameHeader.Size);
        try
        {
            frame.Header.WriteTo(header);
            await WriteLockedAsync(header.AsMemory(0, FrameHeader.Size), frame.Payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(header);
        }
    }

    private async ValueTask WriteLockedAsync(ReadOnlyMemory<byte> first, ReadOnlyMemory<byte> second, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(first, cancellationToken).ConfigureAwait(false);
            if (!second.IsEmpty)
                await stream.WriteAsync(second, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
