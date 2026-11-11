using System.Diagnostics;
using System.Text.Json;

namespace MoonCam.Platform.Linux.Audio;

/// <summary>A microphone (audio input) the computer can record from.</summary>
public sealed record Microphone(string Id, string Name);

/// <summary>
/// Lists the computer's microphones through PipeWire (<c>pw-dump</c>). Monitor sources (what a speaker plays)
/// are not microphones and are skipped. Returns an empty list when PipeWire is not available.
/// </summary>
public static class LinuxMicrophones
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static IReadOnlyList<Microphone> Find()
    {
        try
        {
            var info = new ProcessStartInfo("pw-dump") { RedirectStandardOutput = true, RedirectStandardError = true };
            using var process = Process.Start(info);
            if (process is null)
                return [];
            var json = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }
            return process.ExitCode == 0 ? Parse(json) : [];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Reads a <c>pw-dump</c> JSON array and keeps the audio input nodes.</summary>
    public static IReadOnlyList<Microphone> Parse(string json)
    {
        var microphones = new List<Microphone>();
        using var document = JsonDocument.Parse(json);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("info", out var info) || !info.TryGetProperty("props", out var props))
                continue;
            if (!props.TryGetProperty("media.class", out var mediaClass) || mediaClass.GetString() != "Audio/Source")
                continue;
            var name = props.TryGetProperty("node.name", out var node) ? node.GetString() ?? "" : "";
            if (name.Length == 0 || name.EndsWith(".monitor", StringComparison.Ordinal))
                continue;
            var description = props.TryGetProperty("node.description", out var desc) ? desc.GetString() : null;
            microphones.Add(new Microphone(name, string.IsNullOrWhiteSpace(description) ? name : description));
        }
        return microphones;
    }
}
