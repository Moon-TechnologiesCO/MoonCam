namespace MoonCam.Core.Session;

/// <summary>What the desktop is doing with the phone connection, as shown in the UI.</summary>
public enum ConnectionState
{
    /// <summary>The server is not running.</summary>
    Stopped,
    /// <summary>Waiting for a phone to connect.</summary>
    Listening,
    /// <summary>A phone connected; checking who it is.</summary>
    Handshaking,
    /// <summary>The phone is authorised; no video yet.</summary>
    Connected,
    /// <summary>Video is flowing.</summary>
    Streaming,
    /// <summary>The link dropped unexpectedly; waiting for the same phone to come back.</summary>
    Reconnecting,
}

/// <summary>
/// Guards the connection state: only the transitions below are legal. Illegal ones return false instead of
/// throwing, because they come from network events that can legitimately arrive late or out of order.
/// </summary>
public sealed class ConnectionStateMachine
{
    private static readonly Dictionary<ConnectionState, ConnectionState[]> Allowed = new()
    {
        [ConnectionState.Stopped] = [ConnectionState.Listening],
        [ConnectionState.Listening] = [ConnectionState.Handshaking, ConnectionState.Stopped],
        [ConnectionState.Handshaking] = [ConnectionState.Connected, ConnectionState.Listening, ConnectionState.Reconnecting, ConnectionState.Stopped],
        [ConnectionState.Connected] = [ConnectionState.Streaming, ConnectionState.Listening, ConnectionState.Reconnecting, ConnectionState.Stopped],
        [ConnectionState.Streaming] = [ConnectionState.Connected, ConnectionState.Listening, ConnectionState.Reconnecting, ConnectionState.Stopped],
        [ConnectionState.Reconnecting] = [ConnectionState.Handshaking, ConnectionState.Listening, ConnectionState.Stopped],
    };

    private readonly Lock _lock = new();

    public ConnectionState State { get; private set; } = ConnectionState.Stopped;

    /// <summary>Raised after every successful transition with (previous, current).</summary>
    public event Action<ConnectionState, ConnectionState>? Changed;

    public bool CanMoveTo(ConnectionState next) => Allowed[State].Contains(next);

    public bool TryMoveTo(ConnectionState next)
    {
        ConnectionState previous;
        lock (_lock)
        {
            if (!Allowed[State].Contains(next))
                return false;
            previous = State;
            State = next;
        }
        Changed?.Invoke(previous, next);
        return true;
    }
}
