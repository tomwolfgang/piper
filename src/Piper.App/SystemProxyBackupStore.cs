using System.Text.Json;

namespace Piper.App;

/// <summary>
/// What the system proxy looked like before Piper pointed it at itself, together with the
/// endpoint it was pointed at.
/// </summary>
public sealed class SystemProxyBackup
{
    /// <summary>The endpoint Piper installed, used to recognise its own leftovers later.</summary>
    public string AppliedEndpoint { get; set; } = string.Empty;

    public int? ProxyEnable { get; set; }

    public string? ProxyServer { get; set; }

    public string? ProxyOverride { get; set; }

    public byte[]? DefaultConnectionSettings { get; set; }

    public byte[]? SavedLegacySettings { get; set; }

    public static SystemProxyBackup From(string endpoint, SystemProxy.Snapshot snapshot) => new()
    {
        AppliedEndpoint = endpoint,
        ProxyEnable = snapshot.ProxyEnable,
        ProxyServer = snapshot.ProxyServer,
        ProxyOverride = snapshot.ProxyOverride,
        DefaultConnectionSettings = snapshot.DefaultConnectionSettings,
        SavedLegacySettings = snapshot.SavedLegacySettings,
    };

    public SystemProxy.Snapshot ToSnapshot() =>
        new(ProxyEnable, ProxyServer, ProxyOverride, DefaultConnectionSettings, SavedLegacySettings);
}

/// <summary>
/// Keeps the undo record on disk for as long as Piper is holding the system proxy.
/// </summary>
/// <remarks>
/// Unlike the other settings stores this is not convenience state: it is the only way back if the
/// process never gets to run its shutdown path, so it is written before the registry is touched
/// and deleted only once the settings have actually been put back. A stale file is harmless -
/// startup checks whether the machine still points at the recorded endpoint before using it.
/// </remarks>
public static class SystemProxyBackupStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "system-proxy-backup.json");

    public static void Save(SystemProxyBackup backup, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(backup);
        path ??= DefaultPath;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Deliberately not swallowing IO errors the way the other stores do: without this file a
        // crash would strand the user without a working connection, so the caller needs to know.
        File.WriteAllText(path, JsonSerializer.Serialize(backup));
    }

    public static SystemProxyBackup? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SystemProxyBackup>(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Clear(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
