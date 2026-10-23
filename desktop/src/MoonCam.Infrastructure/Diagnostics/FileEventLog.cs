using System.Globalization;
using MoonCam.Core.Diagnostics;

namespace MoonCam.Infrastructure.Diagnostics;

/// <summary>
/// Appends timestamped lines to ~/.local/state/mooncam/mooncam.log (XDG state directory). When the file passes
/// 1 MB it is renamed to mooncam.log.1, so logs never grow without bound. Thread-safe; never throws.
/// </summary>
public sealed class FileEventLog : IEventLog
{
    private const long MaxBytes = 1024 * 1024;
    private readonly Lock _lock = new();

    public FileEventLog(string? directory = null)
    {
        var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        var root = string.IsNullOrEmpty(state)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state")
            : state;
        FilePath = Path.Combine(directory ?? Path.Combine(root, "mooncam"), "mooncam.log");
    }

    public string FilePath { get; }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? error = null) => Write("WARN", message, error);

    private void Write(string level, string message, Exception? error)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {level} {message}{(error is null ? "" : $" | {error.GetType().Name}: {error.Message}")}{Environment.NewLine}");
        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, line);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the app down.
            }
        }
    }
}
