using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;

namespace MoonCam.Infrastructure.Network;

/// <summary>
/// Accepts phone connections and runs one <see cref="ServerSession"/> at a time. A second phone that connects
/// while one is active is turned away, so two phones can never fight over the virtual camera.
///
/// With a certificate the server speaks TLS 1.3 and requires a client certificate (Wi-Fi). The server only
/// proves the transport; deciding whether that certificate is trusted (paired) is the session handler's job.
/// </summary>
public sealed class MoonLinkServer : IAsyncDisposable
{
    public const int DefaultPort = 47321;
    public const int DefaultWiFiPort = 47322;
    private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly Func<ConnectionInfo, ISessionHandler> _handlerFactory;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly X509Certificate2? _certificate;
    private readonly byte[]? _localFingerprint;
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;
    private ServerSession? _active;

    /// <param name="endpoint">USB mode binds 127.0.0.1 only: nothing on the network can reach it.</param>
    /// <param name="tlsCertificate">Wi-Fi mode: the desktop identity. Null means plain TCP (USB tunnel).</param>
    public MoonLinkServer(IPEndPoint endpoint, Func<ConnectionInfo, ISessionHandler> handlerFactory, TimeProvider time,
        SessionOptions? options = null, X509Certificate2? tlsCertificate = null)
    {
        _listener = new TcpListener(endpoint);
        _handlerFactory = handlerFactory;
        _time = time;
        _options = options ?? new SessionOptions();
        _certificate = tlsCertificate;
        _localFingerprint = tlsCertificate is null ? null : Pairing.Fingerprint(tlsCertificate.RawData);
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>The session currently connected, if any.</summary>
    public ServerSession? ActiveSession => Volatile.Read(ref _active);

    public event Action<ServerSession>? SessionStarted;

    public event Action<ServerSession, SessionEnd>? SessionEnded;

    /// <summary>A TLS handshake failed or timed out (wrong client, scanner, network trouble).</summary>
    public event Action<Exception>? HandshakeFailed;

    public void Start()
    {
        _listener.Start(backlog: 4);
        _acceptLoop = AcceptLoopAsync(_stop.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (ActiveSession is not null)
            {
                client.Dispose(); // one phone at a time
                continue;
            }

            client.NoDelay = true; // send small frames immediately (latency over throughput)
            _ = RunSessionAsync(client, cancellationToken);
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var (stream, info) = await SecureAsync(client, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            client.Dispose();
            return;
        }

        var session = new ServerSession(stream, _handlerFactory(info), _time, _options);
        if (Interlocked.CompareExchange(ref _active, session, null) is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false); // another phone won the race
            client.Dispose();
            return;
        }
        try
        {
            SessionStarted?.Invoke(session);
            var end = await session.RunAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _active, null);
            SessionEnded?.Invoke(session, end); // listeners still see a live session object here
        }
        finally
        {
            Volatile.Write(ref _active, null);
            await session.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
        }
    }

    /// <summary>Plain stream for USB; TLS 1.3 with a mandatory client certificate for Wi-Fi.</summary>
    private async Task<(Stream? Stream, ConnectionInfo Info)> SecureAsync(TcpClient client, CancellationToken cancellationToken)
    {
        if (_certificate is null)
            return (client.GetStream(), ConnectionInfo.Usb);

        // Self-signed certificates on both sides: accept any client certificate here, then the session checks
        // its fingerprint against the paired phones. Without a certificate the handshake fails.
        var ssl = new SslStream(client.GetStream(), leaveInnerStreamOpen: false, (_, certificate, _, _) => certificate is not null);
        using var timeout = new CancellationTokenSource(TlsHandshakeTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, linked.Token).ConfigureAwait(false);
            var peer = ssl.RemoteCertificate ?? throw new AuthenticationException("The phone sent no certificate.");
            return (ssl, new ConnectionInfo(Transport.WiFi, Pairing.Fingerprint(peer.GetRawCertData()), _localFingerprint));
        }
        catch (Exception e) when (e is AuthenticationException or IOException or OperationCanceledException)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            HandshakeFailed?.Invoke(e);
            return (null, ConnectionInfo.Usb);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
