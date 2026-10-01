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
            // A non-positive value (a setting gone wrong) must not throw in here and bar the host.
            if (options.Http3ResponseTimeout > TimeSpan.Zero) connection.IdleTimeout = options.Http3ResponseTimeout;
            if (options.Http3MaxResponseTime > TimeSpan.Zero) connection.MaxResponseTime = options.Http3MaxResponseTime;
            onRequestSent();
            var response = await connection.SendRequestAsync(outbound, ct).ConfigureAwait(false);

            altSvc.RecordSuccess(url.Host, url.Port);
            return response;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Blocked UDP, an unreachable QUIC endpoint, a handshake or idle timeout, a protocol
            // disagreement -- the same decision for the request: proceed over TCP as though h3 had
            // never been considered. How long the ORIGIN is left on TCP depends on what failed.
            if (ex is Http3GoAwayException or Http3ResponseTooLargeException) altSvc.RecordSoftFailure(url.Host, url.Port);
            else altSvc.RecordFailure(url.Host, url.Port);
            return null;
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Two outcomes, by what the failure says about h3 for this origin. GOAWAY for the request and a
    // response too big for the buffered path say nothing against h3 (TCP streams the big one; the
    // origin's other resources are fine), so the origin is left on TCP for the short soft cool-down
    // only -- not for none, or a hostile one would cost a handshake and a wasted response on every
    // request. Everything else is barred for the long one: no handshake, no response, a protocol
    // violation, a stall or a dropped connection part-way through (also what a UDP path that passes
    // small packets and loses large ones looks like), a response over the time ceiling.
}
