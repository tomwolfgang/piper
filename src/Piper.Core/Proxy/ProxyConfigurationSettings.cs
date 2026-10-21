using System.Text.Json;

namespace Piper.Core.Proxy;

/// <summary>Persistent choices exposed by Piper's Configurations and Rules menus.</summary>
public sealed class ProxyConfigurationSettings
{
    public bool DecryptHttps { get; set; } = true;
    public bool AskToTrustCertificate { get; set; } = true;
    public bool DecryptWhenUntrusted { get; set; }
    public bool RecordClosedAfterHandshake { get; set; } = true;
    public bool EnableHttp2Downstream { get; set; } = true;
    public bool EnableHttp2Upstream { get; set; } = true;
    public bool PreferHttp11UpstreamForHttp11Clients { get; set; } = true;
    public bool EnableHttp3Upstream { get; set; }
    public bool ValidateUpstreamCertificates { get; set; } = true;
    public string? GlobalUserAgent { get; set; }
    public HostRemappingSettings HostRemapping { get; set; } = new();

    public static ProxyConfigurationSettings From(ProxyOptions options) => new()
    {
        DecryptHttps = options.DecryptHttps,
        AskToTrustCertificate = options.AskToTrustCertificate,
        DecryptWhenUntrusted = options.DecryptWhenUntrusted,
        RecordClosedAfterHandshake = options.RecordClosedAfterHandshake,
        EnableHttp2Downstream = options.EnableHttp2Downstream,
        EnableHttp2Upstream = options.EnableHttp2Upstream,
        PreferHttp11UpstreamForHttp11Clients = options.PreferHttp11UpstreamForHttp11Clients,
        EnableHttp3Upstream = options.EnableHttp3Upstream,
        ValidateUpstreamCertificates = options.ValidateUpstreamCertificates,
        GlobalUserAgent = options.GlobalUserAgent,
        HostRemapping = options.HostRemapping.Export(),
    };

    public void ApplyTo(ProxyOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.DecryptHttps = DecryptHttps;
        options.AskToTrustCertificate = AskToTrustCertificate;
        options.DecryptWhenUntrusted = DecryptWhenUntrusted;
        options.RecordClosedAfterHandshake = RecordClosedAfterHandshake;
        options.EnableHttp2Downstream = EnableHttp2Downstream;
        options.EnableHttp2Upstream = EnableHttp2Upstream;
        options.PreferHttp11UpstreamForHttp11Clients = PreferHttp11UpstreamForHttp11Clients;
        options.EnableHttp3Upstream = EnableHttp3Upstream;
        options.ValidateUpstreamCertificates = ValidateUpstreamCertificates;
        options.GlobalUserAgent = string.IsNullOrWhiteSpace(GlobalUserAgent) ? null : GlobalUserAgent.Trim();
        options.HostRemapping.Apply(HostRemapping);
    }
}

/// <summary>Best-effort storage for proxy configuration; invalid files simply use defaults.</summary>
public static class ProxyConfigurationSettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "configuration.json");

    public static void Save(ProxyConfigurationSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        path ??= DefaultPath;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static ProxyConfigurationSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ProxyConfigurationSettings>(File.ReadAllText(path))
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }
}
