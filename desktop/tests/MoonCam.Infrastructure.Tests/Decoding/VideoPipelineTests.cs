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

    private static Frame Key(long ts) => new(FrameType.VideoFrame, new byte[] { 1 }, FrameFlags.KeyFrame, ts);

    private static Frame Delta(long ts) => new(FrameType.VideoFrame, new byte[] { 2 }, FrameFlags.None, ts);

    private sealed class FakeDecoder : IVideoDecoder
    {
        public ManualResetEventSlim? Gate { get; init; }
        public int Configured { get; private set; }

        public void Configure(VideoConfig config) => Configured++;

        public IReadOnlyList<VideoFrame> Decode(ReadOnlyMemory<byte> accessUnit, bool isKeyFrame, long timestampUs)
        {
            Gate?.Wait();
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
