using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace MoonCam.Core.Protocol;

/// <summary>
/// The Wi-Fi pairing code (docs/security.md §Pairing). Like Bluetooth numeric comparison, the phone commits to
/// its nonce before seeing the desktop's, so a man in the middle cannot pick values that make both screens show
/// the same six digits: its chance stays one in a million per attempt, and attempts are rate-limited.
/// </summary>
public static class Pairing
{
    public const int NonceSize = 32;

    private static ReadOnlySpan<byte> Label => "MoonCam pairing v1"u8;

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    public static byte[] Commit(ReadOnlySpan<byte> nonce) => SHA256.HashData(nonce);

    public static bool VerifyCommit(ReadOnlySpan<byte> commit, ReadOnlySpan<byte> nonce)
        => nonce.Length == NonceSize && CryptographicOperations.FixedTimeEquals(commit, SHA256.HashData(nonce));

    /// <summary>Six digits both devices display. Inputs are raw bytes (fingerprints are SHA-256 of DER certificates).</summary>
    public static string Code(ReadOnlySpan<byte> phoneFingerprint, ReadOnlySpan<byte> desktopFingerprint,
        ReadOnlySpan<byte> phoneNonce, ReadOnlySpan<byte> desktopNonce)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Label);
        sha.AppendData(phoneFingerprint);
        sha.AppendData(desktopFingerprint);
        sha.AppendData(phoneNonce);
        sha.AppendData(desktopNonce);
        var hash = sha.GetHashAndReset();
        var value = BinaryPrimitives.ReadUInt32BigEndian(hash) % 1_000_000;
        return value.ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>SHA-256 of a DER-encoded certificate: the identity that pairing pins.</summary>
    public static byte[] Fingerprint(ReadOnlySpan<byte> certificateDer) => SHA256.HashData(certificateDer);

    /// <summary>"482 913" — easier to compare at a glance.</summary>
    public static string Format(string code) => code.Length == 6 ? $"{code[..3]} {code[3..]}" : code;
}
