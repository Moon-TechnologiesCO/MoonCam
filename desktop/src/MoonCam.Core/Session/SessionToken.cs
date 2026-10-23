using System.Security.Cryptography;
using System.Text;

namespace MoonCam.Core.Session;

/// <summary>
/// One-time secret the desktop gives the phone through ADB (USB mode). The phone must echo it in HELLO,
/// which proves the connection comes from the app the desktop launched and not from another local process.
/// </summary>
public static class SessionToken
{
    public static string Create()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Constant-time comparison, so response timing reveals nothing about the expected token.</summary>
    public static bool Matches(string expected, string? candidate)
    {
        if (string.IsNullOrEmpty(expected) || candidate is null)
            return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(candidate));
    }
}
