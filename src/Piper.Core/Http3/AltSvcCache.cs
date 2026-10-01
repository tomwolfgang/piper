using System.Globalization;

namespace Piper.Core.Http3;

/// <summary>
/// Decides which origins are worth attempting over HTTP/3, driven by the <c>Alt-Svc</c> header
/// those origins send on their ordinary TCP responses (RFC 7838).
/// </summary>
/// <remarks>
/// The strategy is deliberately conservative about latency. A host is never attempted over QUIC
/// on the first, cold request -- that is the one a user is actively waiting on, and paying a
/// speculative UDP handshake there is the worst possible place to spend time. Only once an origin
/// has told us, on a response we already have, that it speaks h3 does it become eligible. Failures
/// are remembered too: on a network that blocks UDP/443 (common, and true of the network this was
/// developed on) the first failure disables h3 for that host for a cool-down period instead of
/// re-paying the timeout on every request.
/// <para>
/// The header is hostile input, and so is the number of hosts a page can make a client talk to, so
/// the cache is bounded in entries and in the header it will parse. It honours the freshness
/// lifetime (<c>ma</c>, default 24 hours) and the alternative's port. It does NOT follow an
/// alternative on another host: the origin would be choosing where Piper sends UDP, and a
/// misconfigured or malicious one could aim it at an internal address. Such an alternative is
/// ignored and the origin simply stays on TCP.
/// </para>
/// </remarks>
public sealed class AltSvcCache(TimeProvider? timeProvider = null)
{
    /// <summary>Hosts remembered at once. Past it the expired entries go first, then the least
    /// recently used.</summary>
    public const int MaxEntries = 2048;

    /// <summary>The longest Alt-Svc header value parsed. A longer one is ignored outright.</summary>
    public const int MaxHeaderLength = 8 * 1024;

    private const int MaxAlternatives = 16;
    private const int MaxHostLength = 255;
    private static readonly TimeSpan DefaultMaxAge = TimeSpan.FromHours(24);
    private static readonly TimeSpan LongestMaxAge = TimeSpan.FromDays(30);

    private sealed class Entry
    {
        public bool Advertised;
        public int AltPort;
        public int? OriginPort;
        public DateTimeOffset ExpiresAt;
        public DateTimeOffset? FailedUntil;
        public long LastUsed;
    }

    /// <summary>One usable <c>h3</c> alternative out of an Alt-Svc header.</summary>
    internal readonly record struct Alternative(int Port, TimeSpan MaxAge);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Entry> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private long _tick;

    /// <summary>How long a failed QUIC attempt suppresses further attempts to that host.</summary>
    public TimeSpan FailureCooldown { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How many hosts are remembered right now.</summary>
    internal int Count
    {
        get { lock (_gate) return _hosts.Count; }
    }

    /// <summary>Records an origin's <c>Alt-Svc</c> header. Only an <c>h3</c> alternative on the
    /// origin's own host counts; "h3-29" and friends are drafts msquic will not negotiate.</summary>
    public void RecordAltSvc(string host, string? altSvcHeader) => RecordAltSvc(host, null, altSvcHeader);

    /// <summary>As above, for a caller that knows the port the header arrived on. An alternative is
    /// only ever used for that origin port; without it, an alternative on another port than the one
    /// asked for is not used at all.</summary>
    public void RecordAltSvc(string host, int? originPort, string? altSvcHeader)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > MaxHostLength) return;
        if (string.IsNullOrWhiteSpace(altSvcHeader) || altSvcHeader.Length > MaxHeaderLength) return;

        // "clear" and ma=0 both withdraw the alternative; neither may erase a failure cool-down, or an
        // origin could end its own bar from a TCP response.
        if (altSvcHeader.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            Withdraw(host);
            return;
        }

        if (!TryParse(altSvcHeader, host, out var alternative)) return;

