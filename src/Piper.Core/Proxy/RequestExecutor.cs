using System.Diagnostics;
using System.Text;
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
    public async Task<Session> ExecuteAsync(HttpRequestData request, CancellationToken ct = default)
    {
        var session = new Session
        {
            Request = request,
            IsComposed = true,
            State = SessionState.SendingRequest,
            ClientEndpoint = "composer",
            ProcessName = "Piper (composer)",
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

            PrepareHeaders(request, url);

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
    private static void PrepareHeaders(HttpRequestData request, Uri url)
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
        }
        else if (request.Headers.Contains("Content-Length"))
        {
            request.Headers.Set("Content-Length", "0");
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
    public static bool TryParseRaw(string raw, out HttpRequestData request, out string error)
    {
        request = new HttpRequestData();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "The request is empty.";
            return false;
        }

        var normalised = raw.Replace("\r\n", "\n");
        var split = normalised.IndexOf("\n\n", StringComparison.Ordinal);
        var head = split >= 0 ? normalised[..split] : normalised;
        var body = split >= 0 ? normalised[(split + 2)..] : string.Empty;

        var lines = head.Split('\n');
        var startLine = lines[0].Trim();
        if (startLine.Length == 0)
        {
            error = "Missing request line.";
            return false;
        }

        var parts = startLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            error = $"Malformed request line: '{startLine}'";
            return false;
        }

        request.Method = parts[0].ToUpperInvariant();
        request.RequestTarget = parts[1];
        request.HttpVersion = parts.Length > 2 ? parts[2] : "HTTP/1.1";
        request.Headers = HeaderCollection.Parse(string.Join("\n", lines.Skip(1)));
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
