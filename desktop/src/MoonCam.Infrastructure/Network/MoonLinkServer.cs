using System.Net;
using System.Net.Sockets;
using MoonCam.Core.Session;

namespace MoonCam.Infrastructure.Network;

/// <summary>
/// Accepts phone connections and runs one <see cref="ServerSession"/> at a time. A second phone that
/// connects while one is active is turned away, so two phones can never fight over the virtual camera.
/// </summary>
public sealed class MoonLinkServer : IAsyncDisposable
{
    public const int DefaultPort = 47321;

    private readonly TcpListener _listener;
    private readonly Func<ISessionHandler> _handlerFactory;
    private readonly TimeProvider _time;
    private readonly SessionOptions _options;
    private readonly CancellationTokenSource _stop = new();
    private Task? _acceptLoop;
    private ServerSession? _active;

    /// <param name="endpoint">USB mode binds 127.0.0.1 only: nothing on the network can reach it.</param>
    public MoonLinkServer(IPEndPoint endpoint, Func<ISessionHandler> handlerFactory, TimeProvider time, SessionOptions? options = null)
    {
        _listener = new TcpListener(endpoint);
        _handlerFactory = handlerFactory;
        _time = time;
        _options = options ?? new SessionOptions();
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)_listener.LocalEndpoint;

    /// <summary>The session currently connected, if any.</summary>
    public ServerSession? ActiveSession => Volatile.Read(ref _active);

    public event Action<ServerSession>? SessionStarted;

    public event Action<ServerSession, SessionEnd>? SessionEnded;

    public void Start()
    {
        _listener.Start(backlog: 2);
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
        var session = new ServerSession(client.GetStream(), _handlerFactory(), _time, _options);
        try
        {
            Volatile.Write(ref _active, session);
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

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_acceptLoop is not null)
            await _acceptLoop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
