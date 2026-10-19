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
/// The capture list's right-click hides ("Filter Now" in Fiddler): a quick view for this run, kept
/// apart from the Filters tab. Values come from captured sessions, so they are attacker-controlled:
/// they are only compared in memory and shown, never composed into a query or persisted, and both
/// how many there are and how long each may be are bounded.
/// </summary>
public sealed class SessionHideList
{
    // ponytail: a cap, not an eviction policy; past it nothing more is hidden.
    public const int MaxHides = 64;

    /// <summary>Longer than any real host, process name or media type; refuses a pathological one.</summary>
    public const int MaxValueLength = 1024;

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
