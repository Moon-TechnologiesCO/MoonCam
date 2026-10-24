using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace MoonCam.Infrastructure.Security;

/// <summary>
/// The desktop's TLS identity for Wi-Fi: a self-signed ECDSA P-256 certificate created once and kept in
/// ~/.config/mooncam/identity.p12, readable only by the user (0600). Phones pin its fingerprint when pairing.
/// </summary>
public static class DesktopIdentity
{
    public static X509Certificate2 LoadOrCreate(string? directory = null)
    {
        var path = Path.Combine(directory ?? ConfigDirectory(), "identity.p12");
        if (File.Exists(path))
            return X509CertificateLoader.LoadPkcs12FromFile(path, password: null, X509KeyStorageFlags.Exportable);

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=MoonCam Desktop {Environment.MachineName}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false)); // serverAuth
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = certificate.Export(X509ContentType.Pkcs12);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(file.SafeFileHandle, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            file.Write(bytes);
        }
        return X509CertificateLoader.LoadPkcs12(bytes, password: null, X509KeyStorageFlags.Exportable);
    }

    public static string ConfigDirectory()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(config)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
            : config, "mooncam");
    }
}
