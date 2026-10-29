using System.Collections.Concurrent;
using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Hushwire.Core.AdFilter;

/// <summary>
/// A private certificate authority that exists only on this PC and can only vouch for YouTube and Twitch hostnames.
/// The restriction is an X.509 Name Constraints extension: operating systems and browsers refuse any certificate this
/// CA signs for another domain, even if the CA's private key were stolen.
/// </summary>
public sealed class LocalCa : IDisposable
{
    /// <summary>The only domains (and their subdomains) this CA may ever issue for.</summary>
    public static readonly string[] PermittedDomains = ["youtube.com", "ttvnw.net"];

    public const string Subject = "CN=Hushwire Local Ad Filter CA (this PC only), O=Hushwire";

    readonly X509Certificate2 root;                      // with private key
    readonly ConcurrentDictionary<string, X509Certificate2> leaves = new(StringComparer.OrdinalIgnoreCase);

    LocalCa(X509Certificate2 root) => this.root = root;

    /// <summary>Public certificate (what gets installed into the trust store).</summary>
    public X509Certificate2 PublicCertificate => X509CertificateLoader.LoadCertificate(root.Export(X509ContentType.Cert));
    public string Thumbprint => root.Thumbprint;
    public DateTime NotAfter => root.NotAfter;

    public static bool IsPermittedHost(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        foreach (var d in PermittedDomains)
            if (host == d || host.EndsWith("." + d, StringComparison.Ordinal)) return true;
        return false;
    }

    static X509Extension NameConstraints()
    {
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())                                                     // NameConstraints
        using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))             // permittedSubtrees [0]
        {
            foreach (var d in PermittedDomains)
                using (w.PushSequence())                                             // GeneralSubtree
                    w.WriteCharacterString(UniversalTagNumber.IA5String, d, new Asn1Tag(TagClass.ContextSpecific, 2)); // dNSName [2]
        }
        return new X509Extension(new Oid("2.5.29.30"), w.Encode(), critical: true);
    }

    public static LocalCa Create()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(NameConstraints());
        using var selfSigned = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        // re-import so the private key is a persisted, exportable key (SslStream on Windows needs this)
        return new LocalCa(X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable));
    }

    public byte[] ExportPfx() => root.Export(X509ContentType.Pfx, "");

    public static LocalCa Load(byte[] pfx) => new(X509CertificateLoader.LoadPkcs12(pfx, "", X509KeyStorageFlags.Exportable));

    /// <summary>Server certificate for <paramref name="host"/>, signed by this CA. Throws for hosts outside the permitted domains.</summary>
    public X509Certificate2 Issue(string host)
    {
        host = host.TrimEnd('.').ToLowerInvariant();
        if (!IsPermittedHost(host)) throw new ArgumentException($"{host} is not a permitted host", nameof(host));
        if (leaves.TryGetValue(host, out var cached) && cached.NotAfter > DateTime.Now.AddDays(1)) return cached;
        var cert = Mint(host);
        leaves[host] = cert;
        return cert;
    }

    /// <summary>Signs for any host, bypassing the check above. Tests use it to prove the OS enforces the Name Constraints.</summary>
    internal X509Certificate2 MintUnchecked(string host) => Mint(host.ToLowerInvariant());

    X509Certificate2 Mint(string host)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=" + host, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(root, true, false));
        var serial = RandomNumberGenerator.GetBytes(16); serial[0] &= 0x7F;
        using var signed = req.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(30), serial);
        using var withKey = signed.CopyWithPrivateKey(key);
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, ""), "", X509KeyStorageFlags.Exportable);
    }

    public void Dispose()
    {
        foreach (var c in leaves.Values) c.Dispose();
        root.Dispose();
    }
}
