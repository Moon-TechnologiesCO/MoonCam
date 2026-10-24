using System.Net;
using System.Security.Cryptography.X509Certificates;
using MoonCam.Core.Diagnostics;
using MoonCam.Core.Media;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;
using MoonCam.Infrastructure.Adb;
using MoonCam.Infrastructure.Decoding;
using MoonCam.Infrastructure.Discovery;
using MoonCam.Infrastructure.Network;

namespace MoonCam.Infrastructure.Streaming;

/// <summary>Live numbers for the status bar.</summary>
public sealed record StreamStats(int Width, int Height, double FramesPerSecond, long FramesDropped, TimeSpan? RoundTrip);

/// <summary>Optional Wi-Fi setup: the desktop's TLS identity and the phones it trusts.</summary>
public sealed record WiFiOptions(X509Certificate2 Identity, PairingCoordinator Pairing, int Port = MoonLinkServer.DefaultWiFiPort, bool Advertise = true);

/// <summary>
/// The application's single entry point to streaming: it owns the MoonLink servers (USB tunnel and Wi-Fi),
/// authorises phones, runs Wi-Fi pairing, feeds the decoder and the sinks, and drives the connection state.
/// Only one phone is active at a time, whichever way it came in. The UI only talks to this class.
/// </summary>
public sealed class StreamController : IAsyncDisposable
{
    private readonly ConnectionStateMachine _state = new();
    private readonly TimeProvider _time;
    private readonly int _port;
    private readonly Func<IVideoDecoder> _decoderFactory;
    private readonly AdbClient _adb;
    private readonly IEventLog _log;
    private readonly WiFiOptions? _wifi;
    private readonly List<IVideoSink> _sinks = [];
    private readonly Lock _lock = new();
    private MoonLinkServer? _usbServer;
    private MoonLinkServer? _wifiServer;
    private AvahiAdvertiser? _advertiser;
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
    /// <param name="wifi">Enables the Wi-Fi server; null keeps MoonCam USB-only.</param>
    public StreamController(TimeProvider time, Func<IVideoDecoder> decoderFactory, AdbClient? adb = null, int port = MoonLinkServer.DefaultPort,
        string? fixedToken = null, IEventLog? log = null, WiFiOptions? wifi = null)
    {
        _log = log ?? NullEventLog.Instance;
        _time = time;
        _decoderFactory = decoderFactory;
        _adb = adb ?? new AdbClient();
        _port = port;
        _wifi = wifi;
        if (fixedToken is not null)
            _token = fixedToken;
        _state.Changed += (_, now) => StateChanged?.Invoke(now);
    }

    public ConnectionState State => _state.State;

    /// <summary>The token a phone must present over USB. Exposed for the FakePhone tool and tests.</summary>
    public string Token => _token;

    public HelloMessage? Phone => _session?.Phone;

    /// <summary>How the active phone is connected, if any.</summary>
    public Transport? CurrentTransport => (_session?.Handler as Handler)?.Info.Transport;

    public CapabilitiesMessage? Capabilities { get; private set; }

    public StreamSettings Settings => _settings;

    public string? VirtualCameraPath => _virtualCamera?.DevicePath;

    public int Port => _usbServer?.LocalEndpoint.Port ?? _port;

    public int? WiFiPort => _wifiServer?.LocalEndpoint.Port;

    /// <summary>Wi-Fi pairing: open the window, approve or reject codes, list and remove phones.</summary>
    public PairingCoordinator? Pairing => _wifi?.Pairing;

    public event Action<ConnectionState>? StateChanged;

    public event Action<CapabilitiesMessage>? CapabilitiesChanged;

    /// <summary>Human-readable problems and phone status messages for the UI.</summary>
    public event Action<string>? Notice;

