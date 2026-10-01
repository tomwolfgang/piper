using System.Text.RegularExpressions;
using Piper.Core.Http;
using Piper.Core.Sessions;

namespace Piper.Core.Proxy;

/// <summary>
/// The outcome of testing one rule, carrying any regex captures for the action to use. There are three
/// outcomes: a hit (<see cref="Success"/>), a miss, and a regex that ran past its timeout
/// (<see cref="TimedOut"/>, which is never a <see cref="Success"/>). A timeout is its own state because
/// "could not tell" is not "did not match": negating the second is a hit, negating the first would
/// answer traffic with a rule that never actually matched it.
/// </summary>
public readonly record struct AutoResponderMatchResult(
    bool Success, IReadOnlyDictionary<string, string>? Captures, bool TimedOut = false)
{
    public static AutoResponderMatchResult Fail => new(false, null);

    public static AutoResponderMatchResult Hit => new(true, null);

    /// <summary>A pattern ran past its timeout: the rule does not apply, whether or not it is negated.</summary>
    public static AutoResponderMatchResult RegexTimeout => new(false, null, TimedOut: true);

    /// <summary>
    /// Substitutes <c>${name}</c> and <c>${1}</c> references to this match's regex captures. Unknown
    /// references are left alone: a literal <c>${...}</c> in a URL or body is far more likely than a
    /// typo'd capture name, and silently blanking it would be the worse failure.
    /// </summary>
    public string Expand(string template)
    {
        if (Captures is not { Count: > 0 } captures || string.IsNullOrEmpty(template)) return template;

        return CaptureReference.Replace(template, reference =>
        {
            var name = reference.Groups["name"].Value;
            return captures.TryGetValue(name, out var value) ? value : reference.Value;
        });
    }

    private static readonly Regex CaptureReference =
        new(@"\$\{(?<name>[A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// What is worked out about one request while the rules are tried in turn, so a value several rules
/// need is computed once: the first <c>URLWithBody:</c> rule decodes the request body and every later
/// one reuses the text. A struct passed by <c>ref</c>, so the common request (no such rule) allocates
/// nothing.
/// </summary>
internal struct MatchScratch
{
    /// <summary>The URL and the bounded, decoded request body, once some rule has asked for them.</summary>
    public string? UrlWithBody;

    /// <summary>How many times a body was decoded for this request; a test hook for "once, not once per rule".</summary>
    public int BodyDecodes;
}

/// <summary>
/// One rule's match expression, compiled once when the rule set is applied.
/// </summary>
/// <remarks>
/// The syntax is Fiddler's, so rules copied out of a Fiddler setup keep working: a bare expression
/// is a case-insensitive substring of the URL, and a prefix selects something more precise --
/// <c>EXACT:</c>, <c>NOT:</c>, <c>REGEX:</c>, <c>METHOD:</c>, <c>HEADER:Name=Value</c>,
/// <c>URLWithBody:</c>. <c>Q:</c> is Piper's own addition and hands the rest of the expression to
/// <see cref="SearchQuery"/>, the grammar the Filters tab already uses.
///
/// A rule is evaluated before the request has been sent, so anything describing a response is
/// rejected at parse time rather than quietly comparing against nothing -- see <see cref="Warning"/>.
/// </remarks>
public sealed class AutoResponderMatch
{
    private enum Kind
    {
        Substring,
        Exact,
        Regex,
        Method,
        Header,
        UrlWithBody,
        Query,
    }

    /// <summary>Query fields that describe the request, and so can be answered before one is sent.</summary>
    private static readonly HashSet<string> RequestTimeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "method", "m", "host", "h", "domain", "d", "path", "p", "query", "qs", "url", "u",
        "header", "hdr", "reqheader", "rh", "req", "reqbody", "reqsize", "id", "is", "has",
    };

    /// <summary><c>is:</c> values that do not read the response.</summary>
    private static readonly HashSet<string> RequestTimeIsValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "https", "secure", "tls", "http", "plain", "tunnel", "composed", "composer", "captured",
    };

    private readonly Kind _kind;
    private readonly string _value;
    private readonly bool _negated;
    private readonly Regex? _regex;
    private readonly SearchQuery? _query;
    private readonly string _headerName = string.Empty;

    private AutoResponderMatch(Kind kind, string value, bool negated,
        Regex? regex = null, SearchQuery? query = null, string headerName = "")
    {
        _kind = kind;
        _value = value;
        _negated = negated;
        _regex = regex;
        _query = query;
        _headerName = headerName;
    }

    private AutoResponderMatch(string warning)
    {
        _kind = Kind.Substring;
        _value = string.Empty;
        Warning = warning;
    }

    /// <summary>An expression that never matches, used for a blank rule.</summary>
    public static AutoResponderMatch Empty { get; } = new(Kind.Substring, string.Empty, negated: false);

    /// <summary>
    /// Why this expression can never match, or null when it is usable. A broken rule is reported and
    /// skipped rather than throwing: one bad line must not stop the rest of the rule set working.
    /// </summary>
    public string? Warning { get; }

    public bool IsEmpty => Warning is null && _kind == Kind.Substring && _value.Length == 0 && _query is null;

    public static AutoResponderMatch Parse(string? expression)
    {
        var text = expression?.Trim() ?? string.Empty;
        if (text.Length == 0) return Empty;

        // A span, not repeated string slices: a rules file is hostile input and "NOT:NOT:NOT:..."
        // repeated across megabytes would otherwise copy the remainder once per prefix.
        var negated = false;
        var rest = text.AsSpan();
        while (rest.StartsWith("NOT:", StringComparison.OrdinalIgnoreCase))
        {
            negated = !negated;
            rest = rest["NOT:".Length..].TrimStart();
        }

        text = rest.Length == text.Length ? text : rest.ToString();

        if (TryStripPrefix(ref text, "EXACT:")) return new AutoResponderMatch(Kind.Exact, text, negated);
        if (TryStripPrefix(ref text, "METHOD:")) return new AutoResponderMatch(Kind.Method, text, negated);
        if (TryStripPrefix(ref text, "URLWithBody:")) return new AutoResponderMatch(Kind.UrlWithBody, text, negated);

        if (TryStripPrefix(ref text, "REGEX:"))
        {
            try
            {
                // Same options and timeout as the Filters tab: this runs on every request, so a
                // pathological pattern has to fail fast instead of stalling the proxy.
                var regex = new Regex(text, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(250));
                return new AutoResponderMatch(Kind.Regex, text, negated, regex);
            }
            catch (ArgumentException ex)
            {
                return new AutoResponderMatch($"REGEX: {ex.Message}");
            }
        }

        if (TryStripPrefix(ref text, "HEADER:"))
        {
            var separator = text.IndexOf('=');
            var name = (separator >= 0 ? text[..separator] : text).Trim();
            var value = separator >= 0 ? text[(separator + 1)..].Trim() : string.Empty;
            return name.Length == 0
                ? new AutoResponderMatch("HEADER: needs a header name, as in HEADER:User-Agent=Firefox")
                : new AutoResponderMatch(Kind.Header, value, negated, headerName: name);
        }

        if (TryStripPrefix(ref text, "Q:")) return ParseQuery(text, negated);

        return new AutoResponderMatch(Kind.Substring, text, negated);
    }

    private static AutoResponderMatch ParseQuery(string text, bool negated)
    {
        var query = SearchQuery.Parse(text);
        if (query.Warnings.Count > 0) return new AutoResponderMatch($"Q: {string.Join("; ", query.Warnings)}");
        if (query.IsEmpty) return new AutoResponderMatch("Q: needs a query, as in Q:method:POST host:api.example.com");

        var unusable = query.Fields.Where(field => !RequestTimeFields.Contains(field))
            .Concat(query.IsValuesUsed.Where(value => !RequestTimeIsValues.Contains(value)).Select(value => $"is:{value}"))
            .ToArray();

        return unusable.Length > 0
            ? new AutoResponderMatch(
                $"Q: {string.Join(", ", unusable)} describes the response, which does not exist yet when a rule is matched")
            : new AutoResponderMatch(Kind.Query, text, negated, query: query);
    }

    /// <summary>
    /// Tests one request. <paramref name="session"/> carries the request being matched; its response
    /// is deliberately not consulted, because there is not one yet.
    /// </summary>
    public AutoResponderMatchResult Match(Session session)
    {
        var scratch = default(MatchScratch);
        return Match(session, ref scratch);
    }

    /// <summary>
    /// As <see cref="Match(Session)"/>, sharing <paramref name="scratch"/> with the other rules tried
    /// for the same request.
    /// </summary>
    internal AutoResponderMatchResult Match(Session session, ref MatchScratch scratch)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (Warning is not null || IsEmpty || session.Request is not { } request) return AutoResponderMatchResult.Fail;

        var result = Evaluate(session, request, ref scratch);

        // Checked before the negation: NOT: turns a miss into a hit, and a timeout is not a miss. A
        // pattern the rule could not finish does not apply to this request either way.
        if (result.TimedOut) return result;

        // A negated rule has nothing to capture -- it matched by *not* finding the pattern.
        if (!_negated) return result;
        return result.Success ? AutoResponderMatchResult.Fail : AutoResponderMatchResult.Hit;
    }

    private AutoResponderMatchResult Evaluate(Session session, HttpRequestData request, ref MatchScratch scratch) => _kind switch
    {
        Kind.Substring => Result(UrlOf(request).Contains(_value, StringComparison.OrdinalIgnoreCase)),
        Kind.Exact => Result(string.Equals(UrlOf(request), _value, StringComparison.Ordinal)),
        Kind.Method => Result(string.Equals(request.Method, _value, StringComparison.OrdinalIgnoreCase)),
        Kind.Regex => MatchRegex(UrlOf(request)),
        Kind.Header => Result(MatchHeader(request)),
        Kind.UrlWithBody => Result(UrlWithBody(request, ref scratch).Contains(_value, StringComparison.OrdinalIgnoreCase)),
        Kind.Query => MatchQuery(session),
        _ => AutoResponderMatchResult.Fail,
    };

    private AutoResponderMatchResult MatchQuery(Session session) => _query!.Evaluate(session) switch
    {
        SearchOutcome.Match => AutoResponderMatchResult.Hit,
        SearchOutcome.TimedOut => AutoResponderMatchResult.RegexTimeout,
        _ => AutoResponderMatchResult.Fail,
    };

    private AutoResponderMatchResult MatchRegex(string url)
    {
        Match match;
        try
        {
            match = _regex!.Match(url);
        }
        catch (RegexMatchTimeoutException)
        {
            return AutoResponderMatchResult.RegexTimeout;
        }

        if (!match.Success) return AutoResponderMatchResult.Fail;

        // Both ${1} and ${name} resolve against the same table; .NET reports named groups by name
        // and the rest by their number as a string, which is exactly the pair of forms Fiddler uses.
        var captures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Group group in match.Groups)
            if (group.Success) captures[group.Name] = group.Value;

        return new AutoResponderMatchResult(true, captures);
    }

    private bool MatchHeader(HttpRequestData request)
    {
        foreach (var value in request.Headers.GetValues(_headerName))
            if (_value.Length == 0 || value.Contains(_value, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>The URL as the session grid shows it, so an EXACT: rule can be pasted straight from there.</summary>
    private static string UrlOf(HttpRequestData request) => request.Url?.ToString() ?? request.RequestTarget;

    /// <summary>
    /// The most of a request body, once decoded, that <c>URLWithBody:</c> reads. A rule runs before the
    /// request is sent, on every request, so it cannot afford the 64 MiB a viewer may decode; a match
    /// beyond this is not found.
    /// </summary>
    internal const int MaxBodyBytes = 1024 * 1024;

    /// <summary>
    /// URL and request body as one haystack. Fiddler documents URLWithBody only loosely; matching the
    /// two joined by a newline keeps a bare substring working against either half. Built on the first
    /// call for a request and kept in <paramref name="scratch"/>, so any number of rules cost one
    /// decode; the body is decoded to at most <see cref="MaxBodyBytes"/>.
    /// </summary>
    private static string UrlWithBody(HttpRequestData request, ref MatchScratch scratch)
    {
        if (scratch.UrlWithBody is { } cached) return cached;

        var haystack = UrlOf(request);
        if (request.Body.Length > 0)
        {
            scratch.BodyDecodes++;

            // DecodeBounded never throws on a hostile or corrupt body: it returns what it could read
            // and says so. The cut also applies to a body with no Content-Encoding.
            var bytes = ContentCodec.DecodeBounded(request.Body, request.ContentEncoding, MaxBodyBytes).Bytes;
            if (bytes.Length > MaxBodyBytes) bytes = bytes.AsSpan(0, MaxBodyBytes).ToArray();
            haystack = $"{haystack}\n{request.BodyAsText(bytes)}";
        }

        scratch.UrlWithBody = haystack;
        return haystack;
    }

    private static AutoResponderMatchResult Result(bool success) =>
        success ? AutoResponderMatchResult.Hit : AutoResponderMatchResult.Fail;

    private static bool TryStripPrefix(ref string text, string prefix)
    {
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        text = text[prefix.Length..].TrimStart();
        return true;
    }

    /// <summary>Prefixes offered by the panel's help and autocomplete.</summary>
    public static readonly string[] Prefixes =
        ["EXACT:", "NOT:", "REGEX:", "METHOD:", "HEADER:", "URLWithBody:", "Q:"];
}
