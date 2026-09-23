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
    public static async Task<UpstreamResponse> SendAsync(
        UpstreamConnection upstream, HttpRequestData outbound, Action onRequestSent, CancellationToken ct)
    {
        if (upstream.IsHttp2)
        {
            onRequestSent();
            var h2 = new Http2ClientConnection(upstream.Stream);
            var h2Head = await h2.SendRequestHeadAsync(outbound, ct).ConfigureAwait(false);
            var bodyReader = upstream.Http2BodyReader =
                new HttpStreamReader(h2.ResponseBody) { IdleTimeout = upstream.Reader.IdleTimeout };
            return new UpstreamResponse(h2Head, DescribeHttp2Body(h2Head, outbound.Method), IsBuffered: false, bodyReader);
        }

        MakeValidHttp11(outbound);

        await upstream.Stream.WriteAsync(outbound.ToOriginFormBytes(), ct).ConfigureAwait(false);
        await upstream.Stream.FlushAsync(ct).ConfigureAwait(false);
        onRequestSent();

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
        outbound.HttpVersion = "HTTP/1.1";

        if (!outbound.Headers.Contains("Host") && outbound.Url is { } url)
            outbound.Headers.Set("Host", url.IsDefaultPort ? url.Host : $"{url.Host}:{url.Port}");
    }
}
