using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MoonCam.App.ViewModels;
using MoonCam.App.Views;
using MoonCam.Core.Media;
using MoonCam.Core.Protocol;
using MoonCam.Infrastructure.Streaming;

[assembly: AvaloniaTestApplication(typeof(MoonCam.App.Tests.TestAppBuilder))]

namespace MoonCam.App.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering without a display, so screenshots show the actual fonts and colours.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the main window in its key states and saves screenshots (set MOONCAM_SNAPSHOTS to choose the
/// folder). The assertions check the bindings; the images are for human review and the README.
/// </summary>
public class MainWindowSnapshotTests
{
    private static readonly string Output = Environment.GetEnvironmentVariable("MOONCAM_SNAPSHOTS")
                                            ?? Path.Combine(AppContext.BaseDirectory, "snapshots");

    [AvaloniaFact]
    public void Waiting_state_explains_how_to_connect()
    {
        var (window, vm) = Open();

        Assert.True(vm.IsWaiting);
        Assert.False(vm.IsVirtualCameraAvailable);
        Assert.False(vm.IsSetupComplete);
        Assert.Contains(vm.SetupItems, c => !c.IsReady);
    }

    [AvaloniaFact]
    public void Set_up_computer_waits_for_the_phone()
    {
        var (window, vm) = Open(setup: new ReadySetup(), cameras: new OneVirtualCamera());
        vm.StatusTitle = "Waiting for your phone";

        Assert.True(vm.IsSetupComplete);
        Assert.True(vm.IsVirtualCameraAvailable);
        Save(window, "mooncam-waiting.png");
    }

    [AvaloniaFact]
    public void Live_state_shows_the_picture_and_the_badge()
    {
        // A computer where everything is installed, as people see MoonCam once it is set up.
        var (window, vm) = Open(setup: new ReadySetup(), cameras: new OneVirtualCamera());
        vm.IsWaiting = false;
        vm.IsLive = true;
        vm.StatusTitle = "Live";
        vm.PhoneName = "Xiaomi Redmi Note 13 Pro";
        vm.StatsText = "1280×720 · 30 fps · 4 ms";
        vm.IsVirtualCameraOn = true;

        window.Preview.Push(TestPattern(1280, 720));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(window.FindControl<Image>("PreviewImage")!.Source);
        Save(window, "mooncam-live.png");
    }

    [AvaloniaFact]
    public void Pairing_card_shows_the_code_to_compare()
    {
        var config = Directory.CreateTempSubdirectory("mooncam-ui-").FullName;
        var wifi = new WiFiOptions(MoonCam.Infrastructure.Security.DesktopIdentity.LoadOrCreate(config),
            new MoonCam.Core.Session.PairingCoordinator(new MoonCam.Infrastructure.Security.JsonPairedPhoneStore(config), TimeProvider.System),
            Port: 0, Advertise: false);
        var (window, vm) = Open(wifi, new ReadySetup(), new OneVirtualCamera());
        vm.IsPairingOpen = true;
        vm.PairingPhone = "Redmi Note 13 Pro";
        vm.PairingCode = "482 913";
        vm.StatusTitle = "Waiting for your phone";

        Assert.True(vm.IsWiFiAvailable);
        Save(window, "mooncam-pairing.png");
        Directory.Delete(config, recursive: true);
    }

    [AvaloniaFact]
    public void Pairing_on_a_new_network_first_asks_whether_it_is_yours()
    {
        var config = Directory.CreateTempSubdirectory("mooncam-ui-").FullName;
        var monitor = new OneNetwork(new MoonCam.Core.Network.NetworkConnection("home-uuid", "Home Wi-Fi"));
        var networks = new MoonCam.Core.Network.NetworkTrust(monitor, new MoonCam.Infrastructure.Security.JsonTrustedNetworkStore(config), TimeProvider.System);
        var wifi = new WiFiOptions(MoonCam.Infrastructure.Security.DesktopIdentity.LoadOrCreate(config),
            new MoonCam.Core.Session.PairingCoordinator(new MoonCam.Infrastructure.Security.JsonPairedPhoneStore(config), TimeProvider.System),
            Port: 0, Advertise: false, Networks: networks);
        var (window, vm) = Open(wifi, new ReadySetup(), new OneVirtualCamera());
        vm.StatusTitle = "Waiting for your phone";

        vm.PairOverWiFiCommand.Execute(null);

        Assert.True(vm.IsTrustPromptOpen);
        Assert.False(vm.IsPairingOpen);
        Assert.Equal("Home Wi-Fi", vm.TrustPromptNetwork);
        Assert.Contains("not a trusted network", vm.WiFiStatus, StringComparison.Ordinal);
        Save(window, "mooncam-trust-network.png");

        vm.TrustNetworkAndPairCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.IsTrustPromptOpen);
        Assert.True(networks.IsWiFiAllowed);
        Assert.Single(vm.TrustedNetworks);
        Directory.Delete(config, recursive: true);
    }

    private static (MainWindow, MainViewModel) Open(WiFiOptions? wifi = null,
        MoonCam.Core.Setup.ISystemSetup? setup = null, IVirtualCameraProvider? cameras = null)
    {
        var controller = new StreamController(TimeProvider.System, () => new NullDecoder(), port: 0, wifi: wifi);
        var vm = new MainViewModel(controller, cameras ?? new NoVirtualCameras(), setup ?? new MissingDriverSetup());
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 800 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, vm);
    }

    private static void Save(MainWindow window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory(Output);
        window.CaptureRenderedFrame()?.Save(Path.Combine(Output, name), PngBitmapEncoderOptions.Default);
    }

    /// <summary>A night landscape (sky, glowing moon, stars, two mountain ridges) as a stand-in for a camera picture.</summary>
    private static VideoFrame TestPattern(int width, int height)
    {
        var rgb = new (double R, double G, double B)[width * height];
        var random = new Random(7);
        var stars = new HashSet<int>();
        for (var i = 0; i < 260; i++) stars.Add(random.Next(width) + random.Next((int)(height * 0.55)) * width);
        double moonX = width * 0.7, moonY = height * 0.3, moonR = height * 0.085;

        for (var y = 0; y < height; y++)
        {
            var t = (double)y / height;
            // Sky: deep indigo at the top, violet in the middle, a warm glow at the horizon.
            (double R, double G, double B) sky = t < 0.5
                ? Mix((14, 16, 52), (64, 44, 120), t / 0.5)
                : Mix((64, 44, 120), (236, 128, 110), Math.Min(1, (t - 0.5) / 0.3));
            for (var x = 0; x < width; x++)
            {
                var c = sky;
                double dx = x - moonX, dy = y - moonY, d = Math.Sqrt(dx * dx + dy * dy);
                if (d < moonR) c = (244, 240, 226);
                else
                {
                    var glow = Math.Exp(-(d - moonR) / (moonR * 1.6)) * 0.55;
                    c = Mix(c, (220, 210, 255), glow);
                    if (stars.Contains(y * width + x)) c = Mix(c, (255, 255, 255), 0.8);
                }
                var ridgeFar = height * (0.64 + 0.06 * Math.Sin(x / (double)width * 7.0) + 0.03 * Math.Sin(x / (double)width * 19.0));
                var ridgeNear = height * (0.78 + 0.07 * Math.Sin(x / (double)width * 4.2 + 1.3) + 0.02 * Math.Sin(x / (double)width * 23.0));
                if (y > ridgeFar) c = Mix((52, 38, 96), (30, 24, 66), (y - ridgeFar) / (height - ridgeFar));
                if (y > ridgeNear) c = (16, 14, 38);
                rgb[y * width + x] = c;
            }
        }

        // RGB → YUV 4:2:0 (BT.601, studio range is not needed for a preview).
        var data = new byte[VideoFrame.SizeOf(width, height)];
        int yPlane = width * height, chroma = yPlane / 4;
        for (var i = 0; i < rgb.Length; i++)
        {
            var (r, g, b) = rgb[i];
            data[i] = Clamp(0.299 * r + 0.587 * g + 0.114 * b);
        }
        for (var y = 0; y < height / 2; y++)
        {
            for (var x = 0; x < width / 2; x++)
            {
                var (r, g, b) = rgb[y * 2 * width + x * 2];
                data[yPlane + y * (width / 2) + x] = Clamp(128 - 0.168736 * r - 0.331264 * g + 0.5 * b);
                data[yPlane + chroma + y * (width / 2) + x] = Clamp(128 + 0.5 * r - 0.418688 * g - 0.081312 * b);
            }
        }
        return new VideoFrame(width, height, data, 0);

        static (double, double, double) Mix((double R, double G, double B) a, (double R, double G, double B) b, double k) =>
            (a.R + (b.R - a.R) * k, a.G + (b.G - a.G) * k, a.B + (b.B - a.B) * k);
        static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
    }

    private sealed class ReadySetup : MoonCam.Core.Setup.ISystemSetup
    {
        public IReadOnlyList<MoonCam.Core.Setup.Component> Check() =>
        [
            new("USB connection (adb)", "Talks to your phone over the cable.", true),
            new("Virtual camera", "Lets every app use your phone as a webcam.", true),
            new("Video decoder (FFmpeg)", "Turns the phone's video into pictures.", true),
        ];

        public IReadOnlyList<string> Microphones() => ["MoonCam Microphone", "Built-in Audio Analog Stereo"];

        public Task<MoonCam.Core.Setup.SetupResult> InstallAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MoonCam.Core.Setup.SetupResult(MoonCam.Core.Setup.SetupOutcome.Ready, ""));
    }

    private sealed class OneVirtualCamera : IVirtualCameraProvider
    {
        public IReadOnlyList<VirtualCameraDevice> Find() => [new("/dev/video10", "MoonCam")];
        public IVirtualCamera Open(VirtualCameraDevice device) => new SilentCamera(device.Path);
    }

    private sealed class SilentCamera(string path) : IVirtualCamera
    {
        public string DevicePath => path;
        public void Push(VideoFrame frame) { }
        public void Dispose() { }
    }

    private sealed class OneNetwork(MoonCam.Core.Network.NetworkConnection network) : MoonCam.Core.Network.INetworkMonitor
    {
        public IReadOnlyList<MoonCam.Core.Network.NetworkConnection> Current { get; } = [network];
        public event Action? Changed { add { } remove { } }
    }

    private sealed class NoVirtualCameras : IVirtualCameraProvider
    {
        public IReadOnlyList<VirtualCameraDevice> Find() => [];
        public IVirtualCamera Open(VirtualCameraDevice device) => throw new NotSupportedException();
    }

    private sealed class MissingDriverSetup : MoonCam.Core.Setup.ISystemSetup
    {
        public IReadOnlyList<MoonCam.Core.Setup.Component> Check() =>
        [
            new("USB connection (adb)", "Talks to your phone over the cable.", false),
            new("Virtual camera", "Lets every app use your phone as a webcam.", false),
            new("Video decoder (FFmpeg)", "Turns the phone's video into pictures.", true),
        ];

        public IReadOnlyList<string> Microphones() => [];

        public Task<MoonCam.Core.Setup.SetupResult> InstallAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new MoonCam.Core.Setup.SetupResult(MoonCam.Core.Setup.SetupOutcome.Ready, ""));
    }

    private sealed class NullDecoder : IVideoDecoder
    {
        public void Configure(VideoConfig config) { }
        public IReadOnlyList<VideoFrame> Decode(ReadOnlyMemory<byte> accessUnit, bool isKeyFrame, long timestampUs) => [];
        public void Dispose() { }
    }
}
