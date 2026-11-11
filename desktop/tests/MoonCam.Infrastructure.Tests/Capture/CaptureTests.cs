using System.Diagnostics;
using MoonCam.Core.Media;
using MoonCam.Infrastructure.Capture;

namespace MoonCam.Infrastructure.Tests.Capture;

/// <summary>Runs ffmpeg for real; returns early on machines without it.</summary>
public class CaptureTests
{
    private static bool HasFfmpeg => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
        .Any(dir => File.Exists(Path.Combine(dir, "ffmpeg")));

    [Fact]
    public void Recording_without_a_microphone_has_only_video()
    {
        var args = Recorder.Arguments(640, 360, audioSource: null, "/tmp/x.mp4");

        Assert.DoesNotContain("pulse", args);
        Assert.Contains("640x360", args);
        Assert.Equal("/tmp/x.mp4", args[^1]);
    }

    [Fact]
    public void Recording_with_the_microphone_reads_it_from_PipeWire()
    {
        var args = Recorder.Arguments(640, 360, "mooncam_microphone", "/tmp/x.mp4").ToList();

        Assert.Equal("mooncam_microphone", args[args.IndexOf("pulse") + 2]);
        Assert.Contains("aac", args);
    }

    [Fact]
    public async Task A_recording_becomes_a_playable_mp4_even_when_the_size_changes()
    {
        if (!HasFfmpeg)
            return;
        var path = Path.Combine(Path.GetTempPath(), $"mooncam-rec-{Guid.NewGuid():N}.mp4");
        try
        {
            await using var recorder = Recorder.Start(320, 180, audioSource: null, path);
            for (var i = 0; i < 30; i++)
            {
                // Halfway through the shape changes: frames are scaled to the recording size.
                recorder.Push(i < 15 ? Frame(320, 180, (byte)(i * 8)) : Frame(180, 180, 200));
                await Task.Delay(33);
            }
            await recorder.StopAsync();

            Assert.True(new FileInfo(path).Length > 0);
            var probe = Run("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
                "-of", "csv=p=0", path);
            Assert.Equal("320,180", probe.Trim());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task A_photo_is_the_next_frame_saved_as_jpeg()
    {
        if (!HasFfmpeg)
            return;
        var taker = new PhotoTaker();
        var next = taker.NextFrameAsync();
        taker.Push(Frame(160, 90, 120));
        var frame = await next;
        var path = Path.Combine(Path.GetTempPath(), $"mooncam-photo-{Guid.NewGuid():N}.jpg");
        try
        {
            await PhotoTaker.SaveJpegAsync(frame, path);
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.Equal(0xFF, bytes[0]); // JPEG start-of-image marker
            Assert.Equal(0xD8, bytes[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static VideoFrame Frame(int width, int height, byte luma)
    {
        var data = new byte[VideoFrame.SizeOf(width, height)];
        data.AsSpan(0, width * height).Fill(luma);
        data.AsSpan(width * height).Fill(128);
        return new VideoFrame(width, height, data, 0);
    }

    private static string Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { RedirectStandardOutput = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
}
