using System.Security.Cryptography.X509Certificates;

namespace Piper.Core.Security;

/// <summary>
/// Explicit, user-initiated management of the Piper root in the current user's
/// Trusted Root store.
/// </summary>
/// <remarks>
/// Trusting this root means any process holding the matching private key - which sits
/// unencrypted-by-user-password in the local profile - can impersonate any TLS site to
/// this account. Nothing in Piper calls <see cref="Install"/> implicitly; it is wired
/// only to a menu command behind a confirmation prompt, and <see cref="Uninstall"/> is
/// offered alongside it so the change is easy to reverse.
/// </remarks>
public static class TrustStore
{
    /// <summary>True when a certificate with this thumbprint is already trusted by the current user.</summary>
    public static bool IsTrusted(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);
        return store.Certificates.Any(c => c.Thumbprint == certificate.Thumbprint);
    }

    /// <summary>
    /// Adds the root to the current user's Trusted Root store. Windows shows its own
    /// security warning dialog. Call only in response to a direct user command.
    /// </summary>
    public static void Install(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
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
            .Where(c => c.Subject.Contains("Piper Root CA", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var cert in stale) store.Remove(cert);
        return stale.Count;
    }
}
