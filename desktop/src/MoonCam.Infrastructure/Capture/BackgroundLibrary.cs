using System.Diagnostics;
using MoonCam.Core.Media;

namespace MoonCam.Infrastructure.Capture;

/// <summary>
/// The person's own background pictures. Added files are copied to ~/.config/mooncam/backgrounds so they stay
/// available even if the original moves; ffmpeg decodes any common image format (JPEG, PNG, WebP…).
/// </summary>
public sealed class BackgroundLibrary(string? folder = null)
{
    public string Folder { get; } = folder ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mooncam", "backgrounds");

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];

    public IReadOnlyList<string> All() => Directory.Exists(Folder)
        ? Directory.EnumerateFiles(Folder).Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .Order(StringComparer.Ordinal).ToList()
        : [];

    /// <summary>Copies a picture into the library and returns the copy's path.</summary>
    public string Add(string source)
    {
        if (!Extensions.Contains(Path.GetExtension(source).ToLowerInvariant()))
            throw new InvalidOperationException("Choose a JPEG, PNG, WebP or BMP picture.");
        Directory.CreateDirectory(Folder);
        var target = Path.Combine(Folder, Path.GetFileName(source));
        for (var i = 2; File.Exists(target); i++)
            target = Path.Combine(Folder, $"{Path.GetFileNameWithoutExtension(source)} ({i}){Path.GetExtension(source)}");
        File.Copy(source, target);
        return target;
    }

    public void Remove(string path)
    {
        if (Path.GetDirectoryName(Path.GetFullPath(path)) == Path.GetFullPath(Folder))
            File.Delete(path);
    }

    /// <summary>Decodes a picture to I420, filling width x height and cropping what does not fit.</summary>
    public static async Task<VideoFrame> LoadAsync(string path, int width = 1280, int height = 720)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-i", path, "-frames:v", "1",
                     "-vf", $"scale={width}:{height}:force_original_aspect_ratio=increase,crop={width}:{height}",
                     "-pix_fmt", "yuv420p", "-f", "rawvideo", "pipe:1" })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new IOException("Could not start ffmpeg.");
        var errors = process.StandardError.ReadToEndAsync();
        var data = new byte[VideoFrame.SizeOf(width, height)];
        var filled = 0;
        while (filled < data.Length)
        {
            var read = await process.StandardOutput.BaseStream.ReadAsync(data.AsMemory(filled)).ConfigureAwait(false);
            if (read == 0) break;
            filled += read;
        }
        await process.WaitForExitAsync().ConfigureAwait(false);
        if (filled < data.Length)
            throw new IOException($"Could not open the picture: {(await errors.ConfigureAwait(false)).Trim()}");
        return new VideoFrame(width, height, data, 0);
    }
}
