using System.Diagnostics;
using MoonCam.Core.Media;
using MoonCam.Platform.Linux.Video;

namespace MoonCam.Platform.Linux.Tests;

/// <summary>
/// Writes to the real "MoonCam" v4l2loopback device while ffmpeg reads from it, exactly as Zoom or Meet would.
/// Runs only on machines where MoonCam's virtual camera is installed; elsewhere it returns early.
/// </summary>
public class VirtualCameraIntegrationTests
{
    [Fact]
    public async Task Another_app_can_capture_what_MoonCam_writes()
    {
        var device = new LinuxVirtualCameras().Find().FirstOrDefault(d => d.Name.Contains("MoonCam", StringComparison.Ordinal));
        if (device is null || !CommandExists("ffmpeg"))
            return;

        using var camera = V4l2LoopbackCamera.Open(device.Path);
        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            var frame = Gradient(640, 360);
            while (!stop.IsCancellationRequested)
            {
                camera.Push(frame);
                await Task.Delay(33);
            }
        });
        await Task.Delay(300); // exclusive_caps=1: the device becomes a capture device once a writer is active

        var output = Path.Combine(Path.GetTempPath(), $"mooncam-capture-{Guid.NewGuid():N}.png");
        var (exitCode, log) = await RunAsync("ffmpeg", "-hide_banner", "-loglevel", "error", "-f", "v4l2",
            "-i", device.Path, "-frames:v", "1", "-y", output);
        await stop.CancelAsync();
        await writer;

        Assert.True(exitCode == 0, $"ffmpeg could not capture from {device.Path}: {log}");
        var (_, probe) = await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height", "-of", "csv=p=0", output);
        File.Delete(output);
        Assert.Equal("640,360", probe.Trim());
    }

    private static VideoFrame Gradient(int width, int height)
    {
        var data = new byte[VideoFrame.SizeOf(width, height)];
        for (var y = 0; y < height; y++)
            data.AsSpan(y * width, width).Fill((byte)(16 + 200 * y / height));
        data.AsSpan(width * height).Fill(128);
        return new VideoFrame(width, height, data, 0);
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, params string[] arguments)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdout + await stderr);
    }

    private static bool CommandExists(string name)
        => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(dir => File.Exists(Path.Combine(dir, name)));
}
