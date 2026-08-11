using System.Collections.Concurrent;

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
/// </remarks>
public sealed class AltSvcCache
{
    private sealed class Entry
    {
        public bool Advertised;
        public DateTimeOffset? FailedUntil;
    }

    private readonly ConcurrentDictionary<string, Entry> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a failed QUIC attempt suppresses further attempts to that host.</summary>
    public TimeSpan FailureCooldown { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Records an origin's <c>Alt-Svc</c> header. Only entries offering an "h3" protocol
    /// id count; "h3-29" and friends are drafts msquic will not negotiate, so they are ignored.</summary>
    public void RecordAltSvc(string host, string? altSvcHeader)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(altSvcHeader)) return;

        if (altSvcHeader.Trim().Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _hosts.TryRemove(host, out _);
            return;
        }

        if (!AdvertisesHttp3(altSvcHeader)) return;

        var entry = _hosts.GetOrAdd(host, _ => new Entry());
        entry.Advertised = true;
    }

    /// <summary>True when <paramref name="altSvcHeader"/> offers final-standard HTTP/3.</summary>
    public static bool AdvertisesHttp3(string altSvcHeader)
    {
        foreach (var alternative in altSvcHeader.Split(','))
        {
            var trimmed = alternative.Trim();
            var equals = trimmed.IndexOf('=');
            if (equals <= 0) continue;

            // Protocol ids may be quoted; "h3" must match exactly so "h3-29" does not qualify.
            var protocol = trimmed[..equals].Trim().Trim('"');
            if (protocol.Equals("h3", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Whether to attempt QUIC for this host right now.</summary>
    public bool ShouldAttempt(string host)
    {
        if (!Http3ClientConnection.IsSupported) return false;
        if (!_hosts.TryGetValue(host, out var entry) || !entry.Advertised) return false;
        if (entry.FailedUntil is { } until && DateTimeOffset.UtcNow < until) return false;
        return true;
    }

    /// <summary>Suppresses further attempts to this host until the cool-down expires.</summary>
    public void RecordFailure(string host)
    {
        var entry = _hosts.GetOrAdd(host, _ => new Entry());
        entry.FailedUntil = DateTimeOffset.UtcNow + FailureCooldown;
    }

    /// <summary>Clears a previous failure after a successful attempt.</summary>
    public void RecordSuccess(string host)
    {
        if (_hosts.TryGetValue(host, out var entry)) entry.FailedUntil = null;
    }
}
