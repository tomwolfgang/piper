using System.Net;

namespace Piper.Core.Proxy;

public sealed class ProxyOptions
{
    public IPAddress ListenAddress { get; set; } = IPAddress.Loopback;

    public int Port { get; set; } = 8888;

    /// <summary>Terminate TLS so HTTPS bodies can be inspected. Requires the root CA to be trusted.</summary>
    public bool DecryptHttps { get; set; } = true;

    /// <summary>Hosts excluded from decryption; matched as suffixes. Useful for pinned endpoints.</summary>
    public List<string> DecryptionExclusions { get; } = ["update.microsoft.com", "windowsupdate.com"];

    /// <summary>Validate upstream server certificates. Turning this off makes MITM'd traffic insecure.</summary>
    public bool ValidateUpstreamCertificates { get; set; } = true;

    /// <summary>Reject a request body larger than this instead of buffering it.</summary>
    public long MaxBodyBytes { get; set; } = 128L * 1024 * 1024;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long to wait for an origin to send anything at all before giving up on a response.
    /// </summary>
    /// <remarks>
    /// Generous on purpose, and idle-based rather than a budget for the whole response. Long
    /// polling and server-sent events legitimately go quiet for minutes, and a large download
    /// legitimately takes them, so only unbroken silence counts. Without this a stalled origin
    /// held a request open for ever, which reaches the user as an application that has hung with
    /// nothing to show for it -- the failure this is here to turn into a reported 502.
    /// </remarks>
    public TimeSpan UpstreamIdleTimeout { get; set; } = TimeSpan.FromSeconds(300);

    /// <summary>
    /// How much of a response body to keep for inspection. Relaying is never refused because of
    /// it: a larger body still reaches the client whole, only the retained copy stops.
    /// </summary>
    /// <remarks>
    /// Raise it to capture more, at the cost of memory. The default is chosen to be far larger
    /// than anything worth reading in an inspector while still being small enough that hundreds of
    /// captured downloads cannot push the process into collecting garbage instead of proxying.
    /// </remarks>
    public long MaxCapturedBodyBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>Advertise only encodings we can decode, so captured bodies stay readable.</summary>
    public bool NormalizeAcceptEncoding { get; set; } = true;

    /// <summary>When set, replaces the User-Agent of every request forwarded by the running proxy.</summary>
    public string? GlobalUserAgent { get; set; }

    /// <summary>Offer HTTP/2 via ALPN on the browser-facing side of a decrypted tunnel. Defaults
    /// on: browsers already negotiate h2 with virtually every real site today, so silently
    /// forcing HTTP/1.1 through the proxy is itself the anomaly for a tool whose purpose is
    /// accurate interception.</summary>
    public bool EnableHttp2Downstream { get; set; } = true;

    /// <summary>Offer HTTP/2 via ALPN when connecting to origin servers. Defaults on for the same
    /// reason as <see cref="EnableHttp2Downstream"/>: forcing HTTP/1.1 upstream when the real
    /// origin would use h2 misrepresents the traffic shape (multiplexing, header compression,
    /// timing) this proxy exists to capture accurately.</summary>
    public bool EnableHttp2Upstream { get; set; } = true;

    /// <summary>
    /// Attempt HTTP/3 over QUIC when an origin has advertised it via <c>Alt-Svc</c>. Upstream
    /// only: a browser using a system proxy always tunnels over TCP and disables QUIC, so there is
    /// no browser-facing HTTP/3 to enable.
    /// </summary>
    /// <remarks>
    /// Defaults OFF, unlike the HTTP/2 toggles. QUIC needs outbound UDP/443, which plenty of
    /// corporate networks block outright -- on such a network every attempt costs a timeout before
    /// falling back to TCP. Off by default means nobody pays for a capability their network will
    /// not carry; turn it on deliberately when you want to see what an origin serves over h3.
    /// </remarks>
    public bool EnableHttp3Upstream { get; set; }

    /// <summary>Optional per-host origin overrides, applied without changing the requested host identity.</summary>
    public HostRemapping HostRemapping { get; } = new();

    /// <summary>Ordered rules that can answer a request locally instead of sending it upstream.</summary>
    public AutoResponder AutoResponder { get; } = new();

    /// <summary>How long to wait for a QUIC handshake before abandoning h3 and falling back to
    /// TCP. Deliberately short -- this is speculative work in front of a request that has a
    /// perfectly good TCP path available.</summary>
    public TimeSpan Http3ConnectTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long an HTTP/3 request may go without the origin making progress (a complete
    /// header section, or body bytes) once the QUIC handshake has succeeded. An idle timeout, not a
    /// total: a long download that keeps arriving is never cut, while a network that passes the
    /// handshake but drops later UDP is, so the request falls back to TCP instead of hanging.</summary>
    public TimeSpan Http3ResponseTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The longest one HTTP/3 request may take from the end of the handshake to the last
    /// byte of the response, however steadily it progresses. A ceiling over the idle timeout, which
    /// alone would let an origin trickle a response for as long as it likes. A response that needs
    /// longer falls back to TCP, which streams it.</summary>
    public TimeSpan Http3MaxResponseTime { get; set; } = TimeSpan.FromMinutes(15);

    public bool ShouldDecrypt(string host)
    {
        if (!DecryptHttps) return false;
        foreach (var exclusion in DecryptionExclusions)
        {
            if (string.IsNullOrWhiteSpace(exclusion)) continue;
            if (host.Equals(exclusion, StringComparison.OrdinalIgnoreCase)) return false;
            if (host.EndsWith("." + exclusion, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }
}
