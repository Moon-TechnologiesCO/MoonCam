using MoonCam.Core.Media;
using MoonCam.Core.Protocol;
using MoonCam.Infrastructure.Decoding;

namespace MoonCam.Infrastructure.Tests.Decoding;

public class VideoPipelineTests
{
    private static readonly VideoConfig Config = new(VideoCodec.H264, 2, 2, 30, default);

    [Fact]
    public async Task Waits_for_a_key_frame_then_feeds_every_sink()
    {
        var decoder = new FakeDecoder();
        var sink = new CollectingSink();
        await using (var pipeline = new VideoPipeline(decoder))
        {
            pipeline.AddSink(sink);
            pipeline.Configure(Config);
            pipeline.Submit(Delta(1)); // dropped: no key frame yet
            pipeline.Submit(Key(2));
            pipeline.Submit(Delta(3));
            await sink.WaitForAsync(2);
        }

        Assert.Equal([2L, 3L], sink.Timestamps);
        Assert.Equal(1, decoder.Configured);
    }

    [Fact]
    public async Task Falling_behind_drops_the_backlog_and_asks_for_a_key_frame()
    {
        var decoder = new FakeDecoder { Gate = new ManualResetEventSlim(false) };
        var keyFrameRequests = 0;
        var sink = new CollectingSink();
        await using (var pipeline = new VideoPipeline(decoder))
        {
            pipeline.AddSink(sink);
            pipeline.KeyFrameNeeded += () => keyFrameRequests++;
            pipeline.Configure(Config);
            pipeline.Submit(Key(0));
            for (var i = 1; i <= 20; i++) // the decoder is stuck, so the queue overflows
                pipeline.Submit(Delta(i));

            Assert.True(keyFrameRequests >= 1);
            Assert.True(pipeline.FramesDropped > 0);

            pipeline.Submit(Key(100));
            decoder.Gate.Set();
            await sink.WaitForAsync(t => t == 100);
        }
        // After the overflow nothing from the stale backlog reached the sinks: only frames up to the
        // overflow point and the new key frame.
        Assert.Contains(100L, sink.Timestamps);
    }

    [Fact]
    public async Task A_failing_sink_never_stops_the_others()
    {
        // The bug found on a real phone: OBS locked the virtual camera, the camera threw, and the preview froze.
        var preview = new CollectingSink();
        var errors = new List<Exception>();
        await using (var pipeline = new VideoPipeline(new FakeDecoder()))
        {
            pipeline.AddSink(new ThrowingSink());
            pipeline.AddSink(preview);
            pipeline.SinkFailed += (_, e) => { lock (errors) errors.Add(e); };
            pipeline.Configure(Config);
            for (var i = 0; i < 5; i++)
                pipeline.Submit(i == 0 ? Key(i) : Delta(i));
            await preview.WaitForAsync(5);
        }

        Assert.Equal([0L, 1L, 2L, 3L, 4L], preview.Timestamps);
        Assert.Equal(5, errors.Count);
    }

    [Fact]
    public async Task A_decoder_error_skips_one_frame_and_keeps_going()
    {
        var sink = new CollectingSink();
        var failures = 0;
        await using (var pipeline = new VideoPipeline(new FakeDecoder { FailAt = 2 }))
        {
            pipeline.AddSink(sink);
            pipeline.DecodeFailed += _ => Interlocked.Increment(ref failures);
            pipeline.Configure(Config);
            for (var i = 0; i < 5; i++)
                pipeline.Submit(i == 0 ? Key(i) : Delta(i));
            await sink.WaitForAsync(4);
        }

        Assert.Equal([0L, 1L, 3L, 4L], sink.Timestamps);
        Assert.Equal(1, failures);
    }

    private sealed class ThrowingSink : IVideoSink
    {
        public void Push(VideoFrame frame) => throw new IOException("/dev/video10 is busy");
    }

    private static Frame Key(long ts) => new(FrameType.VideoFrame, new byte[] { 1 }, FrameFlags.KeyFrame, ts);

    private static Frame Delta(long ts) => new(FrameType.VideoFrame, new byte[] { 2 }, FrameFlags.None, ts);

    private sealed class FakeDecoder : IVideoDecoder
    {
        public ManualResetEventSlim? Gate { get; init; }
        public long FailAt { get; init; } = -1;
        public int Configured { get; private set; }

        public void Configure(VideoConfig config) => Configured++;

        public IReadOnlyList<VideoFrame> Decode(ReadOnlyMemory<byte> accessUnit, bool isKeyFrame, long timestampUs)
        {
            Gate?.Wait();
            if (timestampUs == FailAt)
                throw new ArgumentException("corrupt slice");
            return [new VideoFrame(2, 2, new byte[6], timestampUs)];
        }

        public void Dispose() { }
    }

    private sealed class CollectingSink : IVideoSink
    {
        private readonly List<long> _timestamps = [];

        public IReadOnlyList<long> Timestamps { get { lock (_timestamps) return [.. _timestamps]; } }

        public void Push(VideoFrame frame)
        {
            lock (_timestamps)
                _timestamps.Add(frame.TimestampUs);
        }

        public Task WaitForAsync(int count) => WaitForAsync(_ => Timestamps.Count >= count, any: true);

        public Task WaitForAsync(Func<long, bool> predicate) => WaitForAsync(predicate, any: false);

        private async Task WaitForAsync(Func<long, bool> predicate, bool any)
        {
            for (var i = 0; i < 200; i++)
            {
                if (any ? predicate(0) : Timestamps.Any(predicate))
                    return;
                await Task.Delay(10);
            }
            throw new TimeoutException("The pipeline did not deliver the expected frames.");
        }
    }
}
