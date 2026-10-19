using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;

namespace Piper.Bench;

/// <summary>
/// Sequential small HTTPS requests over one client connection to an origin a round trip away, to show what
/// the proxy's upstream connection reuse costs per request. The origin offers HTTP/2 and HTTP/1.1; a
/// <see cref="LatencyRelay"/> in front of it delays both directions and also charges each new connection its
/// TCP handshake round trip, so a fresh upstream connection costs what it would on a real network (TCP plus
/// the TLS handshake) and a reused one only the request's own round trip.
///
/// <c>seq_direct_*</c> go straight to the relay without the proxy (the client pins the origin's certificate),
/// the reference for the added latency. <c>seq_h1c_*</c> and <c>seq_h2c_*</c> go through the decrypting proxy
/// with an HTTP/1.1 or an HTTP/2 client; <c>_h2up</c> leaves <c>ProxyOptions.EnableHttp2Upstream</c> at its
/// default (on), <c>_h1up</c> turns it off. Only the proxy-to-origin leg is delayed; the client is on loopback.
/// </summary>
internal static class ReuseScenarios
{
    private const int Requests = 30;
    private const int WarmUp = 3;
    private const int WarmUpUndelayed = 100;
    private const int BodyKb = 16;
    private const string Name = "seq.bench.test";

    public static IReadOnlyList<Scenario> All { get; } = [.. For(50), .. For(10)];

    private static IEnumerable<Scenario> For(int rtt)
    {
        var what = $"{Requests} sequential {BodyKb} KB GETs on one client connection, origin {rtt} ms away and offering h2";
        yield return new($"seq_direct_h1_rtt{rtt}", $"{what}: HTTP/1.1 keep-alive straight to the origin, no proxy", c => RunAsync(c, rtt, direct: true, http2Client: false, http2Upstream: false));
        yield return new($"seq_direct_h2_rtt{rtt}", $"{what}: HTTP/2 straight to the origin, no proxy", c => RunAsync(c, rtt, direct: true, http2Client: true, http2Upstream: false));
        yield return new($"seq_h1c_h2up_rtt{rtt}", $"{what}: HTTP/1.1 client through the decrypting proxy, HTTP/2 upstream allowed (default)", c => RunAsync(c, rtt, direct: false, http2Client: false, http2Upstream: true));
        yield return new($"seq_h1c_h1up_rtt{rtt}", $"{what}: HTTP/1.1 client through the decrypting proxy, EnableHttp2Upstream off", c => RunAsync(c, rtt, direct: false, http2Client: false, http2Upstream: false));
        yield return new($"seq_h2c_h2up_rtt{rtt}", $"{what}: HTTP/2 client through the decrypting proxy, HTTP/2 upstream allowed (default)", c => RunAsync(c, rtt, direct: false, http2Client: true, http2Upstream: true));
        yield return new($"seq_h2c_h1up_rtt{rtt}", $"{what}: HTTP/2 client through the decrypting proxy, EnableHttp2Upstream off", c => RunAsync(c, rtt, direct: false, http2Client: true, http2Upstream: false));
    }

    private static async Task<Dictionary<string, double>> RunAsync(ScenarioContext c, int rttMs, bool direct, bool http2Client, bool http2Upstream)
    {
        var oneWay = TimeSpan.FromMilliseconds(rttMs / 2.0);
        HostClient? host = null;
        if (!direct)
        {
            string[] arguments = ["--decrypt", "--insecure-upstream", "--remap", $"127.0.0.1 {Name}"];
            host = await c.StartHostAsync(http2Upstream ? arguments : [.. arguments, "--no-h2-upstream"]).ConfigureAwait(false);
        }

        await using var relay = new LatencyRelay(c.Origin.TlsPort, oneWay, connectRoundTrip: true);
        double rtt;
        await using (var pingRelay = new LatencyRelay(c.Origin.TunnelPort, oneWay))
            rtt = await TlsScenarios.MeasureRoundTripAsync(pingRelay.Port, c.Token).ConfigureAwait(false);

        var directConnections = 0;
        using var client = direct ? NewDirectClient(c, relay.Port, http2Client, () => Interlocked.Increment(ref directConnections)) : c.NewTlsClient(1, http2Client);
        var expected = http2Client ? HttpVersion.Version20 : HttpVersion.Version11;
        var url = $"https://{Name}:{relay.Port}/kb?n={BodyKb}";

        async Task<double> GetAsync(string? target = null)
        {
            var began = Stopwatch.GetTimestamp();
            HttpResponseMessage response;
            try { response = await client.GetAsync(target ?? url, c.Token).ConfigureAwait(false); }
            catch (HttpRequestException ex) when (ex.InnerException is { } inner)
            {
                // The outer message ("could not be established, see inner exception") says nothing.
                throw new HttpRequestException($"{ex.Message} ({inner.GetType().Name}: {inner.Message}{(inner.InnerException is { } deeper ? " / " + deeper.Message : "")})", ex);
            }
            using var _ = response;
            var body = await response.Content.ReadAsByteArrayAsync(c.Token).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(began).TotalMilliseconds;
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{url} answered {(int)response.StatusCode}.");
            if (response.Version != expected) throw new HttpRequestException($"The response was HTTP/{response.Version}, not HTTP/{expected}.");
            if (body.Length != BodyKb * 1024) throw new InvalidDataException($"Short body: {body.Length} bytes.");
            return elapsed;
        }

        // JIT, including tier-1 recompilation, must not land in the measured CPU: the same paths are first run
        // many times against the origin itself, with no delay (another authority, so another client connection).
        if (!direct)
        {
            for (var i = 0; i < WarmUpUndelayed; i++) await GetAsync($"https://{Name}:{c.Origin.TlsPort}/kb?n={BodyKb}").ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(1), c.Token).ConfigureAwait(false); // background tier-1 compilation settles
        }

