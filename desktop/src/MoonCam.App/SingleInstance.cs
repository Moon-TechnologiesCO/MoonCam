using System.Net.Sockets;

namespace MoonCam.App;

/// <summary>
/// One MoonCam per user. A second launch (the icon clicked while MoonCam is already open) asks the running one
/// to come to the front and exits, instead of fighting it for the camera and the network ports. The lock and
/// the socket live in $XDG_RUNTIME_DIR, which only this user can enter.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly FileStream? _lock;
    private readonly Socket? _listener;
    private readonly string? _socketPath;

    private SingleInstance(bool isFirst, FileStream? lockFile = null, Socket? listener = null, string? socketPath = null)
    {
        IsFirst = isFirst;
        _lock = lockFile;
        _listener = listener;
        _socketPath = socketPath;
        if (listener is not null)
            _ = AcceptAsync(listener);
    }

    /// <summary>False when another MoonCam was already running (it has been asked to show itself).</summary>
    public bool IsFirst { get; }

    /// <summary>Raised (on a background thread) when another launch asks this MoonCam to show its window.</summary>
    public event Action? ShowRequested;

    public static SingleInstance Claim()
    {
        var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(runtime) || !Directory.Exists(runtime))
            return new SingleInstance(isFirst: true); // no private runtime directory: do not guard

        var directory = Directory.CreateDirectory(Path.Combine(runtime, "mooncam")).FullName;
        var socketPath = Path.Combine(directory, "instance.sock");
        FileStream lockFile;
        try
        {
            // FileShare.None takes an exclusive flock() that the system releases if MoonCam crashes.
            lockFile = new FileStream(Path.Combine(directory, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            AskRunningInstanceToShow(socketPath);
            return new SingleInstance(isFirst: false);
        }

        File.Delete(socketPath); // left behind by a crash
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        listener.Listen(4);
        return new SingleInstance(isFirst: true, lockFile, listener, socketPath);
    }

    private async Task AcceptAsync(Socket listener)
    {
        while (true)
        {
            try
            {
                using var client = await listener.AcceptAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException)
            {
                return;
            }
            ShowRequested?.Invoke(); // connecting is the whole message: nothing is read from the socket
        }
    }

    private static void AskRunningInstanceToShow(string socketPath)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        }
        catch (SocketException)
        {
            // The other MoonCam is starting or closing; it will be on screen either way.
        }
    }

    public void Dispose()
    {
        _listener?.Dispose();
        if (_socketPath is not null)
            File.Delete(_socketPath);
        _lock?.Dispose();
    }
}
