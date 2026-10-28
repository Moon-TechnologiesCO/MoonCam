using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using MoonCam.Core.Media;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;
using MoonCam.Infrastructure.Streaming;

namespace MoonCam.Infrastructure.Tests.Streaming;

/// <summary>
/// The phone's camera runs only while someone watches, and the desktop can end a connection for good. A phone
/// over the USB tunnel is played by hand so every command the desktop sends can be checked.
/// </summary>
public sealed class VideoDemandTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = new();
    private readonly StreamController _controller;

    public VideoDemandTests()
    {
        _controller = new StreamController(_time, () => new NullDecoder(), port: 0);
        _controller.Start();
    }

    [Fact]
    public async Task The_camera_starts_while_the_preview_is_shown_and_rests_once_nobody_watches()
    {
        await using var phone = await UsbPhone.ConnectAsync(_controller.Port, _controller.Token);
        Assert.Equal(ControlOps.Start, (await phone.NextControlAsync()).Op);

        _controller.SetPreviewShown(false);
        await AdvanceAsync(StreamController.PauseGrace);

        Assert.Equal(ControlOps.Stop, (await phone.NextControlAsync()).Op);
        Assert.True(_controller.IsVideoPaused);

        _controller.SetPreviewShown(true);
        Assert.Equal(ControlOps.Start, (await phone.NextControlAsync()).Op);
        Assert.False(_controller.IsVideoPaused);
    }

    [Fact]
    public async Task A_phone_that_connects_while_nobody_watches_keeps_its_camera_off()
    {
        _controller.SetPreviewShown(false);
        await using var phone = await UsbPhone.ConnectAsync(_controller.Port, _controller.Token);
        await WaitAsync(() => _controller.IsVideoPaused);

        _controller.SetPreviewShown(true);

        Assert.Equal(ControlOps.Start, (await phone.NextControlAsync()).Op); // the first command is the start
    }

    [Fact]
    public async Task A_quick_minimise_does_not_restart_the_camera()
    {
        await using var phone = await UsbPhone.ConnectAsync(_controller.Port, _controller.Token);
        Assert.Equal(ControlOps.Start, (await phone.NextControlAsync()).Op);

        _controller.SetPreviewShown(false);
        await AdvanceAsync(StreamController.PauseGrace / 2);
        _controller.SetPreviewShown(true);
        await AdvanceAsync(StreamController.PauseGrace);

        Assert.False(_controller.IsVideoPaused);
    }

    [Fact]
    public async Task Disconnect_says_goodbye_and_the_desktop_waits_for_a_new_phone()
    {
        await using var phone = await UsbPhone.ConnectAsync(_controller.Port, _controller.Token);
        await phone.NextControlAsync();

        await _controller.DisconnectAsync();

        Assert.Equal("disconnected", await phone.ByeReasonAsync());
        await WaitAsync(() => _controller.State == ConnectionState.Listening && _controller.Phone is null);
    }

    /// <summary>Moves the clock in small steps so heartbeats keep flowing, as they would in real time.</summary>
    private async Task AdvanceAsync(TimeSpan total)
    {
        for (var elapsed = TimeSpan.Zero; elapsed < total; elapsed += TimeSpan.FromMilliseconds(250))
        {
            _time.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(15);
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync() => await _controller.DisposeAsync();

    /// <summary>The phone's side over the adb tunnel: token handshake, capabilities, then it answers pings and listens.</summary>
    private sealed class UsbPhone : IAsyncDisposable
    {
        private readonly TcpClient _tcp;
        private readonly FrameReader _reader;
        private readonly FrameWriter _writer;
        private readonly Channel<Frame> _received = Channel.CreateUnbounded<Frame>();

        private UsbPhone(TcpClient tcp)
        {
            _tcp = tcp;
            _reader = new FrameReader(tcp.GetStream());
            _writer = new FrameWriter(tcp.GetStream());
        }

        public static async Task<UsbPhone> ConnectAsync(int port, string token)
        {
            var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port);
            var phone = new UsbPhone(tcp);
            await phone._writer.WritePreambleAsync();
            await phone._reader.ReadPreambleAsync();
            await phone._writer.WriteAsync(Messages.Hello(new HelloMessage("Test Phone", "Moon", "Test", "16", "0.3.0", token)));
            Assert.True(Messages.ReadWelcome((await phone._reader.ReadAsync())!).Accepted);
            await phone._writer.WriteAsync(Messages.Capabilities(new CapabilitiesMessage(
                [new CameraInfo("0", "back", "wide", null, 4.7, [new VideoSize(1280, 720)], [30])])));
            _ = phone.PumpAsync();
            return phone;
        }

        private async Task PumpAsync()
        {
            try
            {
                while (await _reader.ReadAsync() is { } frame)
                {
                    if (frame.Type == FrameType.Ping)
                        await _writer.WriteAsync(Heartbeat.Pong(frame));
                    else
                        _received.Writer.TryWrite(frame);
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // The test or the desktop closed the connection.
            }
            _received.Writer.TryComplete();
        }

        public async Task<ControlMessage> NextControlAsync()
        {
            var frame = await NextAsync(FrameType.Control);
            return Messages.ReadControl(frame);
        }

        public async Task<string> ByeReasonAsync() => Messages.ReadBye(await NextAsync(FrameType.Bye)).Reason;

        private async Task<Frame> NextAsync(FrameType type)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await foreach (var frame in _received.Reader.ReadAllAsync(timeout.Token))
            {
                if (frame.Type == type)
                    return frame;
            }
            throw new EndOfStreamException($"The desktop closed the connection before sending {type}.");
        }

        public async ValueTask DisposeAsync()
        {
            _writer.Dispose();
            _tcp.Dispose();
            await Task.CompletedTask;
        }
    }

    private sealed class NullDecoder : IVideoDecoder
    {
        public void Configure(VideoConfig config) { }
        public IReadOnlyList<VideoFrame> Decode(ReadOnlyMemory<byte> accessUnit, bool isKeyFrame, long timestampUs) => [];
        public void Dispose() { }
    }
}
