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
/// first and <c>Restore</c> puts them back exactly. Every change covers both places
/// Windows keeps the configuration - the legacy ProxyEnable/ProxyServer values and the binary
/// connection settings - because leaving the two disagreeing points applications at a proxy that
/// is no longer listening, which the user experiences as having lost their internet connection.
/// Every call here blocks on the registry and on WinINET notifying other processes, so callers on
/// the UI thread go through <see cref="SystemProxySession"/> on a worker thread.
/// </remarks>
public static partial class SystemProxy
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

    /// <summary>The current user's real WinINET settings. Tests use their own <see cref="ISystemProxySettings"/>.</summary>
    public static ISystemProxySettings Settings { get; } = new WinInetSettings();

    private sealed class WinInetSettings : ISystemProxySettings
    {
        public Snapshot Read() => Capture();

        public void Enable(string endpoint) => SystemProxy.Enable(endpoint);

        public void Restore(Snapshot snapshot) => SystemProxy.Restore(snapshot);
    }

    private static Snapshot Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        using var connections = Registry.CurrentUser.OpenSubKey(ConnectionsKeyPath, writable: false);

        return new Snapshot(
            key?.GetValue("ProxyEnable") as int?,
            key?.GetValue("ProxyServer") as string,
            key?.GetValue("ProxyOverride") as string,
            connections?.GetValue(DefaultConnectionValue) as byte[],
            connections?.GetValue(LegacyConnectionValue) as byte[]);
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
    private static void Enable(string endpoint, string bypassList = "<local>")
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
    private static void Restore(Snapshot snapshot)
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
