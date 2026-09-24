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
    /// when the list cannot express the hide -- it is showing only specific hosts, which a single
    /// global <see cref="HostsMode"/> cannot combine with an exclusion -- and then nothing is
    /// changed at all, so the caller must fall back to a transient filter and say it is not kept.
    /// Deliberately never writes <see cref="UseFilters"/>: as in Fiddler Classic, running a filterset
    /// stays an explicit user action.
    /// </summary>
    public bool HideHost(string? host)
    {
        // The host comes from a captured session, so it is attacker-controlled: refuse anything
        // that is not plain hostname material rather than persisting a pattern that could inject
        // extra terms into every future filter run.
        var pattern = host?.Trim();
        if (!HostFilterTerm.IsFilterableHost(pattern)) return false;

        // A hand-edited or truncated file can carry a null list, null entries and blank patterns.
        // Work on a filtered copy so the "cannot express this" path leaves the object untouched.
        var entries = (Hosts ?? []).Where(entry => !string.IsNullOrWhiteSpace(entry?.Pattern)).ToList();

        // Anything but 1 is show-only, matching how the Filters tab coerces a restored mode.
        // Show-only means the shown set *is* the ticked entries, and a single global HostsMode
        // cannot say "show these but not that", so a hide is not recordable here at all. Unticking
        // whatever was showing the host looks like it works, but a pattern broad enough to show
        // this host was showing others, which vanish with it -- and it leaves the Hosts list with
        // nothing naming the host, so after a restart it is hidden with no sign of why. Report the
        // refusal and let the caller hide it for this session only.
        if (HostsMode != 1 && entries.Any(entry => entry.Enabled)) return false;

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

    /// <summary>
    /// Whether the Hosts list, once the filterset runs, hides <paramref name="host"/>: it is in hide
    /// mode and an enabled entry covers the host. The capture list asks this to decide when a host it
    /// hid for "Hide this host" has been unticked or removed here and should show again.
    /// </summary>
    public bool Hides(string host)
    {
        ArgumentNullException.ThrowIfNull(host);
        return HostsMode == 1
            && (Hosts ?? []).Any(entry => entry is { Enabled: true }
                && !string.IsNullOrWhiteSpace(entry.Pattern)
                && Covers(entry.Pattern, host));
    }

    private bool Commit(List<HostFilterEntry> entries)
    {
        Hosts = entries;
        // Keep the legacy text form in step, the same way the Filters tab composes it.
        HostsText = string.Join("; ", entries.Select(entry => entry.Pattern));
        return true;
    }

    /// <summary>
    /// Whether an enabled entry already decides <paramref name="host"/>, using the same
    /// exact-or-subdomain rule <see cref="SearchQuery"/> applies to a composed <c>domain:</c> term.
    /// A pattern that strips to nothing (a lone "*") is dropped by <see cref="HostFilterTerm.Compose"/>
    /// and so covers nothing.
    /// </summary>
    private static bool Covers(string pattern, string host) => HostFilterTerm.Covers(pattern, host);
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