        lock (_gate)
        {
            // ma=0 withdraws the alternative (RFC 7838 §3.1).
            if (alternative.MaxAge <= TimeSpan.Zero)
            {
                WithdrawLocked(host);
                return;
            }

            var entry = Touch(host);
            entry.Advertised = true;
            entry.AltPort = alternative.Port;
            entry.OriginPort = originPort;
            entry.ExpiresAt = _time.GetUtcNow() + alternative.MaxAge;
        }
    }

    private void Withdraw(string host)
    {
        lock (_gate) WithdrawLocked(host);
    }

    // Callers hold _gate. Keeps the entry for its failure cool-down, if one is running.
    private void WithdrawLocked(string host)
    {
        if (_hosts.TryGetValue(host, out var entry)) entry.Advertised = false;
    }

    /// <summary>True when <paramref name="altSvcHeader"/> offers final-standard HTTP/3 (an
    /// <c>h3</c> alternative with a usable authority, on any host).</summary>
    public static bool AdvertisesHttp3(string altSvcHeader) =>
        altSvcHeader.Length <= MaxHeaderLength && TryParse(altSvcHeader, originHost: null, out _);

    /// <summary>
    /// Parses the first <c>h3</c> alternative that may be used: <c>h3="[host]:port"[; ma=seconds]</c>.
    /// With <paramref name="originHost"/> given, an alternative naming a different host is skipped.
    /// </summary>
    internal static bool TryParse(string header, string? originHost, out Alternative alternative)
    {
        alternative = default;

        var examined = 0;
        foreach (var raw in header.Split(','))
        {
            if (++examined > MaxAlternatives) break;

            var parts = raw.Split(';');
            var head = parts[0].Trim();
            var equals = head.IndexOf('=');
            if (equals <= 0) continue;

            // Protocol ids may be quoted; "h3" must match exactly so "h3-29" does not qualify.
            if (!head[..equals].Trim().Trim('"').Equals("h3", StringComparison.OrdinalIgnoreCase)) continue;

            if (!TryParseAuthority(head[(equals + 1)..], originHost, out var port)) continue;

            if (!TryParseMaxAge(parts, out var maxAge)) continue;

            alternative = new Alternative(port, maxAge);
            return true;
        }
        return false;
    }

    // The ma parameter (RFC 7838 §3.1), default 24 hours. Digits only: no sign, no exponent, no
    // separators. One that is present but unparseable makes the alternative unusable rather than
    // quietly falling back to the default.
    private static bool TryParseMaxAge(string[] parts, out TimeSpan maxAge)
    {
        maxAge = DefaultMaxAge;
        for (var i = 1; i < parts.Length; i++)
        {
            var parameter = parts[i].Trim();
            var split = parameter.IndexOf('=');
            if (split <= 0 || !parameter[..split].Trim().Equals("ma", StringComparison.OrdinalIgnoreCase)) continue;

            var text = parameter[(split + 1)..].Trim().Trim('"');
            if (text.Length == 0 || !text.All(char.IsAsciiDigit)) return false;

            // Longer than a long can hold is still just a large number: clamp it like any other.
            var seconds = text.Length > 15 ? long.MaxValue : long.Parse(text, CultureInfo.InvariantCulture);
            maxAge = seconds >= LongestMaxAge.TotalSeconds ? LongestMaxAge : TimeSpan.FromSeconds(seconds);
            return true;
        }
        return true;
    }

    private static bool TryParseAuthority(string text, string? originHost, out int port)
    {
        port = 0;
        var authority = text.Trim().Trim('"');

        // The port follows the last colon; what precedes it is empty (same host) or a host name,
        // an IPv6 literal in brackets included.
        var colon = authority.LastIndexOf(':');
        if (colon < 0) return false;

        var digits = authority[(colon + 1)..];
        if (digits.Length is 0 or > 5 || !digits.All(char.IsAsciiDigit)) return false;
        var value = int.Parse(digits, CultureInfo.InvariantCulture);
        if (value is < 1 or > 65535) return false;

        var alternativeHost = authority[..colon].Trim();
        if (originHost is not null && alternativeHost.Length > 0
            && !alternativeHost.Trim('[', ']').Equals(originHost.Trim('[', ']'), StringComparison.OrdinalIgnoreCase))
            return false;

        port = value;
        return true;
    }

    /// <summary>
    /// Whether to attempt QUIC for this origin right now, and on which UDP port. False when QUIC is
    /// unavailable, nothing fresh was advertised, a recent attempt failed, or the advertised port
    /// would be used for an origin it was not advertised for.
    /// </summary>
    public bool TryGetEndpoint(string host, int originPort, out int port)
    {
        port = 0;
        if (!Http3ClientConnection.IsSupported) return false;

        lock (_gate)
        {
            if (!_hosts.TryGetValue(host, out var entry) || !entry.Advertised) return false;

            var now = _time.GetUtcNow();
            if (now >= entry.ExpiresAt)
            {
                entry.Advertised = false;
                return false;
            }
            if (entry.FailedUntil is { } until && now < until) return false;

            // Advertised on one port, asked about on another: only the same-port case is known to
            // be right, because an h3 server on host:443 answers for host:443, not host:8444.
            if (entry.OriginPort is { } advertisedFor ? advertisedFor != originPort : entry.AltPort != originPort)
                return false;

            entry.LastUsed = ++_tick;
            port = entry.AltPort;
            return true;
        }
    }

    /// <summary>Suppresses further attempts to this host until the cool-down expires.</summary>
    public void RecordFailure(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > MaxHostLength) return;
        lock (_gate)
        {
            var entry = Touch(host);
            entry.FailedUntil = _time.GetUtcNow() + FailureCooldown;
        }
    }

    /// <summary>Clears a previous failure after a successful attempt.</summary>
    public void RecordSuccess(string host)
    {
        lock (_gate)
        {
            if (_hosts.TryGetValue(host, out var entry)) entry.FailedUntil = null;
        }
    }

    // Callers hold _gate. Finds or adds the entry and keeps the table within MaxEntries.
    private Entry Touch(string host)
    {
        if (!_hosts.TryGetValue(host, out var entry))
        {
            if (_hosts.Count >= MaxEntries) Trim();
            entry = new Entry();
            _hosts[host] = entry;
        }
        entry.LastUsed = ++_tick;
        return entry;
    }

    // Nothing advertised that is still fresh and no cool-down running: remembering it is pointless.
    private static bool IsSpent(Entry entry, DateTimeOffset now) =>
        (!entry.Advertised || now >= entry.ExpiresAt) && (entry.FailedUntil is not { } until || now >= until);

    // Frees a quarter of the table so the cost of choosing is paid once per MaxEntries / 4 inserts,
    // not on every one: expired and idle entries first, then the least recently used.
    private void Trim()
    {
        var now = _time.GetUtcNow();
        foreach (var stale in _hosts.Where(pair => IsSpent(pair.Value, now)).Select(pair => pair.Key).ToList())
            _hosts.Remove(stale);

        var target = MaxEntries - MaxEntries / 4;
        if (_hosts.Count <= target) return;

        foreach (var oldest in _hosts.OrderBy(pair => pair.Value.LastUsed).Take(_hosts.Count - target).Select(pair => pair.Key).ToList())
            _hosts.Remove(oldest);
    }
}
