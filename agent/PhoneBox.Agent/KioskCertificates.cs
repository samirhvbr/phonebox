using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace PhoneBox.Agent;

/// <summary>A separate CA and short-lived SAN certificate for browsers, without changing Android's pinned certificate.</summary>
public sealed class KioskCertificates
{
    public X509Certificate2 Server { get; }
    public X509Certificate2 Authority { get; }
    public IReadOnlyList<string> Hosts { get; }

    public KioskCertificates(string directory, string host)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains(':'))
            throw new ArgumentException("The browser hostname must be a DNS name or IPv4 address.");
        Directory.CreateDirectory(directory);
        var caPath = Path.Combine(directory, "kiosk-ca.pfx");
        Authority = File.Exists(caPath) ? Load(caPath) : CreateAuthority(caPath);
        Hosts = new[] { host, "localhost", "127.0.0.1" }.Concat(Lan.Addresses()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var serverPath = Path.Combine(directory, "kiosk-server.pfx");
        X509Certificate2? existing = File.Exists(serverPath) ? Load(serverPath) : null;
        if (existing is { HasPrivateKey: true } && existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(14) &&
            SignedByAuthority(existing, Authority) &&
            Hosts.All(h => existing.MatchesHostname(h, allowWildcards: false, allowCommonName: false)))
        {
            Server = existing;
            return;
        }
        existing?.Dispose();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PhoneBox browser panel", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var value in Hosts)
        {
            if (IPAddress.TryParse(value, out var address)) san.AddIpAddress(address);
            else san.AddDnsName(value);
        }
        request.CertificateExtensions.Add(san.Build());
        using var signed = request.Create(Authority, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(90), RandomNumberGenerator.GetBytes(16));
        using var withKey = signed.CopyWithPrivateKey(key);
        Save(serverPath, withKey.Export(X509ContentType.Pfx));
        Server = Load(serverPath);
    }

    public string Fingerprint => Lan.Fingerprint(Authority);

    public byte[] Profile()
    {
        var identifier = "net.phonebox.kiosk." + Fingerprint[..16];
        // The profile contains only the public CA certificate, never a key or browser credential.
        var certificate = new XElement("dict",
            new XElement("key", "PayloadType"), new XElement("string", "com.apple.security.root"),
            new XElement("key", "PayloadVersion"), new XElement("integer", 1),
            new XElement("key", "PayloadIdentifier"), new XElement("string", identifier + ".certificate"),
            new XElement("key", "PayloadUUID"), new XElement("string", Guid.NewGuid().ToString()),
            new XElement("key", "PayloadDisplayName"), new XElement("string", Authority.GetNameInfo(X509NameType.SimpleName, false)),
            new XElement("key", "PayloadContent"), new XElement("data", Convert.ToBase64String(Authority.RawData)));
        var root = new XElement("dict",
            new XElement("key", "PayloadType"), new XElement("string", "Configuration"),
            new XElement("key", "PayloadVersion"), new XElement("integer", 1),
            new XElement("key", "PayloadIdentifier"), new XElement("string", identifier),
            new XElement("key", "PayloadUUID"), new XElement("string", Guid.NewGuid().ToString()),
            new XElement("key", "PayloadDisplayName"), new XElement("string", "PhoneBox — " + Environment.MachineName),
            new XElement("key", "PayloadDescription"), new XElement("string", "Trust the dedicated PhoneBox certificate authority for this Mac's browser panel. Remove this profile when you stop using PhoneBox."),
            new XElement("key", "PayloadContent"), new XElement("array", certificate));
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), root));
        return Encoding.UTF8.GetBytes(document.Declaration + "\n" + document.ToString());
    }

    private static X509Certificate2 CreateAuthority(string path)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=PhoneBox " + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)) + " Local CA", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(3));
        Save(path, certificate.Export(X509ContentType.Pfx));
        return Load(path);
    }

    private static void Save(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        try
        {
            using (var stream = AppPaths.CreatePrivate(temporary)) stream.Write(bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static bool SignedByAuthority(X509Certificate2 server, X509Certificate2 authority)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(authority);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        return chain.Build(server);
    }

    private static X509Certificate2 Load(string path)
    {
        AppPaths.RestrictToOwner(path);
        return X509CertificateLoader.LoadPkcs12FromFile(path, null, X509KeyStorageFlags.Exportable);
    }
}
