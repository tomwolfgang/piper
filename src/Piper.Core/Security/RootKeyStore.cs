using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;

namespace Piper.Core.Security;

/// <summary>Why <see cref="CertificateAuthority.LoadOrCreate"/> made a new root although one was saved.</summary>
public enum RootReplacementReason
{
    /// <summary>The saved key could not be read (damaged, truncated, or not decryptable by this Windows account).</summary>
    Unreadable,

    /// <summary>The saved root was readable but is expiring, has no key, or was made under an older name.</summary>
    Replaced,
}

/// <summary>
/// A saved root that was replaced by a new one. Anything that trusted the old root no longer
/// accepts Piper's certificates until the user trusts the new root, so this must be shown.
/// </summary>
/// <param name="SetAsidePaths">Unreadable key files that were renamed aside rather than deleted.</param>
public sealed record RootReplacement(RootReplacementReason Reason, IReadOnlyList<string> SetAsidePaths);

/// <summary>
/// The root CA's private key on disk. The key is a PKCS#12 blob wrapped with DPAPI for the current
/// Windows user, written atomically into a folder only that user can open. A root saved by an older
/// Piper (a PFX under the constant password "Piper") is migrated once, keeping its thumbprint so a
/// root the user already trusts keeps working.
/// </summary>
/// <remarks>
/// DPAPI binds the file to this Windows account: a copy taken to another account or machine cannot
/// be decrypted. It does not protect the key from other programs running as the same user; nothing
/// local can, so the trust dialogs keep saying so.
/// </remarks>
internal static class RootKeyStore
{
    internal const string ProtectedKeyFileName = "Piper-Root.key";
    internal const string LegacyPfxFileName = "Piper-Root.pfx";
    internal const string PublicCertificateFileName = "Piper-Root.cer";

    /// <summary>The constant password of the PFX written before S12. Used only to read that file once.</summary>
    private const string LegacyPfxPassword = "Piper";

    /// <summary>
    /// The largest key file read. A 2048-bit root is about 3 KB; anything near this size is not one
    /// of Piper's files, and reading it whole is not worth the memory.
    /// </summary>
    internal const int MaxKeyFileBytes = 64 * 1024;

    /// <summary>Ties the DPAPI blob to this purpose, so a blob some other program protected for the same user is not accepted.</summary>
    private static readonly byte[] Entropy = "Piper root CA private key v1"u8.ToArray();

    private enum Outcome { Missing, Usable, Unreadable, Rejected }

