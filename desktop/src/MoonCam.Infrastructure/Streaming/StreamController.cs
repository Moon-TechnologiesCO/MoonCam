using System.Net;
using MoonCam.Core.Diagnostics;
using MoonCam.Core.Media;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;
using MoonCam.Infrastructure.Adb;
using MoonCam.Infrastructure.Decoding;
using MoonCam.Infrastructure.Network;

namespace MoonCam.Infrastructure.Streaming;

/// <summary>Live numbers for the status bar.</summary>
public sealed record StreamStats(int Width, int Height, double FramesPerSecond, long FramesDropped, TimeSpan? RoundTrip);

/// <summary>
/// The application's single entry point to streaming: it owns the MoonLink server, authorises phones,
/// feeds the decoder and the sinks, and drives the connection state. The UI only talks to this class.
/// </summary>
public sealed class StreamController : IAsyncDisposable
{
    private readonly ConnectionStateMachine _state = new();
    private readonly TimeProvider _time;
    private readonly int _port;
    private readonly Func<IVideoDecoder> _decoderFactory;
    private readonly AdbClient _adb;
    private readonly IEventLog _log;
    private readonly List<IVideoSink> _sinks = [];
    private readonly Lock _lock = new();
    private MoonLinkServer? _server;
    private VideoPipeline? _pipeline;
    private ServerSession? _session;
    private IVirtualCamera? _virtualCamera;
    private string _token = SessionToken.Create();
    private StreamSettings _settings = StreamSettings.Default;
    private VideoConfig? _videoConfig;
    private long _lastStatsFrames;
    private long _lastStatsTimestamp;
    private string? _lastProblem;

    /// <param name="fixedToken">Development only: a known token so the FakePhone tool can connect.</param>
    public StreamController(TimeProvider time, Func<IVideoDecoder> decoderFactory, AdbClient? adb = null, int port = MoonLinkServer.DefaultPort,
        string? fixedToken = null, IEventLog? log = null)
    {
        _log = log ?? NullEventLog.Instance;
        _time = time;
        _decoderFactory = decoderFactory;
        _adb = adb ?? new AdbClient();
        _port = port;
        if (fixedToken is not null)
            _token = fixedToken;
        _state.Changed += (_, now) => StateChanged?.Invoke(now);
    }

    public ConnectionState State => _state.State;

    /// <summary>The token a phone must present. Exposed for the FakePhone tool and tests.</summary>
    public string Token => _token;

    public HelloMessage? Phone => _session?.Phone;

    public CapabilitiesMessage? Capabilities { get; private set; }

    public StreamSettings Settings => _settings;

    public string? VirtualCameraPath => _virtualCamera?.DevicePath;

    public int Port => _server?.LocalEndpoint.Port ?? _port;

    public event Action<ConnectionState>? StateChanged;

    public event Action<CapabilitiesMessage>? CapabilitiesChanged;

    /// <summary>Human-readable problems and phone status messages for the UI.</summary>
    public event Action<string>? Notice;

    /// <summary>Starts listening on 127.0.0.1 (USB mode). Safe to call once.</summary>
    public void Start()
    {
        _pipeline = new VideoPipeline(_decoderFactory());
        _pipeline.KeyFrameNeeded += () => _ = SendAsync(new ControlMessage(ControlOps.KeyFrame));
        _pipeline.DecodeFailed += e => ReportProblem("Video decoding problem", e);
        _pipeline.SinkFailed += (sink, e) => ReportProblem(
            sink is IVirtualCamera ? "The virtual camera could not be updated; the preview continues" : "Preview problem", e);
        foreach (var sink in _sinks)
            _pipeline.AddSink(sink);

        _server = new MoonLinkServer(new IPEndPoint(IPAddress.Loopback, _port), () => new Handler(this), _time);
        _server.SessionStarted += session =>
        {
            _session = session;
            _state.TryMoveTo(ConnectionState.Handshaking);
            _log.Info("Phone connecting");
        };
        _server.SessionEnded += OnSessionEnded;
        _server.Start();
        _state.TryMoveTo(ConnectionState.Listening);
        _log.Info($"Listening on {_server.LocalEndpoint}");
    }

    public void AddSink(IVideoSink sink)
    {
        lock (_lock)
            _sinks.Add(sink);
        _pipeline?.AddSink(sink);
    }

    /// <summary>Finds the first authorised phone on USB, opens the tunnel and launches MoonCam on it.</summary>
    public async Task<string> ConnectUsbAsync(CancellationToken cancellationToken = default)
    {
        if (!await _adb.IsInstalledAsync(cancellationToken).ConfigureAwait(false))
            return "adb is not installed. Install it with: sudo apt install adb";

        var devices = await _adb.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        var phone = devices.FirstOrDefault(d => d.IsReady);
        if (phone is null)
        {
            return devices.Any(d => d.State == AdbDeviceState.Unauthorized)
                ? "Unlock your phone and accept the \"Allow USB debugging\" prompt."
                : "No phone found. Connect it with a USB cable and enable USB debugging.";
        }

        _token = SessionToken.Create(); // a fresh secret for every launch
        await _adb.ReverseAsync(phone.Serial, Port, cancellationToken).ConfigureAwait(false);
        await _adb.LaunchAppAsync(phone.Serial, _token, Port, cancellationToken).ConfigureAwait(false);
        return $"Opening MoonCam on {phone.Model ?? phone.Serial}…";
    }

