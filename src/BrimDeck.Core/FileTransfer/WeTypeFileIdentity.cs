using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BrimDeck.Core.FileTransfer;

// A TLS identity for one file rendezvous, as wx-ime-sdk generates it: a private CA, and a client and a server
// certificate issued by it. The CA goes through the control channel; each side trusts only the other's CA.
public sealed class WeTypeFileIdentity : IDisposable
{
    private readonly ECDsa _rootKey, _clientKey, _serverKey;

    public string ServerName { get; }
    public byte[] CaPem { get; }
    public X509Certificate2 ServerCertificate { get; }
    public X509Certificate2 ClientCertificate { get; }

    public WeTypeFileIdentity()
    {
        _rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _serverKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset from = now.AddDays(-1), to = now.AddDays(2);

        var rootRequest = new CertificateRequest("CN=UniClipboard WeType private CA", _rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        rootRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootRequest.PublicKey, false));
        using var root = rootRequest.CreateSelfSigned(from, to);
        CaPem = Encoding.ASCII.GetBytes(root.ExportCertificatePem());

        ServerName = Guid.NewGuid().ToString("N") + ".wetype.private";
        var serverRequest = new CertificateRequest("CN=" + ServerName, _serverKey, HashAlgorithmName.SHA256);
        LeafExtensions(serverRequest, "1.3.6.1.5.5.7.3.1", root);
        var alternative = new SubjectAlternativeNameBuilder();
        alternative.AddDnsName(ServerName);
        serverRequest.CertificateExtensions.Add(alternative.Build());
        using (var issued = serverRequest.Create(root, from, to, Serial()))
            ServerCertificate = Persist(issued, _serverKey);

        var clientRequest = new CertificateRequest("CN=UniClipboard WeType client", _clientKey, HashAlgorithmName.SHA256);
        LeafExtensions(clientRequest, "1.3.6.1.5.5.7.3.2", root);
        using (var issued = clientRequest.Create(root, from, to, Serial()))
            ClientCertificate = Persist(issued, _clientKey);
    }

    // Schannel cannot use ephemeral keys for TLS, so the private key is imported into a temporary user key container.
    // Without PersistKeySet it is deleted again when the certificate is disposed.
    private static X509Certificate2 Persist(X509Certificate2 issued, ECDsa key)
    {
        using var withKey = issued.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private static void LeafExtensions(CertificateRequest request, string usage, X509Certificate2 root)
    {
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(usage) }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
    }

    private static byte[] Serial()
    {
        byte[] serial = RandomNumberGenerator.GetBytes(16);
        // A positive ASN.1 integer that is not shorter than the others.
        serial[0] = (byte)(serial[0] | 0x80);
        return serial;
    }

    // The peer is trusted only through the CA it sent over the authenticated control channel.
    public static bool Validate(X509Certificate2? certificate, X509Certificate2? authority)
    {
        if (certificate is null || authority is null) return false;
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        return chain.Build(certificate);
    }

    public void Dispose()
    {
        ServerCertificate.Dispose();
        ClientCertificate.Dispose();
        _rootKey.Dispose();
        _clientKey.Dispose();
        _serverKey.Dispose();
    }
}
