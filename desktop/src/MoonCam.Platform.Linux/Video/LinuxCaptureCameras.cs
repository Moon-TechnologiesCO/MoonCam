namespace MoonCam.Platform.Linux.Video;

/// <summary>A camera built into or plugged into this computer (not a virtual one).</summary>
public sealed record CaptureCamera(string Path, string Name);

/// <summary>Finds the computer's own cameras through sysfs, skipping metadata nodes and v4l2loopback devices.</summary>
public static class LinuxCaptureCameras
{
    public static IReadOnlyList<CaptureCamera> Find(string sysfs = "/sys/class/video4linux")
    {
        if (!Directory.Exists(sysfs))
            return [];
        var loopback = V4l2LoopbackCamera.FindDevices().Select(d => d.Path).ToHashSet(StringComparer.Ordinal);
        var cameras = new List<CaptureCamera>();
        foreach (var dir in Directory.EnumerateDirectories(sysfs, "video*").Order(StringComparer.Ordinal))
        {
            var path = "/dev/" + System.IO.Path.GetFileName(dir);
            if (loopback.Contains(path))
                continue;
            // Each USB camera exposes a capture node (index 0) and a metadata node (index 1).
            if (ReadText(System.IO.Path.Combine(dir, "index")) is { } index && index != "0")
                continue;
            if (!Directory.Exists(System.IO.Path.Combine(dir, "device")))
                continue; // virtual devices have no hardware behind them
            var name = ReadText(System.IO.Path.Combine(dir, "name")) ?? path;
            cameras.Add(new CaptureCamera(path, Tidy(name)));
        }
        return cameras;
    }

    /// <summary>"Integrated Camera: Integrated C" → "Integrated Camera".</summary>
    private static string Tidy(string name)
    {
        var colon = name.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? name[..colon].Trim() : name.Trim();
    }

    private static string? ReadText(string file)
    {
        try
        {
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
