using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Piper.Core.Security;

// S12: how the root CA's private key is kept on disk, migrated from the old constant-password PFX, and
// what happens when it cannot be read. Every file lives in a fresh folder under the per-run temp folder
// (never %LOCALAPPDATA%\Piper), and the Windows trust store is never opened: the store matching is
// checked through the pure selection that Install and Uninstall use.
internal static class RootKeyAtRestTests
{
    private const string LegacyPassword = "Piper";
    private const string CurrentSubject = $"CN={CertificateAuthority.RootCommonName}, O=Piper, OU=Generated locally";
    private const string OldSubject = "CN=Piper Root CA, O=Piper, OU=Generated locally - do not trust elsewhere";

    public static async Task RunAsync(TestRunner runner)
    {
        await RunFreshRootAsync(runner);
        await RunMigrationAsync(runner);
        await RunInterruptedMigrationAsync(runner);
        await RunAfterDowngradeAsync(runner);
        await RunNotOpenableWithOldPasswordAsync(runner);
        await RunTruncatedLegacyAsync(runner);
        await RunTruncatedProtectedAsync(runner);
        await RunMalformedSizesAsync(runner);
        await RunUnreadableLegacyBesideGoodKeyAsync(runner);
        await RunRenamedRootNoticeAsync(runner);
        await RunPublicCertificateRepairAsync(runner);
        await RunStoreMatchingAsync(runner);
    }

