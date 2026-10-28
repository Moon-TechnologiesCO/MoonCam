using Avalonia;

namespace MoonCam.App;

internal static class Program
{
    /// <summary>This process's claim to be the only MoonCam; read by the composition root.</summary>
    internal static SingleInstance? Instance { get; private set; }

    // Avalonia is not initialised yet here: keep Main free of UI code.
    [STAThread]
    public static void Main(string[] args)
    {
        using var instance = SingleInstance.Claim();
        if (!instance.IsFirst)
            return; // the MoonCam that is already open comes to the front instead
        Instance = instance;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer and by the headless UI tests.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
