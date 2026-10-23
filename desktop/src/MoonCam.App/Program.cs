using Avalonia;

namespace MoonCam.App;

internal static class Program
{
    // Avalonia is not initialised yet here: keep Main free of UI code.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the visual designer and by the headless UI tests.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
