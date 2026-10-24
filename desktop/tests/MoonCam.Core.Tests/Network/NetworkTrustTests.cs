using Microsoft.Extensions.Time.Testing;
using MoonCam.Core.Network;

namespace MoonCam.Core.Tests.Network;

public class NetworkTrustTests
{
    private static readonly NetworkConnection Home = new("home-uuid", "Casa 5G");
    private static readonly NetworkConnection Cafe = new("cafe-uuid", "Café Free Wi-Fi");
    private readonly FakeMonitor _monitor = new();
    private readonly MemoryStore _store = new();
    private readonly NetworkTrust _trust;

    public NetworkTrustTests() => _trust = new NetworkTrust(_monitor, _store, new FakeTimeProvider());

    [Fact]
    public void Wi_Fi_stays_off_on_a_network_nobody_trusted()
    {
        _monitor.Set(Home);

        Assert.False(_trust.IsWiFiAllowed);
        Assert.Equal([Home], _trust.Untrusted);
    }

    [Fact]
    public void Trusting_the_current_network_turns_Wi_Fi_on_there_and_only_there()
    {
        _monitor.Set(Home);
        _trust.TrustCurrent();
        Assert.True(_trust.IsWiFiAllowed);

        _monitor.Set(Cafe);
        Assert.False(_trust.IsWiFiAllowed);

        _monitor.Set(Home);
        Assert.True(_trust.IsWiFiAllowed);
    }

    [Fact]
    public void One_untrusted_network_alongside_a_trusted_one_keeps_Wi_Fi_off()
    {
        _store.Add(new TrustedNetwork(Home.Id, Home.Name, DateTimeOffset.UnixEpoch));
        _monitor.Set(Home, Cafe); // e.g. wired at home and joined to a hotspot

        Assert.False(_trust.IsWiFiAllowed);
        Assert.Equal([Cafe], _trust.Untrusted);
    }

    [Fact]
    public void Without_any_network_Wi_Fi_is_off()
    {
        _monitor.Set();

        Assert.False(_trust.IsWiFiAllowed);
    }

    [Fact]
    public void Forgetting_a_network_turns_Wi_Fi_off_and_tells_listeners()
    {
        _monitor.Set(Home);
        _trust.TrustCurrent();
        var changes = 0;
        _trust.Changed += () => changes++;

        _store.Remove(Home.Id);

        Assert.False(_trust.IsWiFiAllowed);
        Assert.Equal(1, changes);
    }

    private sealed class FakeMonitor : INetworkMonitor
    {
        public IReadOnlyList<NetworkConnection> Current { get; private set; } = [];
        public event Action? Changed;
        public void Set(params NetworkConnection[] networks)
        {
            Current = networks;
            Changed?.Invoke();
        }
    }

    private sealed class MemoryStore : ITrustedNetworkStore
    {
        private readonly List<TrustedNetwork> _networks = [];
        public IReadOnlyList<TrustedNetwork> All => _networks;
        public bool IsTrusted(string id) => _networks.Any(n => n.Id == id);
        public void Add(TrustedNetwork network) { _networks.Add(network); Changed?.Invoke(); }
        public void Remove(string id) { _networks.RemoveAll(n => n.Id == id); Changed?.Invoke(); }
        public event Action? Changed;
    }
}
