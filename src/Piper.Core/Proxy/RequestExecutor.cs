using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Piper.Core.Http;
using Piper.Core.Sessions;

namespace Piper.Core.Proxy;

/// <summary>
/// Executes a hand-authored request straight to the origin server.
/// </summary>
/// <remarks>
/// Deliberately not built on <c>HttpClient</c>: that would reorder, normalise and reject
/// headers, which defeats the point of a composer. Going down to the socket means what
/// you type is what goes on the wire, including duplicate, malformed or unusual headers.
/// </remarks>
public sealed class RequestExecutor(ProxyOptions options, SessionStore store)
{
    /// <summary>Sends <paramref name="request"/> and records the exchange as a composed session.</summary>
    /// <param name="labelJsonBody">
    /// Adds <c>Content-Type: application/json</c> to an unlabelled JSON body. Only for a request
    /// typed in the Composer: a replay must reproduce the captured request, missing header and all.
    /// </param>
    public async Task<Session> ExecuteAsync(
        HttpRequestData request, CancellationToken ct = default, bool isUpdateCheck = false,
        bool labelJsonBody = false)
    {
        var session = new Session
        {
            Request = request,
            IsComposed = true,
            IsUpdateCheck = isUpdateCheck,
            State = SessionState.SendingRequest,
            ClientEndpoint = "composer",
            ProcessName = isUpdateCheck ? "Piper (update check)" : "Piper (composer)",
        };

        var url = request.Url ?? HttpParser.ResolveUrl(request);
        if (url is null)
        {
            session.State = SessionState.Failed;
            session.Error = "Could not resolve an absolute URL. Provide a full URL or a Host header.";
            session.Completed = DateTimeOffset.Now;
            store.Add(session);
            return session;
        }

        request.Url = url;
        session.IsHttps = url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
        store.Add(session);

        var stopwatch = Stopwatch.StartNew();
        UpstreamConnection? upstream = null;
        try
        {
            // allowHttp2: false -- the composer sends verbatim wire bytes (see the class remarks),
            // which HTTP/2's binary framing has no equivalent for, so this must always be h1.1.
            upstream = await UpstreamConnection.ConnectAsync(
                url.Host, url.Port, session.IsHttps, options, ct, allowHttp2: false).ConfigureAwait(false);
            session.ConnectTime = stopwatch.Elapsed;
            session.ServerEndpoint = upstream.RemoteEndpoint;

            PrepareHeaders(request, url, labelJsonBody);

            await upstream.Stream.WriteAsync(request.ToOriginFormBytes(), ct).ConfigureAwait(false);
            await upstream.Stream.FlushAsync(ct).ConfigureAwait(false);

            session.State = SessionState.AwaitingResponse;
            store.NotifyUpdated(session);

            var beforeResponse = stopwatch.Elapsed;
            session.Response = await HttpParser.ReadResponseAsync(upstream.Reader, request.Method, ct).ConfigureAwait(false);
            session.TimeToFirstByte = stopwatch.Elapsed - beforeResponse;
            session.State = SessionState.Complete;
        }
        catch (Exception ex)
        {
            session.State = SessionState.Failed;
            session.Error = ProxyServer.Describe(ex);
        }
        finally
        {
            upstream?.Dispose();
            session.Completed = DateTimeOffset.Now;
            session.InvalidateSearchIndex();
            store.NotifyUpdated(session);
        }

        return session;
    }

