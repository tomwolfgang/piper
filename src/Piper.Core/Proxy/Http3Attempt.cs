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
        if (!altSvc.ShouldAttempt(url.Host)) return null;

        Http3ClientConnection? connection = null;

        // One budget covering the whole attempt, re-armed after the handshake. Bounding only the
        // handshake is not enough: a network that completes the QUIC handshake and then drops UDP
        // leaves the response hanging forever, and because the caller's own token is the one that
        // eventually fires, the failure would not be attributed to h3 and the host would stay
        // eligible -- hanging every subsequent request too.
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            attempt.CancelAfter(options.Http3ConnectTimeout);
            connection = await Http3ClientConnection.ConnectAsync(url.Host, url.Port, options, attempt.Token).ConfigureAwait(false);

            attempt.CancelAfter(options.Http3ResponseTimeout);
            onRequestSent();
            var response = await connection.SendRequestAsync(outbound, attempt.Token).ConfigureAwait(false);

            altSvc.RecordSuccess(url.Host);
            return response;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Blocked UDP, an unreachable QUIC endpoint, a handshake or response timeout, a
            // protocol disagreement -- all the same decision: stop trying this host for a while
            // and let the caller proceed over TCP as though h3 had never been considered.
            altSvc.RecordFailure(url.Host);
            return null;
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