    /// <summary>Applies new settings; if a phone is connected it restarts the stream with them.</summary>
    public async Task ApplySettingsAsync(StreamSettings settings)
    {
        var errors = settings.Validate();
        if (errors.Count > 0)
            throw new ArgumentException(string.Join(" ", errors), nameof(settings));
        _settings = settings;
        _log.Info($"Settings: camera {settings.CameraId}, {settings.Width}x{settings.Height} @ {settings.Fps} fps, {settings.Quality}");
        if (_session?.Phone is not null)
            await SendAsync(settings.ToStartCommand()).ConfigureAwait(false);
    }

    /// <summary>Starts writing to a v4l2loopback (or other) virtual camera.</summary>
    public void EnableVirtualCamera(IVirtualCamera camera)
    {
        DisableVirtualCamera();
        _virtualCamera = camera;
        AddSink(camera);
        _log.Info($"Virtual camera on: {camera.DevicePath}");
    }

    public void DisableVirtualCamera()
    {
        if (_virtualCamera is null)
            return;
        _pipeline?.RemoveSink(_virtualCamera);
        lock (_lock)
            _sinks.Remove(_virtualCamera);
        _virtualCamera.Dispose();
        _virtualCamera = null;
    }

    public StreamStats GetStats()
    {
        var now = _time.GetTimestamp();
        var frames = _pipeline?.FramesDecoded ?? 0;
        var seconds = _lastStatsTimestamp == 0 ? 0 : _time.GetElapsedTime(_lastStatsTimestamp, now).TotalSeconds;
        var fps = seconds > 0 ? (frames - _lastStatsFrames) / seconds : 0;
        (_lastStatsFrames, _lastStatsTimestamp) = (frames, now);
        return new StreamStats(_videoConfig?.Width ?? 0, _videoConfig?.Height ?? 0, fps, _pipeline?.FramesDropped ?? 0, _session?.RoundTripTime);
    }

    /// <summary>Logs every problem; shows it to the user once until a different one happens.</summary>
    private void ReportProblem(string what, Exception error)
    {
        _log.Warn(what, error);
        var text = $"{what}: {error.Message}";
        if (text == _lastProblem)
            return;
        _lastProblem = text;
        Notice?.Invoke(text);
    }

    private async Task SendAsync(ControlMessage message)
    {
        try
        {
            if (_session is { } session)
                await session.SendControlAsync(message).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // The session is ending; the state machine already reflects it.
        }
    }

    private void OnSessionEnded(ServerSession session, SessionEnd end)
    {
        var wasAuthorised = session.Phone is not null;
        _log.Info($"Session ended: {end.Reason}{(end.Detail is null ? "" : $" ({end.Detail})")}");
        _session = null;
        _videoConfig = null;
        var next = wasAuthorised && end.Reason is SessionEndReason.TimedOut or SessionEndReason.ProtocolError
            ? ConnectionState.Reconnecting
            : ConnectionState.Listening;
        if (!_state.TryMoveTo(next))
            _state.TryMoveTo(ConnectionState.Listening);
        if (end.Reason == SessionEndReason.Rejected)
            Notice?.Invoke("A phone tried to connect without a valid invitation and was refused.");
        else if (end.Detail is { Length: > 0 } detail && end.Reason != SessionEndReason.PeerClosed)
            Notice?.Invoke($"Connection lost: {detail}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
            await session.SayGoodbyeAsync("desktop closing").ConfigureAwait(false);
        if (_server is not null)
            await _server.DisposeAsync().ConfigureAwait(false);
        if (_pipeline is not null)
            await _pipeline.DisposeAsync().ConfigureAwait(false);
        DisableVirtualCamera();
        _state.TryMoveTo(ConnectionState.Stopped);
    }

    /// <summary>Per-session callbacks; it forwards everything to the controller.</summary>
    private sealed class Handler(StreamController owner) : ISessionHandler
    {
        public WelcomeMessage Authenticate(HelloMessage hello)
        {
            if (!SessionToken.Matches(owner._token, hello.Token))
            {
                owner._log.Warn($"Refused a phone with an invalid token ({hello.Manufacturer} {hello.Model})");
                return new WelcomeMessage(false, null, Environment.MachineName, "This phone was not invited by this computer.");
            }
            owner._state.TryMoveTo(ConnectionState.Connected);
            owner._log.Info($"Phone connected: {hello.Manufacturer} {hello.Model}, Android {hello.AndroidVersion}, app {hello.AppVersion}");
            return new WelcomeMessage(true, Guid.NewGuid().ToString("N"), Environment.MachineName);
        }

        public void OnCapabilities(CapabilitiesMessage capabilities)
        {
            owner.Capabilities = capabilities;
            owner.CapabilitiesChanged?.Invoke(capabilities);
            _ = owner.SendAsync(owner._settings.ToStartCommand());
        }

        public void OnVideoConfig(VideoConfig config)
        {
            owner._log.Info($"Video: {config.Codec} {config.Width}x{config.Height} @ {config.Fps} fps");
            owner._videoConfig = config;
            owner._pipeline?.Configure(config);
        }

        public void OnVideoFrame(Frame frame)
        {
            owner._pipeline?.Submit(frame);
            if (owner.State == ConnectionState.Connected)
                owner._state.TryMoveTo(ConnectionState.Streaming);
        }

        public void OnAudioConfig(AudioConfig config) { } // stage 3

        public void OnAudioFrame(Frame frame) { } // stage 3

        public void OnControl(ControlMessage message)
        {
            if (message.Op == ControlOps.Status && message.Message is { Length: > 0 } text)
                owner.Notice?.Invoke(text);
        }
    }
}
