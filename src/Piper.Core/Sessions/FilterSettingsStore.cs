using System.Text.Json;

namespace Piper.Core.Sessions;

/// <summary>
/// The state of Piper's Filters tab. The property names intentionally match the filterset
/// file format so saved filtersets and the automatically-restored settings stay compatible.
/// </summary>
public sealed class FilterSettings
{
    public bool UseFilters { get; set; }
    public int HostsMode { get; set; }
    public string HostsText { get; set; } = string.Empty;
    /// <summary>
    /// Per-host enabled state used by the Filters tab. <see cref="HostsText"/> is retained for
    /// compatibility with existing filterset files; when this list is empty, callers should
    /// populate it from that legacy text field.
    /// </summary>
    public List<HostFilterEntry> Hosts { get; set; } = [];
    public bool HideSuccess { get; set; }
    public bool HideNonSuccess { get; set; }
    public bool HideRedirects { get; set; }
    public bool HideAuthDemands { get; set; }
    public bool HideNotModified { get; set; }
}

/// <summary>A host pattern in a filterset and whether it participates when the filter is run.</summary>
public sealed class HostFilterEntry
{
    public string Pattern { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Persists the Filters tab state under the user's local app-data directory. Settings are a
/// convenience feature, so a missing, unreadable, or malformed file is treated as no settings.
/// </summary>
public static class FilterSettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "filter-settings.json");

    public static void Save(FilterSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        path ??= DefaultPath;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static FilterSettings? Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<FilterSettings>(File.ReadAllText(path));
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
}
