using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Piper.Core.Security;

/// <summary>
/// Explicit, user-initiated management of the Piper root in the current user's
/// Trusted Root store.
/// </summary>
/// <remarks>
/// Trusting this root means any process holding the matching private key - which sits
/// unencrypted-by-user-password in the local profile - can impersonate any TLS site to
/// this account. Piper calls <see cref="Install"/> only after the user confirms either
/// the startup prompt or the explicit configuration action; <see cref="Uninstall"/> is
/// offered alongside it so the change is easy to reverse.
/// </remarks>
public static class TrustStore
{
    /// <summary>True when a certificate with this thumbprint is trusted by the user or machine root store.</summary>
    public static bool IsTrusted(X509Certificate2 certificate)
    {
        return IsTrusted(certificate, StoreLocation.CurrentUser)
            || IsTrusted(certificate, StoreLocation.LocalMachine);
    }

    private static bool IsTrusted(X509Certificate2 certificate, StoreLocation location)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, location);
            store.Open(OpenFlags.ReadOnly);
            return store.Certificates.Any(c => c.Thumbprint == certificate.Thumbprint);
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException)
        {
            // Some locked-down environments deny access to the machine store. A trusted
            // current-user root remains sufficient for Piper, so treat that store as unavailable.
            return false;
        }
    }

    /// <summary>
    /// Adds the root to the current user's Trusted Root store. Windows shows its own
    /// security warning dialog. Call only in response to a direct user command.
    /// </summary>
    public static void Install(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        // A root's displayed subject cannot be changed in place. When the user
        // explicitly trusts Piper's replacement root, retire legacy Piper roots at
        // the same time so the Windows Certificates list has one clear entry.
        var legacyRoots = store.Certificates
            .Where(c => c.Thumbprint != certificate.Thumbprint
                && c.Subject.Contains("Piper Root CA", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var legacy in legacyRoots) store.Remove(legacy);

        // Store only the public certificate - the private key stays in the profile PFX.
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        store.Add(publicOnly);
    }

    /// <summary>Removes every Piper root previously added to the current user's store.</summary>
    public static int Uninstall()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var stale = store.Certificates
            // Include the old name so upgrading Piper does not leave an obsolete
            // interception root trusted in the user's store.
            .Where(c => c.Subject.Contains(CertificateAuthority.RootCommonName, StringComparison.OrdinalIgnoreCase)
                || c.Subject.Contains("Piper Root CA", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var cert in stale) store.Remove(cert);
        return stale.Count;
    }
}
