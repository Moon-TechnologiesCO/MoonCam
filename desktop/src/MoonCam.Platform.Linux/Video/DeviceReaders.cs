using System.Runtime.InteropServices;

namespace MoonCam.Platform.Linux.Video;

/// <summary>
/// Tells whether other programs have a video device open, and when that changes. inotify reports every open
/// and close of the device node, and only then is /proc consulted for who still holds it; so this costs nothing
/// while nobody touches the camera. MoonCam's own process is not counted.
/// </summary>
internal sealed partial class DeviceReaders : IDisposable
{
    private const int InNonBlock = 0x800;
    private const int InCloseOnExec = 0x80000;
    private const uint InOpen = 0x20;
    private const uint InCloseWrite = 0x8;
    private const uint InCloseNoWrite = 0x10;
    private const short PollIn = 0x1;
    private const int PollTimeoutMs = 500;

    private readonly string _devicePath;
    private readonly int _inotify;
    private readonly Thread? _thread;
    private volatile bool _stopping;
    private volatile bool _any;

    public DeviceReaders(string devicePath)
    {
        _devicePath = devicePath;
        _any = CountReaders(devicePath, Environment.ProcessId) > 0;
        _inotify = inotify_init1(InNonBlock | InCloseOnExec);
        if (_inotify < 0 || inotify_add_watch(_inotify, devicePath, InOpen | InCloseWrite | InCloseNoWrite) < 0)
            return; // without inotify the camera simply counts as unwatched; the preview still drives the phone
        _thread = new Thread(Watch) { IsBackground = true, Name = "MoonCam camera readers" };
        _thread.Start();
    }

    public bool Any => _any;

    public event Action? Changed;

    /// <summary>Processes other than <paramref name="excludePid"/> holding <paramref name="devicePath"/> open.</summary>
    internal static int CountReaders(string devicePath, int excludePid)
    {
        var count = 0;
        foreach (var process in Directory.EnumerateDirectories("/proc"))
        {
            var name = Path.GetFileName(process);
            if (name.Length == 0 || !char.IsAsciiDigit(name[0]) || !int.TryParse(name, out var pid) || pid == excludePid)
                continue;
            try
            {
                foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(process, "fd")))
                {
                    if (new FileInfo(fd).LinkTarget == devicePath)
                    {
                        count++;
                        break;
                    }
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                // Another user's process, or it just exited.
            }
        }
        return count;
    }

    private unsafe void Watch()
    {
        var events = new byte[4096];
        var poll = new PollFd { Fd = _inotify, Events = PollIn };
        while (!_stopping)
        {
            if (Poll(&poll, 1, PollTimeoutMs) <= 0)
                continue;
            fixed (byte* buffer = events)
            {
                while (read(_inotify, buffer, (nuint)events.Length) > 0)
                {
                    // Drain: one recount covers every event read so far.
                }
            }
            var any = CountReaders(_devicePath, Environment.ProcessId) > 0;
            if (any == _any)
                continue;
            _any = any;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(PollTimeoutMs * 2);
        if (_inotify >= 0)
            close(_inotify);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int inotify_init1(int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int inotify_add_watch(int fd, string path, uint mask);

    [LibraryImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static unsafe partial int Poll(PollFd* fds, nuint count, int timeoutMs);

    [LibraryImport("libc", SetLastError = true)]
    private static unsafe partial nint read(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
}
