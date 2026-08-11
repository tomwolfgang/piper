using System.Globalization;

namespace Piper.Core.Http;

/// <summary>Reads HTTP/1.x messages off a <see cref="HttpStreamReader"/>.</summary>
public static class HttpParser
{
    private const long MaxBodyBytes = 256L * 1024 * 1024;

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
        request.Body = await ReadBodyAsync(reader, request.Headers, isRequest: true, statusCode: 0, ct).ConfigureAwait(false);
        return request;
    }

    /// <summary>Reads a response head and body. <paramref name="requestMethod"/> is needed because HEAD has no body.</summary>
    public static async Task<HttpResponseData> ReadResponseAsync(HttpStreamReader reader, string requestMethod, CancellationToken ct)
    {
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

        // 1xx are interim: consume and read the real response that follows.
        if (status is >= 100 and < 200)
            return await ReadResponseAsync(reader, requestMethod, ct).ConfigureAwait(false);

        var hasBody = !(string.Equals(requestMethod, "HEAD", StringComparison.OrdinalIgnoreCase)
                        || status is 204 or 304);

        if (hasBody)
            response.Body = await ReadBodyAsync(reader, response.Headers, isRequest: false, statusCode: status, ct).ConfigureAwait(false);

        return response;
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

    /// <summary>
    /// Body framing per RFC 9112 6.3: Transfer-Encoding wins over Content-Length; a
    /// response with neither is delimited by connection close; a request with neither
    /// has no body at all.
    /// </summary>
    private static async Task<byte[]> ReadBodyAsync(
        HttpStreamReader reader, HeaderCollection headers, bool isRequest, int statusCode, CancellationToken ct)
    {
        if (headers.HasToken("Transfer-Encoding", "chunked"))
            return await ReadChunkedAsync(reader, ct).ConfigureAwait(false);

        var contentLength = headers["Content-Length"];
        if (contentLength is not null && long.TryParse(contentLength, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            if (length > MaxBodyBytes) throw new HttpParseException($"Body of {length} bytes exceeds the {MaxBodyBytes} byte cap.");
            return await reader.ReadExactlyAsync((int)length, ct).ConfigureAwait(false);
        }

        if (isRequest) return [];

        // Response with no framing headers: read until close.
        return await reader.ReadToEndAsync(MaxBodyBytes, ct).ConfigureAwait(false);
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
            if (sizeText.Length == 0) continue;

            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var chunkSize))
                throw new HttpParseException($"Bad chunk size: '{Truncate(sizeText)}'");

            if (chunkSize == 0)
            {
                // Consume trailers up to the terminating blank line.
                while (true)
                {
                    var trailer = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (trailer is null || trailer.Length == 0) break;
                }
                return body.ToArray();
            }

            if (body.Length + chunkSize > MaxBodyBytes)
                throw new HttpParseException("Chunked body exceeded the size cap.");

            var chunk = await reader.ReadExactlyAsync(chunkSize, ct).ConfigureAwait(false);
            body.Write(chunk, 0, chunk.Length);

            // Each chunk is followed by its own CRLF.
            await reader.ReadLineAsync(ct).ConfigureAwait(false);
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

    private static string Truncate(string value) => value.Length <= 120 ? value : value[..120] + "...";
}
