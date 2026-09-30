using Microsoft.Win32;
using Piper.Core.Telemetry;

namespace Piper.App;

/// <summary>
/// The stable per-machine identifier reported as MUID, kept in the current user's registry so it
/// survives reinstalls and settings resets and stays the same across Piper versions.
///
/// It lives in <c>HKCU</c> rather than app-data precisely because app-data is what an uninstall or a
/// "clear my settings" step removes. Piper already writes elsewhere under <c>HKCU</c> for the system
/// proxy, so the kind of access is not new even though the key is; nothing here needs administrator
/// rights.
///
/// Created lazily, and only ever called once reporting is switched on, so a user who declines - or
/// who never answers the question - never has an identifier written for them at all.
/// </summary>
internal static class MachineIdStore
{
    private const string KeyPath = @"Software\Piper";
    private const string ValueName = "MachineId";

    /// <summary>
    /// Removes the stored identifier, reporting whether it is now gone. Opting out promises the
    /// identifier is forgotten, and this is the one that actually travels, so a later opt-in starts
    /// an identity the collector cannot join to anything reported before - but only if the delete
    /// succeeded, which is why the caller is told rather than left to assume.
    /// </summary>
    public static bool Delete()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the machine identifier, creating and storing one on first use. Returns
    /// <see langword="null"/> if the registry cannot be read or written, which leaves the caller to
    /// fall back to a per-installation identifier rather than failing a report.
    /// </summary>
    public static string? GetOrCreate()
    {
        try
        {
            // Read back as untrusted input: the value is user-editable, and it ends up in a URL, so
            // it gets the same validation as anything else that leaves the machine.
            using (var existing = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false))
            {
                if (existing?.GetValue(ValueName) is string stored
                    && AnalyticsSchema.SanitiseValue(stored) == stored)
                {
                    return stored;
                }
            }

            // Dashed, the form the collector indexes; AnalyticsClient re-renders older undashed ones.
            var created = Guid.NewGuid().ToString("D");
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key?.SetValue(ValueName, created, RegistryValueKind.String);
            return created;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (System.Security.SecurityException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