    private static readonly string DefaultUserAgent =
        $"Piper/{System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0"}";

    /// <summary>Fills in only the headers a request cannot go out without, leaving the rest untouched.</summary>
    private static void PrepareHeaders(HttpRequestData request, Uri url, bool labelJsonBody)
    {
        // Always overwritten, never just filled in when absent: editing the URL after loading a
        // captured session (or after typing a stale Host by hand) must not leave a Host that
        // points at a different domain than the request is actually being sent to.
        request.Headers.Set("Host", url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}");

        // A default, not an override -- the user's own User-Agent (typed, or loaded from a
        // captured session) always wins.
        if (!request.Headers.Contains("User-Agent"))
            request.Headers.Add("User-Agent", DefaultUserAgent);

        // Chunked framing is not supported for composed bodies; send an explicit length.
        request.Headers.Remove("Transfer-Encoding");

        if (request.Body.Length > 0)
        {
            request.Headers.Set("Content-Length", request.Body.Length.ToString());

            // Also a default: the Composer's starting headers name no Content-Type, and origins
            // (Express json(), ASP.NET [FromBody], ...) treat an unlabelled JSON body as no body.
            // Runs after the connect await, so a large body is scanned off the UI thread.
            if (labelJsonBody && !request.Headers.Contains("Content-Type") && IsJsonDocument(request.Body))
                request.Headers.Add("Content-Type", "application/json");
        }
        else if (request.Headers.Contains("Content-Length"))
        {
            request.Headers.Set("Content-Length", "0");
        }
    }

    /// <summary>True for a single, complete JSON object or array, the only bodies worth labelling.</summary>
    /// <remarks>A forward-only reader: no DOM is built, and its default depth limit bounds nesting.</remarks>
    private static bool IsJsonDocument(byte[] body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType is not (JsonTokenType.StartObject or JsonTokenType.StartArray))
                return false;
            reader.Skip();
            // Throws on anything but whitespace after the root value.
            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renders an editable raw block from the composer's separate method/URL/header/body fields.
    /// The inverse of <see cref="TryParseRaw"/>, which must round-trip this text unchanged.
    /// </summary>
    /// <remarks>
    /// CRLF, because the text lands in a multiline WinForms text box. An empty header block still
    /// has to produce exactly one blank line: appending a header terminator to nothing would push
    /// the blank line one CRLF early and leak a newline into the parsed body.
    /// </remarks>
    public static string BuildRawText(string method, string target, string headerBlock, string body)
    {
        var sb = new StringBuilder();
        sb.Append(method.Trim().ToUpperInvariant()).Append(' ')
          .Append(target.Trim()).Append(" HTTP/1.1\r\n");
        var headers = headerBlock.TrimEnd();
        if (headers.Length > 0) sb.Append(headers).Append("\r\n");
        sb.Append("\r\n").Append(body);
        return sb.ToString();
    }

    /// <summary>
    /// Parses a raw "METHOD url HTTP/1.1" + headers + blank line + body block, so a request
    /// can be pasted in whole from logs, curl output or another tool.
    /// </summary>
    /// <remarks>
    /// Line endings are normalised in the head only. The body is sent exactly as typed: a multipart
    /// body needs its CRLFs, and flattening them to LF broke its framing on the way out.
    /// </remarks>
    public static bool TryParseRaw(string raw, out HttpRequestData request, out string error)
    {
        request = new HttpRequestData();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "The request is empty.";
            return false;
        }

        var (headEnd, bodyStart) = HttpWireFormat.FindBlankLine(raw);
        var head = raw[..headEnd].TrimEnd('\r').Replace("\r\n", "\n");
        var body = raw[bodyStart..];

        var lines = head.Split('\n');
        var startLine = lines[0].Trim();
        if (startLine.Length == 0)
        {
            error = "Missing request line.";
            return false;
        }

        if (!HttpSyntax.TryParseRequestLine(startLine, lenient: true, "HTTP/1.1", out var start, out error))
            return false;

        if (!HeaderCollection.TryParse(string.Join("\n", lines.Skip(1)), out var headers, out error))
            return false;

        request.Method = start.Method.ToUpperInvariant();
        request.RequestTarget = start.Target;
        request.HttpVersion = start.Version;
        request.Headers = headers;
        request.Body = body.Length > 0 ? Encoding.UTF8.GetBytes(body) : [];

        request.Url = HttpParser.ResolveUrl(request);
        if (request.Url is null)
        {
            error = "Could not resolve a URL. Use an absolute URL in the request line, or add a Host header.";
            return false;
        }

        return true;
    }

    /// <summary>Renders a session's request as an editable raw block for the composer.</summary>
    public static string ToRawText(HttpRequestData request)
    {
        var sb = new StringBuilder();
        var target = request.Url?.ToString() ?? request.RequestTarget;
        sb.Append(request.Method).Append(' ').Append(target).Append(' ').Append(request.HttpVersion).Append('\n');
        foreach (var header in request.Headers)
            sb.Append(header.Name).Append(": ").Append(header.Value).Append('\n');
        sb.Append('\n');
        if (request.Body.Length > 0) sb.Append(request.BodyAsText());
        return sb.ToString();
    }
}