    /// <summary>
    /// Loads the saved root or makes a new one. Throws only for I/O failures (a locked or denied file),
    /// never for a damaged key: that is kept aside and reported through <paramref name="replacement"/>.
    /// </summary>
    internal static X509Certificate2 LoadOrCreate(string directory, Func<X509Certificate2, bool> isUsable,
        Func<byte[]> createRootPfx, out RootReplacement? replacement, out string? restrictionError)
    {
        Directory.CreateDirectory(directory);
        restrictionError = RestrictToCurrentUser(directory);

        var keyPath = Path.Combine(directory, ProtectedKeyFileName);
        var legacyPath = Path.Combine(directory, LegacyPfxFileName);
        var cerPath = Path.Combine(directory, PublicCertificateFileName);
        var setAside = new List<string>();
        var rejected = false;
        replacement = null;

        // The old file goes first. It exists next to a protected key only when a migration was
        // interrupted before the old file was deleted (same key) or when an older Piper ran after an
        // upgrade and minted a root of its own (the one most recently written, and the one that older
        // Piper's Trust action left trusted). Either way it is the right root to keep.
        switch (TryRead(legacyPath, LoadLegacy, isUsable, out var legacy))
        {
            case Outcome.Usable:
                using (legacy)
                    return Migrate(legacy!, keyPath, legacyPath, cerPath, isUsable);
            case Outcome.Unreadable:
                if (SetAside(legacyPath) is { } legacyAside) setAside.Add(legacyAside);
                break;
            case Outcome.Rejected:
                rejected = true;
                break;
        }

        switch (TryRead(keyPath, LoadProtected, isUsable, out var saved))
        {
            case Outcome.Usable:
                return DisposeIfThrows(saved!, loaded =>
                {
                    // A rejected old file beside a good key is obsolete; it is weakly protected, so it goes.
                    if (rejected) File.Delete(legacyPath);
                    EnsurePublicCertificate(cerPath, loaded);
                });
            case Outcome.Unreadable:
                if (SetAside(keyPath) is { } keyAside) setAside.Add(keyAside);
                break;
            case Outcome.Rejected:
                rejected = true;
                break;
        }

        var pfx = createRootPfx();
        try
        {
            var root = DisposeIfThrows(LoadUnprotectedPfx(pfx), created =>
            {
                // The public certificate first: until the key is swapped in, the old key (if any) is still
                // what loads, and a run that dies here simply makes a new root again next time.
                WriteAtomic(cerPath, Encoding.ASCII.GetBytes(CertificateAuthority.ExportPem(created)));
                WriteAtomic(keyPath, Protect(pfx));
                if (rejected && File.Exists(legacyPath)) File.Delete(legacyPath);
            });

            replacement = setAside.Count > 0 ? new RootReplacement(RootReplacementReason.Unreadable, setAside)
                : rejected ? new RootReplacement(RootReplacementReason.Replaced, [])
                : null;
            return root;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>
    /// Re-saves an old constant-password root under DPAPI, verifies the saved file holds the same
    /// root, and only then deletes the old file. Each step leaves a loadable root behind, so a crash
    /// at any point keeps the thumbprint.
    /// </summary>
    private static X509Certificate2 Migrate(X509Certificate2 legacy, string keyPath, string legacyPath, string cerPath,
        Func<X509Certificate2, bool> isUsable)
    {
        var pfx = legacy.Export(X509ContentType.Pfx);
        try
        {
            WriteAtomic(cerPath, Encoding.ASCII.GetBytes(CertificateAuthority.ExportPem(legacy)));

            // A protected key holding a different root is the one an older Piper replaced after a
            // downgrade. Keep it (or an unreadable one) aside rather than overwrite a key file nobody
            // asked to delete.
            switch (TryRead(keyPath, LoadProtected, _ => true, out var existing))
            {
                case Outcome.Usable:
                    var same = existing!.Thumbprint == legacy.Thumbprint;
                    existing.Dispose();
                    if (!same) SetAside(keyPath, "superseded");
                    break;
                case Outcome.Unreadable:
                    SetAside(keyPath);
                    break;
            }

            WriteAtomic(keyPath, Protect(pfx));
            AfterMigrationWriteForTests.Value?.Invoke(keyPath);

            if (TryRead(keyPath, LoadProtected, isUsable, out var saved) == Outcome.Usable && saved!.Thumbprint == legacy.Thumbprint)
                return DisposeIfThrows(saved, _ => File.Delete(legacyPath));

            // The file just written does not read back as the same root. Keep the old file so the
            // migration runs again next time, and use the root it holds.
            saved?.Dispose();
            return LoadUnprotectedPfx(pfx);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>
    /// Test seam: runs between writing the migrated key and reading it back, so a test can make the
    /// read-back fail the way a concurrent writer or a bad disk would. Null outside tests.
    /// </summary>
    internal static readonly AsyncLocal<Action<string>?> AfterMigrationWriteForTests = new();

    /// <summary>Runs <paramref name="then"/> and returns <paramref name="certificate"/>, disposing it if <paramref name="then"/> throws.</summary>
    private static X509Certificate2 DisposeIfThrows(X509Certificate2 certificate, Action<X509Certificate2> then)
    {
        try
        {
            then(certificate);
            return certificate;
        }
        catch
        {
            certificate.Dispose();
            throw;
        }
    }

    private static Outcome TryRead(string path, Func<byte[], X509Certificate2> load, Func<X509Certificate2, bool> isUsable,
        out X509Certificate2? certificate)
    {
        certificate = null;
        byte[]? bytes;
        try
        {
            bytes = ReadBounded(path);
        }
        catch (FileNotFoundException)
        {
            // Covers both "never existed" and "another Piper starting at the same time just migrated it".
            return Outcome.Missing;
        }

        if (bytes is null) return Outcome.Unreadable;

        try
        {
            certificate = load(bytes);
        }
        catch (CryptographicException)
        {
            return Outcome.Unreadable;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        if (isUsable(certificate)) return Outcome.Usable;
        certificate.Dispose();
        certificate = null;
        return Outcome.Rejected;
    }

    /// <summary>
    /// Reads a whole file of 1 to <see cref="MaxKeyFileBytes"/> bytes in one pass, or returns null when
    /// it is empty or larger. The bound is enforced on what is read, not on a size checked beforehand,
    /// so a file swapped for a bigger one in between cannot make this read more.
    /// </summary>
    private static byte[]? ReadBounded(string path)
    {
        var buffer = new byte[MaxKeyFileBytes + 1];
        try
        {
            var total = 0;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                int read;
                while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
                    total += read;
            }

            return total is 0 or > MaxKeyFileBytes ? null : buffer[..total];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static X509Certificate2 LoadLegacy(byte[] bytes) =>
        // Exportable only so it can be re-saved under DPAPI; the copy Piper keeps using is reloaded
        // from the protected file without it.
        X509CertificateLoader.LoadPkcs12(bytes, LegacyPfxPassword,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);

    private static X509Certificate2 LoadProtected(byte[] bytes)
    {
        var pfx = Unprotect(bytes);
        try
        {
            return LoadUnprotectedPfx(pfx);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pfx);
        }
    }

    /// <summary>
    /// The root as Piper uses it: the key lives only in this process (ephemeral) and cannot be
    /// exported again; signing leaves is all it is needed for.
    /// </summary>
    private static X509Certificate2 LoadUnprotectedPfx(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.EphemeralKeySet);

    private static byte[] Protect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The root key is protected with Windows DPAPI.");
        return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
    }

    private static byte[] Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The root key is protected with Windows DPAPI.");
        return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
    }

    /// <summary>Rewrites the public certificate when it is missing or describes another root.</summary>
    private static void EnsurePublicCertificate(string cerPath, X509Certificate2 root)
    {
        var pem = Encoding.ASCII.GetBytes(CertificateAuthority.ExportPem(root));
        try
        {
            if (ReadBounded(cerPath) is { } current && current.AsSpan().SequenceEqual(pem)) return;
        }
        catch (FileNotFoundException)
        {
            // Rewritten below.
        }

        WriteAtomic(cerPath, pem);
    }

    /// <summary>
    /// Writes to a temporary file in the same folder, flushes it to disk, then renames it over the
    /// target, so a crash leaves either the old file or the new one, never a torn one.
    /// </summary>
    internal static void WriteAtomic(string path, byte[] bytes)
    {
        var temporary = path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The write already failed and that is what gets reported; a stray .tmp is overwritten next time.
        }
    }

    /// <summary>
    /// Renames a file aside (never deletes it) and returns the new path, or null when the file is
    /// already gone (another Piper starting at the same time moved it first, and reports it).
    /// </summary>
    private static string? SetAside(string path, string tag = "corrupt")
    {
        var target = $"{path}.{tag}-{DateTime.UtcNow:yyyyMMdd'T'HHmmssfff'Z'}-{Guid.NewGuid().ToString("N")[..8]}";
        try
        {
            File.Move(path, target);
            return target;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Replaces the folder's permissions with a single entry for the current user, inherited by the
    /// files in it, and stops inheriting from the parent. Returns why that failed, or null.
    /// </summary>
    /// <remarks>
    /// DPAPI is what protects the key; this is defence in depth (other accounts on the machine cannot
    /// even list the folder). A profile on a file system without ACLs must not stop Piper starting,
    /// so a failure is reported rather than thrown.
    /// </remarks>
    private static string? RestrictToCurrentUser(string directory)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("The current Windows account has no SID.");

            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException
            or InvalidOperationException)
        {
            return ex.Message;
        }
    }
}
