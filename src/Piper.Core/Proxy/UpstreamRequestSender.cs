using Piper.Core.Http;
using Piper.Core.Http2;

namespace Piper.Core.Proxy;

/// <summary>
/// What an origin answered with, and whether its body is already in hand.
/// </summary>
/// <param name="Head">Status line and headers. Carries the body too when <paramref name="IsBuffered"/>.</param>
/// <param name="Body">How the body is framed, for a caller that still has to read it.</param>
/// <param name="IsBuffered">
/// True when the whole message was read before returning, which is still the case for an HTTP/3
/// upstream leg. False for HTTP/1.1 and HTTP/2, where the body is left to be relayed onward as it
/// arrives.
/// </param>
/// <param name="BodyReader">Where the body is read from when it was not buffered.</param>
internal readonly record struct UpstreamResponse(
    HttpResponseData Head, HttpBodyDescriptor Body, bool IsBuffered, HttpStreamReader? BodyReader = null);

/// <summary>
/// How far one exchange got, for deciding whether a failure on a reused connection can be retried
/// on a fresh one without the origin seeing the request twice or the client half an answer.
/// </summary>
internal sealed class UpstreamSendProgress
{
    /// <summary>Every byte of the request was handed to the connection.</summary>
    public bool RequestWritten { get; set; }

    /// <summary>At least one byte of the answer arrived.</summary>
    public bool ResponseStarted { get; set; }

    /// <summary>
    /// True when the exchange can go again on a fresh connection: nothing of an answer arrived, and
    /// either the request never fully left or repeating it is harmless (an idempotent method, RFC 9110
    /// 9.2.2). A request whose write failed part way is incomplete, so the origin cannot have acted on
    /// it; one that was written in full may have been, and a POST must not be sent twice.
    /// </summary>
    public bool CanRetry(string method) =>
        !ResponseStarted && (!RequestWritten || IsIdempotent(method));

    private static bool IsIdempotent(string method) => method.ToUpperInvariant() switch
    {
        "GET" or "HEAD" or "OPTIONS" or "TRACE" or "PUT" or "DELETE" => true,
        _ => false,
    };
}

/// <summary>
/// Sends one request over an already-connected <see cref="UpstreamConnection"/>, branching on
/// whichever protocol ALPN actually negotiated. Shared by every downstream direction that can
/// reach an ALPN-h2-capable upstream (the HTTP/1.1 loop in <see cref="ProxyServer"/> and
/// <see cref="Http2RequestForwarder"/>) so that branch exists in exactly one place -- the
/// Composer/<see cref="RequestExecutor"/> never needs it, because it always forces h1.1 upstream
/// (see <see cref="UpstreamConnection.ConnectAsync"/>'s <c>allowHttp2</c> parameter).
/// </summary>
internal static class UpstreamRequestSender
{
    /// <param name="onRequestSent">Fired once the request has been handed off and this side is now
    /// waiting on the response -- the right moment for a caller to flip a <c>Session</c> to
    /// <c>AwaitingResponse</c> and start timing time-to-first-byte. For HTTP/2, sending and
    /// receiving are fused into one call on <see cref="Http2ClientConnection"/>, so this fires
    /// immediately before that call rather than after only the request bytes are flushed.</param>
    /// <param name="progress">Filled in as the exchange gets further, for a caller deciding whether a
    /// failure can be retried on another connection.</param>
    public static async Task<UpstreamResponse> SendAsync(
        UpstreamConnection upstream, HttpRequestData outbound, Action onRequestSent, CancellationToken ct,
        UpstreamSendProgress? progress = null)
    {
        if (upstream.IsHttp2)
        {
            // Sending and receiving are one call here, so the request counts as delivered from the start.
            if (progress is not null) progress.RequestWritten = true;
            onRequestSent();
            var h2 = new Http2ClientConnection(upstream.Stream, upstream.Reader.IdleTimeout);
            var h2Head = await h2.SendRequestHeadAsync(outbound, ct).ConfigureAwait(false);
            var bodyReader = new HttpStreamReader(h2.ResponseBody) { IdleTimeout = upstream.Reader.IdleTimeout };
            upstream.AdoptResponseBodyReader(bodyReader);
            return new UpstreamResponse(h2Head, DescribeHttp2Body(h2Head, outbound.Method), IsBuffered: false, bodyReader);
        }

        MakeValidHttp11(outbound);

        await outbound.WriteOriginFormAsync(upstream.Stream, ct).ConfigureAwait(false);
        // Before the flush: once the last write has returned, the bytes may reach the origin even if
        // the flush then fails.
        if (progress is not null) progress.RequestWritten = true;
        await upstream.Stream.FlushAsync(ct).ConfigureAwait(false);
        onRequestSent();

        // The first read on its own, so a caller can tell an origin that closed without answering at
        // all from one that failed part way through its answer: only the first is safe to retry.
        if (!await upstream.Reader.HasMoreDataAsync(ct).ConfigureAwait(false))
            throw new HttpParseException("Connection closed before a response was received.");
        if (progress is not null) progress.ResponseStarted = true;

        // Head only. The body stays on the connection so the caller can decide whether to relay it
        // onward as it arrives or buffer it, which is a decision that has to be made here -- once
        // any of the body has been read there is no going back to streaming it.
        var (head, body) = await HttpParser
            .ReadResponseHeadAsync(upstream.Reader, outbound.Method, ct).ConfigureAwait(false);
        return new UpstreamResponse(head, body, IsBuffered: false, upstream.Reader);
    }

