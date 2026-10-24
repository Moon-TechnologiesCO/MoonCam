using MoonCam.Core.Network;

namespace MoonCam.Infrastructure.Security;

/// <summary>Networks the person trusts for Wi-Fi, in ~/.config/mooncam/trusted-networks.json (0600).</summary>
public sealed class JsonTrustedNetworkStore : ITrustedNetworkStore
{
    private readonly PrivateJsonFile<TrustedNetwork> _file;
    private readonly Lock _lock = new();
    private readonly List<TrustedNetwork> _networks;

    public JsonTrustedNetworkStore(string? directory = null)
    {
        _file = new PrivateJsonFile<TrustedNetwork>(Path.Combine(directory ?? DesktopIdentity.ConfigDirectory(), "trusted-networks.json"));
        _networks = _file.Load();
    }

    public event Action? Changed;

    public IReadOnlyList<TrustedNetwork> All
    {
        get { lock (_lock) return [.. _networks]; }
    }

    public bool IsTrusted(string id)
    {
        lock (_lock)
            return _networks.Any(n => n.Id == id);
    }

    public void Add(TrustedNetwork network)
    {
        lock (_lock)
        {
            _networks.RemoveAll(n => n.Id == network.Id);
            _networks.Add(network);
            _file.Save(_networks);
        }
        Changed?.Invoke();
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            _networks.RemoveAll(n => n.Id == id);
            _file.Save(_networks);
        }
        Changed?.Invoke();
    }
}
