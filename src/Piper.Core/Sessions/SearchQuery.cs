using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Piper.Core.Http;

namespace Piper.Core.Sessions;

/// <summary>
/// Compiled filter over captured sessions. Drives both the session-list filter box and
/// the Composer's request search.
/// </summary>
/// <remarks>
/// Grammar: whitespace-separated terms, ANDed. A term is <c>[-][field:]value</c>.
/// <list type="bullet">
/// <item><c>checkout</c> - substring across method, URL, headers and textual bodies</item>
/// <item><c>"exact phrase"</c> - quoted literal</item>
/// <item><c>/re?gex/</c> - regex over the URL, or over a field when prefixed</item>
/// <item><c>method:GET|POST</c> - alternatives with <c>|</c></item>
/// <item><c>status:2xx</c>, <c>status:&gt;=400</c>, <c>status:200..299</c></item>
/// <item><c>size:&gt;100kb</c>, <c>dur:&gt;500</c> (ms)</item>
/// <item><c>is:https is:json -is:tunnel</c></item>
/// <item><c>-host:cdn.example.com</c> - negation</item>
/// </list>
/// </remarks>
public sealed class SearchQuery
{
    private readonly List<Func<Session, bool>> _predicates;

    public static readonly SearchQuery Empty = new([], [], string.Empty);

    private SearchQuery(List<Func<Session, bool>> predicates, IReadOnlyList<string> plainTerms, string text)
    {
        _predicates = predicates;
        PlainTerms = plainTerms;
        Text = text;
    }

    /// <summary>The original query text.</summary>
    public string Text { get; }

    /// <summary>Non-negated literal terms, for match highlighting in the UI.</summary>
    public IReadOnlyList<string> PlainTerms { get; }

    public bool IsEmpty => _predicates.Count == 0;

    /// <summary>Any parse problems. The query still runs, ignoring the bad terms.</summary>
    public IReadOnlyList<string> Warnings { get; private init; } = [];

    public bool Matches(Session session)
    {
        for (var i = 0; i < _predicates.Count; i++)
            if (!_predicates[i](session))
                return false;
        return true;
    }

    public IEnumerable<Session> Filter(IEnumerable<Session> sessions) =>
        IsEmpty ? sessions : sessions.Where(Matches);

    public static SearchQuery Parse(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return Empty;

        var predicates = new List<Func<Session, bool>>();
        var plainTerms = new List<string>();
        var warnings = new List<string>();

        foreach (var token in Tokenize(query))
        {
            try
            {
                var predicate = Compile(token, plainTerms);
                if (predicate is null) continue;
                predicates.Add(token.Negated ? Negate(predicate) : predicate);
            }
            catch (Exception ex) when (ex is ArgumentException or RegexParseException or FormatException)
            {
                warnings.Add($"{token.Field ?? "term"}: {ex.Message}");
            }
        }

        return new SearchQuery(predicates, plainTerms, query) { Warnings = warnings };
    }

    private static Func<Session, bool> Negate(Func<Session, bool> inner) => s => !inner(s);

    // ---------------------------------------------------------------- tokenizer

    private readonly record struct Token(string? Field, string Value, bool Negated, bool IsRegex, bool IsQuoted);

    private static List<Token> Tokenize(string query)
    {
        var tokens = new List<Token>();
        var i = 0;

        while (i < query.Length)
        {
            while (i < query.Length && char.IsWhiteSpace(query[i])) i++;
            if (i >= query.Length) break;

            var negated = false;
            if (query[i] is '-' or '!' && i + 1 < query.Length && !char.IsWhiteSpace(query[i + 1]))
            {
                negated = true;
                i++;
            }

            // A field prefix is an unquoted run of letters followed by ':'.
            string? field = null;
            var fieldStart = i;
            while (i < query.Length && char.IsLetter(query[i])) i++;
            if (i < query.Length && i > fieldStart && query[i] == ':')
            {
                field = query[fieldStart..i].ToLowerInvariant();
                i++;
            }
            else
            {
                i = fieldStart;
            }

            var (value, isRegex, isQuoted) = ReadValue(query, ref i);
            if (value.Length == 0 && field is null) continue;

            tokens.Add(new Token(field, value, negated, isRegex, isQuoted));
        }

        return tokens;
    }

    private static (string Value, bool IsRegex, bool IsQuoted) ReadValue(string query, ref int i)
    {
        if (i >= query.Length) return (string.Empty, false, false);

        if (query[i] == '"')
        {
            i++;
            var sb = new StringBuilder();
            while (i < query.Length && query[i] != '"')
            {
                if (query[i] == '\\' && i + 1 < query.Length) i++;
                sb.Append(query[i++]);
            }
            if (i < query.Length) i++; // closing quote
            return (sb.ToString(), false, true);
        }

        if (query[i] == '/')
        {
            i++;
            var sb = new StringBuilder();
            while (i < query.Length && query[i] != '/')
            {
                if (query[i] == '\\' && i + 1 < query.Length) sb.Append(query[i++]);
                sb.Append(query[i++]);
            }
            if (i < query.Length) i++; // closing slash
            return (sb.ToString(), true, false);
        }

        var start = i;
        while (i < query.Length && !char.IsWhiteSpace(query[i])) i++;
        return (query[start..i], false, false);
    }

