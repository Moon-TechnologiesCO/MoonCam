using MoonCam.Core.Session;

namespace MoonCam.Infrastructure.Security;

/// <summary>Paired phones in ~/.config/mooncam/paired-phones.json (0600). Fingerprints are public, but who is trusted is not.</summary>
public sealed class JsonPairedPhoneStore : IPairedPhoneStore
{
    private readonly PrivateJsonFile<PairedPhone> _file;
    private readonly Lock _lock = new();
    private readonly List<PairedPhone> _phones;

    public JsonPairedPhoneStore(string? directory = null)
    {
        _file = new PrivateJsonFile<PairedPhone>(Path.Combine(directory ?? DesktopIdentity.ConfigDirectory(), "paired-phones.json"));
        _phones = _file.Load();
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
            _file.Save(_phones);
        }
        Changed?.Invoke();
    }

    public void Remove(string fingerprintHex)
    {
        lock (_lock)
        {
            _phones.RemoveAll(p => p.FingerprintHex == fingerprintHex);
            _file.Save(_phones);
        }
        Changed?.Invoke();
    }
}
