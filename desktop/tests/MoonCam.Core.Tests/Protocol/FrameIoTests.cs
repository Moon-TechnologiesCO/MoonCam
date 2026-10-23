using MoonCam.Core.Protocol;

namespace MoonCam.Core.Tests.Protocol;

public class FrameIoTests
{
    [Fact]
    public async Task Frames_round_trip_through_a_stream()
    {
        var stream = new MemoryStream();
        using (var writer = new FrameWriter(stream))
        {
            await writer.WritePreambleAsync();
            await writer.WriteAsync(new Frame(FrameType.VideoFrame, new byte[] { 1, 2, 3 }, FrameFlags.KeyFrame, 42));
            await writer.WriteAsync(Heartbeat.Ping(7, 99));
        }
        stream.Position = 0;
        var reader = new FrameReader(stream);

        Assert.Equal(1, await reader.ReadPreambleAsync());
        var video = await reader.ReadAsync();
        Assert.True(video!.IsKeyFrame);
        Assert.Equal(42, video.TimestampUs);
        Assert.Equal(new byte[] { 1, 2, 3 }, video.Payload.ToArray());
        var ping = await reader.ReadAsync();
        Assert.Equal(7UL, Heartbeat.ReadNonce(ping!));
        Assert.Null(await reader.ReadAsync()); // clean end of stream between frames
    }

    [Fact]
    public async Task Stream_ending_inside_a_payload_is_an_error()
    {
        var stream = new MemoryStream();
        using (var writer = new FrameWriter(stream))
            await writer.WriteAsync(new Frame(FrameType.VideoFrame, new byte[100]));
        var truncated = new MemoryStream(stream.ToArray()[..50]);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await new FrameReader(truncated).ReadAsync());
    }

    [Fact]
    public async Task Bad_preamble_is_rejected()
    {
        var stream = new MemoryStream("GET / HTTP/1.1\r\n"u8.ToArray());

        await Assert.ThrowsAsync<ProtocolException>(async () => await new FrameReader(stream).ReadPreambleAsync());
    }

    [Fact]
    public async Task Concurrent_writes_never_interleave()
    {
        var stream = new MemoryStream();
        using var writer = new FrameWriter(stream);
        var big = new byte[64 * 1024];
        Array.Fill(big, (byte)0xAB);

        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => i % 2 == 0
            ? writer.WriteAsync(new Frame(FrameType.VideoFrame, big)).AsTask()
            : writer.WriteAsync(Heartbeat.Ping((ulong)i, i)).AsTask()));

        stream.Position = 0;
        var reader = new FrameReader(stream);
        var count = 0;
        while (await reader.ReadAsync() is { } frame)
        {
            Assert.True(frame.Type is FrameType.VideoFrame or FrameType.Ping);
            count++;
        }
        Assert.Equal(50, count);
    }

    [Fact]
    public void Header_refuses_to_write_an_oversized_length()
    {
        var header = new FrameHeader(FrameType.VideoFrame, FrameFlags.None, FrameHeader.MaxPayloadLength + 1, 0);

        Assert.Throws<ProtocolException>(() => header.WriteTo(new byte[FrameHeader.Size]));
    }
}
