using Piper.Core.Proxy;

namespace Piper.App;

// The registry-free half of SystemProxy: what a set of proxy settings is and how it compares with
// an endpoint. Kept apart from the registry and WinINET calls so the restore logic built on it
// (SystemProxySession, SystemProxyRecovery) can be tested without touching the machine's settings.
public static partial class SystemProxy
{
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

        /// <summary>
        /// What to record as the user's own settings before Piper points the machine at
        /// <paramref name="ownEndpoint"/>: settings already pointing there belong to a previous Piper
        /// that never got to clean up, and recording them would make every close from now on put a
        /// dead proxy back.
        /// </summary>
        public Snapshot ForBackupBefore(string ownEndpoint) =>
            PointsAt(ownEndpoint) ? AsDirectConnection() : this;

        private static byte[]? AsDirectBlob(byte[]? value) =>
            ConnectionSettingsBlob.TryParse(value, out var blob)
                ? (blob with
                {
                    Flags = (blob.Flags & ~ConnectionSettingsBlob.ProxyFlag) | ConnectionSettingsBlob.DirectFlag,
                    ProxyServer = string.Empty,
                }).ToBytes()
                : value;
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
}
