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

    /// <summary>Rebuilds a request from a decoded h2 field list. <paramref name="isHttps"/> is used
    /// only as a fallback when a peer omits <c>:scheme</c>, which compliant peers never do.</summary>
    public static HttpRequestData ToRequest(IReadOnlyList<(string Name, string Value)> fields, bool isHttps = true)
    {
        var request = new HttpRequestData { HttpVersion = "HTTP/2" };
        string? scheme = null, authority = null, path = null;

        foreach (var (name, value) in fields)
        {
            switch (name)
            {
                case ":method": request.Method = value; break;
                case ":scheme": scheme = value; break;
                case ":authority": authority = value; break;
                case ":path": path = value; break;
                default:
                    if (name.Length > 0 && name[0] == ':') break; // unknown pseudo-header: ignore
                    request.Headers.Add(name, value);
                    break;
            }
        }

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
                response.StatusCode = int.Parse(value, CultureInfo.InvariantCulture);
                response.ReasonPhrase = ReasonPhraseFor(response.StatusCode);
            }
            else if (name.Length > 0 && name[0] == ':')
            {
                // unknown pseudo-header: ignore
            }
            else
            {
                response.Headers.Add(name, value);
            }
        }

        return response;
    }

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
