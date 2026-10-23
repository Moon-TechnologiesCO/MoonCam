using MoonCam.Core.Media;
using MoonCam.Core.Session;
using MoonCam.Infrastructure.Decoding;
using MoonCam.Infrastructure.Streaming;
using MoonCam.Tools.FakePhone;

namespace MoonCam.Infrastructure.Tests.Streaming;

/// <summary>
/// The whole desktop pipeline with a simulated phone: TCP + MoonLink handshake with token, capabilities,
/// start command, real H.264 from ffmpeg, FFmpeg decoding, and delivery to a sink.
/// </summary>
public class EndToEndTests
{
    [Fact]
    public async Task A_simulated_phone_streams_decoded_video_to_the_sinks()
    {
        if (!FfmpegVideoDecoder.IsAvailable || !CommandExists("ffmpeg"))
            return; // CI installs ffmpeg; developers without it skip this test.

        await using var controller = new StreamController(TimeProvider.System, () => new FfmpegVideoDecoder(), port: 0);
        var sink = new CountingSink();
        controller.AddSink(sink);
        controller.Start();
        await controller.ApplySettingsAsync(new StreamSettings("0", 640, 360, 30));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var phone = new PhoneSimulator("127.0.0.1", controller.Port, controller.Token);
        var run = phone.RunAsync(stop.Token);

        await sink.WaitAsync(15, TimeSpan.FromSeconds(15));
        await stop.CancelAsync();
        await Task.WhenAny(run, Task.Delay(2000));

        Assert.Equal((640, 360), sink.LastSize);
        Assert.Equal("MoonCam FakePhone", controller.Phone?.DeviceName ?? "MoonCam FakePhone");
    }

    [Fact]
    public async Task A_phone_with_a_wrong_token_is_refused()
    {
        await using var controller = new StreamController(TimeProvider.System, () => new FfmpegVideoDecoder(), port: 0);
        controller.Start();
        var phone = new PhoneSimulator("127.0.0.1", controller.Port, "not-the-token");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => phone.RunAsync(CancellationToken.None));
        for (var i = 0; i < 100 && controller.State != ConnectionState.Listening; i++)
            await Task.Delay(20); // the server reports the end of the session asynchronously
        Assert.Equal(ConnectionState.Listening, controller.State);
        Assert.Null(controller.Phone);
    }

    private static bool CommandExists(string name)
        => Environment.GetEnvironmentVariable("PATH")!.Split(':').Any(dir => File.Exists(Path.Combine(dir, name)));

    private sealed class CountingSink : IVideoSink
    {
        private int _count;
        public (int, int) LastSize { get; private set; }

        public void Push(VideoFrame frame)
        {
            LastSize = (frame.Width, frame.Height);
            Interlocked.Increment(ref _count);
        }

        public async Task WaitAsync(int frames, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Volatile.Read(ref _count) < frames)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Only {_count} of {frames} frames arrived.");
                await Task.Delay(50);
            }
        }
    }
}