    // ----------------------------------------------------------------- compiler

    private static Func<Session, bool>? Compile(Token token, List<string> plainTerms)
    {
        if (token.Field is null)
        {
            if (token.IsRegex)
            {
                var re = BuildRegex(token.Value);
                return s => re.IsMatch(s.SearchIndex);
            }
            if (!token.Negated) plainTerms.Add(token.Value);
            var needle = token.Value.ToLowerInvariant();
            return s => s.SearchIndex.Contains(needle, StringComparison.Ordinal);
        }

        return token.Field switch
        {
            "method" or "m" => TextField(token, s => s.Method),
            "host" or "h" => TextField(token, s => s.Host),
            "path" or "p" => TextField(token, s => s.Path),
            "query" or "qs" => TextField(token, s => s.Query),
            "url" or "u" => TextField(token, s => s.Url),
            "ct" or "mime" => TextField(token, s => s.ContentType),
            "error" or "err" => TextField(token, s => s.Error ?? string.Empty),

            "status" or "s" or "code" => CompileStatus(token),

            "header" or "hdr" => HeaderField(token, request: true, response: true),
            "reqheader" or "rh" => HeaderField(token, request: true, response: false),
            "respheader" or "sh" => HeaderField(token, request: false, response: true),

            "req" or "reqbody" => BodyField(token, request: true, response: false),
            "resp" or "respbody" => BodyField(token, request: false, response: true),
            "body" or "b" => BodyField(token, request: true, response: true),

            "size" or "respsize" => NumericField(token, s => s.ResponseSize, ParseSize),
            "reqsize" => NumericField(token, s => s.RequestSize, ParseSize),
            "dur" or "time" or "ms" => NumericField(token, s => (long)s.Duration.TotalMilliseconds, ParsePlain),
            "id" => NumericField(token, s => s.Id, ParsePlain),

            "is" or "has" => CompileIs(token.Value),

            _ => throw new ArgumentException($"unknown field '{token.Field}'"),
        };
    }

