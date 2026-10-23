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
        Save(window, "mooncam-waiting.png");
    }

    [AvaloniaFact]
    public void Live_state_shows_the_picture_and_the_badge()
    {
        var (window, vm) = Open();
        vm.IsWaiting = false;
        vm.IsLive = true;
        vm.StatusTitle = "Live";
        vm.PhoneName = "Google Pixel 9";
        vm.StatsText = "1280×720 · 30 fps · 4 ms";

        window.Preview.Push(TestPattern(1280, 720));
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(window.FindControl<Image>("PreviewImage")!.Source);
        Save(window, "mooncam-live.png");
    }

    private static (MainWindow, MainViewModel) Open()
    {
        var controller = new StreamController(TimeProvider.System, () => new NullDecoder(), port: 0);
        var vm = new MainViewModel(controller, new NoVirtualCameras(), new MissingDriverSetup());
        var window = new MainWindow { DataContext = vm, Width = 1080, Height = 700 };
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

    /// <summary>A soft night-sky gradient with a bright moon, as a stand-in for a camera picture.</summary>
    private static VideoFrame TestPattern(int width, int height)
    {
        var data = new byte[VideoFrame.SizeOf(width, height)];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                double dx = x - width * 0.68, dy = y - height * 0.42;
                var moon = dx * dx + dy * dy < 120 * 120;
                data[y * width + x] = moon ? (byte)225 : (byte)(40 + 50 * y / height);
            }
        }
        int chroma = width * height / 4, yPlane = width * height;
        data.AsSpan(yPlane, chroma).Fill(150);         // U: towards blue
        data.AsSpan(yPlane + chroma, chroma).Fill(122); // V: a little violet
        return new VideoFrame(width, height, data, 0);
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
