using System.Diagnostics;
using System.Threading.Channels;
using MoonCam.Core.Media;

namespace MoonCam.Infrastructure.Capture;

/// <summary>
/// Records what apps see (shape and background included) to an MP4 with ffmpeg: H.264 video and, when
/// "MoonCam Microphone" exists, AAC sound from it. Frames go through a small queue and are dropped when the
/// encoder falls behind, so recording never slows the live picture. If the picture changes size mid-recording
/// (a new shape or resolution) frames are scaled to the size the recording started with.
/// </summary>
public sealed class Recorder : IVideoSink, IAsyncDisposable
{
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(8)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
    });
    private readonly Process _ffmpeg;
    private readonly Task _writer;
    private readonly int _width;
    private readonly int _height;
    private long _frames;
    private long _dropped;

    private Recorder(Process ffmpeg, int width, int height, string path)
    {
        _ffmpeg = ffmpeg;
        _width = width;
        _height = height;
        Path = path;
        _writer = Task.Run(WriteLoopAsync);
    }

    public string Path { get; }
    public DateTimeOffset Started { get; } = DateTimeOffset.Now;
    public long Frames => Interlocked.Read(ref _frames);
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>ffmpeg arguments: raw I420 from stdin, timed by the clock so sound and picture stay together.</summary>
    public static IReadOnlyList<string> Arguments(int width, int height, string? audioSource, string path)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-use_wallclock_as_timestamps", "1", "-thread_queue_size", "64",
            "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", $"{width}x{height}", "-i", "pipe:0",
        };
        if (audioSource is not null)
            args.AddRange(["-thread_queue_size", "512", "-f", "pulse", "-i", audioSource]);
        args.AddRange(["-map", "0:v"]);
        if (audioSource is not null)
            args.AddRange(["-map", "1:a", "-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p",
            "-fps_mode", "vfr", "-movflags", "+faststart", path]);
        return args;
    }

    public static Recorder Start(int width, int height, string? audioSource, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var arg in Arguments(width, height, audioSource, path))
            info.ArgumentList.Add(arg);
        var process = Process.Start(info) ?? throw new IOException("Could not start ffmpeg.");
        process.ErrorDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        return new Recorder(process, width, height, path);
    }

    public void Push(VideoFrame frame)
    {
        if ((frame.Width, frame.Height) != (_width, _height))
            frame = I420Scaler.Scale(frame, _width, _height);
        if (_queue.Writer.TryWrite(frame.Data.AsSpan(0, VideoFrame.SizeOf(_width, _height)).ToArray()))
            Interlocked.Increment(ref _frames);
        else
            Interlocked.Increment(ref _dropped);
    }

    private async Task WriteLoopAsync()
    {
        var stdin = _ffmpeg.StandardInput.BaseStream;
        try
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync())
                await stdin.WriteAsync(frame);
        }
        catch (IOException)
        {
            // ffmpeg exited; StopAsync reports what was saved.
        }
        finally
        {
            try { stdin.Close(); } catch (IOException) { }
        }
    }

    /// <summary>Finishes the file (ffmpeg writes the index when its input closes). Returns the file path.</summary>
    public async Task<string> StopAsync()
    {
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _ffmpeg.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ffmpeg.Kill(entireProcessTree: true);
        }
        return Path;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_ffmpeg.HasExited)
            await StopAsync().ConfigureAwait(false);
        _ffmpeg.Dispose();
    }
}

/// <summary>Takes the next picture that passes through and saves it as a JPEG.</summary>
public sealed class PhotoTaker : IVideoSink
{
    private TaskCompletionSource<VideoFrame>? _next;

    public Task<VideoFrame> NextFrameAsync()
    {
        var next = new TaskCompletionSource<VideoFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _next, next);
        return next.Task;
    }

    public void Push(VideoFrame frame)
    {
        var next = Interlocked.Exchange(ref _next, null);
        next?.TrySetResult(new VideoFrame(frame.Width, frame.Height,
            frame.Data.AsSpan(0, VideoFrame.SizeOf(frame.Width, frame.Height)).ToArray(), frame.TimestampUs));
    }

    public static async Task SaveJpegAsync(VideoFrame frame, string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardInput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-y", "-f", "rawvideo", "-pix_fmt", "yuv420p",
                     "-s", $"{frame.Width}x{frame.Height}", "-i", "pipe:0", "-frames:v", "1", "-q:v", "2", path })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start ffmpeg.");
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardInput.BaseStream.WriteAsync(frame.Data.AsMemory(0, VideoFrame.SizeOf(frame.Width, frame.Height)));
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !File.Exists(path))
            throw new IOException($"Could not save the photo: {(await errors).Trim()}");
    }
}
