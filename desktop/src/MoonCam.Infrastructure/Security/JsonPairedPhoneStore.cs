using System.Text.Json;
using MoonCam.Core.Session;

namespace MoonCam.Infrastructure.Security;

/// <summary>Paired phones in ~/.config/mooncam/paired-phones.json (0600). Fingerprints are public, but who is trusted is not.</summary>
public sealed class JsonPairedPhoneStore : IPairedPhoneStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;
    private readonly Lock _lock = new();
    private List<PairedPhone> _phones;

    public JsonPairedPhoneStore(string? directory = null)
    {
        _path = Path.Combine(directory ?? DesktopIdentity.ConfigDirectory(), "paired-phones.json");
        _phones = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<PairedPhone>>(File.ReadAllText(_path), Options) ?? []
            : [];
    }

    public event Action? Changed;

    public IReadOnlyList<PairedPhone> All
    {
        get { lock (_lock) return [.. _phones]; }
    }

    public bool IsPaired(ReadOnlySpan<byte> fingerprint)
    {
        var hex = Convert.ToHexStringLower(fingerprint);
        lock (_lock)
            return _phones.Any(p => p.FingerprintHex == hex);
    }

    public void Add(PairedPhone phone)
    {
        lock (_lock)
        {
            _phones.RemoveAll(p => p.FingerprintHex == phone.FingerprintHex);
            _phones.Add(phone);
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(string fingerprintHex)
    {
        lock (_lock)
        {
            _phones.RemoveAll(p => p.FingerprintHex == fingerprintHex);
            Save();
        }
        Changed?.Invoke();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_phones, Options));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, _path, overwrite: true); // atomic: a crash never leaves a half-written trust list
    }
}
