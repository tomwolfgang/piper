using System.Globalization;
using Piper.Core.Http;

namespace Piper.Core.Http2;

/// <summary>
/// Pure translation between <see cref="HttpRequestData"/>/<see cref="HttpResponseData"/> and the
/// pseudo-header-plus-fields shape HTTP/2 puts on the wire (RFC 9113 §8.3). No I/O, no HPACK --
/// callers HPACK-encode/decode the field list produced/consumed here.
/// </summary>
public static class Http2MessageAdapter
{
    // ProxyServer.HopByHopHeaders, plus Host -- h2 has no Host header at all (RFC 9113 §8.3.1),
    // and none of these can appear on a compliant h2 wire in the first place.
    private static readonly string[] ForbiddenHeaders =
    [
        "Connection", "Proxy-Connection", "Keep-Alive", "Transfer-Encoding",
        "TE", "Trailer", "Upgrade", "Proxy-Authenticate", "Proxy-Authorization", "Host",
    ];

    public static IReadOnlyList<(string Name, string Value)> ToHeaderFields(HttpRequestData request)
    {
        var url = request.Url ?? throw new InvalidOperationException("Cannot send over HTTP/2: request has no resolved URL.");

        // Pseudo-headers first, per RFC 9113 §8.3.
        var fields = new List<(string Name, string Value)>
        {
            (":method", request.Method),
            (":scheme", url.Scheme),
            (":authority", url.Authority),
            (":path", url.PathAndQuery),
        };
        AppendRegularHeaders(fields, request.Headers);
        return fields;
    }

    public static IReadOnlyList<(string Name, string Value)> ToHeaderFields(HttpResponseData response)
    {
        var fields = new List<(string Name, string Value)>
        {
            (":status", response.StatusCode.ToString(CultureInfo.InvariantCulture)),
        };
        AppendRegularHeaders(fields, response.Headers);
        return fields;
    }

    private static void AppendRegularHeaders(List<(string Name, string Value)> fields, HeaderCollection headers)
    {
        foreach (var header in headers)
        {
            if (IsForbidden(header.Name)) continue;
            // RFC 9113 §8.2.1: field names MUST be lowercase on the wire. This only affects the
            // wire representation built here -- it never mutates the captured HeaderCollection.
            fields.Add((header.Name.ToLowerInvariant(), header.Value));
        }
    }

