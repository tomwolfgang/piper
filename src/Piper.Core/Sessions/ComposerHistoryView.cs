using System.Globalization;
using System.Text;

namespace Piper.Core.Sessions;

/// <summary>What a row in the Composer history pane stands for.</summary>
public enum ComposerRowKind
{
    /// <summary>A host group header. Collapsing it hides every request beneath it.</summary>
    Host,

    /// <summary>One endpoint under a host. Repeat sends of it collapse into this single row.</summary>
    Request,

    /// <summary>One individual send of a repeated request, shown when its request row is expanded.</summary>
    Send,
}

/// <summary>
/// A single flattened row of the Composer history tree.
/// </summary>
/// <param name="Kind">Whether this is a host group, an endpoint, or one send of an endpoint.</param>
/// <param name="Host">The group this row belongs to, already bounded for display.</param>
/// <param name="Key">
/// The identity used to remember expand/collapse state: the host for a <see cref="ComposerRowKind.Host"/>
/// row, <see cref="ComposerHistoryView.ExpandKey"/> for a <see cref="ComposerRowKind.Request"/> row, and
/// empty for a <see cref="ComposerRowKind.Send"/> row, which is always a leaf.
/// </param>
/// <param name="Sends">
/// Every session behind this row, newest first. Carrying them here rather than making callers
/// re-derive them is what lets "remove this row" delete all of a repeated request's sends instead
/// of silently turning a x7 row into a x6 one that looks unchanged.
/// </param>
/// <param name="Expanded">Whether this row's children are currently emitted beneath it.</param>
public readonly record struct ComposerRow(
    ComposerRowKind Kind,
    string Host,
    string Key,
    IReadOnlyList<Session> Sends,
    bool Expanded)
{
    /// <summary>The newest send behind this row - the one a double-click loads into the editor.</summary>
    public Session Session => Sends[0];

    /// <summary>How many sends this row stands for; the repeat badge when greater than one.</summary>
    public int Count => Sends.Count;
}

/// <summary>
/// Flattens the Composer's sent-request history into the rows its tree pane draws: requests grouped
/// under collapsible hosts, with repeat sends of the same endpoint folded into one counted row.
/// </summary>
/// <remarks>
/// Pure logic, deliberately in Piper.Core rather than beside the control that draws it, because the
/// smoke tests only reference Piper.Core and this is where the behaviour worth testing lives.
/// </remarks>
public static class ComposerHistoryView
{
    /// <summary>Group for requests carrying no usable authority at all.</summary>
    public const string NoHost = "(no host)";

    /// <summary>
    /// Longest host a group is allowed to be named after. A Host header arrives from the wire or
    /// from an imported archive, so it is attacker-controlled and needs a ceiling before it reaches
    /// text measurement or the persisted view state.
    /// </summary>
    public const int MaxHostLength = 255;

    /// <summary>
    /// Longest request target a row is allowed to show or group on. A request line is bounded only
    /// by the parser's line limit, and an imported archive is not bounded at all, so without this
    /// a single crafted entry would be re-measured by the painter on every mouse-move and
    /// re-concatenated into an expand key on every keystroke.
    /// </summary>
    public const int MaxTargetLength = 2048;

    /// <summary>
    /// Longest method a row will show or group on. A method is the third field of the same
    /// attacker-controlled request line as the target, and an imported archive bounds none of it,
    /// so leaving it alone would let a payload simply move from the target into the method.
    /// RFC 9110 tokens are short, so the ceiling can be too.
    /// </summary>
    public const int MaxMethodLength = 32;

    /// <summary>
    /// Builds the visible rows. Sessions that are tunnels or carry no request are dropped, the rest
    /// are filtered by <paramref name="query"/> exactly as the flat list used to filter them.
    /// </summary>
    /// <param name="collapsedHosts">Hosts the user has folded away. Ignored while a search is active.</param>
    /// <param name="expandedRequests">
    /// <see cref="ExpandKey"/> values for repeated requests whose individual sends should be listed.
    /// </param>
    public static IReadOnlyList<ComposerRow> Build(
        IReadOnlyList<Session> history,
        SearchQuery? query,
        IReadOnlySet<string>? collapsedHosts,
        IReadOnlySet<string>? expandedRequests)
    {
        ArgumentNullException.ThrowIfNull(history);
        query ??= SearchQuery.Empty;

        // A live search has to reach inside groups the user folded away. Honouring the collapsed
        // set while typing shows a pane that looks empty exactly when it has the most to say.
        var collapsed = query.IsEmpty ? collapsedHosts : null;

        var buckets = new Dictionary<string, Bucket>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in history)
        {
            if (session is null || session.IsTunnel || session.Request is null) continue;
            if (!query.Matches(session)) continue;

            var host = HostOf(session);
            if (!buckets.TryGetValue(host, out var bucket)) buckets[host] = bucket = new Bucket(host);
            bucket.Add(session);
        }

