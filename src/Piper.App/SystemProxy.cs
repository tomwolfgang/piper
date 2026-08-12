using System.Runtime.InteropServices;
using Microsoft.Win32;
using Piper.Core.Proxy;

namespace Piper.App;

/// <summary>
/// Registers Piper as the WinINET proxy for the current user, which is what routes
/// Chrome, Edge and most desktop apps through it.
/// </summary>
/// <remarks>
/// Called when capture starts and undone when it stops, so the previous settings are captured
/// first and <see cref="Restore"/> puts them back exactly. Every change covers both places
/// Windows keeps the configuration - the legacy ProxyEnable/ProxyServer values and the binary
/// connection settings - because leaving the two disagreeing points applications at a proxy that
/// is no longer listening, which the user experiences as having lost their internet connection.
/// </remarks>
public static class SystemProxy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string ConnectionsKeyPath = KeyPath + @"\Connections";

    private const string DefaultConnectionValue = "DefaultConnectionSettings";
    private const string LegacyConnectionValue = "SavedLegacySettings";

    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;
    private const int InternetOptionProxySettingsChanged = 95;

    // DllImport rather than LibraryImport: the source generator requires AllowUnsafeBlocks
    // for the whole project, and this call only ever passes IntPtr.Zero.
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public sealed record Snapshot(
        int? ProxyEnable,
        string? ProxyServer,
        string? ProxyOverride,
        byte[]? DefaultConnectionSettings,
        byte[]? SavedLegacySettings)
    {
        /// <summary>True when these settings route traffic through <paramref name="endpoint"/>.</summary>
        public bool PointsAt(string endpoint) =>
            (ProxyEnable is int enabled && enabled != 0 && MatchesEndpoint(ProxyServer, endpoint))
            || (ConnectionSettingsBlob.TryParse(DefaultConnectionSettings, out var blob)
                && (blob.Flags & ConnectionSettingsBlob.ProxyFlag) != 0
                && MatchesEndpoint(blob.ProxyServer, endpoint));

        /// <summary>The same settings with the manual proxy taken out of them.</summary>
        public Snapshot AsDirectConnection() => new(
            0, null, ProxyOverride, AsDirectBlob(DefaultConnectionSettings), AsDirectBlob(SavedLegacySettings));

        private static byte[]? AsDirectBlob(byte[]? value) =>
            ConnectionSettingsBlob.TryParse(value, out var blob)
                ? (blob with
                {
                    Flags = (blob.Flags & ~ConnectionSettingsBlob.ProxyFlag) | ConnectionSettingsBlob.DirectFlag,
                    ProxyServer = string.Empty,
                }).ToBytes()
                : value;
    }

    /// <summary>
    /// Captures the current settings so they can be restored later. Pass the endpoint Piper is
    /// about to listen on: settings already pointing there belong to a previous Piper that never
    /// got to clean up, and recording them as the user's own would make every close from now on
    /// put a dead proxy back.
    /// </summary>
    public static Snapshot Capture(string? ownEndpoint = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        using var connections = Registry.CurrentUser.OpenSubKey(ConnectionsKeyPath, writable: false);

        var snapshot = new Snapshot(
            key?.GetValue("ProxyEnable") as int?,
            key?.GetValue("ProxyServer") as string,
            key?.GetValue("ProxyOverride") as string,
            connections?.GetValue(DefaultConnectionValue) as byte[],
            connections?.GetValue(LegacyConnectionValue) as byte[]);

        return ownEndpoint is not null && snapshot.PointsAt(ownEndpoint)
            ? snapshot.AsDirectConnection()
            : snapshot;
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue("ProxyEnable") is int enabled && enabled != 0;
    }

    public static string? CurrentServer()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        return key?.GetValue("ProxyServer") as string;
    }

    /// <summary>Points WinINET at <paramref name="endpoint"/> (e.g. "127.0.0.1:8888").</summary>
    public static void Enable(string endpoint, string bypassList = "<local>")
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true)
                        ?? throw new InvalidOperationException("Could not open Internet Settings for writing.");

        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        key.SetValue("ProxyServer", endpoint, RegistryValueKind.String);
        key.SetValue("ProxyOverride", bypassList, RegistryValueKind.String);

        WriteConnectionSettings((existing, counter) =>
            (existing.WithProxy(endpoint, bypassList) with { Counter = counter }).ToBytes());

        Notify();
    }

    /// <summary>Restores exactly what <see cref="Capture"/> recorded.</summary>
    public static void Restore(Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true))
        {
            if (key is not null)
            {
                if (snapshot.ProxyEnable is { } enable) key.SetValue("ProxyEnable", enable, RegistryValueKind.DWord);
                else key.DeleteValue("ProxyEnable", throwOnMissingValue: false);

                if (snapshot.ProxyServer is { } server) key.SetValue("ProxyServer", server, RegistryValueKind.String);
                else key.DeleteValue("ProxyServer", throwOnMissingValue: false);

                if (snapshot.ProxyOverride is { } over) key.SetValue("ProxyOverride", over, RegistryValueKind.String);
                else key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            }
        }

        RestoreConnectionSettings(snapshot);
        Notify();
    }

    public static void Disable()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true))
        {
            if (key is null) return;
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        }

        WriteConnectionSettings((existing, counter) => (existing with
        {
            Counter = counter,
            Flags = (existing.Flags & ~ConnectionSettingsBlob.ProxyFlag) | ConnectionSettingsBlob.DirectFlag,
        }).ToBytes());

        Notify();
    }

    /// <summary>
    /// Undoes a proxy left behind by a run that ended without restoring - a crash, a kill from
    /// Task Manager, or a Windows shutdown. Returns the endpoint that was cleaned up, or null if
    /// there was nothing to undo. Best effort by design: it runs on paths where nothing useful
    /// can be reported to the user.
    /// </summary>
    public static string? RestoreLeftovers()
    {
        SystemProxyBackup? backup;
        try
        {
            backup = SystemProxyBackupStore.Load();
        }
        catch (Exception)
        {
            return null;
        }

        if (backup is null) return null;

        try
        {
            // Only undo Piper's own leftovers. If the machine points somewhere else the user has
            // since changed it themselves, and putting the recorded settings back would undo that.
            if (!Capture().PointsAt(backup.AppliedEndpoint)) return null;

            Restore(backup.ToSnapshot());
            return backup.AppliedEndpoint;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            SystemProxyBackupStore.Clear();
        }
    }

    /// <summary>Compares a ProxyServer value ("host:port", or a per-scheme list) with one endpoint.</summary>
    private static bool MatchesEndpoint(string? proxyServer, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(proxyServer) || string.IsNullOrWhiteSpace(endpoint)) return false;

        foreach (var entry in proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = entry;

            var scheme = value.IndexOf('=');            // "http=127.0.0.1:8888"
            if (scheme >= 0) value = value[(scheme + 1)..];

            var separator = value.IndexOf("://", StringComparison.Ordinal);
            if (separator >= 0) value = value[(separator + 3)..];

            if (Normalize(value) == Normalize(endpoint)) return true;
        }

        return false;
    }

    private static string Normalize(string endpoint) => endpoint.Trim().ToLowerInvariant()
        .Replace("localhost:", "127.0.0.1:", StringComparison.Ordinal)
        .Replace("[::1]:", "127.0.0.1:", StringComparison.Ordinal);

    /// <summary>Rewrites both copies WinINET keeps of the connection settings.</summary>
    private static void WriteConnectionSettings(Func<ConnectionSettingsBlob, int, byte[]> update)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ConnectionsKeyPath);
        if (key is null) return;

        var counter = NextCounter(key);
        foreach (var name in new[] { DefaultConnectionValue, LegacyConnectionValue })
        {
            var existing = ConnectionSettingsBlob.TryParse(key.GetValue(name) as byte[], out var parsed)
                ? parsed
                : ConnectionSettingsBlob.Direct;
            key.SetValue(name, update(existing, counter), RegistryValueKind.Binary);
        }
    }

    private static void RestoreConnectionSettings(Snapshot snapshot)
    {
        using var key = Registry.CurrentUser.CreateSubKey(ConnectionsKeyPath);
        if (key is null) return;

        var counter = NextCounter(key);
        Write(DefaultConnectionValue, snapshot.DefaultConnectionSettings);
        Write(LegacyConnectionValue, snapshot.SavedLegacySettings);

        void Write(string name, byte[]? value)
        {
            // Written back byte for byte, only the counter moved forward: anything Windows keeps
            // in there that Piper does not model survives the round trip.
            if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
            else key.SetValue(name, ConnectionSettingsBlob.WithCounter(value, counter), RegistryValueKind.Binary);
        }
    }

    private static int NextCounter(RegistryKey key) => Math.Max(
        ConnectionSettingsBlob.ReadCounter(key.GetValue(DefaultConnectionValue) as byte[]),
        ConnectionSettingsBlob.ReadCounter(key.GetValue(LegacyConnectionValue) as byte[])) + 1;

    /// <summary>Tells running processes to re-read the settings instead of waiting for a restart.</summary>
    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);

        // Not in the documented option list, but it is what makes WinHTTP-based callers (Chrome
        // and Edge among them) pick the change up without being restarted. Older builds simply
        // fail the call.
        InternetSetOption(IntPtr.Zero, InternetOptionProxySettingsChanged, IntPtr.Zero, 0);
    }
}
