using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MoonCam.App.ViewModels;
using MoonCam.App.Views;
using MoonCam.Infrastructure.Decoding;
using MoonCam.Infrastructure.Streaming;
using MoonCam.Platform.Linux.Setup;
using MoonCam.Platform.Linux.Video;

namespace MoonCam.App;

/// <summary>Composition root: the only place where concrete implementations are chosen.</summary>
public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var controller = new StreamController(TimeProvider.System, () => new FfmpegVideoDecoder(), fixedToken: DevelopmentToken(desktop.Args));
            var setup = new LinuxSystemSetup(() => FfmpegVideoDecoder.IsAvailable);
            var viewModel = new MainViewModel(controller, new LinuxVirtualCameras(), setup, action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
            var window = new MainWindow { DataContext = viewModel };
            controller.AddSink(window.Preview);
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) => await controller.DisposeAsync();
            controller.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// <c>--dev-token X</c> lets the FakePhone tool connect during development. Debug builds only: release
    /// builds always use a fresh random token per launch.
    /// </summary>
    private static string? DevelopmentToken(string[]? args)
    {
#if DEBUG
        var index = Array.IndexOf(args ?? [], "--dev-token");
        return index >= 0 && index + 1 < args!.Length ? args[index + 1] : null;
#else
        return null;
#endif
    }
}
