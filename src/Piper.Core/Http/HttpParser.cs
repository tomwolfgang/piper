using System.Globalization;

namespace Piper.Core.Http;

/// <summary>Reads HTTP/1.x messages off a <see cref="HttpStreamReader"/>.</summary>
public static class HttpParser
{
    private const long MaxBodyBytes = 256L * 1024 * 1024;

    /// <summary>
    /// How many interim (1xx) responses may precede the real one before the exchange is treated as
    /// hostile. RFC 9112 puts no limit on them, so without a cap an origin can hold a connection
    /// and its reader open indefinitely while sending nothing else.
    /// </summary>
    private const int MaxInterimResponses = 8;

    /// <summary>
    /// Reads a request head and body. Returns null when the connection closed cleanly
    /// before a new request started (the normal end of a keep-alive session).
    /// </summary>
    public static async Task<HttpRequestData?> ReadRequestAsync(HttpStreamReader reader, CancellationToken ct)
    {
        string? line;
        // Tolerate leading blank lines between pipelined requests (RFC 9112 2.2).
        do
        {
            line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) return null;
        } while (line.Length == 0);

        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new HttpParseException($"Malformed request line: '{Truncate(line)}'");

        var request = new HttpRequestData
        {
            Method = parts[0],
            RequestTarget = parts[1],
            HttpVersion = parts.Length > 2 ? parts[2] : "HTTP/1.0",
        };

