using System.Diagnostics;
using MoonCam.Core.Media;

namespace MoonCam.Infrastructure.Capture;

/// <summary>
/// Reads one of the computer's own cameras with ffmpeg (V4L2) as raw I420 at 1280x720, 30 fps, so MoonCam can
/// take photos and record videos — with shapes, filters and blur — when no phone is connected.
/// </summary>
public sealed class LocalCameraSource : IAsyncDisposable
{
    public const int Width = 1280;
    public const int Height = 720;
    private readonly Process _ffmpeg;
    private readonly Task _reader;
    private readonly CancellationTokenSource _stop = new();

    private LocalCameraSource(Process ffmpeg, Action<VideoFrame> onFrame, Action<string>? onEnded)
    {
        _ffmpeg = ffmpeg;
        _reader = Task.Run(() => ReadLoopAsync(onFrame, onEnded));
    }

    public static IReadOnlyList<string> Arguments(string device) =>
    [
        "-hide_banner", "-loglevel", "error",
        "-f", "v4l2", "-framerate", "30", "-video_size", $"{Width}x{Height}", "-i", device,
        "-vf", $"scale={Width}:{Height}:force_original_aspect_ratio=increase,crop={Width}:{Height}",
        "-pix_fmt", "yuv420p", "-f", "rawvideo", "pipe:1",
    ];

    public static LocalCameraSource Start(string device, Action<VideoFrame> onFrame, Action<string>? onEnded = null)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in Arguments(device))
            info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new IOException("Could not start ffmpeg.");
        return new LocalCameraSource(process, onFrame, onEnded);
    }

    private async Task ReadLoopAsync(Action<VideoFrame> onFrame, Action<string>? onEnded)
    {
        var size = VideoFrame.SizeOf(Width, Height);
        var buffer = new byte[size];
        var stdout = _ffmpeg.StandardOutput.BaseStream;
        var errors = _ffmpeg.StandardError.ReadToEndAsync();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var filled = 0;
                while (filled < size)
                {
                    var read = await stdout.ReadAsync(buffer.AsMemory(filled), _stop.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        onEnded?.Invoke((await errors.ConfigureAwait(false)).Trim() is { Length: > 0 } message
                            ? message : "The computer's camera stopped.");
                        return;
                    }
                    filled += read;
                }
                onFrame(new VideoFrame(Width, Height, buffer, Environment.TickCount64 * 1000));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (!_ffmpeg.HasExited)
        {
            try { _ffmpeg.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await _ffmpeg.WaitForExitAsync().ConfigureAwait(false);
        }
        await _reader.ConfigureAwait(false);
        _ffmpeg.Dispose();
        _stop.Dispose();
    }
}
