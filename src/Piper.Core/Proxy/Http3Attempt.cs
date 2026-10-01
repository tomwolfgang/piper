using Piper.Core.Http;
using Piper.Core.Http3;

namespace Piper.Core.Proxy;

/// <summary>
/// The one place that decides whether a given request goes out over HTTP/3, and quietly gives up
/// in favour of the normal TCP path whenever it cannot.
/// </summary>
/// <remarks>
/// Every failure mode here -- QUIC unsupported, UDP blocked, handshake timeout, the origin
/// dropping us mid-request -- resolves to "return null, let the caller use TCP". HTTP/3 is a
/// fidelity improvement, never a reason for a request to fail that would otherwise have worked.
/// </remarks>
internal static class Http3Attempt
{
    /// <summary>
    /// Safe methods only (RFC 9110 §9.2.1). Falling back to TCP after an h3 attempt failed means
    /// re-sending the request, and for anything with side effects that risks the origin processing
    /// it twice -- the h3 attempt may well have been received even though no response came back.
    /// Restricting the attempt to methods that carry no side effects removes that hazard entirely,
    /// and still covers what someone actually wants to watch over h3: page loads and assets.
    /// </summary>
    private static bool IsSafeToRetry(string method) =>
        method.Equals("GET", StringComparison.OrdinalIgnoreCase)
        || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
        || method.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase);

    /// <summary>Returns the response if h3 was attempted and succeeded, or null to fall back.</summary>
    public static async Task<HttpResponseData?> TryFetchAsync(
        HttpRequestData outbound, Uri url, ProxyOptions options, AltSvcCache altSvc,
        Action onRequestSent, CancellationToken ct)
    {
        if (!options.EnableHttp3Upstream) return null;
        if (!url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) return null; // h3 is always over QUIC+TLS
        if (!IsSafeToRetry(outbound.Method)) return null;
        if (!altSvc.TryGetEndpoint(url.Host, url.Port, out var udpPort)) return null;

        Http3ClientConnection? connection = null;

        try
        {
            // The handshake gets its own short budget: a network that drops UDP never answers, and
            // that must not cost the request more than a moment before it goes over TCP.
            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                handshake.CancelAfter(options.Http3ConnectTimeout);
                connection = await Http3ClientConnection.ConnectAsync(url.Host, udpPort, options, handshake.Token).ConfigureAwait(false);
            }

            // After the handshake the wait is an idle timeout owned by the connection, re-armed by the
            // origin's progress. A total budget here killed every long download, restarted it over TCP
            // from its first byte and barred the host from h3 for half an hour; a network that passes
            // the handshake and then drops UDP is still caught, because then nothing arrives.
            connection.IdleTimeout = options.Http3ResponseTimeout;
            onRequestSent();
            var response = await connection.SendRequestAsync(outbound, ct).ConfigureAwait(false);

            altSvc.RecordSuccess(url.Host);
            return response;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Blocked UDP, an unreachable QUIC endpoint, a handshake or idle timeout, a protocol
            // disagreement -- the same decision for the request: proceed over TCP as though h3 had
            // never been considered. Whether the HOST is blamed depends on what failed.
            if (BlamesHost(ex, connection)) altSvc.RecordFailure(url.Host);
            return null;
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Whether a failed attempt says h3 does not work for this host. It does not when the origin
    /// merely will not take this request on this connection (GOAWAY), when the response is too big for
    /// the buffered path (TCP streams it; the same host's other resources are fine), or when it was
    /// answering and then went quiet or dropped the connection: that is the network or the one
    /// resource, not the protocol, and barring the host would cost every later request its h3.
    /// Everything else -- no handshake, no response at all, a protocol violation -- counts.
    /// </summary>
    private static bool BlamesHost(Exception failure, Http3ClientConnection? connection) => failure switch
    {
        Http3GoAwayException or Http3ResponseTooLargeException => false,
        HttpParseException or Http3ProtocolException => true,
        _ when connection is { ResponseStarted: true } => false, // an idle timeout, or a connection dropped mid-response
        _ => true,
    };
}
