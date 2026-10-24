using System.Text.Json;

namespace MoonCam.Infrastructure.Security;

/// <summary>
/// A JSON list only the user can read or write (0600), saved atomically: a crash never leaves a half-written
/// trust list. Used for everything MoonCam trusts (phones, networks).
/// </summary>
internal sealed class PrivateJsonFile<T>(string path)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public List<T> Load() => File.Exists(path) ? JsonSerializer.Deserialize<List<T>>(File.ReadAllText(path), Options) ?? [] : [];

    public void Save(List<T> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, Options));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }
}