        // The first request pays the client's connection (and through the proxy CONNECT, the minted certificate
        // and the client's handshake): reported apart, then a few more on the measured connection.
        var first = await GetAsync().ConfigureAwait(false);
        for (var i = 1; i < WarmUp; i++) await GetAsync().ConfigureAwait(false);

        c.ResetConnectionCounts();
        var relayedBefore = relay.Accepted;
        var directBefore = Volatile.Read(ref directConnections);
        var before = host is null ? null : await host.StatsAsync(c.Token).ConfigureAwait(false);
        var latencies = new List<double>(Requests);
        var total = Stopwatch.StartNew();
        for (var i = 0; i < Requests; i++) latencies.Add(await GetAsync().ConfigureAwait(false));
        var totalMs = total.Elapsed.TotalMilliseconds;
        var after = host is null ? null : await host.StatsAsync(c.Token).ConfigureAwait(false);

        var metrics = new Dictionary<string, double>
        {
            ["p50_ms"] = BenchStats.Percentile(latencies, 0.50),
            ["p90_ms"] = BenchStats.Percentile(latencies, 0.90),
            ["p99_ms"] = BenchStats.Percentile(latencies, 0.99),
            ["total_ms"] = totalMs,
            ["first_ms"] = first,
            ["rtt_actual_ms"] = rtt,
            ["upstream_conns"] = relay.Accepted - relayedBefore,
            ["origin_conns"] = c.Origin.ConnectionCount,
            ["client_conns"] = direct ? Volatile.Read(ref directConnections) - directBefore : c.ClientConnections,
        };
        if (host is not null && before is not null && after is not null)
        {
            metrics["cpu_per_req_us"] = BenchStats.PerRequest(after["cpums"] - before["cpums"], Requests, 1000.0);
            // What the host burns doing nothing for a second, to tell its idle cost from the requests' cost.
            await Task.Delay(TimeSpan.FromSeconds(1), c.Token).ConfigureAwait(false);
            var idle = await host.StatsAsync(c.Token).ConfigureAwait(false);
            metrics["idle_cpu_per_s_ms"] = idle["cpums"] - after["cpums"];
            if (before.TryGetValue("cycles", out var c0) && after.TryGetValue("cycles", out var c1) && idle.TryGetValue("cycles", out var c2))
            {
                metrics["mcycles_per_req"] = (c1 - c0) / 1e6 / Requests;
                metrics["idle_mcycles_per_s"] = (c2 - c1) / 1e6;
            }
        }
        return metrics;
    }

    /// <summary>A client that goes straight to the relay, without the proxy, and accepts only the origin's own
    /// certificate (by hash: it is self-signed and named for no host). Nothing is added to any store.</summary>
    private static HttpClient NewDirectClient(ScenarioContext c, int port, bool http2, Action connected)
    {
        var pinned = c.Origin.CertificateHash;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            MaxConnectionsPerServer = 1,
            EnableMultipleHttp2Connections = false,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectCallback = async (_, ct) =>
            {
                connected();
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            certificate is not null && string.Equals(certificate.GetCertHashString(), pinned, StringComparison.OrdinalIgnoreCase);
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        if (http2)
        {
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        return client;
    }
}
