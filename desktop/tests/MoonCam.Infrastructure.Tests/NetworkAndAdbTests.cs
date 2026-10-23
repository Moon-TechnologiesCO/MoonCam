using System.Net;
using System.Net.Sockets;
using MoonCam.Core.Protocol;
using MoonCam.Core.Session;
using MoonCam.Infrastructure.Adb;
using MoonCam.Infrastructure.Network;

namespace MoonCam.Infrastructure.Tests;

public class NetworkAndAdbTests
{
    [Fact]
    public void Parses_adb_device_list()
    {
        const string output = """
            List of devices attached
            R5CT20ABCDE            device usb:1-2 product:a54xnsxx model:SM_A546E device:a54x transport_id:3
            emulator-5554          unauthorized
            0123456789             offline
            * daemon started successfully

            """;

        var devices = AdbOutput.ParseDevices(output);

        Assert.Equal(3, devices.Count);
        Assert.Equal(new AdbDevice("R5CT20ABCDE", AdbDeviceState.Device, "SM A546E"), devices[0]);
        Assert.Equal(AdbDeviceState.Unauthorized, devices[1].State);
        Assert.False(devices[2].IsReady);
    }

    [Fact]
    public async Task Server_runs_one_session_and_turns_a_second_phone_away()
    {
        var started = new TaskCompletionSource<ServerSession>();
        await using var server = new MoonLinkServer(new IPEndPoint(IPAddress.Loopback, 0), () => new AcceptAll(), TimeProvider.System);
        server.SessionStarted += s => started.TrySetResult(s);
        server.Start();

        using var first = new TcpClient();
        await first.ConnectAsync(server.LocalEndpoint);
        var reader = new FrameReader(first.GetStream());
        using var writer = new FrameWriter(first.GetStream());
        await writer.WritePreambleAsync();
        await writer.WriteAsync(Messages.Hello(new HelloMessage("Pixel", "Google", "Pixel", "16", "0.1.0", "t")));
        Assert.Equal(1, await reader.ReadPreambleAsync());
        Assert.True(Messages.ReadWelcome((await reader.ReadAsync())!).Accepted);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        using var second = new TcpClient();
        await second.ConnectAsync(server.LocalEndpoint);
        var buffer = new byte[16];
        var read = await second.GetStream().ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, read); // closed without a word
    }

    private sealed class AcceptAll : ISessionHandler
    {
        public WelcomeMessage Authenticate(HelloMessage hello) => new(true, "s", "test");
        public void OnCapabilities(CapabilitiesMessage capabilities) { }
        public void OnVideoConfig(VideoConfig config) { }
        public void OnVideoFrame(Frame frame) { }
        public void OnAudioConfig(AudioConfig config) { }
        public void OnAudioFrame(Frame frame) { }
        public void OnControl(ControlMessage message) { }
    }
}
