using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Piper.Core.Security;

/// <summary>
/// Root CA plus an on-demand per-host leaf certificate factory, used to terminate TLS
/// for inspection. The root's key lives in the user profile, protected with DPAPI for the
/// current Windows user (see <see cref="RootKeyStore"/>).
/// </summary>
/// <remarks>
/// This class never touches the machine or user trust stores. Installing the root is a
/// deliberate, separately-invoked user action (see <see cref="TrustStore"/>) because it
/// changes the security posture of the whole machine.
/// </remarks>
public sealed class CertificateAuthority : IDisposable
{
    /// <summary>
    /// Only for the in-memory round trip that gives a freshly signed leaf a key SChannel can use.
    /// Nothing protected by it is ever written to disk.
    /// </summary>
    private const string InMemoryPfxPassword = "Piper";
    private const int LeafCacheLimit = 512;

    /// <summary>The file holding the DPAPI-protected root key, in the certificates folder.</summary>
    public const string ProtectedKeyFileName = RootKeyStore.ProtectedKeyFileName;

    /// <inheritdoc cref="RootKeyStore.MaxKeyFileBytes"/>
    internal const int MaxKeyFileBytes = RootKeyStore.MaxKeyFileBytes;

    /// <summary>
    /// The name shown in the Windows Certificates list for Piper's interception root.
    /// It deliberately makes clear that this is a locally generated trust anchor, not
    /// a certificate issued by a public CA.
    /// </summary>
    public const string RootCommonName = "Piper Root CA - DO NOT TRUST";

    /// <summary>The full subject of the root this version makes.</summary>
    internal const string RootSubject = $"CN={RootCommonName}, O=Piper, OU=Generated locally";

    /// <summary>The subject of the roots Piper made before <see cref="RootCommonName"/> was introduced.</summary>
    internal const string LegacyRootSubject = "CN=Piper Root CA, O=Piper, OU=Generated locally - do not trust elsewhere";

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

    /// <summary>The DPAPI-protected file holding the root's private key (the name predates the format).</summary>
    public string RootPfxPath { get; }

    /// <summary>
    /// Set when a saved root existed but a new one had to be made this run. The user must be told:
    /// anything that trusted the old root rejects Piper's certificates until the new one is trusted.
    /// </summary>
    public RootReplacement? Replacement { get; }

    /// <summary>Why the certificates folder could not be restricted to the current user, or null when it was.</summary>
    public string? FolderRestrictionError { get; }

    private CertificateAuthority(X509Certificate2 root, string pfxPath, RootReplacement? replacement, string? folderRestrictionError)
    {
        RootCertificate = root;
        RootPfxPath = pfxPath;
        Replacement = replacement;
        FolderRestrictionError = folderRestrictionError;
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "Certificates");

    /// <summary>
    /// Loads the saved root (migrating one saved by an older Piper without changing it), or makes one
    /// if none is saved or the saved one is unreadable, expiring or under an old name. A replaced
    /// root is reported through <see cref="Replacement"/>; an unreadable key file is kept aside.
    /// </summary>
    public static CertificateAuthority LoadOrCreate(string? directory = null)
    {
        directory ??= DefaultDirectory;

        // Root subjects are immutable. Roots made by earlier Piper versions under another name are
        // replaced so the trusted-root list identifies this local MITM CA unambiguously; the new root
        // still requires an explicit Trust action.
        var root = RootKeyStore.LoadOrCreate(directory,
            existing => existing.NotAfter > DateTime.Now.AddDays(30)
                && existing.HasPrivateKey
                && existing.GetNameInfo(X509NameType.SimpleName, forIssuer: false) == RootCommonName,
            CreateRootPfx, out var replacement, out var restrictionError);

        return new CertificateAuthority(root, Path.Combine(directory, RootKeyStore.ProtectedKeyFileName), replacement, restrictionError);
    }

    /// <summary>A new self-signed root and its key as an unencrypted PKCS#12 blob, for the caller to protect and zero.</summary>
    private static byte[] CreateRootPfx()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            RootSubject,
            rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        var now = DateTimeOffset.UtcNow;
        using var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(5));
        return cert.Export(X509ContentType.Pfx);
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
            withKey.Export(X509ContentType.Pfx, InMemoryPfxPassword), InMemoryPfxPassword,
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
