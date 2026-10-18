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
    public static bool IsTrusted(X509Certificate2 certificate) =>
        IsTrusted(certificate.Thumbprint, static location => new RootStoreSource(location));

    /// <summary>A root store as <see cref="IsTrusted(string, Func{StoreLocation, IRootCertificateSource})"/> reads it.</summary>
    internal interface IRootCertificateSource : IDisposable
    {
        /// <summary>Every certificate in the store. Each call returns fresh copies the caller must dispose.</summary>
        X509Certificate2Collection Certificates { get; }
    }

    internal static bool IsTrusted(string thumbprint, Func<StoreLocation, IRootCertificateSource> open) =>
        IsTrusted(thumbprint, StoreLocation.CurrentUser, open) || IsTrusted(thumbprint, StoreLocation.LocalMachine, open);

    private static bool IsTrusted(string thumbprint, StoreLocation location, Func<StoreLocation, IRootCertificateSource> open)
    {
        try
        {
            using var store = open(location);

            // X509Store.Certificates and Find both hand back new certificate objects that own native
            // handles. Reading them without disposing leaked them on every call, and the status bar
            // makes this call repeatedly.
            var all = store.Certificates;
            X509Certificate2Collection? found = null;
            try
            {
                found = all.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                return found.Count > 0;
            }
            finally
            {
                DisposeAll(found);
                DisposeAll(all);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or UnauthorizedAccessException)
        {
            // Some locked-down environments deny access to the machine store. A trusted
            // current-user root remains sufficient for Piper, so treat that store as unavailable.
            return false;
        }
    }

    private static void DisposeAll(IEnumerable<X509Certificate2>? certificates)
    {
        if (certificates is null) return;
        foreach (var certificate in certificates) certificate.Dispose();
    }

    private sealed class RootStoreSource : IRootCertificateSource
    {
        private readonly X509Store _store;

        public RootStoreSource(StoreLocation location)
        {
            _store = new X509Store(StoreName.Root, location);
            try { _store.Open(OpenFlags.ReadOnly); }
            catch { _store.Dispose(); throw; }
        }

        public X509Certificate2Collection Certificates => _store.Certificates;

        public void Dispose() => _store.Dispose();
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
        var all = store.Certificates;
        try
        {
            foreach (var legacy in all.Where(c => c.Thumbprint != certificate.Thumbprint
                && c.Subject.Contains("Piper Root CA", StringComparison.OrdinalIgnoreCase)))
                store.Remove(legacy);
        }
        finally
        {
            DisposeAll(all);
        }

        // Store only the public certificate - the private key stays in the profile PFX.
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.RawData);
        store.Add(publicOnly);
    }

    /// <summary>Removes every Piper root previously added to the current user's store.</summary>
    public static int Uninstall()
    {
        using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var all = store.Certificates;
        try
        {
            var removed = 0;
            foreach (var cert in all.Where(c =>
                // Include the old name so upgrading Piper does not leave an obsolete
                // interception root trusted in the user's store.
                c.Subject.Contains(CertificateAuthority.RootCommonName, StringComparison.OrdinalIgnoreCase)
                || c.Subject.Contains("Piper Root CA", StringComparison.OrdinalIgnoreCase)))
            {
                store.Remove(cert);
                removed++;
            }

            return removed;
        }
        finally
        {
            DisposeAll(all);
        }
    }
}
