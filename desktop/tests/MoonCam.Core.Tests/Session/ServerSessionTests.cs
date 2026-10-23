using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Time.Testing;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;

namespace MoonCam.Core.Tests.Session;

/// <summary>Drives a <see cref="ServerSession"/> through a real loopback socket, playing the phone by hand.</summary>
public sealed class ServerSessionTests : IAsyncDisposable
{
    private const string Token = "secret-token";
    private readonly RecordingHandler _handler = new(Token);
    private readonly FakeTimeProvider _time = new();
    private readonly NetworkStream _desktop;
    private readonly NetworkStream _phone;
    private readonly FrameReader _phoneReader;
    private readonly FrameWriter _phoneWriter;

    public ServerSessionTests()
    {
        (_desktop, _phone) = ConnectedPair();
        _phoneReader = new FrameReader(_phone);
        _phoneWriter = new FrameWriter(_phone);
    }

    [Fact]
    public async Task Accepts_a_phone_with_the_right_token_and_dispatches_frames()
    {
        await using var session = new ServerSession(_desktop, _handler, TimeProvider.System);
        var run = session.RunAsync(CancellationToken.None);

        var welcome = await HandshakeAsync(Token);
        Assert.True(welcome.Accepted);

        await _phoneWriter.WriteAsync(new VideoConfig(VideoCodec.H264, 1280, 720, 30, new byte[] { 0, 0, 0, 1, 0x67 }).ToFrame());
        await _phoneWriter.WriteAsync(new Frame(FrameType.VideoFrame, new byte[] { 0, 0, 0, 1, 0x65 }, FrameFlags.KeyFrame, 1000));
        await _phoneWriter.WriteAsync(Messages.Bye("done"));

        var end = await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SessionEndReason.PeerClosed, end.Reason);
        Assert.Equal("done", end.Detail);
        Assert.Equal(1280, _handler.VideoConfig!.Width);
        Assert.Single(_handler.VideoFrames);
        Assert.Equal("Pixel 9", session.Phone!.DeviceName);
    }

    [Fact]
    public async Task Rejects_a_phone_with_a_wrong_token()
    {
        await using var session = new ServerSession(_desktop, _handler, TimeProvider.System);
        var run = session.RunAsync(CancellationToken.None);

        var welcome = await HandshakeAsync("guess");
        var bye = await _phoneReader.ReadAsync();

        Assert.False(welcome.Accepted);
        Assert.Equal(FrameType.Bye, bye!.Type);
        Assert.Equal(SessionEndReason.Rejected, (await run.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
    }

    [Fact]
    public async Task Sends_pings_and_times_out_when_the_phone_goes_silent()
    {
        var options = new SessionOptions { PingInterval = TimeSpan.FromSeconds(1), PeerTimeout = TimeSpan.FromSeconds(3) };
        await using var session = new ServerSession(_desktop, _handler, _time, options);
        var run = session.RunAsync(CancellationToken.None);
        await HandshakeAsync(Token);

        _time.Advance(TimeSpan.FromSeconds(1));
        var ping = await _phoneReader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(FrameType.Ping, ping!.Type);

        for (var i = 0; i < 4 && !run.IsCompleted; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(50);
        }

        Assert.Equal(SessionEndReason.TimedOut, (await run.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
    }

    [Fact]
    public async Task A_client_that_is_not_moonlink_is_a_protocol_error()
    {
        await using var session = new ServerSession(_desktop, _handler, TimeProvider.System);
        var run = session.RunAsync(CancellationToken.None);

        await _phone.WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());

        Assert.Equal(SessionEndReason.ProtocolError, (await run.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
    }

    [Fact]
    public async Task A_second_hello_after_the_handshake_is_a_protocol_error()
    {
        await using var session = new ServerSession(_desktop, _handler, TimeProvider.System);
        var run = session.RunAsync(CancellationToken.None);
        await HandshakeAsync(Token);

        await _phoneWriter.WriteAsync(Messages.Hello(Phone(Token)));

        Assert.Equal(SessionEndReason.ProtocolError, (await run.WaitAsync(TimeSpan.FromSeconds(5))).Reason);
    }

    private async Task<WelcomeMessage> HandshakeAsync(string token)
    {
        await _phoneWriter.WritePreambleAsync();
        Assert.Equal(1, await _phoneReader.ReadPreambleAsync());
        await _phoneWriter.WriteAsync(Messages.Hello(Phone(token)));
        return Messages.ReadWelcome((await _phoneReader.ReadAsync())!);
    }

    private static HelloMessage Phone(string token) => new("Pixel 9", "Google", "Pixel 9", "16", "0.1.0", token);

    private static (NetworkStream Server, NetworkStream Client) ConnectedPair()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        var server = listener.AcceptTcpClient();
        return (server.GetStream(), client.GetStream());
    }

    public async ValueTask DisposeAsync()
    {
        _phoneWriter.Dispose();
        await _phone.DisposeAsync();
        await _desktop.DisposeAsync();
    }

    private sealed class RecordingHandler(string token) : ISessionHandler
    {
        public VideoConfig? VideoConfig { get; private set; }
        public List<Frame> VideoFrames { get; } = [];

        public WelcomeMessage Authenticate(HelloMessage hello) => SessionToken.Matches(token, hello.Token)
            ? new WelcomeMessage(true, "s-1", "test-desktop")
            : new WelcomeMessage(false, null, "test-desktop", "This phone was not invited.");

        public void OnCapabilities(CapabilitiesMessage capabilities) { }
        public void OnVideoConfig(VideoConfig config) => VideoConfig = config;
        public void OnVideoFrame(Frame frame) => VideoFrames.Add(frame);
        public void OnAudioConfig(AudioConfig config) { }
        public void OnAudioFrame(Frame frame) { }
        public void OnControl(ControlMessage message) { }
    }
}
