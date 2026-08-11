using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Piper.App;

/// <summary>
/// Registers Piper as the WinINET proxy for the current user, which is what routes
/// Chrome, Edge and most desktop apps through it.
/// </summary>
/// <remarks>
/// Only ever called from the explicit "System Proxy" toggle, never on startup, and the
/// previous settings are captured so <see cref="Restore"/> can put them back exactly.
/// </remarks>
public static class SystemProxy
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    // DllImport rather than LibraryImport: the source generator requires AllowUnsafeBlocks
    // for the whole project, and this call only ever passes IntPtr.Zero.
    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    public sealed record Snapshot(int? ProxyEnable, string? ProxyServer, string? ProxyOverride);

    /// <summary>Captures the current settings so they can be restored later.</summary>
    public static Snapshot Capture()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
        if (key is null) return new Snapshot(null, null, null);

        return new Snapshot(
            key.GetValue("ProxyEnable") as int?,
            key.GetValue("ProxyServer") as string,
            key.GetValue("ProxyOverride") as string);
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
        Notify();
    }

    /// <summary>Restores exactly what <see cref="Capture"/> recorded.</summary>
    public static void Restore(Snapshot snapshot)
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null) return;

        if (snapshot.ProxyEnable is { } enable) key.SetValue("ProxyEnable", enable, RegistryValueKind.DWord);
        else key.DeleteValue("ProxyEnable", throwOnMissingValue: false);

        if (snapshot.ProxyServer is { } server) key.SetValue("ProxyServer", server, RegistryValueKind.String);
        else key.DeleteValue("ProxyServer", throwOnMissingValue: false);

        if (snapshot.ProxyOverride is { } over) key.SetValue("ProxyOverride", over, RegistryValueKind.String);
        else key.DeleteValue("ProxyOverride", throwOnMissingValue: false);

        Notify();
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        if (key is null) return;
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        Notify();
    }

    /// <summary>Tells running processes to re-read the settings instead of waiting for a restart.</summary>
    private static void Notify()
    {
        InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }
}
