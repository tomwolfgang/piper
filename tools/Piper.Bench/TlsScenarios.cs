using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Piper.Bench;

/// <summary>
/// Scenarios that decrypt HTTPS. The host runs with decryption on and a throwaway certificate authority
/// in the driver's temporary folder; the load generator trusts that authority inside its own process
/// (see <see cref="ScenarioContext.NewTlsClient"/>), so no certificate is installed anywhere. The origin's
/// certificate is self-signed and not validated by the host. Names under <c>bench.test</c> are remapped to
/// 127.0.0.1 inside the host, so each is a new host with a new certificate to mint.
/// </summary>
internal static class TlsScenarios
{
    private const int Hosts = 30;
    private static readonly TimeSpan OneWay = TimeSpan.FromMilliseconds(25);

    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("tls_handshake_30", "HTTPS through the proxy to 30 new hosts: first request (CONNECT, both handshakes, a minted certificate) against a repeat on that connection", HandshakeAsync),
        new("h2_c100", "HTTP/2 end to end through the proxy: one client connection, 100 parallel streams", Http2ParallelAsync),
        new("rtt50_h1", "Download of up to 1 GB over HTTP/1.1 from an origin 50 ms away (the proxy-to-origin leg is delayed)", c => RttDownloadAsync(c, http2: false)),
        new("rtt50_h2", "Download of up to 1 GB over HTTP/2 from an origin 50 ms away (the proxy-to-origin leg is delayed)", c => RttDownloadAsync(c, http2: true)),
    ];

    private static string[] HostArguments()
    {
        var names = new[] { "warm", "h2", "rtt" }.Concat(Enumerable.Range(1, Hosts).Select(i => $"h{i}"));
        return ["--decrypt", "--insecure-upstream", "--remap", string.Join(';', names.Select(n => $"127.0.0.1 {n}.bench.test"))];
    }

    private static async Task<double> TimedGetAsync(HttpClient client, string url, CancellationToken ct)
    {
        var began = Stopwatch.GetTimestamp();
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{url} answered {(int)response.StatusCode}: {body[..Math.Min(body.Length, 300)]}");
        return Stopwatch.GetElapsedTime(began).TotalMilliseconds;
    }

    private static async Task<Dictionary<string, double>> HandshakeAsync(ScenarioContext c)
    {
        await c.StartHostAsync(HostArguments()).ConfigureAwait(false);
        using var client = c.NewTlsClient(4, http2: false);
        string Url(string name) => $"https://{name}.bench.test:{c.Origin.TlsPort}/small";
        await TimedGetAsync(client, Url("warm"), c.Token).ConfigureAwait(false); // JIT and the first mint are not what is measured

        var first = new List<double>();
        var repeat = new List<double>();
        for (var i = 1; i <= Hosts; i++)
        {
            first.Add(await TimedGetAsync(client, Url($"h{i}"), c.Token).ConfigureAwait(false));
            repeat.Add(await TimedGetAsync(client, Url($"h{i}"), c.Token).ConfigureAwait(false));
        }

        return new()
        {
            ["first_p50_ms"] = BenchStats.Percentile(first, 0.5),
            ["first_max_ms"] = first.Max(),
            ["repeat_p50_ms"] = BenchStats.Percentile(repeat, 0.5),
            ["handshake_cost_p50_ms"] = BenchStats.Percentile(first.Zip(repeat, (f, r) => f - r).ToList(), 0.5),
        };
    }

    private static async Task<Dictionary<string, double>> Http2ParallelAsync(ScenarioContext c)
    {
        await c.StartHostAsync(HostArguments()).ConfigureAwait(false);
        var url = $"https://h2.bench.test:{c.Origin.TlsPort}/small";
        HttpClient NewClient() => c.NewTlsClient(100, http2: true);
        async Task<HttpResponseMessage> SendAsync(HttpClient client, CancellationToken ct)
        {
            var response = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (response.Version == HttpVersion.Version20) return response;
            response.Dispose();
            throw new HttpRequestException("The response was not HTTP/2."); // counted as an error, not hidden
        }

        // Bounded by request count as well as time: if upstream connections are not reused, every request
        // takes a loopback port that stays in TIME_WAIT for minutes, and the whole machine (not just this
        // tool) would run out of them. 3,000 requests keep that to a fraction of the range.
        await LoadGenerator.RunAsync(c, 100, TimeSpan.Zero, TimeSpan.FromSeconds(3), SendAsync, NewClient, maxRequests: 300).ConfigureAwait(false);
        return await LoadGenerator.RunAsync(c, 100, TimeSpan.Zero, c.Options.Duration, SendAsync, NewClient, maxRequests: 3000).ConfigureAwait(false);
    }

    /// <summary>Reads <c>/big?mb=1024</c> through the proxy for at most max(--duration, 10 s) and reports the
    /// rate, so a slow build is measured rather than waited for. The proxy dials a <see cref="LatencyRelay"/>
    /// in front of the origin, so only its upstream leg has the delay; the measured round trip is reported.</summary>
    private static async Task<Dictionary<string, double>> RttDownloadAsync(ScenarioContext c, bool http2)
    {
        var host = await c.StartHostAsync(HostArguments()).ConfigureAwait(false);
        await using var relay = new LatencyRelay(http2 ? c.Origin.TlsPort : c.Origin.HttpPort, OneWay);
        await using var pingRelay = new LatencyRelay(c.Origin.TunnelPort, OneWay);
        var rtt = await MeasureRoundTripAsync(pingRelay.Port, c.Token).ConfigureAwait(false);

        var url = http2 ? $"https://rtt.bench.test:{relay.Port}/big?mb=1024" : $"http://127.0.0.1:{relay.Port}/big?mb=1024";
        using var client = http2 ? c.NewTlsClient(1, http2: true) : c.NewClient(1, Timeout.InfiniteTimeSpan);
        client.Timeout = Timeout.InfiniteTimeSpan; // the window below bounds it
        using var window = CancellationTokenSource.CreateLinkedTokenSource(c.Token);
        window.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, c.Options.Duration.TotalSeconds)));

        var before = await host.StatsAsync(c.Token).ConfigureAwait(false);
        long received = 0;
        var began = 0L;
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, window.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(window.Token).ConfigureAwait(false);
            began = Stopwatch.GetTimestamp();
            var buffer = new byte[1 << 20];
            int read;
            while ((read = await body.ReadAsync(buffer, window.Token).ConfigureAwait(false)) > 0) received += read;
        }
        catch (OperationCanceledException) when (window.IsCancellationRequested && !c.Token.IsCancellationRequested)
        {
            // The window ended: whatever arrived is the measurement.
        }

        if (began == 0) throw new InvalidOperationException("No response arrived within the window.");
        var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        var after = await host.StatsAsync(c.Token).ConfigureAwait(false);
        return new()
        {
            ["mbps"] = received / 1048576.0 / seconds,
            ["moved_mbytes"] = received / 1048576.0,
            ["rtt_actual_ms"] = rtt,
            ["proxy_cpu_ms"] = after["cpums"] - before["cpums"],
            ["peak_ws_mb"] = after["peakws"] / 1048576.0,
        };
    }

    /// <summary>Median of five 1 KB echoes through a relay, straight to the origin's tunnel port.</summary>
    private static async Task<double> MeasureRoundTripAsync(int port, CancellationToken ct)
    {
        var samples = new List<double>();
        for (var i = 0; i < 5; i++)
        {
            using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(IPAddress.Loopback, port, ct).ConfigureAwait(false);
            await using var stream = new NetworkStream(socket);
            var payload = new byte[Origin.PayloadBytes];
            payload[0] = (byte)'P';
            var began = Stopwatch.GetTimestamp();
            await stream.WriteAsync(payload, ct).ConfigureAwait(false);
            await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
            samples.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds);
        }
        return BenchStats.Percentile(samples, 0.5);
    }
}