    /// <summary>
    /// Framing for a body arriving in HTTP/2 DATA frames. A content-length still counts, since a
    /// client drawing progress needs it; without one the body ends at END_STREAM, which unlike a
    /// close-delimited HTTP/1.1 body says nothing about the connection.
    /// </summary>
    private static HttpBodyDescriptor DescribeHttp2Body(HttpResponseData head, string method)
    {
        // RFC 9113 8.2.2: Transfer-Encoding has no meaning in HTTP/2, so a response carrying it is
        // malformed rather than chunked.
        if (head.Headers.Contains("Transfer-Encoding"))
            throw new HttpParseException("HTTP/2 response carries Transfer-Encoding.");

        var body = HttpParser.DescribeResponseBody(head.Headers, method, head.StatusCode);
        return body.Framing == HttpBodyFraming.UntilClose ? HttpBodyDescriptor.StreamEnd : body;
    }

    /// <summary>
    /// Makes an outbound request valid as literal HTTP/1.1 wire bytes, whatever protocol it
    /// originally arrived on. A request received over HTTP/2 carries <c>HttpVersion = "HTTP/2"</c>
    /// (correct -- that is genuinely what the browser spoke, and the captured session should say
    /// so) and carries no <c>Host</c> header at all, because HTTP/2 replaces it with the
    /// <c>:authority</c> pseudo-header. Serialising that as-is produces
    /// <c>GET / HTTP/2</c> with no Host: not a valid HTTP/1.1 request, and origins behind a CDN
    /// tend to simply never answer it rather than reject it, so the request hangs until the
    /// client gives up.
    /// </summary>
    /// <remarks>
    /// <paramref name="outbound"/> is always a clone built for this one hop, so overwriting these
    /// fields cannot affect what the captured <c>Session</c> reports about the original request.
    /// </remarks>
    private static void MakeValidHttp11(HttpRequestData outbound)
    {
        // Only a request that arrived over HTTP/2 or HTTP/3 can carry crumbs; an HTTP/1.1 client's
        // header lines go upstream as it sent them.
        if (!outbound.HttpVersion.StartsWith("HTTP/1.", StringComparison.Ordinal))
            JoinCookieCrumbs(outbound.Headers);

        outbound.HttpVersion = "HTTP/1.1";

        if (!outbound.Headers.Contains("Host") && outbound.Url is { } url)
            outbound.Headers.Set("Host", url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}");
    }

    /// <summary>
    /// Joins the <c>cookie</c> fields of an HTTP/2 or HTTP/3 request into the single field HTTP/1.1
    /// expects, in their original order and at the position of the first one. Browsers split the
    /// cookie into one field per pair ("crumbs") so each compresses on its own, and RFC 9113 §8.2.3
    /// and RFC 9114 §4.2.1 require the crumbs to be concatenated with "; " before the request
    /// reaches an HTTP/1.1 context. Sent as separate lines, an origin may read only the first one, or
    /// join them with commas and misread every cookie after it.
    /// </summary>
    /// <remarks>
    /// Applied to the outbound clone only, so the captured session still shows the crumbs the client
    /// sent. The joined value is shorter than the separate lines it replaces, and the decoded header
    /// list it is built from is already capped (<c>SETTINGS_MAX_HEADER_LIST_SIZE</c>), so it needs no
    /// bound of its own. Values were checked for CR, LF and NUL when the request was decoded.
    /// </remarks>
    internal static void JoinCookieCrumbs(HeaderCollection headers)
    {
        string? name = null;
        var count = 0;
        foreach (var header in headers)
        {
            if (!string.Equals(header.Name, "cookie", StringComparison.OrdinalIgnoreCase)) continue;
            name ??= header.Name;
            count++;
        }

        if (count > 1)
            headers.Set(name!, string.Join("; ", headers.GetValues(name!)));
    }
}
