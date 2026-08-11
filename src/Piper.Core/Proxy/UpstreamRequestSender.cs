using Piper.Core.Http;
using Piper.Core.Http2;

namespace Piper.Core.Proxy;

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
    public static async Task<HttpResponseData> SendAsync(
        UpstreamConnection upstream, HttpRequestData outbound, Action onRequestSent, CancellationToken ct)
    {
        if (upstream.IsHttp2)
        {
            onRequestSent();
            return await new Http2ClientConnection(upstream.Stream).SendRequestAsync(outbound, ct).ConfigureAwait(false);
        }

        MakeValidHttp11(outbound);

        await upstream.Stream.WriteAsync(outbound.ToOriginFormBytes(), ct).ConfigureAwait(false);
        await upstream.Stream.FlushAsync(ct).ConfigureAwait(false);
        onRequestSent();
        return await HttpParser.ReadResponseAsync(upstream.Reader, outbound.Method, ct).ConfigureAwait(false);
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
