using System.Diagnostics;
using MoonCam.Core.Diagnostics;

namespace MoonCam.Infrastructure.Discovery;

/// <summary>
/// Announces the desktop on the local network as "_mooncam._tcp" (mDNS / DNS-SD) through Avahi, so phones find
/// it without typing an IP address. The TXT record carries the start of the certificate fingerprint, which lets a
/// paired phone recognise this computer even if its name or address changes. The announcement lives exactly as
/// long as this object.
/// </summary>
public sealed class AvahiAdvertiser(IEventLog log) : IDisposable
{
    public const string ServiceType = "_mooncam._tcp";
    private Process? _process;

    public void Start(string name, int port, string fingerprintHex)
    {
        var info = new ProcessStartInfo("avahi-publish") { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-s", name, ServiceType, port.ToString(System.Globalization.CultureInfo.InvariantCulture), "v=1", $"fp={fingerprintHex[..16]}" })
            info.ArgumentList.Add(argument);
        try
        {
            _process = Process.Start(info);
            log.Info($"Announcing \"{name}\" on the local network ({ServiceType}, port {port})");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            log.Warn("avahi-publish is not installed; phones must be pointed at this computer by address", e);
        }
    }

    public void Dispose()
    {
        if (_process is { HasExited: false })
            _process.Kill();
        _process?.Dispose();
    }
}
