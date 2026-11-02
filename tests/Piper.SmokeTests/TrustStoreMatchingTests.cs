using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Piper.Core.Security;

// S12: which certificates Install and Uninstall treat as Piper's. The Windows store is never opened:
// the pure selection both of them use is checked against certificates made here.
internal static class TrustStoreMatchingTests
{
    private const string CurrentSubject = $"CN={CertificateAuthority.RootCommonName}, O=Piper, OU=Generated locally";
    private const string OldSubject = "CN=Piper Root CA, O=Piper, OU=Generated locally - do not trust elsewhere";

    public static Task RunAsync(TestRunner runner) => runner.RunAsync("trust store cleanup matches Piper roots exactly and never a look-alike", () =>
    {
        using var current = NewRoot(CurrentSubject);
        using var stale = NewRoot(CurrentSubject);
        using var old = NewRoot(OldSubject);
        using var lookAlike = NewRoot("CN=Piper Root CA - DO NOT TRUST, O=Piper, OU=Generated locally, OU=Someone else");
        using var substring = NewRoot("CN=Not the Piper Root CA, O=Contoso");
        using var oldSubstring = NewRoot("CN=Piper Root CA, O=Contoso");
        using var issuedByOther = NewIssued(CurrentSubject, substring);

        var store = new[] { current, stale, old, lookAlike, substring, oldSubstring, issuedByOther };

        var onInstall = TrustStore.SelectPiperRoots(store, current, includeCurrent: false).Select(c => c.Thumbprint).ToHashSet();
        runner.IsTrue(onInstall.SetEquals([stale.Thumbprint, old.Thumbprint]),
            "installing retires only earlier Piper roots with the exact Piper name");

        var onRemove = TrustStore.SelectPiperRoots(store, current, includeCurrent: true).Select(c => c.Thumbprint).ToHashSet();
        runner.IsTrue(onRemove.SetEquals([current.Thumbprint, stale.Thumbprint, old.Thumbprint]),
            "removing takes this root (by thumbprint) and earlier Piper roots, nothing else");

        runner.IsTrue(!onRemove.Contains(lookAlike.Thumbprint) && !onRemove.Contains(substring.Thumbprint)
            && !onRemove.Contains(oldSubstring.Thumbprint), "a foreign root whose name contains Piper's is left alone");
        runner.IsTrue(!onRemove.Contains(issuedByOther.Thumbprint), "a certificate with Piper's name issued by someone else is left alone");
        runner.AreEqual(CurrentSubject, CertificateAuthority.RootSubject, "the subject matched is the one Piper issues");
        runner.AreEqual(OldSubject, CertificateAuthority.LegacyRootSubject, "and the one earlier versions issued");
        return Task.CompletedTask;
    });

    private static X509Certificate2 NewRoot(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 NewIssued(string subject, X509Certificate2 issuer)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddMonths(6), [1, 2, 3, 4]);
    }
}