    /// <summary>Starts listening: 127.0.0.1 for the USB tunnel and, if configured, the LAN for Wi-Fi.</summary>
    public void Start()
    {
        _pipeline = new VideoPipeline(_decoderFactory());
        _pipeline.KeyFrameNeeded += () => _ = SendAsync(new ControlMessage(ControlOps.KeyFrame));
        _pipeline.DecodeFailed += e => ReportProblem("Video decoding problem", e);
        _pipeline.SinkFailed += (sink, e) => ReportProblem(
            sink is IVirtualCamera ? "The virtual camera could not be updated; the preview continues" : "Preview problem", e);
        foreach (var sink in _sinks)
            _pipeline.AddSink(sink);

        _usbServer = StartServer(new IPEndPoint(IPAddress.Loopback, _port), certificate: null);
        if (_wifi is not null)
        {
            _wifiServer = StartServer(new IPEndPoint(IPAddress.Any, _wifi.Port), _wifi.Identity);
            _wifiServer.HandshakeFailed += e => _log.Warn("Wi-Fi TLS handshake failed", e);
            if (_wifi.Advertise)
            {
                _advertiser = new AvahiAdvertiser(_log);
                _advertiser.Start($"MoonCam on {Environment.MachineName}", _wifiServer.LocalEndpoint.Port,
                    Convert.ToHexStringLower(Core.Protocol.Pairing.Fingerprint(_wifi.Identity.RawData)));
            }
        }
        _state.TryMoveTo(ConnectionState.Listening);
    }