    private static bool IsForbidden(string name)
    {
        foreach (var forbidden in ForbiddenHeaders)
            if (string.Equals(name, forbidden, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // RFC 9113 §8.2.2: connection-specific fields make an h2 message malformed. TE is checked
    // separately because "trailers" is the one value it may carry.
    private static readonly string[] ConnectionSpecificFields =
        ["connection", "keep-alive", "proxy-connection", "transfer-encoding", "upgrade"];

    /// <summary>Rebuilds a request from a decoded h2 field list. <paramref name="isHttps"/> is used
    /// only as a fallback when a peer omits <c>:scheme</c>, which compliant peers never do.
    /// Throws <see cref="HttpParseException"/> for a malformed message (RFC 9113 §8.2): these
    /// fields are written verbatim onto an HTTP/1.1 wire when the origin speaks it, so a CR or LF
    /// let through here would smuggle headers or a whole second request upstream.</summary>
    public static HttpRequestData ToRequest(IReadOnlyList<(string Name, string Value)> fields, bool isHttps = true)
    {
        var request = new HttpRequestData { HttpVersion = "HTTP/2" };
        string? method = null, scheme = null, authority = null, path = null;

        foreach (var (name, value) in fields)
        {
            switch (name)
            {
                // RFC 9113 §8.3: each pseudo-header at most once, or which one wins is ambiguous.
                case ":method":
                    if (method is not null) throw Malformed("duplicate :method");
                    if (!IsToken(value)) throw Malformed(":method is not a token");
                    method = value;
                    break;
                case ":scheme":
                    if (scheme is not null) throw Malformed("duplicate :scheme");
                    scheme = value;
                    break;
                case ":authority":
                    if (authority is not null) throw Malformed("duplicate :authority");
                    authority = value;
                    break;
                case ":path":
                    if (path is not null) throw Malformed("duplicate :path");
                    // Becomes the HTTP/1.1 request target when the URL does not resolve.
                    if (value.Length == 0 || value.Any(c => c <= ' ' || c == '\x7f'))
                        throw Malformed(":path is empty or contains whitespace or a control character");
                    path = value;
                    break;
                default:
                    if (name.Length > 0 && name[0] == ':') break; // unknown pseudo-header: ignore
                    ValidateRegularField(name, value);
                    if (ConnectionSpecificFields.Contains(name))
                        throw Malformed($"connection-specific field '{name}'");
                    if (name == "te" && value != "trailers")
                        throw Malformed("te other than \"trailers\"");
                    request.Headers.Add(name, value);
                    break;
            }
        }

        // The upstream socket follows :authority but an HTTP/1.1 origin routes on Host, so two
        // Hosts, or one naming a different entity, would send the request somewhere other than
        // where the session, the filters and the AutoResponder believe it went (RFC 9113 §8.3.1).
        var hosts = request.Headers.GetValues("host").ToList();
        if (hosts.Count > 1) throw Malformed("more than one host field");
        if (hosts.Count == 1 && authority is not null && !string.Equals(hosts[0], authority, StringComparison.OrdinalIgnoreCase))
            throw Malformed("host differs from :authority");

        if (method is not null) request.Method = method;
        request.RequestTarget = path ?? "/";
        request.Url = ResolveUrl(scheme ?? (isHttps ? "https" : "http"), authority, path);
        return request;
    }

    public static HttpResponseData ToResponse(IReadOnlyList<(string Name, string Value)> fields)
    {
        var response = new HttpResponseData { HttpVersion = "HTTP/2" };

        foreach (var (name, value) in fields)
        {
            if (string.Equals(name, ":status", StringComparison.Ordinal))
            {
                if (value.Length != 3 || !value.All(char.IsAsciiDigit)) throw Malformed(":status is not three digits");
                response.StatusCode = int.Parse(value, CultureInfo.InvariantCulture);
                response.ReasonPhrase = ReasonPhraseFor(response.StatusCode);
            }
            else if (name.Length > 0 && name[0] == ':')
            {
                // unknown pseudo-header: ignore
            }
            else
            {
                // Relayed verbatim to HTTP/1.1 clients by HeadAsText(), so the same CR/LF danger as
                // in ToRequest. Connection-specific fields are tolerated here: the proxy strips them
                // as hop-by-hop before a response reaches a client.
                ValidateRegularField(name, value);
                response.Headers.Add(name, value);
            }
        }

        return response;
    }

    // RFC 9113 §8.2.1: a name is a lowercase token; a value has no NUL, CR or LF and no leading or
    // trailing SP/HTAB.
    private static void ValidateRegularField(string name, string value)
    {
        if (!IsToken(name) || name.Any(char.IsAsciiLetterUpper))
            throw Malformed("field name is not a lowercase token");
        if (value.Any(c => c is '\0' or '\r' or '\n'))
            throw Malformed($"field '{name}' value contains NUL, CR or LF");
        if (value.Length > 0 && (value[0] is ' ' or '\t' || value[^1] is ' ' or '\t'))
            throw Malformed($"field '{name}' value has leading or trailing whitespace");
    }

    // RFC 9110 §5.6.2 tchar.
    private static bool IsToken(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c));

    // Names only, never values: a value is captured data and must stay out of diagnostics.
    private static HttpParseException Malformed(string what) => new($"Malformed HTTP/2 message: {what}.");

    /// <summary>h2's counterpart to <see cref="HttpParser.ResolveUrl"/>: builds an absolute URL
    /// from the scheme/authority/path pseudo-headers instead of a request line and Host header.</summary>
    public static Uri? ResolveUrl(string? scheme, string? authority, string? path)
    {
        if (string.IsNullOrEmpty(scheme) || string.IsNullOrEmpty(authority) || string.IsNullOrEmpty(path))
            return null;
        return Uri.TryCreate($"{scheme}://{authority}{path}", UriKind.Absolute, out var url) ? url : null;
    }

    // h2 carries no reason phrase (RFC 9113 §8.3.2) -- this is purely cosmetic, so HttpResponseData's
    // StartLine/HeadAsText() (which the UI just displays) still render something sensible.
    private static string ReasonPhraseFor(int status) => ReasonPhrases.For(status);
}