        request.Headers = await ReadHeadersAsync(reader, ct).ConfigureAwait(false);
        request.Url = ResolveUrl(request);
        request.Body = await ReadBodyAsync(reader, DescribeRequestBody(request.Headers), ct).ConfigureAwait(false);
        return request;
    }

    /// <summary>Reads a response head and body. <paramref name="requestMethod"/> is needed because HEAD has no body.</summary>
    public static async Task<HttpResponseData> ReadResponseAsync(HttpStreamReader reader, string requestMethod, CancellationToken ct)
    {
        var (response, body) = await ReadResponseHeadAsync(reader, requestMethod, ct).ConfigureAwait(false);
        response.Body = await ReadBodyAsync(reader, body, ct).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Reads a response head and works out how its body is framed, leaving the body itself on the
    /// reader. Interim 1xx responses are consumed and the real response that follows is returned.
    /// </summary>
    /// <remarks>
    /// This is the point at which a caller still has a free choice between buffering the body and
    /// relaying it onward as it arrives; see <see cref="HttpBodyDescriptor"/>.
    /// </remarks>
    public static async Task<(HttpResponseData Head, HttpBodyDescriptor Body)> ReadResponseHeadAsync(
        HttpStreamReader reader, string requestMethod, CancellationToken ct)
    {
        // A loop rather than recursion: an origin that never stops sending 1xx would otherwise grow
        // the stack until the process dies on an uncatchable StackOverflowException.
        for (var interim = 0; ; interim++)
        {
            if (interim > MaxInterimResponses)
                throw new HttpParseException($"More than {MaxInterimResponses} interim responses before a final one.");

            string? line;
            do
            {
                line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) throw new HttpParseException("Connection closed before a response was received.");
            } while (line.Length == 0);

            var parts = line.Split(' ', 3);
            if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
                throw new HttpParseException($"Malformed status line: '{Truncate(line)}'");

            var response = new HttpResponseData
            {
                HttpVersion = parts[0],
                StatusCode = status,
                ReasonPhrase = parts.Length > 2 ? parts[2] : string.Empty,
            };

            response.Headers = await ReadHeadersAsync(reader, ct).ConfigureAwait(false);

            // 1xx are interim: consume and read the real response that follows. 101 is the
            // exception -- it shares the 1xx range but is final, because it hands the connection
            // over to another protocol. Skipping it would leave us reading HTTP off a socket that
            // has just become WebSocket, waiting for a response the peer will never send.
            if (status is >= 100 and < 200 and not 101) continue;

            return (response, DescribeResponseBody(response.Headers, requestMethod, status));
        }
    }

    /// <summary>
    /// Body framing for a response, per RFC 9112 6.3: a bodiless status or a HEAD request means no
    /// body at all; Transfer-Encoding wins over Content-Length; a response with neither is
    /// delimited by the connection closing.
    /// </summary>
    /// <remarks>
    /// The bodiless cases are decided first, ahead of every framing header. A 304 or a HEAD reply
    /// routinely carries a Content-Length describing the body a GET would have returned, and
    /// reading that many bytes would consume the next response off a keep-alive connection.
    /// </remarks>
    public static HttpBodyDescriptor DescribeResponseBody(HeaderCollection headers, string requestMethod, int statusCode)
    {
        if (!ResponseCanHaveBody(requestMethod, statusCode)) return HttpBodyDescriptor.None;

        if (headers.HasToken("Transfer-Encoding", "chunked")) return HttpBodyDescriptor.Chunked;

        if (!headers.Contains("Content-Length")) return HttpBodyDescriptor.UntilClose;

        // RFC 9112 6.3 rule 5: a length that is unreadable, or that differs between two copies of
        // the header, is an error rather than a response to be read until close. Falling back would
        // relay the header Piper had just distrusted to a client that may well frame on it.
        if (TryReadContentLength(headers, out var length)) return HttpBodyDescriptor.OfLength(length);

        throw new HttpParseException("Response has an invalid or conflicting Content-Length.");
    }

    /// <summary>
    /// Whether a response to <paramref name="requestMethod"/> with this status can carry a body at
    /// all (RFC 9112 6.3, rule 1). False for HEAD and for every status that is defined without one.
    /// </summary>
    /// <remarks>
    /// 101 counts as bodiless deliberately: whatever follows it belongs to the protocol being
    /// switched to and must not be read, or forwarded, as an HTTP body.
    /// </remarks>
    public static bool ResponseCanHaveBody(string requestMethod, int statusCode) =>
        !string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
        && statusCode is not (204 or 304)
        && statusCode is < 100 or >= 200;

    /// <summary>
    /// Body framing for a request. Identical to <see cref="DescribeResponseBody"/> except that a
    /// request with no framing headers has no body at all: a request can never be delimited by the
    /// connection closing, because the client still has to read the answer on it.
    /// </summary>
    public static HttpBodyDescriptor DescribeRequestBody(HeaderCollection headers)
    {
        if (headers.HasToken("Transfer-Encoding", "chunked")) return HttpBodyDescriptor.Chunked;

        if (!headers.Contains("Content-Length")) return HttpBodyDescriptor.None;

        // RFC 9112 6.3 rule 6: an unreadable or conflicting length is a 400, never "no body". Read
        // as no body, the bytes the client meant as its body would be parsed as the next request
        // on the connection -- a request Piper would see and a front proxy would not.
        if (TryReadContentLength(headers, out var length)) return HttpBodyDescriptor.OfLength(length);

        throw new HttpParseException("Request has an invalid or conflicting Content-Length.");
    }

    /// <summary>
    /// Reads the Content-Length, which counts only when every copy of the header agrees.
    /// <c>NumberStyles.None</c> refuses a sign, whitespace and the other leniencies that would let
    /// "+5" or " 5 " mean one length here and another to the next hop.
    /// </summary>
    private static bool TryReadContentLength(HeaderCollection headers, out long length)
    {
        length = 0;
        var values = headers.GetValues("Content-Length").Distinct(StringComparer.Ordinal).ToList();
        return values.Count == 1
               && long.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out length);
    }

    private static async Task<HeaderCollection> ReadHeadersAsync(HttpStreamReader reader, CancellationToken ct)
    {
        var headers = new HeaderCollection();
        while (true)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null) throw new HttpParseException("Connection closed inside the header block.");
            if (line.Length == 0) return headers;

            if (line[0] == ' ' || line[0] == '\t')
            {
                if (headers.Count == 0) throw new HttpParseException("Header block starts with a folded line.");
                var last = headers.Count - 1;
                headers[last] = headers[last] with { Value = headers[last].Value + " " + line.Trim() };
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0) throw new HttpParseException($"Malformed header line: '{Truncate(line)}'");
            headers.Add(line[..colon].TrimEnd(), line[(colon + 1)..].Trim());

            if (headers.Count > 200) throw new HttpParseException("Too many headers.");
        }
    }

    /// <summary>Buffers a whole body according to a framing already worked out from the headers.</summary>
    /// <remarks>
    /// The counterpart to <see cref="ReadResponseHeadAsync"/> for a caller that wants the message
    /// in hand rather than relayed onward -- the Composer, and the legs that cannot stream yet.
    /// </remarks>
    public static async Task<byte[]> ReadBodyAsync(
        HttpStreamReader reader, HttpBodyDescriptor body, CancellationToken ct)
    {
        switch (body.Framing)
        {
            case HttpBodyFraming.None:
                return [];

            case HttpBodyFraming.Chunked:
                return await ReadChunkedAsync(reader, ct).ConfigureAwait(false);

            case HttpBodyFraming.Length:
                if (body.Length > MaxBodyBytes)
                    throw new HttpParseException($"Body of {body.Length} bytes exceeds the {MaxBodyBytes} byte cap.");
                return await reader.ReadExactlyAsync((int)body.Length, ct).ConfigureAwait(false);

            default:
                return await reader.ReadToEndAsync(MaxBodyBytes, ct).ConfigureAwait(false);
        }
    }

    private static async Task<byte[]> ReadChunkedAsync(HttpStreamReader reader, CancellationToken ct)
    {
        using var body = new MemoryStream();
        while (true)
        {
            var sizeLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (sizeLine is null) throw new HttpParseException("Connection closed inside a chunked body.");

            // Strip any chunk extensions after ';'.
            var semi = sizeLine.IndexOf(';');
            var sizeText = (semi >= 0 ? sizeLine[..semi] : sizeLine).Trim();

            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize))
                throw new HttpParseException($"Bad chunk size: '{Truncate(sizeText)}'");

            if (chunkSize == 0)
            {
                // Consume trailers up to the terminating blank line.
                await SkipTrailersAsync(reader, ct).ConfigureAwait(false);
                return body.ToArray();
            }

            if (body.Length + chunkSize > MaxBodyBytes)
                throw new HttpParseException("Chunked body exceeded the size cap.");

            var chunk = await reader.ReadExactlyAsync(chunkSize, ct).ConfigureAwait(false);
            body.Write(chunk, 0, chunk.Length);

            // Each chunk is followed by its own CRLF, and by nothing else: a chunk that runs on
            // past its declared size means the two ends disagree about where it stops.
            if (await reader.ReadLineAsync(ct).ConfigureAwait(false) is not "")
                throw new HttpParseException("Chunk not terminated by CRLF.");
        }
    }

    /// <summary>Builds an absolute URL from the request target, using Host for origin-form targets.</summary>
    public static Uri? ResolveUrl(HttpRequestData request, bool assumeHttps = false)
    {
        var target = request.RequestTarget;

        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate(target, UriKind.Absolute, out var abs) ? abs : null;

        if (string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
            return Uri.TryCreate("https://" + target, UriKind.Absolute, out var authority) ? authority : null;

        var host = request.Headers["Host"];
        if (string.IsNullOrEmpty(host)) return null;

        var scheme = assumeHttps ? "https" : "http";
        if (!target.StartsWith('/')) target = "/" + target;
        return Uri.TryCreate($"{scheme}://{host}{target}", UriKind.Absolute, out var url) ? url : null;
    }

    /// <summary>
    /// Reads and discards a chunked body's trailer section. Bounded like a header block, or an
    /// origin could keep sending trailer lines for ever and the body would never end.
    /// </summary>
    internal static async Task SkipTrailersAsync(HttpStreamReader reader, CancellationToken ct)
    {
        for (var lines = 0; ; lines++)
        {
            if (lines > MaxTrailerLines) throw new HttpParseException("Too many trailer lines.");
            var trailer = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (trailer is null || trailer.Length == 0) return;
        }
    }

    private const int MaxTrailerLines = 200;

    internal static string Truncate(string value) => value.Length <= 120 ? value : value[..120] + "...";
}
