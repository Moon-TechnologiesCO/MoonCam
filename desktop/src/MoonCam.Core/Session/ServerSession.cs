using MoonCam.Core.Protocol;

namespace MoonCam.Core.Session;

/// <summary>Receives what a connected phone sends. Called from the session's read loop, one call at a time.</summary>
public interface ISessionHandler
{
    /// <summary>Decides whether the phone may connect. Return a rejected welcome to refuse it.</summary>
    WelcomeMessage Authenticate(HelloMessage hello);

    void OnCapabilities(CapabilitiesMessage capabilities);

    void OnVideoConfig(VideoConfig config);

    void OnVideoFrame(Frame frame);

    void OnAudioConfig(AudioConfig config);

    void OnAudioFrame(Frame frame);

    void OnControl(ControlMessage message);
}

public enum SessionEndReason
{
    /// <summary>The phone said goodbye (BYE) or closed the socket between frames.</summary>
    PeerClosed,
    /// <summary>The phone was not authorised.</summary>
    Rejected,
    /// <summary>Nothing arrived for longer than the heartbeat timeout.</summary>
    TimedOut,
    /// <summary>The phone broke the protocol, or the connection broke mid-frame.</summary>
    ProtocolError,
    /// <summary>The desktop ended the session.</summary>
    Cancelled,
}

public sealed record SessionEnd(SessionEndReason Reason, string? Detail = null);

public sealed record SessionOptions
{
    public string ServerName { get; init; } = Environment.MachineName;
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan PeerTimeout { get; init; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// The desktop side of one MoonLink connection: preamble, HELLO/WELCOME, heartbeat and dispatch.
/// It works on any <see cref="Stream"/>, so it is tested without sockets.
/// </summary>
public sealed class ServerSession : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly ISessionHandler _handler;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly FrameReader _reader;
    private readonly FrameWriter _writer;
    private readonly HeartbeatMonitor _heartbeat;
    private ulong _nextNonce;

    public ServerSession(Stream stream, ISessionHandler handler, TimeProvider time, SessionOptions? options = null)
    {
        _stream = stream;
        _handler = handler;
        _time = time;
        _options = options ?? new SessionOptions();
        _reader = new FrameReader(stream);
        _writer = new FrameWriter(stream);
        _heartbeat = new HeartbeatMonitor(time, _options.PeerTimeout);
    }

    /// <summary>Receives this session's messages (one handler per connection).</summary>
    public ISessionHandler Handler => _handler;

    /// <summary>The phone, once HELLO has been accepted.</summary>
    public HelloMessage? Phone { get; private set; }

    /// <summary>Last measured round-trip time, from PING/PONG.</summary>
    public TimeSpan? RoundTripTime { get; private set; }

    public async Task<SessionEnd> RunAsync(CancellationToken cancellationToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var rejected = await HandshakeAsync(session.Token).ConfigureAwait(false);
            if (rejected is not null)
                return rejected;

            var heartbeat = HeartbeatLoopAsync(session);
            var end = await ReadLoopAsync(session.Token).ConfigureAwait(false);
            await session.CancelAsync().ConfigureAwait(false);
            var heartbeatEnd = await heartbeat.ConfigureAwait(false);
            return heartbeatEnd ?? end;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new SessionEnd(SessionEndReason.Cancelled);
        }
        catch (OperationCanceledException)
        {
            return new SessionEnd(SessionEndReason.TimedOut, "The phone stopped answering.");
        }
        catch (Exception e) when (e is ProtocolException or EndOfStreamException or IOException)
        {
            return new SessionEnd(SessionEndReason.ProtocolError, e.Message);
        }
    }

    public ValueTask SendControlAsync(ControlMessage message, CancellationToken cancellationToken = default)
        => _writer.WriteAsync(Messages.Control(message), cancellationToken);

    /// <summary>Politely ends the session: BYE, then the caller disposes the stream.</summary>
    public async ValueTask SayGoodbyeAsync(string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            await _writer.WriteAsync(Messages.Bye(reason), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The peer is already gone; nothing to say goodbye to.
        }
    }

    private async Task<SessionEnd?> HandshakeAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_options.HandshakeTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var version = await _reader.ReadPreambleAsync(linked.Token).ConfigureAwait(false);
        await _writer.WritePreambleAsync(Preamble.CurrentVersion, linked.Token).ConfigureAwait(false);
        if (version != Preamble.CurrentVersion)
            return new SessionEnd(SessionEndReason.ProtocolError, $"Unsupported protocol version {version}.");

        var first = await _reader.ReadAsync(linked.Token).ConfigureAwait(false)
                    ?? throw new EndOfStreamException("The phone closed the connection before HELLO.");
        var hello = Messages.ReadHello(first);
        var welcome = _handler.Authenticate(hello);
        await _writer.WriteAsync(Messages.Welcome(welcome), linked.Token).ConfigureAwait(false);

        if (!welcome.Accepted)
        {
            await SayGoodbyeAsync(welcome.Reason ?? "rejected", linked.Token).ConfigureAwait(false);
            return new SessionEnd(SessionEndReason.Rejected, welcome.Reason);
        }

        Phone = hello;
        _heartbeat.MarkReceived();
        return null;
    }

    private async Task<SessionEnd> ReadLoopAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var frame = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
                return new SessionEnd(SessionEndReason.PeerClosed);

            _heartbeat.MarkReceived();
            switch (frame.Type)
            {
                case FrameType.VideoFrame: _handler.OnVideoFrame(frame); break;
                case FrameType.AudioFrame: _handler.OnAudioFrame(frame); break;
                case FrameType.VideoConfig: _handler.OnVideoConfig(VideoConfig.FromFrame(frame)); break;
                case FrameType.AudioConfig: _handler.OnAudioConfig(AudioConfig.FromFrame(frame)); break;
                case FrameType.Capabilities: _handler.OnCapabilities(Messages.ReadCapabilities(frame)); break;
                case FrameType.Control: _handler.OnControl(Messages.ReadControl(frame)); break;
                case FrameType.Pong: RoundTripTime = TimeSpan.FromMicroseconds(NowUs() - frame.TimestampUs); break;
                case FrameType.Ping: await _writer.WriteAsync(Heartbeat.Pong(frame), cancellationToken).ConfigureAwait(false); break;
                case FrameType.Bye: return new SessionEnd(SessionEndReason.PeerClosed, Messages.ReadBye(frame).Reason);
                default: throw new ProtocolException($"{frame.Type} is not allowed after the handshake.");
            }
        }
    }

    /// <summary>Sends PINGs and watches for silence. Returns a TimedOut end, or null when cancelled normally.</summary>
    private async Task<SessionEnd?> HeartbeatLoopAsync(CancellationTokenSource session)
    {
        try
        {
            while (!session.IsCancellationRequested)
            {
                await Task.Delay(_options.PingInterval, _time, session.Token).ConfigureAwait(false);
                if (_heartbeat.IsTimedOut)
                {
                    await session.CancelAsync().ConfigureAwait(false);
                    return new SessionEnd(SessionEndReason.TimedOut, $"No data for {_heartbeat.SinceLastReceived.TotalSeconds:0.0} s.");
                }
                await _writer.WriteAsync(Heartbeat.Ping(++_nextNonce, NowUs()), session.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            await session.CancelAsync().ConfigureAwait(false);
        }
        return null;
    }

    private long NowUs() => (long)_time.GetElapsedTime(0).TotalMicroseconds;

    public async ValueTask DisposeAsync()
    {
        _writer.Dispose();
        await _stream.DisposeAsync().ConfigureAwait(false);
    }
}
