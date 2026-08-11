using System.Collections.ObjectModel;

namespace Piper.Core.Proxy;

/// <summary>Persisted host-remapping choices. Each non-comment line has the form
/// <c>NewIP-or-Host OriginalURLHost</c>, matching Fiddler's Host Remapping syntax.</summary>
public sealed class HostRemappingSettings
{
    public bool Enabled { get; set; }
    public string Mappings { get; set; } = string.Empty;

    public HostRemappingSettings Clone() => new() { Enabled = Enabled, Mappings = Mappings };
}

/// <summary>
/// Thread-safe origin override used by the proxy. IP targets behave like a normal hosts-file DNS
/// override; hostname targets also rewrite the outbound HTTP authority and TLS SNI so virtual
/// hosts receive a request for the replacement hostname.
/// </summary>
public sealed class HostRemapping
{
    private sealed record Snapshot(bool Enabled, string Mappings, IReadOnlyDictionary<string, string> Entries, long Revision);

    private Snapshot _snapshot = new(false, string.Empty,
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)), 0);

    public bool Enabled => Volatile.Read(ref _snapshot).Enabled;
    public long Revision => Volatile.Read(ref _snapshot).Revision;

    public HostRemappingSettings Export()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return new HostRemappingSettings { Enabled = snapshot.Enabled, Mappings = snapshot.Mappings };
    }

    /// <summary>Returns the hostname or IP address to connect to for <paramref name="host"/>.</summary>
    public string Resolve(string host) => ResolveTarget(host).Host;

    /// <summary>Gets a connection target and its configuration generation from one atomic snapshot.</summary>
    public HostRemappingTarget ResolveTarget(string host)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var target = snapshot.Enabled && snapshot.Entries.TryGetValue(NormalizeHost(host), out var replacement)
            ? replacement : host;
        return new HostRemappingTarget(target, snapshot.Revision);
    }

    public void Apply(HostRemappingSettings? settings)
    {
        settings ??= new HostRemappingSettings();
        var mappings = settings.Mappings ?? string.Empty;
        var entries = Parse(mappings);
        var current = Volatile.Read(ref _snapshot);
        Volatile.Write(ref _snapshot, new Snapshot(settings.Enabled, mappings, entries, current.Revision + 1));
    }

    /// <summary>Parses both Piper/Fiddler mappings and ordinary Windows hosts-file entries.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string? mappings)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(mappings))
            return new ReadOnlyDictionary<string, string>(result);

        foreach (var rawLine in mappings.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.Length == 0) continue;

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2 || !IsValidTarget(fields[0])) continue;

            // A Windows hosts line permits aliases after the canonical name. Supporting each one
            // makes importing C:\\Windows\\System32\\drivers\\etc\\hosts useful without conversion.
            for (var i = 1; i < fields.Length; i++)
            {
                if (IsValidHost(fields[i])) result[NormalizeHost(fields[i])] = fields[0];
            }
        }
        return new ReadOnlyDictionary<string, string>(result);
    }

    private static bool IsValidTarget(string value) => IsValidHost(value) || System.Net.IPAddress.TryParse(value, out _);

    private static bool IsValidHost(string value) => Uri.CheckHostName(NormalizeHost(value)) is not UriHostNameType.Unknown;

    private static string NormalizeHost(string host) => host.Trim().TrimEnd('.');
}

/// <summary>A host or IP connection target paired with the remapping generation that chose it.</summary>
public readonly record struct HostRemappingTarget(string Host, long Revision)
{
    /// <summary>Hostname targets are full authority rewrites; IP targets only replace DNS resolution.</summary>
    public bool RewritesAuthority => !System.Net.IPAddress.TryParse(Host, out _);
}
