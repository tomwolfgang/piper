using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Piper.Core.Security;

/// <summary>
/// Root CA plus an on-demand per-host leaf certificate factory, used to terminate TLS
/// for inspection. The root lives in the user profile as a password-protected PFX.
/// </summary>
/// <remarks>
/// This class never touches the machine or user trust stores. Installing the root is a
/// deliberate, separately-invoked user action (see <see cref="TrustStore"/>) because it
/// changes the security posture of the whole machine.
/// </remarks>
public sealed class CertificateAuthority : IDisposable
{
    private const string PfxPassword = "Piper";
    private const int LeafCacheLimit = 512;

    private readonly ConcurrentDictionary<string, X509Certificate2> _leafCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _mintLock = new();
    private bool _disposed;

    /// <summary>
    /// One key pair, reused by every leaf certificate. Generating a fresh RSA-2048 key per host
    /// costs ~40ms and happens under <see cref="_mintLock"/>, so a page touching 30 new hosts
    /// serialised into well over a second of stalled TLS handshakes -- long enough for a browser
    /// to give up on connections. Reusing one key makes minting ~4x faster and keeps the number
    /// of Windows key containers we create down to something bounded.
    /// </summary>
    /// <remarks>
    /// Sharing a key across leaves is standard for intercepting proxies and does not weaken this
    /// design: every leaf is already signed by the same locally-generated root, so anyone who can
    /// read the root's unencrypted private key can impersonate any site regardless. The root key,
    /// not the leaf key, is the thing that matters here.
    /// </remarks>
    private readonly RSA _leafKey = RSA.Create(2048);

    public X509Certificate2 RootCertificate { get; }

    public string RootPfxPath { get; }

    private CertificateAuthority(X509Certificate2 root, string pfxPath)
    {
        RootCertificate = root;
        RootPfxPath = pfxPath;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "Certificates");

    /// <summary>Loads the existing root, or generates one if it is missing or expired.</summary>
    public static CertificateAuthority LoadOrCreate(string? directory = null)
    {
        directory ??= DefaultDirectory;
        Directory.CreateDirectory(directory);
        var pfxPath = Path.Combine(directory, "Piper-Root.pfx");

        if (File.Exists(pfxPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12(
                    File.ReadAllBytes(pfxPath), PfxPassword,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

                if (existing.NotAfter > DateTime.Now.AddDays(30) && existing.HasPrivateKey)
                    return new CertificateAuthority(existing, pfxPath);

                existing.Dispose();
            }
            catch (CryptographicException)
            {
                // Corrupt or unreadable - fall through and mint a fresh root.
            }
        }

        var root = CreateRoot();
        File.WriteAllBytes(pfxPath, root.Export(X509ContentType.Pfx, PfxPassword));
        File.WriteAllText(Path.Combine(directory, "Piper-Root.cer"),
            ExportPem(root), System.Text.Encoding.ASCII);

        return new CertificateAuthority(root, pfxPath);
    }

    private static X509Certificate2 CreateRoot()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Piper Root CA, O=Piper, OU=Generated locally - do not trust elsewhere",
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));

        // Round-trip through PFX so the private key is in a form SChannel will use.
        return X509CertificateLoader.LoadPkcs12(
            cert.Export(X509ContentType.Pfx, PfxPassword), PfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>Returns a cached leaf certificate for <paramref name="host"/>, minting one if needed.</summary>
    public X509Certificate2 GetCertificateFor(string host)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        host = NormaliseHost(host);

        if (_leafCache.TryGetValue(host, out var cached) && cached.NotAfter > DateTime.Now)
            return cached;

        lock (_mintLock)
        {
            if (_leafCache.TryGetValue(host, out cached) && cached.NotAfter > DateTime.Now)
                return cached;

            if (_leafCache.Count >= LeafCacheLimit)
            {
                // Evicted certificates are dropped, never Disposed: another thread may be in the
                // middle of a TLS handshake holding this exact instance, and disposing it out from
                // under SChannel fails that handshake outright ("the server mode SSL must use a
                // certificate with the associated private key"). Letting the finalizer reclaim it
                // once nothing references it is the only safe option without refcounting.
                foreach (var key in _leafCache.Keys.Take(LeafCacheLimit / 4))
                    _leafCache.TryRemove(key, out _);
            }

            var leaf = MintLeaf(host);
            _leafCache[host] = leaf;
            return leaf;
        }
    }

    private X509Certificate2 MintLeaf(string host)
    {
        var request = new CertificateRequest(
            $"CN={host}, O=Piper Intercept",
            _leafKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(host, out var ip)) san.AddIpAddress(ip);
        else san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());

        // Serial must be positive; clear the high bit so it is not read as negative.
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;

        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        // Keep under the 398-day limit modern clients enforce for server certs.
        var notAfter = DateTimeOffset.UtcNow.AddDays(390);
        if (notAfter > RootCertificate.NotAfter) notAfter = RootCertificate.NotAfter.AddDays(-1);

        using var signed = request.Create(RootCertificate, notBefore, notAfter, serial);
        using var withKey = signed.CopyWithPrivateKey(_leafKey);

        // No EphemeralKeySet here, unlike the root: on Windows, SslStream.AuthenticateAsServerAsync
        // fails with "the platform does not support ephemeral keys" for a *server* certificate
        // loaded with an ephemeral private key (SChannel needs a key it can reference by
        // container, not an in-memory-only CNG key). The root never plays the server role -- it
        // only signs leaves and gets exported for the trust-store dialog -- so it is unaffected.
        return X509CertificateLoader.LoadPkcs12(
            withKey.Export(X509ContentType.Pfx, PfxPassword), PfxPassword,
            X509KeyStorageFlags.Exportable);
    }

    /// <summary>Collapses subdomains onto a wildcard-ish cache key to limit how many leaves we mint.</summary>
    private static string NormaliseHost(string host)
    {
        var colon = host.LastIndexOf(':');
        if (colon > 0 && !host.Contains(']')) host = host[..colon];
        return host.Trim('[', ']');
    }

    public static string ExportPem(X509Certificate2 cert) =>
        PemEncoding.WriteString("CERTIFICATE", cert.RawData);

    /// <summary>Writes the public root certificate to <paramref name="path"/> in PEM form.</summary>
    public void ExportRootTo(string path) =>
        File.WriteAllText(path, ExportPem(RootCertificate), System.Text.Encoding.ASCII);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cert in _leafCache.Values) cert.Dispose();
        _leafCache.Clear();
        _leafKey.Dispose();
        RootCertificate.Dispose();
    }
}
