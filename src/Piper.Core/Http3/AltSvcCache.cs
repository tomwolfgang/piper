using System.Globalization;

namespace Piper.Core.Http3;

/// <summary>
/// Decides which origins are worth attempting over HTTP/3, driven by the <c>Alt-Svc</c> header
/// those origins send on their ordinary TLS responses (RFC 7838).
/// </summary>
/// <remarks>
/// The strategy is deliberately conservative about latency. An origin is never attempted over QUIC
/// on the first, cold request -- that is the one a user is actively waiting on, and paying a
/// speculative UDP handshake there is the worst possible place to spend time. Only once an origin
/// has told us, on a response we already have, that it speaks h3 does it become eligible. Failures
/// are remembered too: on a network that blocks UDP/443 (common, and true of the network this was
/// developed on) the first failure disables h3 for that origin for a cool-down period instead of
/// re-paying the timeout on every request.
/// <para>
/// An origin is a host AND a port: an advertisement made by <c>host:8443</c> says nothing about
/// <c>host:443</c>, which is a different server. The caller must say which origin the header came
/// from, and must only offer headers that arrived over TLS -- a plain-HTTP response can be forged
/// by anything on the path.
/// </para>
/// <para>
/// The header is hostile input, and so is the number of origins a page can make a client talk to,
/// so the cache is bounded in entries and in the header it will parse. It honours the freshness
/// lifetime (<c>ma</c>, default 24 hours) and the alternative's port. It does NOT follow an
/// alternative on another host: the origin would be choosing where Piper sends UDP, and a
/// misconfigured or malicious one could aim it at an internal address. Such an alternative is
/// ignored and the origin simply stays on TCP.
/// </para>
/// </remarks>
public sealed class AltSvcCache(TimeProvider? timeProvider = null)
{
    /// <summary>Origins remembered at once. Past it the spent entries go first, then the least
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
        public DateTimeOffset ExpiresAt;
        public DateTimeOffset? FailedUntil;
        public DateTimeOffset? SoftUntil;
        public long LastUsed;
    }

    /// <summary>One usable <c>h3</c> alternative out of an Alt-Svc header.</summary>
    internal readonly record struct Alternative(int Port, TimeSpan MaxAge);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<string, Entry> _origins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private long _tick;

    /// <summary>How long a failed QUIC attempt suppresses further attempts to that origin.</summary>
    public TimeSpan FailureCooldown { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How long an origin that merely cannot serve this kind of request over h3 (it sent
    /// GOAWAY, or the response does not fit the buffered path) is left on TCP. Short, because
    /// nothing is wrong with h3 itself -- but not zero, or such an origin would cost a QUIC handshake
    /// and a wasted response on every request.</summary>
    public TimeSpan SoftCooldown { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>How many origins are remembered right now.</summary>
    internal int Count
    {
        get { lock (_gate) return _origins.Count; }
    }

    private static string Key(string host, int port) => string.Create(CultureInfo.InvariantCulture, $"{host}:{port}");

    /// <summary>Records the <c>Alt-Svc</c> header of a response that arrived over TLS from
    /// <paramref name="host"/>:<paramref name="originPort"/>. Only an <c>h3</c> alternative on that
    /// host counts; "h3-29" and friends are drafts msquic will not negotiate.</summary>
    public void RecordAltSvc(string host, int originPort, string? altSvcHeader)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > MaxHostLength || originPort is < 1 or > 65535) return;
        if (string.IsNullOrWhiteSpace(altSvcHeader) || altSvcHeader.Length > MaxHeaderLength) return;

        var key = Key(host, originPort);

        // "clear" and ma=0 both withdraw the alternative; neither may erase a failure cool-down, or an
        // origin could end its own bar from a TLS response.
        if (altSvcHeader.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            lock (_gate) WithdrawLocked(key);
            return;
        }

        var found = TryParse(altSvcHeader, host, out var alternative, out var withdrawn);

        lock (_gate)
        {
            if (found)
            {
                var entry = Touch(key);
                entry.Advertised = true;
                entry.AltPort = alternative.Port;
                entry.ExpiresAt = _time.GetUtcNow() + alternative.MaxAge;
            }
            else if (withdrawn)
            {
                WithdrawLocked(key); // ma=0 (RFC 7838 §3.1) and nothing else usable
            }
        }
    }

    // Callers hold _gate. Keeps the entry for its cool-downs, if any are running.
    private void WithdrawLocked(string key)
    {
        if (_origins.TryGetValue(key, out var entry)) entry.Advertised = false;
    }

    /// <summary>True when <paramref name="altSvcHeader"/> offers final-standard HTTP/3 (an
    /// <c>h3</c> alternative with a usable authority, on any host).</summary>
    public static bool AdvertisesHttp3(string altSvcHeader) =>
        altSvcHeader.Length <= MaxHeaderLength && TryParse(altSvcHeader, originHost: null, out _, out _);

    /// <summary>
    /// Parses the first <c>h3</c> alternative that may be used: <c>h3="[host]:port"[; ma=seconds]</c>.
    /// With <paramref name="originHost"/> given, an alternative naming a different host is skipped.
    /// An alternative with <c>ma=0</c> is not usable but is not the end of the header either: the
    /// scan goes on, and <paramref name="withdrawn"/> says one was seen.
    /// </summary>
    internal static bool TryParse(string header, string? originHost, out Alternative alternative, out bool withdrawn)
    {
        alternative = default;
        withdrawn = false;

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

            if (maxAge <= TimeSpan.Zero)
            {
                withdrawn = true;
                continue;
            }

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
    /// unavailable or <see cref="TryGetRecorded"/> says no.
    /// </summary>
    public bool TryGetEndpoint(string host, int originPort, out int port)
    {
        port = 0;
        return Http3ClientConnection.IsSupported && TryGetRecorded(host, originPort, out port);
    }

    /// <summary>What the cache itself holds for this origin, whether or not QUIC can run on this
    /// machine: a fresh advertisement made by this very origin and no cool-down running.</summary>
    internal bool TryGetRecorded(string host, int originPort, out int port)
    {
        port = 0;
        lock (_gate)
        {
            if (!_origins.TryGetValue(Key(host, originPort), out var entry) || !entry.Advertised) return false;

            var now = _time.GetUtcNow();
            if (now >= entry.ExpiresAt)
            {
                entry.Advertised = false;
                return false;
            }
            if (entry.FailedUntil is { } failed && now < failed) return false;
            if (entry.SoftUntil is { } soft && now < soft) return false;

            entry.LastUsed = ++_tick;
            port = entry.AltPort;
            return true;
        }
    }

    /// <summary>Suppresses further attempts to this origin until the cool-down expires.</summary>
    public void RecordFailure(string host, int originPort) => Cool(host, originPort, FailureCooldown, soft: false);

    /// <summary>Leaves this origin on TCP for the short <see cref="SoftCooldown"/>: h3 is not at
    /// fault, but this origin cannot take requests over it right now.</summary>
    public void RecordSoftFailure(string host, int originPort) => Cool(host, originPort, SoftCooldown, soft: true);

    private void Cool(string host, int originPort, TimeSpan duration, bool soft)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > MaxHostLength || originPort is < 1 or > 65535) return;
        lock (_gate)
        {
            var entry = Touch(Key(host, originPort));
            var until = _time.GetUtcNow() + duration;
            if (soft) entry.SoftUntil = until;
            else entry.FailedUntil = until;
        }
    }

    /// <summary>Clears previous failures after a successful attempt.</summary>
    public void RecordSuccess(string host, int originPort)
    {
        lock (_gate)
        {
            if (_origins.TryGetValue(Key(host, originPort), out var entry))
            {
                entry.FailedUntil = null;
                entry.SoftUntil = null;
            }
        }
    }

    // Callers hold _gate. Finds or adds the entry and keeps the table within MaxEntries.
    private Entry Touch(string key)
    {
        if (!_origins.TryGetValue(key, out var entry))
        {
            if (_origins.Count >= MaxEntries) Trim();
            entry = new Entry();
            _origins[key] = entry;
        }
        entry.LastUsed = ++_tick;
        return entry;
    }

    // Nothing advertised that is still fresh and no cool-down running: remembering it is pointless.
    private static bool IsSpent(Entry entry, DateTimeOffset now) =>
        (!entry.Advertised || now >= entry.ExpiresAt)
        && (entry.FailedUntil is not { } failed || now >= failed)
        && (entry.SoftUntil is not { } soft || now >= soft);

    // Frees a quarter of the table so the cost of choosing is paid once per MaxEntries / 4 inserts,
    // not on every one: spent entries first, then the least recently used.
    private void Trim()
    {
        var now = _time.GetUtcNow();
        foreach (var stale in _origins.Where(pair => IsSpent(pair.Value, now)).Select(pair => pair.Key).ToList())
            _origins.Remove(stale);

        var target = MaxEntries - MaxEntries / 4;
        if (_origins.Count <= target) return;

        foreach (var oldest in _origins.OrderBy(pair => pair.Value.LastUsed).Take(_origins.Count - target).Select(pair => pair.Key).ToList())
            _origins.Remove(oldest);
    }
}