    private static Task RunFreshRootAsync(TestRunner runner) => runner.RunAsync("a new root key is DPAPI-protected, user-only, and written with its public certificate", () =>
    {
        var directory = NewDirectory();
        try
        {
            using var ca = CertificateAuthority.LoadOrCreate(directory);
            runner.IsTrue(ca.Replacement is null, "a first run is not reported as a replaced root");
            runner.AreEqual(KeyPath(directory), ca.RootPfxPath, "the key path points at the protected file");
            runner.IsTrue(File.Exists(KeyPath(directory)), "the protected key file exists");
            runner.IsTrue(!File.Exists(LegacyPath(directory)), "no constant-password PFX is written");
            runner.AreEqual(CertificateAuthority.ExportPem(ca.RootCertificate), File.ReadAllText(CerPath(directory)),
                "the public certificate matches the root");
            runner.IsTrue(Directory.GetFiles(directory).All(f => !f.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)),
                "no temporary file is left behind");

            var exported = true;
            try { _ = ca.RootCertificate.GetRSAPrivateKey()!.ExportParameters(includePrivateParameters: true); }
            catch (CryptographicException) { exported = false; }
            runner.IsTrue(!exported, "the loaded root key cannot be exported again");

            AssertLeafChainsTo(runner, ca);
            AssertUserOnly(runner, directory);
        }
        finally
        {
            Delete(directory);
        }

        return Task.CompletedTask;
    });

    private static Task RunMigrationAsync(TestRunner runner) => runner.RunAsync("an old constant-password root is migrated and keeps its thumbprint", () =>
    {
        var directory = NewDirectory();
        try
        {
            using var legacy = NewRoot(CurrentSubject);
            File.WriteAllBytes(LegacyPath(directory), legacy.Export(X509ContentType.Pfx, LegacyPassword));

            using (var ca = CertificateAuthority.LoadOrCreate(directory))
            {
                runner.AreEqual(legacy.Thumbprint, ca.RootCertificate.Thumbprint, "the trusted root survives the upgrade");
                runner.IsTrue(ca.Replacement is null, "a migration is not a replacement");
                runner.IsTrue(!File.Exists(LegacyPath(directory)), "the weakly protected PFX is removed");
                runner.IsTrue(File.Exists(KeyPath(directory)), "the protected key file was written");
                runner.AreEqual(CertificateAuthority.ExportPem(legacy), File.ReadAllText(CerPath(directory)),
                    "the public certificate is the same root");
                AssertLeafChainsTo(runner, ca);
            }

            using var again = CertificateAuthority.LoadOrCreate(directory);
            runner.AreEqual(legacy.Thumbprint, again.RootCertificate.Thumbprint, "the next run loads the same root from the protected file");
            runner.IsTrue(again.Replacement is null, "and reports nothing");
            AssertUserOnly(runner, directory);
        }
        finally
        {
            Delete(directory);
        }

        return Task.CompletedTask;
    });

    private static Task RunInterruptedMigrationAsync(TestRunner runner) => runner.RunAsync("a migration interrupted before the old file was removed finishes on the next run", () =>
    {
        var directory = NewDirectory();
        try
        {
            using var legacy = NewRoot(CurrentSubject);
            var legacyBytes = legacy.Export(X509ContentType.Pfx, LegacyPassword);
            File.WriteAllBytes(LegacyPath(directory), legacyBytes);
            using (CertificateAuthority.LoadOrCreate(directory)) { }

            // The state a crash between "protected file in place" and "old file deleted" leaves behind,
            // plus a stray temporary file from an earlier interrupted write.
            File.WriteAllBytes(LegacyPath(directory), legacyBytes);
            File.WriteAllText(KeyPath(directory) + ".tmp", "partial");

            using var ca = CertificateAuthority.LoadOrCreate(directory);
            runner.AreEqual(legacy.Thumbprint, ca.RootCertificate.Thumbprint, "the same root is loaded");
            runner.IsTrue(ca.Replacement is null, "nothing is reported");
            runner.IsTrue(!File.Exists(LegacyPath(directory)), "the old file is removed this time");
        }
        finally
        {
            Delete(directory);
        }

        return Task.CompletedTask;
    });

    private static Task RunAfterDowngradeAsync(TestRunner runner) => runner.RunAsync("after a downgrade made its own root, upgrading again keeps that root and the older key aside", () =>
    {
        var directory = NewDirectory();
        try
        {
            string upgraded;
            using (var first = CertificateAuthority.LoadOrCreate(directory)) upgraded = first.RootCertificate.Thumbprint;

            // An older Piper cannot read the protected key, so it writes a constant-password root of its own.
            using var downgraded = NewRoot(CurrentSubject);
            File.WriteAllBytes(LegacyPath(directory), downgraded.Export(X509ContentType.Pfx, LegacyPassword));

            using var ca = CertificateAuthority.LoadOrCreate(directory);
            runner.AreEqual(downgraded.Thumbprint, ca.RootCertificate.Thumbprint, "the root the older Piper made (and had trusted) is kept");
            runner.IsTrue(ca.Replacement is null, "it is migrated, not replaced");
            runner.IsTrue(!File.Exists(LegacyPath(directory)), "its weakly protected file is removed");
            var superseded = Directory.GetFiles(directory, CertificateAuthority.ProtectedKeyFileName + ".superseded-*");
            runner.AreEqual(1, superseded.Length, "the earlier protected key is kept aside, not overwritten");
            runner.IsTrue(upgraded != downgraded.Thumbprint, "(the two roots differ)");
        }
        finally
        {
            Delete(directory);
        }

        return Task.CompletedTask;
    });

    private static Task RunNotOpenableWithOldPasswordAsync(TestRunner runner) => runner.RunAsync("the protected key file cannot be opened as a PFX, with or without the old password", () =>
    {
        var directory = NewDirectory();
        try
        {
            using (CertificateAuthority.LoadOrCreate(directory)) { }
            var bytes = File.ReadAllBytes(KeyPath(directory));

            foreach (var password in new[] { LegacyPassword, null, string.Empty })
            {
                var opened = true;
                try { using var _ = X509CertificateLoader.LoadPkcs12(bytes, password); }
                catch (CryptographicException) { opened = false; }
                runner.IsTrue(!opened, $"the file does not open as a PFX with password '{password ?? "(none)"}'");
            }
        }
        finally
        {
            Delete(directory);
        }

        return Task.CompletedTask;
    });

    private static Task RunTruncatedLegacyAsync(TestRunner runner) => runner.RunAsync("a truncated old root is kept aside and its replacement is reported, never silent", () =>
    {
        var directory = NewDirectory();
        try
        {
            using var legacy = NewRoot(CurrentSubject);
            var full = legacy.Export(X509ContentType.Pfx, LegacyPassword);
            var truncated = full[..(full.Length / 2)];
            File.WriteAllBytes(LegacyPath(directory), truncated);

            using var ca = CertificateAuthority.LoadOrCreate(directory);
            runner.IsTrue(ca.RootCertificate.Thumbprint != legacy.Thumbprint, "a new root had to be made");
            AssertUnreadableKeptAside(runner, ca, directory, truncated);
        }
        finally
        {
            Delete(directory);
        }

        return Task.CompletedTask;
    });

    private static Task RunTruncatedProtectedAsync(TestRunner runner) => runner.RunAsync("a truncated protected key is kept aside and its replacement is reported", () =>
    {
        var directory = NewDirectory();
        try
        {
            string before;
            using (var first = CertificateAuthority.LoadOrCreate(directory)) before = first.RootCertificate.Thumbprint;
            var full = File.ReadAllBytes(KeyPath(directory));
            var truncated = full[..(full.Length - 7)];
            File.WriteAllBytes(KeyPath(directory), truncated);

            using var ca = CertificateAuthority.LoadOrCreate(directory);
            runner.IsTrue(ca.RootCertificate.Thumbprint != before, "a new root had to be made");
            AssertUnreadableKeptAside(runner, ca, directory, truncated);

            using var next = CertificateAuthority.LoadOrCreate(directory);
            runner.AreEqual(ca.RootCertificate.Thumbprint, next.RootCertificate.Thumbprint, "the replacement is kept from then on");
            runner.IsTrue(next.Replacement is null, "and reported only once");
        }
        finally
        {
            Delete(directory);
        }

        return Task.CompletedTask;
    });

    private static Task RunMalformedSizesAsync(TestRunner runner) => runner.RunAsync("empty, garbage and oversized key files are unreadable roots, kept aside", () =>
    {
        var cases = new (string Name, bool Legacy, byte[] Bytes)[]
        {
            ("empty old file", true, []),
            ("empty protected file", false, []),
            ("garbage protected file", false, RandomNumberGenerator.GetBytes(300)),
            ("oversized protected file", false, new byte[CertificateAuthority.MaxKeyFileBytes + 1]),
            ("oversized old file", true, new byte[CertificateAuthority.MaxKeyFileBytes + 1]),
        };

        foreach (var (name, isLegacy, bytes) in cases)
        {
            var directory = NewDirectory();
            try
            {
                File.WriteAllBytes(isLegacy ? LegacyPath(directory) : KeyPath(directory), bytes);
                using var ca = CertificateAuthority.LoadOrCreate(directory);
                runner.IsTrue(ca.Replacement is { Reason: RootReplacementReason.Unreadable }, $"{name}: reported as unreadable");
                runner.AreEqual(1, ca.Replacement?.SetAsidePaths.Count ?? 0, $"{name}: one file kept aside");
                runner.IsTrue(ca.Replacement is { SetAsidePaths: [var aside] } && File.ReadAllBytes(aside).AsSpan().SequenceEqual(bytes),
                    $"{name}: the kept file holds the original bytes");
            }
            finally
            {
                Delete(directory);
            }
        }

        return Task.CompletedTask;
    });

    private static Task RunUnreadableLegacyBesideGoodKeyAsync(TestRunner runner) => runner.RunAsync("a damaged old file next to a good protected key does not replace the root", () =>
    {
        var directory = NewDirectory();
        try
        {
            string before;
            using (var first = CertificateAuthority.LoadOrCreate(directory)) before = first.RootCertificate.Thumbprint;
            File.WriteAllText(LegacyPath(directory), "not a pfx");

            using var ca = CertificateAuthority.LoadOrCreate(directory);
            runner.AreEqual(before, ca.RootCertificate.Thumbprint, "the protected root is still used");
            runner.IsTrue(ca.Replacement is null, "nothing was replaced, so nothing is reported");
            runner.IsTrue(!File.Exists(LegacyPath(directory)), "the damaged file is moved out of the way");
            runner.IsTrue(Directory.GetFiles(directory, "Piper-Root.pfx.corrupt-*").Length == 1, "and kept aside");
        }
        finally
        {
            Delete(directory);
        }

        return Task.CompletedTask;
    });

    private static Task RunRenamedRootNoticeAsync(TestRunner runner) => runner.RunAsync("rotating a root made under the old name is reported as needing trust again", () =>
    {
        var directory = NewDirectory();
        try
        {
            using var old = NewRoot(OldSubject);
            File.WriteAllBytes(LegacyPath(directory), old.Export(X509ContentType.Pfx, LegacyPassword));

            using var ca = CertificateAuthority.LoadOrCreate(directory);
            runner.IsTrue(ca.RootCertificate.Thumbprint != old.Thumbprint, "the old-name root is replaced");
            runner.IsTrue(ca.Replacement is { Reason: RootReplacementReason.Replaced, SetAsidePaths.Count: 0 }, "and the replacement is reported");
            runner.IsTrue(!File.Exists(LegacyPath(directory)), "the obsolete weakly protected key is not kept");
        }
        finally
        {
            Delete(directory);
        }

        return Task.CompletedTask;
    });

    private static Task RunPublicCertificateRepairAsync(TestRunner runner) => runner.RunAsync("a missing or stale public certificate file is rewritten from the root", () =>
    {
        var directory = NewDirectory();
        try
        {
            string pem;
            using (var first = CertificateAuthority.LoadOrCreate(directory)) pem = CertificateAuthority.ExportPem(first.RootCertificate);

            File.Delete(CerPath(directory));
            using (CertificateAuthority.LoadOrCreate(directory)) { }
            runner.AreEqual(pem, File.ReadAllText(CerPath(directory)), "a deleted .cer comes back");

            using (var other = NewRoot(CurrentSubject)) File.WriteAllText(CerPath(directory), CertificateAuthority.ExportPem(other));
            using (CertificateAuthority.LoadOrCreate(directory)) { }
            runner.AreEqual(pem, File.ReadAllText(CerPath(directory)), "a .cer for another root is corrected");

            File.WriteAllBytes(CerPath(directory), new byte[CertificateAuthority.MaxKeyFileBytes + 1]);
            using (CertificateAuthority.LoadOrCreate(directory)) { }
            runner.AreEqual(pem, File.ReadAllText(CerPath(directory)), "an oversized .cer is replaced without being read whole");
        }
        finally
        {
            Delete(directory);
        }

        return Task.CompletedTask;
    });

    private static Task RunStoreMatchingAsync(TestRunner runner) => runner.RunAsync("trust store cleanup matches Piper roots exactly and never a look-alike", () =>
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
        return Task.CompletedTask;
    });

    private static void AssertUnreadableKeptAside(TestRunner runner, CertificateAuthority ca, string directory, byte[] original)
    {
        runner.IsTrue(ca.Replacement is { Reason: RootReplacementReason.Unreadable }, "the replacement is reported as an unreadable root");
        runner.IsTrue(ca.Replacement is { SetAsidePaths: [var aside] }
            && Path.GetDirectoryName(aside) == directory
            && Path.GetFileName(aside).Contains(".corrupt-", StringComparison.Ordinal)
            && File.ReadAllBytes(aside).AsSpan().SequenceEqual(original),
            "the unreadable file is kept aside, byte for byte, not deleted");
        runner.IsTrue(File.Exists(KeyPath(directory)), "the new root is saved");
        runner.AreEqual(CertificateAuthority.ExportPem(ca.RootCertificate), File.ReadAllText(CerPath(directory)),
            "and its public certificate");
    }

    private static void AssertLeafChainsTo(TestRunner runner, CertificateAuthority ca)
    {
        var leaf = ca.GetCertificateFor("example.test");
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca.RootCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        runner.IsTrue(chain.Build(leaf), "a leaf signed by the loaded root key chains to it");
    }

    private static void AssertUserOnly(TestRunner runner, string directory)
    {
        if (OperatingSystem.IsWindows()) AssertUserOnlyOnWindows(runner, directory);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void AssertUserOnlyOnWindows(TestRunner runner, string directory)
    {
        var security = new DirectoryInfo(directory).GetAccessControl();
        var me = WindowsIdentity.GetCurrent().User!;
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        runner.IsTrue(security.AreAccessRulesProtected, "the folder does not inherit its parent's permissions");
        runner.IsTrue(rules.Count > 0 && rules.All(r => me.Equals(r.IdentityReference)), "only the current user has access to the folder");

        foreach (var file in Directory.GetFiles(directory))
        {
            var fileRules = new FileInfo(file).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToList();
            runner.IsTrue(fileRules.Count > 0 && fileRules.All(r => me.Equals(r.IdentityReference)),
                $"only the current user has access to {Path.GetFileName(file)}");
        }
    }

    private static X509Certificate2 NewRoot(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx, LegacyPassword), LegacyPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    private static X509Certificate2 NewIssued(string subject, X509Certificate2 issuer)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), [1, 2, 3, 4]);
    }

    private static string NewDirectory() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"Piper-SmokeTest-RootKey-{Guid.NewGuid():N}")).FullName;

    private static string KeyPath(string directory) => Path.Combine(directory, CertificateAuthority.ProtectedKeyFileName);

    private static string LegacyPath(string directory) => Path.Combine(directory, "Piper-Root.pfx");

    private static string CerPath(string directory) => Path.Combine(directory, "Piper-Root.cer");

    private static void Delete(string directory)
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}