    private static Func<Session, bool> TextField(Token token, Func<Session, string> selector)
    {
        if (token.IsRegex)
        {
            var re = BuildRegex(token.Value);
            return s => re.IsMatch(selector(s));
        }

        // '|' introduces alternatives, unless the value was quoted.
        if (!token.IsQuoted && token.Value.Contains('|'))
        {
            var options = token.Value.Split('|', StringSplitOptions.RemoveEmptyEntries);
            return s =>
            {
                var value = selector(s);
                foreach (var option in options)
                    if (value.Contains(option, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            };
        }

        var needle = token.Value;
        return s => selector(s).Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private static Func<Session, bool> HeaderField(Token token, bool request, bool response)
    {
        // "header:Name=value" checks a specific header; "header:text" scans the whole block.
        var eq = token.Value.IndexOf('=');
        string? name = null;
        var needle = token.Value;
        if (eq > 0 && !token.IsRegex)
        {
            name = token.Value[..eq].Trim();
            needle = token.Value[(eq + 1)..].Trim();
        }

        var re = token.IsRegex ? BuildRegex(token.Value) : null;

        return s =>
        {
            if (request && s.Request is not null && MatchHeaders(s.Request.Headers)) return true;
            if (response && s.Response is not null && MatchHeaders(s.Response.Headers)) return true;
            return false;
        };

        bool MatchHeaders(HeaderCollection headers)
        {
            if (name is not null)
            {
                foreach (var value in headers.GetValues(name))
                    if (needle.Length == 0 || value.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        return true;
                return false;
            }

            foreach (var header in headers)
            {
                var line = header.Name + ": " + header.Value;
                if (re is not null ? re.IsMatch(line) : line.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    private static Func<Session, bool> BodyField(Token token, bool request, bool response)
    {
        var re = token.IsRegex ? BuildRegex(token.Value) : null;
        var needle = token.Value;

        return s =>
        {
            if (request && s.Request is not null && MatchBody(s.Request)) return true;
            if (response && s.Response is not null && MatchBody(s.Response)) return true;
            return false;
        };

        bool MatchBody(HttpMessage message)
        {
            if (message.Body.Length == 0) return false;
            if (!ContentCodec.LooksTextual(message.ContentType, message.Body)) return false;
            string text;
            try { text = message.BodyAsText(); }
            catch { return false; }
            return re is not null ? re.IsMatch(text) : text.Contains(needle, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Func<Session, bool> CompileStatus(Token token)
    {
        var raw = token.Value.Trim();

        // 2xx / 4XX class shorthand.
        if (raw.Length == 3 && char.IsDigit(raw[0]) && (raw[1] is 'x' or 'X') && (raw[2] is 'x' or 'X'))
        {
            var hundreds = (raw[0] - '0') * 100;
            return s => s.StatusCode >= hundreds && s.StatusCode < hundreds + 100;
        }

        if (!token.IsQuoted && raw.Contains('|'))
        {
            var codes = raw.Split('|', StringSplitOptions.RemoveEmptyEntries)
                           .Select(x => int.TryParse(x, out var c) ? c : -1)
                           .Where(c => c > 0).ToHashSet();
            return s => codes.Contains(s.StatusCode);
        }

        return NumericField(token, s => s.StatusCode, ParsePlain);
    }

    private static Func<Session, bool> NumericField(Token token, Func<Session, long> selector, Func<string, long> parse)
    {
        var matcher = NumericMatcher.Parse(token.Value, parse);
        return s => matcher.Matches(selector(s));
    }

    private static Func<Session, bool> CompileIs(string value) => value.ToLowerInvariant() switch
    {
        "https" or "secure" or "tls" => s => s.IsHttps,
        "http" or "plain" => s => !s.IsHttps,
        "tunnel" or "connect" => s => s.IsTunnel,
        "composed" or "composer" => s => s.IsComposed,
        "captured" => s => !s.IsComposed,
        "error" or "failed" => s => s.State == SessionState.Failed || s.StatusCode >= 400,
        "complete" or "done" => s => s.State == SessionState.Complete,
        "pending" or "inflight" => s => s.State is SessionState.Pending or SessionState.SendingRequest or SessionState.AwaitingResponse,
        "redirect" => s => s.StatusCode is >= 300 and < 400,
        "ok" or "success" => s => s.StatusCode is >= 200 and < 300,
        "json" => s => s.ContentType.Contains("json", StringComparison.OrdinalIgnoreCase),
        "xml" => s => s.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase),
        "html" => s => s.ContentType.Contains("html", StringComparison.OrdinalIgnoreCase),
        "image" or "img" => s => s.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase),
        "script" or "js" => s => s.ContentType.Contains("javascript", StringComparison.OrdinalIgnoreCase),
        "css" => s => s.ContentType.Contains("css", StringComparison.OrdinalIgnoreCase),
        "slow" => s => s.Duration.TotalMilliseconds > 1000,
        "cached" => s => s.StatusCode == 304,
        "body" => s => s.ResponseSize > 0,
        _ => throw new ArgumentException($"unknown 'is:' value '{value}'"),
    };

    private static Regex BuildRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

    private static long ParsePlain(string text) =>
        long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);

    /// <summary>Parses byte counts with optional b/kb/mb/gb suffixes.</summary>
    private static long ParseSize(string text)
    {
        text = text.Trim().ToLowerInvariant();
        long multiplier = 1;
        if (text.EndsWith("gb")) { multiplier = 1024L * 1024 * 1024; text = text[..^2]; }
        else if (text.EndsWith("mb")) { multiplier = 1024L * 1024; text = text[..^2]; }
        else if (text.EndsWith("kb")) { multiplier = 1024L; text = text[..^2]; }
        else if (text.EndsWith('k')) { multiplier = 1024L; text = text[..^1]; }
        else if (text.EndsWith('m')) { multiplier = 1024L * 1024; text = text[..^1]; }
        else if (text.EndsWith('b')) { text = text[..^1]; }

        return (long)(double.Parse(text.Trim(), CultureInfo.InvariantCulture) * multiplier);
    }

    /// <summary>Comparison over a numeric field: <c>&gt;N</c>, <c>&lt;=N</c>, <c>N..M</c> or plain equality.</summary>
    private readonly struct NumericMatcher
    {
        private readonly long _low;
        private readonly long _high;

        private NumericMatcher(long low, long high)
        {
            _low = low;
            _high = high;
        }

        public bool Matches(long value) => value >= _low && value <= _high;

        public static NumericMatcher Parse(string text, Func<string, long> parse)
        {
            text = text.Trim();
            if (text.Length == 0) throw new ArgumentException("missing value");

            var range = text.IndexOf("..", StringComparison.Ordinal);
            if (range > 0)
                return new NumericMatcher(parse(text[..range]), parse(text[(range + 2)..]));

            if (text.StartsWith(">=")) return new NumericMatcher(parse(text[2..]), long.MaxValue);
            if (text.StartsWith("<=")) return new NumericMatcher(long.MinValue, parse(text[2..]));
            if (text.StartsWith('>')) return new NumericMatcher(parse(text[1..]) + 1, long.MaxValue);
            if (text.StartsWith('<')) return new NumericMatcher(long.MinValue, parse(text[1..]) - 1);
            if (text.StartsWith('=')) text = text[1..];

            var exact = parse(text);
            return new NumericMatcher(exact, exact);
        }
    }

    /// <summary>Field names offered by the UI's autocomplete.</summary>
    public static readonly string[] FieldNames =
    [
        "method:", "host:", "path:", "query:", "url:", "status:", "ct:",
        "header:", "reqheader:", "respheader:",
        "req:", "resp:", "body:",
        "size:", "reqsize:", "dur:", "id:", "error:", "is:",
    ];

    public static readonly string[] IsValues =
    [
        "is:https", "is:http", "is:tunnel", "is:composed", "is:captured", "is:error",
        "is:complete", "is:pending", "is:redirect", "is:ok", "is:json", "is:xml",
        "is:html", "is:image", "is:script", "is:css", "is:slow", "is:cached", "is:body",
    ];
}
