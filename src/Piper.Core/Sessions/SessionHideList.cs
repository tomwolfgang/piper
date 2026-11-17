using System.Text.Json;

namespace Piper.Core.Sessions;

/// <summary>What a right-click hide in the capture list matches on.</summary>
public enum SessionHideKind
{
    Host,
    Process,
    ContentType,
}

/// <summary>One hide, and how many sessions it matched at the last count.</summary>
public sealed class SessionHide
{
    internal SessionHide(SessionHideKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    public SessionHideKind Kind { get; }
    public string Value { get; }
    public int Hidden { get; internal set; }

    public bool Matches(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Kind switch
        {
            SessionHideKind.Host => HostFilterTerm.Covers(Value, session.Host),
            SessionHideKind.Process => string.Equals((session.ProcessName ?? string.Empty).Trim(), Value, StringComparison.OrdinalIgnoreCase),
            _ => string.Equals(SessionHideList.MediaType(session), Value, StringComparison.OrdinalIgnoreCase),
        };
    }
}

/// <summary>
/// The capture list's right-click hides ("Filter Now" in Fiddler): a quick view kept apart from the
/// Filters tab and restored on the next start. Values come from captured sessions, so they are
/// attacker-controlled: they are only compared in memory, shown and saved as data, never composed
/// into a query, and both how many there are and how long each may be are bounded, again on load.
/// </summary>
public sealed class SessionHideList
{
    // ponytail: a cap, not an eviction policy; past it nothing more is hidden.
    public const int MaxHides = 64;

    /// <summary>Longer than any real host, process name or media type; refuses a pathological one.</summary>
    public const int MaxValueLength = 1024;

    /// <summary>Room for <see cref="MaxHides"/> full-length values even with every character JSON-escaped.</summary>
    public const long MaxFileBytes = 512 * 1024;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "session-hides.json");

    private sealed record Stored(SessionHideKind Kind, string? Value);

    /// <summary>Writes the hides; like the Filters tab settings, a failed write just loses the convenience.</summary>
    public static void Save(IEnumerable<SessionHide> hides, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(hides);
        path ??= DefaultPath;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            // Write then replace, so a crash mid-write leaves the previous hides rather than a torn file.
            // ponytail: synchronous on the caller's thread; at most 64 short entries, as FilterSettingsStore does.
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(hides.Select(hide => new Stored(hide.Kind, hide.Value))));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Reads saved hides back through <see cref="Add"/>, so a tampered file gets the same checks a
    /// right-click does. A missing, oversized, unreadable or malformed file is an empty list.
    /// </summary>
    public static SessionHideList Load(string? path = null)
    {
        path ??= DefaultPath;
        var list = new SessionHideList();
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes) return list;
            foreach (var stored in JsonSerializer.Deserialize<List<Stored?>>(File.ReadAllText(path)) ?? [])
            {
                if (stored is not null && Enum.IsDefined(stored.Kind)) list.Add(stored.Kind, stored.Value);
            }
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
        return list;
    }

    private readonly List<SessionHide> _hides = [];

    public IReadOnlyList<SessionHide> Items => _hides;
    public int Count => _hides.Count;
    public bool IsFull => _hides.Count >= MaxHides;

    /// <summary>
    /// Adds a hide, trimmed. False, with nothing changed, for a blank or overlong value, a host that is
    /// not plain hostname material (Session.Host is the raw Host header when the request line had no
    /// parseable URL), a duplicate of an existing hide of the same kind, or a full list.
    /// </summary>
    public bool Add(SessionHideKind kind, string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > MaxValueLength || IsFull) return false;
        if (kind == SessionHideKind.Host && !HostFilterTerm.IsFilterableHost(trimmed)) return false;
        if (_hides.Any(hide => hide.Kind == kind && string.Equals(hide.Value, trimmed, StringComparison.OrdinalIgnoreCase)))
            return false;

        _hides.Add(new SessionHide(kind, trimmed));
        return true;
    }

    public bool Remove(SessionHide hide) => _hides.Remove(hide);

    public bool Clear()
    {
        if (_hides.Count == 0) return false;
        _hides.Clear();
        return true;
    }

    /// <summary>Zeroes every hide's count before a fresh pass over the sessions.</summary>
    public void ResetCounts()
    {
        foreach (var hide in _hides) hide.Hidden = 0;
    }

    /// <summary>
    /// Whether any hide matches <paramref name="session"/>. Every matching hide counts it, so each one
    /// says what removing it alone would not bring back, not just what no earlier hide already took.
    /// </summary>
    public bool HidesAndCounts(Session session)
    {
        var hidden = false;
        foreach (var hide in _hides)
        {
            if (!hide.Matches(session)) continue;
            hide.Hidden++;
            hidden = true;
        }
        return hidden;
    }

    /// <summary>The response's media type without parameters, as a content-type hide matches it.</summary>
    public static string MediaType(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return (session.ContentType ?? string.Empty).Split(';', 2)[0].Trim();
    }
}