    private MoonLinkServer StartServer(IPEndPoint endpoint, X509Certificate2? certificate)
    {
        var server = new MoonLinkServer(endpoint, info => new Handler(this, info), _time, tlsCertificate: certificate);
        server.SessionStarted += session =>
        {
            ((Handler)session.Handler).Session = session;
            if (_session is null)
                _state.TryMoveTo(ConnectionState.Handshaking);
        };
        server.SessionEnded += OnSessionEnded;
        server.Start();
        _log.Info($"Listening on {server.LocalEndpoint} ({(certificate is null ? "USB tunnel" : "Wi-Fi, TLS 1.3")})");
        return server;
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
            return "adb is not installed. Reinstall MoonCam to restore it.";

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
        if (_session?.Handler is Handler { MediaAllowed: true })
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

    private Task SendAsync(ControlMessage message) => SendAsync(_session, message);

    private static async Task SendAsync(ServerSession? session, ControlMessage message)
    {
        try
        {
            if (session is not null)
                await session.SendControlAsync(message).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // The session is ending; the state machine already reflects it.
        }
    }

    private void OnSessionEnded(ServerSession session, SessionEnd end)
    {
        _log.Info($"Session ended: {end.Reason}{(end.Detail is null ? "" : $" ({end.Detail})")}");
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session))
        {
            // Not the active phone (e.g. a second phone that was turned away): nothing else changes.
            if (end.Reason == SessionEndReason.Rejected && _session is null)
                _state.TryMoveTo(ConnectionState.Listening);
            return;
        }
        _videoConfig = null;
        var next = end.Reason is SessionEndReason.TimedOut or SessionEndReason.ProtocolError
            ? ConnectionState.Reconnecting
            : ConnectionState.Listening;
        if (!_state.TryMoveTo(next))
            _state.TryMoveTo(ConnectionState.Listening);
        if (end.Detail is { Length: > 0 } detail && end.Reason != SessionEndReason.PeerClosed)
            Notice?.Invoke($"Connection lost: {detail}");
    }

    public async ValueTask DisposeAsync()
    {
        if (_session is { } session)
            await session.SayGoodbyeAsync("desktop closing").ConfigureAwait(false);
        _advertiser?.Dispose();
        if (_usbServer is not null)
            await _usbServer.DisposeAsync().ConfigureAwait(false);
        if (_wifiServer is not null)
            await _wifiServer.DisposeAsync().ConfigureAwait(false);
        if (_pipeline is not null)
            await _pipeline.DisposeAsync().ConfigureAwait(false);
        DisableVirtualCamera();
        _state.TryMoveTo(ConnectionState.Stopped);
    }

    /// <summary>
    /// One per connection. It authorises the phone (token over USB, pinned certificate over Wi-Fi), runs the
    /// pairing exchange when an unpaired phone is allowed to pair, and only then lets media through.
    /// </summary>
    private sealed class Handler(StreamController owner, ConnectionInfo info) : ISessionHandler
    {
        private PairingExchange? _pairing;

        public ConnectionInfo Info { get; } = info;

        public ServerSession? Session { get; set; }

        /// <summary>False while pairing: a phone that is not trusted yet cannot stream or receive commands.</summary>
        public bool MediaAllowed { get; private set; }

        public WelcomeMessage Authenticate(HelloMessage hello)
        {
            var server = Environment.MachineName;
            var phone = $"{hello.Manufacturer} {hello.Model}";
            if (owner._session is { } active && !ReferenceEquals(active, Session))
                return Refuse(phone, "Another phone is already connected to this computer.");

            if (Info.Transport == Transport.Usb)
            {
                if (!SessionToken.Matches(owner._token, hello.Token))
                    return Refuse(phone, "This phone was not invited by this computer.");
                MediaAllowed = true;
            }
            else if (owner._wifi!.Pairing.Store.IsPaired(Info.PeerFingerprint))
            {
                MediaAllowed = true;
            }
            else if (hello.Pairing == true)
            {
                _pairing = owner._wifi.Pairing.TryBegin(hello.DeviceName, Info.PeerFingerprint!, Info.LocalFingerprint!, out var refusal);
                if (_pairing is null)
                    return Refuse(phone, refusal!);
                _pairing.StateChanged += OnPairingChanged;
                owner._log.Info($"Pairing with {phone} over Wi-Fi");
            }
            else
            {
                return Refuse(phone, "This phone is not paired with this computer. Pair it first.");
            }

            owner._session = Session;
            owner._state.TryMoveTo(ConnectionState.Connected);
            owner._log.Info($"Phone connected over {Info.Transport}: {phone}, Android {hello.AndroidVersion}, app {hello.AppVersion}");
            return new WelcomeMessage(true, Guid.NewGuid().ToString("N"), server, Pairing: _pairing is null ? null : true);
        }

        private WelcomeMessage Refuse(string phone, string reason)
        {
            owner._log.Warn($"Refused {phone} over {Info.Transport}: {reason}");
            return new WelcomeMessage(false, null, Environment.MachineName, reason);
        }

        public void OnCapabilities(CapabilitiesMessage capabilities)
        {
            if (!MediaAllowed)
                return;
            owner.Capabilities = capabilities;
            owner.CapabilitiesChanged?.Invoke(capabilities);
            owner._log.Info($"Cameras: {string.Join(", ", capabilities.Cameras.Select(c => $"{c.Facing}/{c.Lens}#{c.PhysicalId ?? c.Id}"))}");
            _ = SendAsync(Session, owner._settings.ToStartCommand());
        }

        public void OnVideoConfig(VideoConfig config)
        {
            if (!MediaAllowed)
                return;
            owner._log.Info($"Video: {config.Codec} {config.Width}x{config.Height} @ {config.Fps} fps");
            owner._videoConfig = config;
            owner._pipeline?.Configure(config);
        }

        public void OnVideoFrame(Frame frame)
        {
            if (!MediaAllowed)
                return;
            owner._pipeline?.Submit(frame);
            if (owner.State == ConnectionState.Connected)
                owner._state.TryMoveTo(ConnectionState.Streaming);
        }

        public void OnAudioConfig(AudioConfig config) { } // stage 3

        public void OnAudioFrame(Frame frame) { } // stage 3

        public void OnControl(ControlMessage message)
        {
            switch (message.Op)
            {
                case ControlOps.PairCommit when _pairing is not null:
                    var desktopNonce = _pairing.OnCommit(Decode(message.Data));
                    _ = SendAsync(Session, new ControlMessage(ControlOps.PairNonce, Data: Convert.ToBase64String(desktopNonce)));
                    break;
                case ControlOps.PairReveal when _pairing is not null:
                    _pairing.OnReveal(Decode(message.Data));
                    break;
                case ControlOps.PairConfirm when _pairing is not null:
                    _pairing.ConfirmOnPhone();
                    break;
                case ControlOps.Status when message.Message is { Length: > 0 } text:
                    owner.Notice?.Invoke(text);
                    break;
            }
        }

        /// <summary>Both sides confirmed: trust the phone and let media through. A rejection ends the session.</summary>
        private void OnPairingChanged(PairingExchange exchange)
        {
            if (exchange.IsComplete && !MediaAllowed)
            {
                MediaAllowed = true;
                owner._log.Info($"Paired with {exchange.PhoneName}");
                _ = SendAsync(Session, new ControlMessage(ControlOps.Paired, Message: Environment.MachineName));
            }
            else if (exchange.IsFailed && Session is { } session)
            {
                owner._log.Warn($"Pairing with {exchange.PhoneName} was cancelled");
                _ = session.SayGoodbyeAsync("pairing cancelled").AsTask();
            }
        }

        private static byte[] Decode(string? base64)
        {
            try
            {
                return Convert.FromBase64String(base64 ?? "");
            }
            catch (FormatException e)
            {
                throw new ProtocolException("Pairing data is not valid base64.", e);
            }
        }
    }
}