        var rows = new List<ComposerRow>();
        foreach (var bucket in buckets.Values
            .OrderByDescending(bucket => bucket.Newest)
            .ThenBy(bucket => bucket.Host, StringComparer.OrdinalIgnoreCase))
        {
            var hostExpanded = collapsed?.Contains(bucket.Host) != true;
            rows.Add(new ComposerRow(ComposerRowKind.Host, bucket.Host, bucket.Host, bucket.Sorted(), hostExpanded));
            if (!hostExpanded) continue;

            foreach (var group in bucket.Groups())
            {
                var key = ExpandKey(bucket.Host, group.Key);
                var expanded = group.Sends.Count > 1 && expandedRequests?.Contains(key) == true;
                rows.Add(new ComposerRow(ComposerRowKind.Request, bucket.Host, key, group.Sends, expanded));
                if (!expanded) continue;

                foreach (var send in group.Sends)
                    rows.Add(new ComposerRow(ComposerRowKind.Send, bucket.Host, string.Empty, [send], false));
            }
        }

        return rows;
    }

    /// <summary>
    /// Returns every host group present in a history, ready for the Composer's initial folded
    /// state. Hosts added later in the same Composer session remain visible, so a just-completed
    /// send does not disappear beneath a group the user has not seen yet.
    /// </summary>
    /// <param name="expandedHosts">Host groups the user has explicitly opened in an earlier session.</param>
    public static IReadOnlySet<string> InitiallyCollapsedHosts(
        IReadOnlyList<Session> history,
        IReadOnlySet<string>? expandedHosts = null)
    {
        ArgumentNullException.ThrowIfNull(history);

        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in history)
        {
            if (session is null || session.IsTunnel || session.Request is null) continue;
            var host = HostOf(session);
            if (expandedHosts?.Contains(host) != true) hosts.Add(host);
        }

        return hosts;
    }

    /// <summary>
    /// What makes two sends "the same request" inside a host: the method and the full target. The
    /// query is part of it because /search?q=a and /search?q=b are different requests.
    /// </summary>
    public static string RequestKey(Session session) => MethodOf(session) + " " + TargetOf(session);

    /// <summary>
    /// The method a row shows and groups on, bounded and sanitised like the host and the target.
    /// </summary>
    /// <remarks>
    /// This is not belt-and-braces. <see cref="RequestKey"/> runs for every session on every
    /// rebuild -- so on every keystroke in the search box -- and its result is hashed, compared and
    /// concatenated into an expand key, which an unbounded method turns into per-keystroke work
    /// proportional to the whole history. The pane also glues the method to the sanitised target to
    /// make one displayed string, so a bidi override in the method would reverse the target beside
    /// it and a CRLF would break the tooltip into extra lines -- the very spoofs sanitising the
    /// target was meant to prevent.
    /// </remarks>
    public static string MethodOf(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return Bound(session.Method, MaxMethodLength);
    }

    /// <summary>
    /// The path-and-query a row shows and groups on.
    /// </summary>
    /// <remarks>
    /// <see cref="Session.Path"/> and <see cref="Session.Query"/> are both empty when the request
    /// carries a relative target with no absolute URL, so fall back to the target itself rather
    /// than folding every such request in a host into one row.
    /// </remarks>
    public static string TargetOf(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);

        var target = session.Request?.Url is not null
            ? session.Path + session.Query
            : session.Request?.RequestTarget ?? string.Empty;
        if (target.Length == 0) return "/";

        // Bounded and sanitised for the same reason the host is: this string is measured by the
        // painter, concatenated into an expand key on every rebuild, and shown to a user who is
        // reading it to decide what was requested. Two targets identical for the first
        // MaxTargetLength characters therefore share a row, which is the right trade for a pane
        // that exists to be scanned.
        var bounded = Bound(target, MaxTargetLength);
        return bounded.Length == 0 ? "/" : bounded;
    }

    /// <summary>Identity of a request row across rebuilds, for remembering that it is expanded.</summary>
    public static string ExpandKey(string host, string requestKey) => host + "\u0000" + requestKey;

    /// <summary>
    /// Whether a host is safe to show and to persist: bounded in length and free of control
    /// characters. Shared with <see cref="ComposerViewStateStore"/> so a hand-edited state file
    /// cannot reintroduce a value <see cref="Build"/> would never have produced.
    /// </summary>
    public static bool IsDisplayableHost(string? host) =>
        !string.IsNullOrEmpty(host) &&
        host.Length <= MaxHostLength &&
        host.All(IsSafeCharacter);

    /// <summary>
    /// Whether a character may appear in a name this pane shows and persists.
    /// </summary>
    /// <remarks>
    /// Controls are the obvious case. Format characters (U+202E and friends) matter because this
    /// pane's whole job is telling the user which origin was contacted, and a bidi override makes
    /// one hostname render as another. Surrogates are excluded wholesale, which is what keeps
    /// <see cref="Bound"/> from ever cutting a pair in half: ill-formed UTF-16 survives neither a
    /// JSON round-trip nor a comparison against what this method would produce next time, so a
    /// group named by one would silently re-expand on every restart and leave a junk twin behind
    /// in the state file. Nothing valid in an authority or a request target is lost by this.
    /// </remarks>
    private static bool IsSafeCharacter(char value) => char.GetUnicodeCategory(value) is not (
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or
        UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse);

    /// <summary>Truncates to <paramref name="max"/> and neutralises anything unsafe to display.</summary>
    /// <remarks>
    /// Replaces rather than discards, so two hostile values stay distinguishable instead of
    /// merging into one unexplained group.
    /// </remarks>
    private static string Bound(string value, int max)
    {
        var limit = Math.Min(value.Length, max);
        var builder = new StringBuilder(limit);
        for (var i = 0; i < limit; i++)
            builder.Append(IsSafeCharacter(value[i]) ? value[i] : '�');

        return builder.ToString().Trim();
    }

    private static string HostOf(Session session)
    {
        // Group on the authority, not Session.Host: to a debugging proxy localhost:8080 and
        // localhost:3000 are two different servers, and folding them together would hide exactly
        // the distinction the user is working on. Session.Host itself is left alone -- the capture
        // grid and the host filter are built around the bare hostname.
        var url = session.Request?.Url;
        var host = url is not null && !url.IsDefaultPort ? url.Authority : session.Host;
        if (string.IsNullOrWhiteSpace(host)) return NoHost;
        if (IsDisplayableHost(host)) return host;

        var bounded = Bound(host, MaxHostLength);
        return bounded.Length == 0 ? NoHost : bounded;
    }

    private static DateTimeOffset When(Session session) => session.Completed ?? session.Started;

    /// <summary>One host group, accumulated in one pass and sorted only when rows are emitted.</summary>
    private sealed class Bucket(string host)
    {
        private readonly Dictionary<string, List<Session>> _byRequest = new(StringComparer.Ordinal);
        private readonly List<Session> _all = [];

        public string Host { get; } = host;

        /// <summary>Most recent send under this host; decides where the group sorts.</summary>
        public DateTimeOffset Newest { get; private set; } = DateTimeOffset.MinValue;

        public void Add(Session session)
        {
            _all.Add(session);
            var key = RequestKey(session);
            if (!_byRequest.TryGetValue(key, out var sends)) _byRequest[key] = sends = [];
            sends.Add(session);

            var when = When(session);
            if (when > Newest) Newest = when;
        }

        /// <summary>Every send under this host, newest first.</summary>
        public List<Session> Sorted() => [.. _all.OrderByDescending(When)];

        /// <summary>Endpoints under this host, most recently used first, each newest-send first.</summary>
        public IEnumerable<(string Key, List<Session> Sends)> Groups() => _byRequest
            .Select(pair => (pair.Key, Sends: pair.Value.OrderByDescending(When).ToList()))
            .OrderByDescending(group => When(group.Sends[0]))
            .ThenBy(group => group.Key, StringComparer.Ordinal);
    }
}
