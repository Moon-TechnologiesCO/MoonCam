namespace MoonCam.Core.Network;

/// <summary>A network the computer is on, as the system identifies it: a saved Wi-Fi network or wired profile.</summary>
public sealed record NetworkConnection(string Id, string Name);

/// <summary>Reports the networks the computer is connected to right now.</summary>
public interface INetworkMonitor
{
    IReadOnlyList<NetworkConnection> Current { get; }

    event Action? Changed;
}

/// <summary>A network the person said is theirs (home, office). MoonCam uses Wi-Fi only on these.</summary>
public sealed record TrustedNetwork(string Id, string Name, DateTimeOffset TrustedAt);

/// <summary>Where trusted networks are remembered. Removing one turns Wi-Fi off there immediately.</summary>
public interface ITrustedNetworkStore
{
    IReadOnlyList<TrustedNetwork> All { get; }

    bool IsTrusted(string id);

    void Add(TrustedNetwork network);

    void Remove(string id);

    event Action? Changed;
}

/// <summary>
/// Decides whether MoonCam may use Wi-Fi: only while every network the computer is on is one the person trusts.
/// On a café or airport network the desktop neither announces itself nor accepts connections. USB is never
/// affected.
/// </summary>
public sealed class NetworkTrust
{
    private readonly INetworkMonitor _monitor;
    private readonly TimeProvider _time;

    public NetworkTrust(INetworkMonitor monitor, ITrustedNetworkStore store, TimeProvider time)
    {
        _monitor = monitor;
        _time = time;
        Store = store;
        monitor.Changed += () => Changed?.Invoke();
        store.Changed += () => Changed?.Invoke();
    }

    public ITrustedNetworkStore Store { get; }

    public IReadOnlyList<NetworkConnection> Current => _monitor.Current;

    /// <summary>Raised when the computer changes network or the person trusts or forgets one.</summary>
    public event Action? Changed;

    public bool IsWiFiAllowed
    {
        get
        {
            var current = _monitor.Current;
            return current.Count > 0 && current.All(n => Store.IsTrusted(n.Id));
        }
    }

    /// <summary>The networks the computer is on that are not trusted yet: what the trust prompt asks about.</summary>
    public IReadOnlyList<NetworkConnection> Untrusted => [.. _monitor.Current.Where(n => !Store.IsTrusted(n.Id))];

    /// <summary>The person confirmed that the networks the computer is on now are theirs.</summary>
    public void TrustCurrent()
    {
        foreach (var network in Untrusted)
            Store.Add(new TrustedNetwork(network.Id, network.Name, _time.GetUtcNow()));
    }
}
