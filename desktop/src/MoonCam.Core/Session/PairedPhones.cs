namespace MoonCam.Core.Session;

/// <summary>A phone allowed to connect over Wi-Fi, identified by the SHA-256 of its TLS certificate.</summary>
public sealed record PairedPhone(string Name, string FingerprintHex, DateTimeOffset PairedAt);

/// <summary>Where pairings are remembered. Removing a phone revokes it immediately.</summary>
public interface IPairedPhoneStore
{
    IReadOnlyList<PairedPhone> All { get; }

    bool IsPaired(ReadOnlySpan<byte> fingerprint);

    void Add(PairedPhone phone);

    void Remove(string fingerprintHex);

    event Action? Changed;
}

/// <summary>How a phone reached the desktop, and what it proved about itself at the transport level.</summary>
public sealed record ConnectionInfo(Transport Transport, byte[]? PeerFingerprint, byte[]? LocalFingerprint)
{
    public static readonly ConnectionInfo Usb = new(Transport.Usb, null, null);
}

public enum Transport
{
    Usb,
    WiFi,
}
