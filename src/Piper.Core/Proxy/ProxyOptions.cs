using System.Diagnostics;
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

    /// <summary>
    /// How long to wait for the TCP connection to an origin before giving up. Name resolution and
    /// every address tried (see <see cref="ConnectionAttemptDelay"/>) are inside this one budget.
    /// </summary>
    public TimeSpan ConnectTimeout
    {
        get => _connectTimeout;
        set => _connectTimeout = ClampTimeout(value);
    }

    private TimeSpan _connectTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a connection attempt to an origin gets before the next address of the same name is
    /// tried in parallel with it (RFC 8305 "Connection Attempt Delay"; 250 ms is the value it
    /// recommends). A failed attempt starts the next one at once, and at most six addresses are tried.
    /// A name with an unreachable IPv6 address ahead of a working IPv4 one is therefore reached after
    /// this delay rather than after <see cref="ConnectTimeout"/>.
    /// </summary>
    public TimeSpan ConnectionAttemptDelay { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long a TLS handshake may take, separately for the handshake with an origin (it starts once
    /// the TCP connection is made) and for the one with a client of a decrypted tunnel. A peer that
    /// completes the TCP connection and then never speaks would otherwise hold the request, and its
    /// client connection, for ever. A handshake that outlasts it fails with an <see cref="IOException"/>.
    /// Clamped to 1 ms..1 day, like <see cref="ConnectTimeout"/>.
    /// </summary>
    public TimeSpan TlsHandshakeTimeout
    {
        get => _tlsHandshakeTimeout;
        set => _tlsHandshakeTimeout = ClampTimeout(value);
    }

    private TimeSpan _tlsHandshakeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a client may say nothing before its connection is closed. It applies to waiting for
    /// the next request on a kept-alive connection (the first request of a connection waits only
    /// <see cref="RequestHeadTimeout"/>), to each pause while a request body is being sent, and to
    /// each write of a response to a client that has stopped reading. An idle connection is closed
    /// quietly; a stalled request body is answered with <c>408</c> (best effort: a client that is
    /// not reading, or a TLS stream broken by the cancelled read, may not receive it) and recorded as
    /// a failed session. A body is also held to <see cref="MinRequestBodyBytesPerWindow"/>, so
    /// trickling a byte per window does not keep a connection for ever. Clamped to 1 ms..1 day
    /// (a non-positive or infinite value is not a way to switch it off).
    /// </summary>
    public TimeSpan IdleTimeout
    {
        get => _idleTimeout;
        set => _idleTimeout = ClampTimeout(value);
    }

    private TimeSpan _idleTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long a client has to send the first byte of the first request on a connection, then,
    /// from the first byte of any request, to finish its request line and headers. A budget rather
    /// than an idle timeout on purpose: a peer that drips one byte every few seconds stays "active"
    /// for ever and would otherwise hold a connection slot indefinitely. It does not cover the body
    /// or the TLS handshake of a decrypted tunnel (<see cref="TlsHandshakeTimeout"/>). Clamped to
    /// 1 ms..1 day.
    /// </summary>
    public TimeSpan RequestHeadTimeout
    {
        get => _requestHeadTimeout;
        set => _requestHeadTimeout = ClampTimeout(value);
    }

    private TimeSpan _requestHeadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The least a request body must deliver in each <see cref="IdleTimeout"/>-long window for the
    /// upload to be allowed to continue (default 1,024 bytes, about 9 bytes a second at the default
    /// timeout). Silence is already cut by <see cref="IdleTimeout"/>; this is what stops a client
    /// that sends one byte just inside every window, and so never looks silent. Zero (or less)
    /// switches the floor off.
    /// </summary>
    public long MinRequestBodyBytesPerWindow
    {
        get => _minRequestBodyBytesPerWindow;
        set => _minRequestBodyBytesPerWindow = Math.Max(0, value);
    }

    private long _minRequestBodyBytesPerWindow = 1024;

    /// <summary>
    /// How many client connections are served at once. A connection over the limit is not refused:
    /// it waits, unserved, in the operating system's accept queue (Piper asks for a backlog of 512;
    /// the system decides what it grants, and beyond that turns clients away) until a slot frees. When the limit is reached the
    /// connection that has been idle longest (waiting for a first byte, or between kept-alive
    /// requests) is closed to make room, so silent sockets cannot starve real clients. It bounds the
    /// number of connections, not what each may hold: a request body is still read into memory up to
    /// the declared length (at most 256 MB), so the worst case is this many such connections.
    /// There is deliberately no per-address quota: every local process connects from 127.0.0.1.
    /// Read when the proxy starts; clamped to 1..100,000.
    /// </summary>
    public int MaxConcurrentConnections
    {
        get => _maxConcurrentConnections;
        set => _maxConcurrentConnections = Math.Clamp(value, 1, 100_000);
    }

    private int _maxConcurrentConnections = 1024;

    private static readonly TimeSpan MinTimeout = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan MaxTimeout = TimeSpan.FromDays(1);

    /// <summary>A timeout that <c>CancelAfter</c> and <c>Task.Delay</c> accept: negative, zero and
    /// infinite values (and anything past a day) would otherwise throw where it is used, or hold a
    /// connection for ever.</summary>
    private static TimeSpan ClampTimeout(TimeSpan value) =>
        value < MinTimeout ? (value == Timeout.InfiniteTimeSpan ? MaxTimeout : MinTimeout)
        : value > MaxTimeout ? MaxTimeout
        : value;

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

    /// <summary>Where the running <see cref="ProxyServer"/> is listening; null while none is. Set by
    /// the server so that a connection made on its behalf can tell it would be dialling itself.</summary>
    internal IPEndPoint? ListeningEndpoint
    {
        get => _listeningEndpoint;
        set => _listeningEndpoint = value;
    }

    // Written when a run starts or ends and read on every connection thread.
    private volatile IPEndPoint? _listeningEndpoint;

    /// <summary>Whether the listening socket also accepts IPv4 clients while bound to an IPv6 address
    /// (a dual-mode socket). Meaningful only with <see cref="ListeningEndpoint"/>.</summary>
    internal bool ListeningDualMode
    {
        get => _listeningDualMode;
        set => _listeningDualMode = value;
    }

    private volatile bool _listeningDualMode;

    /// <summary>
    /// True when connecting to <paramref name="address"/>:<paramref name="port"/> would reach the
    /// running proxy itself. Such a request would be forwarded back into the proxy, each hop holding a
    /// connection slot, until the cap is exhausted. The port is compared first, so the common case
    /// costs nothing. Precise about address families: a listener on <c>0.0.0.0</c> is an IPv4 socket
    /// and does not own <c>::1</c>, where the same port may belong to another service; a listener on
    /// <c>::</c> owns IPv4 addresses only when its socket is dual-mode. An IPv4-mapped IPv6 address is
    /// the IPv4 address it maps.
    /// </summary>
    internal bool IsOwnEndpoint(IPAddress address, int port)
    {
        if (ListeningEndpoint is not { } own || port != own.Port) return false;

        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        var listensOnAnyV4 = own.Address.Equals(IPAddress.Any);
        var listensOnAnyV6 = own.Address.Equals(IPAddress.IPv6Any);
        if (!listensOnAnyV4 && !listensOnAnyV6) return address.Equals(own.Address);

        var reachable = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? listensOnAnyV4 || ListeningDualMode
            : listensOnAnyV6;
        if (!reachable) return false;

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        return LocalAddresses().Contains(address);
    }

    // The addresses of this machine's interfaces, as of the last time they were listed. Listing them
    // is a heavy system call and a client picks how often the question is asked (a name with many
    // addresses, on the proxy's port), so it is answered from a snapshot that is retaken after
    // LocalAddressLifetime. An address acquired within that time is not known yet: a request for it
    // on the proxy's port can loop, which the connection cap and idle eviction bound.
    private sealed record LocalAddressSnapshot(HashSet<IPAddress> Addresses, long TakenAt);

    private volatile LocalAddressSnapshot? _localAddresses;

    /// <summary>How long a listing of the machine's addresses is trusted.</summary>
    internal TimeSpan LocalAddressLifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Test seam: where the machine's addresses come from. Null means its network interfaces.</summary>
    internal Func<IEnumerable<IPAddress>>? LocalAddressSource { get; set; }

    private HashSet<IPAddress> LocalAddresses()
    {
        var snapshot = _localAddresses;
        if (snapshot is not null && Stopwatch.GetElapsedTime(snapshot.TakenAt) < LocalAddressLifetime)
            return snapshot.Addresses;

        HashSet<IPAddress> addresses;
        try
        {
            addresses = [.. (LocalAddressSource ?? ListInterfaceAddresses)()];
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            // The system will not list its interfaces (a restricted or containerised host): no address
            // is known to be ours, and that is remembered for the same time, not retried per request.
            // If one was in fact our own, the connect that follows ends in the same loop that was
            // always possible, bounded by the connection cap and eviction.
            addresses = [];
        }

        _localAddresses = new LocalAddressSnapshot(addresses, Stopwatch.GetTimestamp());
        return addresses;
    }

    private static IEnumerable<IPAddress> ListInterfaceAddresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address);

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
