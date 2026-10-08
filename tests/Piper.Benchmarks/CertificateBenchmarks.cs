using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BenchmarkDotNet.Attributes;
using Piper.Core.Security;

namespace Piper.Benchmarks;

/// <summary>
/// What minting a leaf certificate for a new host costs. <see cref="PiperMint"/> is the real
/// <see cref="CertificateAuthority.GetCertificateFor"/> (one shared RSA-2048 leaf key); the others
/// rebuild the same certificate with a different key to show what an ECDSA leaf, or a fresh key per
/// host, would cost. The authority lives in a temporary folder that is deleted afterwards: this never
/// touches the user's real one and installs nothing.
/// </summary>
[MemoryDiagnoser]
public class CertificateBenchmarks
{
    private string _directory = "";
    private CertificateAuthority _ca = null!;
    private readonly ECDsa _sharedEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        // A run that was killed leaves its folder (a throwaway root key, Piper-Root.pfx): remove the ones from earlier days.
        foreach (var old in Directory.EnumerateDirectories(Path.GetTempPath(), "piper-benchmarks-????????????????????????????????"))
        {
            try { if (DateTime.UtcNow - Directory.GetCreationTimeUtc(old) > TimeSpan.FromDays(1)) Directory.Delete(old, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use by another run: left for the next one.
            }
        }
        _directory = Path.Combine(Path.GetTempPath(), "piper-benchmarks-" + Guid.NewGuid().ToString("N"));
        _ca = CertificateAuthority.LoadOrCreate(_directory);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _ca.Dispose();
        _sharedEcdsa.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private string NewHost() => $"host{Interlocked.Increment(ref _next)}.example.com"; // never a cache hit

    [Benchmark(Baseline = true)]
    public X509Certificate2 PiperMint() => _ca.GetCertificateFor(NewHost());

    [Benchmark]
    public void EcdsaSharedKey() => Mint(NewHost(), _sharedEcdsa, ownsKey: false);

    [Benchmark]
    public void EcdsaFreshKey() => Mint(NewHost(), ECDsa.Create(ECCurve.NamedCurves.nistP256), ownsKey: true);

    [Benchmark]
    public void RsaFreshKey() => Mint(NewHost(), RSA.Create(2048), ownsKey: true);

    // A fresh key is released even when minting throws.
    private void Mint(string host, AsymmetricAlgorithm key, bool ownsKey)
    {
        try { MintCore(host, key); }
        finally { if (ownsKey) key.Dispose(); }
    }

    /// <summary>The same extensions, signature by the root and PFX round trip as <c>CertificateAuthority.MintLeaf</c>.</summary>
    private void MintCore(string host, AsymmetricAlgorithm key)
    {
        var request = key switch
        {
            RSA rsa => new CertificateRequest($"CN={host}, O=Piper Intercept", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            ECDsa ecdsa => new CertificateRequest($"CN={host}, O=Piper Intercept", ecdsa, HashAlgorithmName.SHA256),
            _ => throw new ArgumentException("Unsupported key type.", nameof(key)),
        };
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());

        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        // The root is RSA whatever the leaf key is, so the signature is made through a generator for it.
        using var rootKey = _ca.RootCertificate.GetRSAPrivateKey()!;
        var rootSigner = X509SignatureGenerator.CreateForRSA(rootKey, RSASignaturePadding.Pkcs1);
        using var signed = request.Create(_ca.RootCertificate.SubjectName, rootSigner, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(390), serial);
        using var withKey = key switch
        {
            RSA rsa => signed.CopyWithPrivateKey(rsa),
            ECDsa ecdsa => signed.CopyWithPrivateKey(ecdsa),
            _ => throw new ArgumentException("Unsupported key type.", nameof(key)),
        };
        using var loaded = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "bench"), "bench", X509KeyStorageFlags.Exportable);
    }
}
