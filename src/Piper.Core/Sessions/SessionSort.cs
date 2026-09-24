using Piper.Core.Http;

namespace Piper.Core.Sessions;

/// <summary>The session grid's columns, in display order, as sort keys.</summary>
public enum SessionSortColumn
{
    Id,
    Result,
    Method,
    Host,
    Path,
    Type,
    Process,
    Size,
    Time,
}

/// <summary>
/// Orders a list of sessions by one grid column.
/// </summary>
/// <remarks>
/// Every key is read once into a snapshot before sorting. Proxy threads keep writing to sessions
/// that are already in the list (a response lands, a pending request completes), so a comparer over
/// the live properties could see a session change between two comparisons. The framework sort treats
/// that as a broken comparer and either throws or leaves the list scrambled. Ties fall back to the
/// session id, so rows with equal keys keep a fixed order instead of trading places on every refresh.
/// </remarks>
public static class SessionSort
{
    /// <summary>Result keys for rows without a status code, sorted after every real code.</summary>
    private const long PendingResult = 1_000;
    private const long TunnelResult = 1_001;
    private const long FailedResult = 1_002;

    /// <summary>
    /// Time and Size key for a session still in flight, sorted after every finished one, the way the
    /// Result column puts "-" after real codes. Its elapsed time and received bytes grow on every
    /// refresh, and sorting by them moved each in-flight row a little further down the list every
    /// 150 ms.
    /// </summary>
    private const long PendingTime = long.MaxValue;

    public static void Sort(List<Session> sessions, SessionSortColumn column, bool descending)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (sessions.Count < 2) return;

        var keys = new SortKey[sessions.Count];
        for (var i = 0; i < keys.Length; i++) keys[i] = SortKey.For(sessions[i], column);

        Array.Sort(keys, descending ? CompareDescending : CompareAscending);

        for (var i = 0; i < keys.Length; i++) sessions[i] = keys[i].Session;
    }

    private static int CompareAscending(SortKey x, SortKey y) => Compare(x, y);

    private static int CompareDescending(SortKey x, SortKey y) => Compare(y, x);

    private static int Compare(SortKey x, SortKey y)
    {
        var order = x.Text is not null || y.Text is not null
            ? CompareText(x.Text, y.Text)
            : x.Number.CompareTo(y.Number);
        return order != 0 ? order : x.Id.CompareTo(y.Id);
    }

    private static int CompareText(string? x, string? y)
    {
        var order = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        return order != 0 ? order : string.CompareOrdinal(x, y);
    }

    private readonly record struct SortKey(Session Session, int Id, long Number, string? Text)
    {
        public static SortKey For(Session session, SessionSortColumn column) => column switch
        {
            SessionSortColumn.Id => Numeric(session, session.Id),
            SessionSortColumn.Result => Numeric(session, ResultKey(session)),
            SessionSortColumn.Method => Textual(session, session.IsTunnel ? "CONNECT" : session.Method),
            SessionSortColumn.Host => Textual(session, session.Host),
            SessionSortColumn.Path => Textual(session, session.Path + session.Query),
            SessionSortColumn.Type => Textual(session, MimeTypes.ShortName(session.ContentType)),
            SessionSortColumn.Process => Textual(session, session.ProcessName),
            // A body still arriving counts up in place, so its size is unknown until it completes.
            SessionSortColumn.Size => Numeric(session, session.Completed is null ? PendingTime : session.ResponseSize),
            SessionSortColumn.Time => Numeric(session, session.Completed is { } completed
                ? (completed - session.Started).Ticks
                : PendingTime),
            _ => throw new ArgumentOutOfRangeException(nameof(column), column, null),
        };

        private static SortKey Numeric(Session session, long value) => new(session, session.Id, value, null);

        private static SortKey Textual(Session session, string? value) =>
            new(session, session.Id, 0, value ?? string.Empty);

        /// <summary>Mirrors <see cref="Session.StatusText"/>, reading the response only once.</summary>
        private static long ResultKey(Session session) => session.State switch
        {
            SessionState.Failed => FailedResult,
            SessionState.Tunnel => TunnelResult,
            _ => session.Response is { } response ? response.StatusCode : PendingResult,
        };
    }
}
