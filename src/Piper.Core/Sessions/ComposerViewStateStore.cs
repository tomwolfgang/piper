using System.Text.Json;

namespace Piper.Core.Sessions;

/// <summary>Which host groups the user has explicitly opened in the Composer history pane.</summary>
/// <remarks>
/// Only explicitly opened hosts are recorded, so a missing, unreadable, or truncated file keeps
/// the default folded state for every group.
/// </remarks>
public sealed class ComposerViewState
{
    public List<string> ExpandedHosts { get; set; } = [];
}

/// <summary>
/// Persists the Composer history pane's explicitly opened host groups under the user's local
/// app-data directory, alongside the history itself. View state is a convenience, so every failure
/// degrades to the compact folded default rather than surfacing.
/// </summary>
public static class ComposerViewStateStore
{
    /// <summary>
    /// Ceiling on remembered hosts. The file is written from host names that ultimately come off
    /// the wire, so it needs a bound that a long capture session cannot grow past.
    /// </summary>
    public const int MaxHosts = 500;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "composer-view.json");

    public static void Save(ComposerViewState state, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        path ??= DefaultPath;

        var sanitized = new ComposerViewState { ExpandedHosts = Clean(state.ExpandedHosts) };

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(sanitized));
        }
        catch (IOException)
        {
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static ComposerViewState Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path)) return new ComposerViewState();
            var state = JsonSerializer.Deserialize<ComposerViewState>(File.ReadAllText(path));
            return state is null
                ? new ComposerViewState()
                : new ComposerViewState { ExpandedHosts = Clean(state.ExpandedHosts) };
        }
        catch (IOException)
        {
            return new ComposerViewState();
        }
        catch (JsonException)
        {
            return new ComposerViewState();
        }
        catch (UnauthorizedAccessException)
        {
            return new ComposerViewState();
        }
    }

    /// <summary>
    /// Applies the same bounds on the way in and on the way out, so a hand-edited file cannot carry
    /// a host the pane would never itself have produced, and one bad entry does not discard the rest.
    /// </summary>
    private static List<string> Clean(List<string>? hosts) => (hosts ?? [])
        .Where(ComposerHistoryView.IsDisplayableHost)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        // Ordered before the cap because the caller hands over a materialised HashSet, whose order
        // is not meaningful and not stable between saves. Without this, which hosts survive past
        // the ceiling could change on every write and an opened host could quietly fold again.
        .OrderBy(host => host, StringComparer.OrdinalIgnoreCase)
        .Take(MaxHosts)
        .ToList();
}
