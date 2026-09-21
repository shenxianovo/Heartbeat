using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Heartbeat.Dev;

/// <summary>Short-lived import files; the durable private key lives only in the login keychain.</summary>
internal sealed class DevelopmentCertificate : IDisposable
{
    private readonly string _directory;
    public string IdentityPath => Path.Combine(_directory, "identity.pem");
    public string CertificatePath => Path.Combine(_directory, "certificate.cer");

    private DevelopmentCertificate(string directory) => _directory = directory;

    public static DevelopmentCertificate Create()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var directory = Path.Combine(Path.GetTempPath(), $"heartbeat-signing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var files = new DevelopmentCertificate(directory);
        try
        {
            using var key = RSA.Create(3072);
            var request = new CertificateRequest($"CN={MacDevelopmentSigning.IdentityName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyCertSign, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.3") }, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
            // security import rejects our empty-password PKCS#12 export. PEM needs no command-line passphrase;
            // the containing directory is user-only and is removed immediately after import.
            File.WriteAllText(files.IdentityPath, certificate.ExportCertificatePem() + "\n" + key.ExportPkcs8PrivateKeyPem());
            File.SetUnixFileMode(files.IdentityPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllBytes(files.CertificatePath, certificate.Export(X509ContentType.Cert));
            return files;
        }
        catch
        {
            files.Dispose();
            throw;
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
