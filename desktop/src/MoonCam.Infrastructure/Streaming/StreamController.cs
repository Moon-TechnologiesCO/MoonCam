using System.Net;
using System.Security.Cryptography.X509Certificates;
using MoonCam.Core.Diagnostics;
using MoonCam.Core.Media;
using MoonCam.Core.Network;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;
using MoonCam.Infrastructure.Adb;
using MoonCam.Infrastructure.Decoding;
using MoonCam.Infrastructure.Discovery;
using MoonCam.Infrastructure.Network;

namespace MoonCam.Infrastructure.Streaming;

/// <summary>Live numbers for the status bar.</summary>
public sealed record StreamStats(int Width, int Height, double FramesPerSecond, long FramesDropped, TimeSpan? RoundTrip);

/// <summary>
/// Optional Wi-Fi setup: the desktop's TLS identity, the phones it trusts and, with <paramref name="Networks"/>,
/// the networks it may use Wi-Fi on (null: any network, for tests and tools).
/// </summary>
public sealed record WiFiOptions(X509Certificate2 Identity, PairingCoordinator Pairing, int Port = MoonLinkServer.DefaultWiFiPort,
    bool Advertise = true, NetworkTrust? Networks = null);

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
    private readonly SemaphoreSlim _wifiGate = new(1, 1);
    private readonly Lock _demandLock = new();
    private volatile bool _previewShown = true;
    private bool _videoRequested;
    private ITimer? _pauseTimer;
    private bool _disposed;
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

    /// <summary>True while the Wi-Fi server is listening (only on trusted networks when those are configured).</summary>
    public bool IsWiFiOn => _wifiServer is not null;

    /// <summary>Which networks Wi-Fi may be used on; null when Wi-Fi is off or not limited by network.</summary>
    public NetworkTrust? Networks => _wifi?.Networks;

    /// <summary>Raised when Wi-Fi turns on or off because the network changed or was trusted or forgotten.</summary>
    public event Action? WiFiChanged;

    /// <summary>Wi-Fi pairing: open the window, approve or reject codes, list and remove phones.</summary>
    public PairingCoordinator? Pairing => _wifi?.Pairing;

    public event Action<ConnectionState>? StateChanged;

    public event Action<CapabilitiesMessage>? CapabilitiesChanged;

    /// <summary>Human-readable problems and phone status messages for the UI.</summary>
    public event Action<string>? Notice;

    /// <summary>How long video keeps flowing after the last viewer leaves, so a quick minimise does not restart the camera.</summary>
    public static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(4);

    /// <summary>A phone is connected but its camera is off because nobody is watching (saves both devices' power).</summary>
    public bool IsVideoPaused
    {
        get
        {
            lock (_demandLock)
                return _session?.Handler is Handler { MediaAllowed: true } && !_videoRequested;
        }
    }

    /// <summary>Raised when video is paused or resumed because viewers came or went.</summary>
    public event Action? VideoDemandChanged;

    /// <summary>
    /// Video is wanted only while someone can see it: the preview is on screen, or another app has the virtual
    /// camera open. Otherwise the phone turns its camera off and nothing is decoded.
    /// </summary>
    private bool IsVideoWanted => _previewShown || _virtualCamera?.HasReaders == true;

    /// <summary>The window tells whether the preview is on screen (false while minimised).</summary>
    public void SetPreviewShown(bool shown)
    {
        _previewShown = shown;
        UpdateVideoDemand();
    }

    /// <summary>Starts the phone's camera as soon as someone watches; stops it a few seconds after the last viewer leaves.</summary>
    private void UpdateVideoDemand()
    {
        ControlMessage? command = null;
        lock (_demandLock)
        {
            if (_session?.Handler is not Handler { MediaAllowed: true })
                return;
            if (IsVideoWanted)
            {
                _pauseTimer?.Dispose();
                _pauseTimer = null;
                if (!_videoRequested)
                {
                    _videoRequested = true;
                    command = _settings.ToStartCommand();
                }
            }
            else if (_videoRequested && _pauseTimer is null)
            {
                _pauseTimer = _time.CreateTimer(_ => PauseVideo(), null, PauseGrace, Timeout.InfiniteTimeSpan);
            }
        }
        if (command is null)
            return;
        _log.Info("Someone is watching: starting the phone's camera");
        _ = SendAsync(command);
        VideoDemandChanged?.Invoke();
    }

    private void PauseVideo()
    {
        lock (_demandLock)
        {
            _pauseTimer?.Dispose();
            _pauseTimer = null;
            if (IsVideoWanted || !_videoRequested || _session?.Handler is not Handler { MediaAllowed: true })
                return;
            _videoRequested = false;
        }
        _log.Info("Nobody is watching: pausing the phone's camera");
        _ = SendAsync(new ControlMessage(ControlOps.Stop));
        _state.TryMoveTo(ConnectionState.Connected);
        VideoDemandChanged?.Invoke();
    }

    private void ResetVideoDemand()
    {
        lock (_demandLock)
        {
            _pauseTimer?.Dispose();
            _pauseTimer = null;
            _videoRequested = false;
        }
    }

    /// <summary>Ends the connection from this computer. The phone stops and does not reconnect on its own.</summary>
    public async Task DisconnectAsync(string reason = "disconnected")
    {
        if (_session is not { } session)
            return;
        _log.Info($"Disconnecting the phone ({reason})");
        await session.SayGoodbyeAsync(reason).ConfigureAwait(false);
        session.End();
    }

    /// <summary>Stops trusting a phone. If it is connected now, it is told to forget this computer and disconnected.</summary>
    public async Task UnpairAsync(string fingerprintHex)
    {
        Pairing?.Store.Remove(fingerprintHex);
        if (_session is { Handler: Handler { Info.PeerFingerprint: { } peer } } session && Convert.ToHexStringLower(peer) == fingerprintHex)
        {
            await SendAsync(session, new ControlMessage(ControlOps.Unpair)).ConfigureAwait(false);
            await DisconnectAsync("unpaired").ConfigureAwait(false);
        }
    }

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

        try
        {
            _usbServer = StartServer(new IPEndPoint(IPAddress.Loopback, _port), certificate: null);
        }
        catch (System.Net.Sockets.SocketException e)
        {
            ReportProblem($"USB connections are unavailable: another program is using port {_port}", e);
        }
        if (IsWiFiWanted)
        {
            try
            {
                StartWiFi();
            }
            catch (System.Net.Sockets.SocketException e)
            {
                _wifiServer = null;
                ReportProblem("Wi-Fi is unavailable: another program is using its port", e);
            }
        }
        if (_wifi?.Networks is { } networks)
            networks.Changed += OnNetworksChanged;
        _state.TryMoveTo(ConnectionState.Listening);
    }

    private bool IsWiFiWanted => !_disposed && _wifi is not null && (_wifi.Networks?.IsWiFiAllowed ?? true);

    private void OnNetworksChanged() => _ = UpdateWiFiAsync();

    /// <summary>Starts or stops Wi-Fi to match the network. Calls are serialised; the last state wins.</summary>
    private async Task UpdateWiFiAsync()
    {
        bool changed;
        await _wifiGate.WaitAsync().ConfigureAwait(false);
        try
        {
            changed = IsWiFiWanted != IsWiFiOn;
            if (IsWiFiWanted && !IsWiFiOn)
                StartWiFi();
            else if (!IsWiFiWanted && IsWiFiOn)
                await StopWiFiAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException)
        {
            ReportProblem("Wi-Fi could not start", e);
            return;
        }
        finally
        {
            _wifiGate.Release();
        }
        if (changed)
            WiFiChanged?.Invoke();
    }

    private void StartWiFi()
    {
        var wifi = _wifi!;
        _wifiServer = StartServer(new IPEndPoint(IPAddress.Any, wifi.Port), wifi.Identity);
        _wifiServer.HandshakeFailed += e => _log.Warn("Wi-Fi TLS handshake failed", e);
        if (wifi.Advertise)
        {
            _advertiser = new AvahiAdvertiser(_log);
            _advertiser.Start($"MoonCam on {Environment.MachineName}", _wifiServer.LocalEndpoint.Port,
                Convert.ToHexStringLower(Core.Protocol.Pairing.Fingerprint(wifi.Identity.RawData)));
        }
    }

    /// <summary>Stops announcing, closes pairing and ends any Wi-Fi session: this network is not trusted.</summary>
    private async Task StopWiFiAsync()
    {
        _log.Info("Wi-Fi off: this computer is on a network that is not trusted");
        _advertiser?.Dispose();
        _advertiser = null;
        _wifi!.Pairing.CloseWindow();
        var server = _wifiServer!;
        _wifiServer = null;
        await server.DisposeAsync().ConfigureAwait(false);
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
        bool streaming;
        lock (_demandLock)
            streaming = _videoRequested && _session?.Handler is Handler { MediaAllowed: true };
        if (streaming) // a paused camera picks the new settings up when it resumes
            await SendAsync(settings.ToStartCommand()).ConfigureAwait(false);
    }

    /// <summary>Starts writing to a v4l2loopback (or other) virtual camera.</summary>
    public void EnableVirtualCamera(IVirtualCamera camera)
    {
        DisableVirtualCamera();
        _virtualCamera = camera;
        camera.ReadersChanged += OnVirtualCameraReadersChanged;
        AddSink(camera);
        _log.Info($"Virtual camera on: {camera.DevicePath}");
        UpdateVideoDemand();
    }

    private void OnVirtualCameraReadersChanged()
    {
        _log.Info(_virtualCamera?.HasReaders == true ? "An app opened the MoonCam camera" : "No app is using the MoonCam camera");
        UpdateVideoDemand();
    }

    public void DisableVirtualCamera()
    {
        if (_virtualCamera is null)
            return;
        _virtualCamera.ReadersChanged -= OnVirtualCameraReadersChanged;
        _pipeline?.RemoveSink(_virtualCamera);
        lock (_lock)
            _sinks.Remove(_virtualCamera);
        _virtualCamera.Dispose();
        _virtualCamera = null;
        UpdateVideoDemand();
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
        ((Handler)session.Handler).AbandonPairing();
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session))
        {
            // Not the active phone (e.g. a second phone that was turned away): nothing else changes.
            if (end.Reason == SessionEndReason.Rejected && _session is null)
                _state.TryMoveTo(ConnectionState.Listening);
            return;
        }
        _videoConfig = null;
        ResetVideoDemand();
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
        _disposed = true;
        if (_wifi?.Networks is { } networks)
            networks.Changed -= OnNetworksChanged;
        if (_session is { } session)
            await session.SayGoodbyeAsync("desktop closing").ConfigureAwait(false);
        if (_usbServer is not null)
            await _usbServer.DisposeAsync().ConfigureAwait(false);
        await _wifiGate.WaitAsync().ConfigureAwait(false); // let a network change in flight finish first
        try
        {
            _advertiser?.Dispose();
            if (_wifiServer is not null)
                await _wifiServer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _wifiGate.Release();
        }
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
            owner.ResetVideoDemand();
            owner.UpdateVideoDemand(); // the camera starts only if someone is watching
            owner.VideoDemandChanged?.Invoke();
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
            if (owner.State == ConnectionState.Connected && !owner.IsVideoPaused) // late frames after a pause do not count
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
                case ControlOps.Unpair when MediaAllowed && Info is { Transport: Transport.WiFi, PeerFingerprint: { } peer }:
                    owner._log.Info("The phone unpaired itself from this computer");
                    owner.Pairing?.Store.Remove(Convert.ToHexStringLower(peer));
                    owner.Notice?.Invoke("The phone was unpaired. Pair it again to use Wi-Fi.");
                    _ = owner.DisconnectAsync("unpaired");
                    break;
            }
        }

        /// <summary>The phone left before pairing finished: free the slot so a new attempt can start at once.</summary>
        public void AbandonPairing()
        {
            if (_pairing is not { IsFinished: false } pairing)
                return;
            pairing.StateChanged -= OnPairingChanged;
            pairing.Fail("The phone left before pairing finished.");
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
