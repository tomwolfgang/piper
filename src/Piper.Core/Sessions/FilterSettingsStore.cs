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

    /// <summary>
    /// Records "hide this host" in <see cref="Hosts"/> so the session grid's right-click menu item
    /// survives a restart instead of only touching the grid's transient filter box. Returns false
    /// when the current filterset cannot express the hide without inverting a list the user set up,
    /// in which case nothing is changed at all and the caller should fall back to a transient filter.
    /// Deliberately never writes <see cref="UseFilters"/>: as in Fiddler Classic, running a filterset
    /// stays an explicit user action.
    /// </summary>
    public bool HideHost(string? host)
    {
        var pattern = host?.Trim();
        if (string.IsNullOrEmpty(pattern)) return false;

        // A hand-edited or truncated file can carry a null list, null entries and blank patterns.
        // Work on a filtered copy so the "cannot express this" path leaves the object untouched.
        var entries = (Hosts ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry?.Pattern)).ToList();

        // Anything but 1 is show-only, matching how the Filters tab coerces a restored mode.
        if (HostsMode != 1 && entries.Any(entry => entry.Enabled))
        {
            // Show-only means the shown set *is* the ticked entries, so hiding a host means
            // unticking whichever ones let it through. Switching HostsMode instead would invert the
            // whole list and hide everything the user asked to see.
            var shows = entries.Where(entry => entry.Enabled && Covers(entry.Pattern, pattern)).ToList();

            // Nothing ticked shows this host, so there is no way to say "hide just this one"
            // without inverting the list.
            if (shows.Count == 0) return false;

            foreach (var entry in shows) entry.Enabled = false;

            // Unticking the last ticked entry leaves a show-only list that shows everything, this
            // host included, so only stop here while something is still ticked.
            if (entries.Any(entry => entry.Enabled)) return Commit(entries);
        }

        // Either already hiding, or a show-only list with nothing ticked -- which composes to an
        // empty term and so filters nothing, meaning hide mode inverts no live intent.
        HostsMode = 1;
        if (!entries.Any(entry => entry.Enabled && Covers(entry.Pattern, pattern)))
        {
            var existing = entries.FirstOrDefault(entry =>
                string.Equals(entry.Pattern.Trim(), pattern, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) existing.Enabled = true;
            else entries.Add(new HostFilterEntry { Pattern = pattern, Enabled = true });
        }

        return Commit(entries);
    }

    private bool Commit(List<HostFilterEntry> entries)
    {
        Hosts = entries;
        // Keep the legacy text form in step, the same way the Filters tab composes it.
        HostsText = string.Join("; ", entries.Select(entry => entry.Pattern));
        return true;
    }

    /// <summary>
    /// Whether an enabled entry already decides <paramref name="host"/>, using the same substring,
    /// case-insensitive comparison <see cref="SearchQuery"/> applies to a composed <c>host:</c> term.
    /// </summary>
    private static bool Covers(string pattern, string host)
    {
        var stripped = HostFilterTerm.StripWildcard(pattern);
        // A pattern that strips to nothing (a lone "*") is dropped by HostFilterTerm.Compose and so
        // filters nothing; without this guard Contains("") would report every host as covered.
        return stripped.Length > 0 && host.Contains(stripped, StringComparison.OrdinalIgnoreCase);
    }
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
