using MoonCam.Core.Network;
using MoonCam.Platform.Linux.Network;

namespace MoonCam.Platform.Linux.Tests;

public class NetworkManagerMonitorTests
{
    [Fact]
    public void Keeps_real_networks_and_ignores_loopback_containers_and_VPNs()
    {
        const string output = """
            595c0c38-5830-4a20-99a4-7fac31411b41:802-11-wireless:TheWorldSystem5G
            2344c33f-7022-403d-b5f7-5ef6473ebc93:loopback:lo
            1b2c3d4e-0000-4000-8000-000000000001:bridge:docker0
            1b2c3d4e-0000-4000-8000-000000000002:wireguard:Office VPN
            1b2c3d4e-0000-4000-8000-000000000003:802-3-ethernet:Wired connection 1

            """;

        var networks = NetworkManagerMonitor.Parse(output);

        Assert.Equal(
            [
                new NetworkConnection("595c0c38-5830-4a20-99a4-7fac31411b41", "TheWorldSystem5G"),
                new NetworkConnection("1b2c3d4e-0000-4000-8000-000000000003", "Wired connection 1"),
            ],
            networks);
    }

    [Fact]
    public void Unescapes_colons_and_backslashes_in_network_names()
    {
        var networks = NetworkManagerMonitor.Parse(@"aa-bb:802-11-wireless:Casa\:Piso 2 \\ Norte");

        Assert.Equal("Casa:Piso 2 \\ Norte", Assert.Single(networks).Name);
    }
}
