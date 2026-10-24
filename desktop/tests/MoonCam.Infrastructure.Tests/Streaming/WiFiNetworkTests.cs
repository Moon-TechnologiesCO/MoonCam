using System.Net.Sockets;
using MoonCam.Core.Media;
using MoonCam.Core.Network;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;
using MoonCam.Infrastructure.Security;
using MoonCam.Infrastructure.Streaming;

namespace MoonCam.Infrastructure.Tests.Streaming;

/// <summary>Wi-Fi listens only on trusted networks, and stops the moment the computer joins another one.</summary>
public sealed class WiFiNetworkTests : IAsyncDisposable
{
    private static readonly NetworkConnection Home = new("home-uuid", "Casa 5G");
    private static readonly NetworkConnection Cafe = new("cafe-uuid", "Café Free Wi-Fi");
    private readonly string _config = Directory.CreateTempSubdirectory("mooncam-networks-").FullName;
    private readonly FakeMonitor _monitor = new();
    private readonly NetworkTrust _networks;
    private readonly StreamController _controller;

    public WiFiNetworkTests()
    {
        _monitor.Set(Home);
        _networks = new NetworkTrust(_monitor, new JsonTrustedNetworkStore(_config), TimeProvider.System);
        var pairing = new PairingCoordinator(new JsonPairedPhoneStore(_config), TimeProvider.System);
        _controller = new StreamController(TimeProvider.System, () => new NullDecoder(), port: 0,
            wifi: new WiFiOptions(DesktopIdentity.LoadOrCreate(_config), pairing, Port: 0, Advertise: false, Networks: _networks));
        _controller.Start();
    }

    [Fact]
    public async Task Wi_Fi_listens_only_after_the_network_is_trusted_and_stops_on_another_network()
    {
        Assert.False(_controller.IsWiFiOn);

        _networks.TrustCurrent();
        await WaitAsync(() => _controller.IsWiFiOn);
        using var phone = new TcpClient();
        await phone.ConnectAsync("127.0.0.1", _controller.WiFiPort!.Value);

        _monitor.Set(Cafe);
        await WaitAsync(() => !_controller.IsWiFiOn);
        Assert.Null(_controller.WiFiPort);
        Assert.True(await HungUpAsync(phone)); // the desktop drops connections the moment the network is not trusted
        Assert.Equal(new TrustedNetwork(Home.Id, Home.Name, default), _networks.Store.All.Single() with { TrustedAt = default });
    }

    [Fact]
    public async Task Forgetting_the_network_closes_pairing_and_turns_Wi_Fi_off()
    {
        _networks.TrustCurrent();
        await WaitAsync(() => _controller.IsWiFiOn);
        _controller.Pairing!.OpenWindow();

        _networks.Store.Remove(Home.Id);

        await WaitAsync(() => !_controller.IsWiFiOn);
        Assert.False(_controller.Pairing.IsWindowOpen);
    }

    private static async Task<bool> HungUpAsync(TcpClient client)
    {
        try
        {
            return await client.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 0;
        }
        catch (IOException)
        {
            return true; // reset instead of closed: hung up all the same
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25);
        Assert.True(condition());
    }

    public async ValueTask DisposeAsync()
    {
        await _controller.DisposeAsync();
        Directory.Delete(_config, recursive: true);
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

    private sealed class NullDecoder : IVideoDecoder
    {
        public void Configure(VideoConfig config) { }
        public IReadOnlyList<VideoFrame> Decode(ReadOnlyMemory<byte> accessUnit, bool isKeyFrame, long timestampUs) => [];
        public void Dispose() { }
    }
}
