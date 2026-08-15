using System.Text.RegularExpressions;
using Piper.Core.Http;
using Piper.Core.Sessions;

namespace Piper.Core.Proxy;

/// <summary>The outcome of testing one rule, carrying any regex captures for the action to use.</summary>
public readonly record struct AutoResponderMatchResult(bool Success, IReadOnlyDictionary<string, string>? Captures)
{
    public static AutoResponderMatchResult Fail => new(false, null);

    public static AutoResponderMatchResult Hit => new(true, null);

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
        "method", "m", "host", "h", "path", "p", "query", "qs", "url", "u",
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

        var negated = false;
        while (TryStripPrefix(ref text, "NOT:"))
        {
            negated = !negated;
            text = text.TrimStart();
        }

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
        ArgumentNullException.ThrowIfNull(session);
        if (Warning is not null || IsEmpty || session.Request is not { } request) return AutoResponderMatchResult.Fail;

        var result = Evaluate(session, request);

        // A negated rule has nothing to capture -- it matched by *not* finding the pattern.
        if (!_negated) return result;
        return result.Success ? AutoResponderMatchResult.Fail : AutoResponderMatchResult.Hit;
    }

    private AutoResponderMatchResult Evaluate(Session session, HttpRequestData request) => _kind switch
    {
        Kind.Substring => Result(UrlOf(request).Contains(_value, StringComparison.OrdinalIgnoreCase)),
        Kind.Exact => Result(string.Equals(UrlOf(request), _value, StringComparison.Ordinal)),
        Kind.Method => Result(string.Equals(request.Method, _value, StringComparison.OrdinalIgnoreCase)),
        Kind.Regex => MatchRegex(UrlOf(request)),
        Kind.Header => Result(MatchHeader(request)),
        Kind.UrlWithBody => Result(UrlWithBody(request).Contains(_value, StringComparison.OrdinalIgnoreCase)),
        Kind.Query => Result(_query!.Matches(session)),
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
            return AutoResponderMatchResult.Fail;
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
        var values = request.Headers.GetValues(_headerName).ToArray();
        if (values.Length == 0) return false;
        return _value.Length == 0
               || values.Any(value => value.Contains(_value, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The URL as the session grid shows it, so an EXACT: rule can be pasted straight from there.</summary>
    private static string UrlOf(HttpRequestData request) => request.Url?.ToString() ?? request.RequestTarget;

    /// <summary>
    /// URL and request body as one haystack. Fiddler documents URLWithBody only loosely; matching the
    /// two joined by a newline keeps a bare substring working against either half.
    /// </summary>
    private static string UrlWithBody(HttpRequestData request)
    {
        if (request.Body.Length == 0) return UrlOf(request);
        try
        {
            return $"{UrlOf(request)}\n{request.BodyAsText()}";
        }
        catch (Exception) // a body that will not decode simply has nothing to match against
        {
            return UrlOf(request);
        }
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
