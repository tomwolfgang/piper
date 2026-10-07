using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Piper.Bench;

internal sealed record Scenario(string Name, string Description, Func<ScenarioContext, Task<Dictionary<string, double>>> Run);

/// <summary>The scenarios. Each starts a fresh proxy host, so no run inherits another's heap, caches or
/// JIT state, warms it up unmeasured, and returns metrics named with their unit (see
/// <see cref="BenchStats.Direction"/>). Names are stable: results files are compared by them.</summary>
internal static class Scenarios
{
    private static readonly TimeSpan Ramp = TimeSpan.FromSeconds(2);
    private const int SessionTarget = 100_000;

    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("startup", "Process start to the first response through the proxy", StartupAsync),
        Get(1), Get(16), Get(64),
        new("hdr_c16", "Keep-alive GET with 40 request and 40 response headers, 16 concurrent", HeadersAsync),
        new("download_cl", "256 MB download with Content-Length", c => DownloadAsync(c, "/big", 256)),
        new("download_chunked", "256 MB chunked download", c => DownloadAsync(c, "/bigchunked", 256)),
        new("upload_64mb", "64 MB upload with Content-Length", UploadAsync),
        new("tunnel_ping", "CONNECT tunnel: setup plus a 1 KB round trip, sequential", TunnelPingAsync),
        new("tunnel_down", "CONNECT tunnel: 256 MB download", c => TunnelTransferAsync(c, 'D')),
        new("tunnel_up", "CONNECT tunnel: 256 MB upload", c => TunnelTransferAsync(c, 'U')),
        new("sessions_100k", "100,000 captured sessions: memory per session and heap after a collection", SessionsAsync),
    ];

    private static Scenario Get(int concurrency) => new($"get_c{concurrency}",
        $"Keep-alive GET of 256 bytes, {concurrency} concurrent", async c =>
        {
            await c.StartHostAsync().ConfigureAwait(false);
            await WarmUpAsync(c).ConfigureAwait(false);
            var url = c.OriginUrl + "/small";
            return await LoadGenerator.RunAsync(c, concurrency, Ramp, c.Options.Duration, (client, ct) => client.GetAsync(url, ct)).ConfigureAwait(false);
        });

    // JIT and the first pool growth are not what is being measured.
    private static async Task WarmUpAsync(ScenarioContext c)
    {
        var url = c.OriginUrl + "/small";
        await LoadGenerator.RunAsync(c, 16, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), (client, ct) => client.GetAsync(url, ct)).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, double>> StartupAsync(ScenarioContext c)
    {
        var timer = Stopwatch.StartNew();
        var host = await c.StartHostAsync().ConfigureAwait(false);
        using var client = c.NewClient(1);
        using var response = await client.GetAsync(c.OriginUrl + "/small", c.Token).ConfigureAwait(false);
        await response.Content.ReadAsByteArrayAsync(c.Token).ConfigureAwait(false);
        return new() { ["first_response_ms"] = timer.Elapsed.TotalMilliseconds, ["host_start_ms"] = host.StartMs };
    }

    private static async Task<Dictionary<string, double>> HeadersAsync(ScenarioContext c)
    {
        await c.StartHostAsync().ConfigureAwait(false);
        await WarmUpAsync(c).ConfigureAwait(false);
        var url = c.OriginUrl + "/hdr";
        return await LoadGenerator.RunAsync(c, 16, Ramp, c.Options.Duration, (client, ct) =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            for (var i = 0; i < 40; i++) request.Headers.TryAddWithoutValidation($"X-Bench-Header-{i}", "bench-value-" + new string('h', 36));
            return client.SendAsync(request, ct);
        }).ConfigureAwait(false);
    }

    /// <summary>Counters of a transfer: its throughput, the proxy's CPU and GC pauses during it, and the
    /// most memory the proxy process ever held.</summary>
    private static async Task<Dictionary<string, double>> MeasureTransferAsync(ScenarioContext c, double megabytes, Func<Task> transfer)
    {
        var before = await c.Host.StatsAsync(c.Token).ConfigureAwait(false);
        var began = Stopwatch.GetTimestamp();
        await transfer().ConfigureAwait(false);
        var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
        var after = await c.Host.StatsAsync(c.Token).ConfigureAwait(false);
        return new()
        {
            ["mbps"] = megabytes / seconds,
            ["proxy_cpu_ms"] = after["cpums"] - before["cpums"],
            ["gc_pause_ms"] = after["gcpausems"] - before["gcpausems"],
            ["peak_ws_mb"] = after["peakws"] / 1048576.0,
        };
    }

    private static async Task<Dictionary<string, double>> DownloadAsync(ScenarioContext c, string path, int megabytes)
    {
        await c.StartHostAsync().ConfigureAwait(false);
        using var client = c.NewClient(1, TimeSpan.FromMinutes(3));
        async Task FetchAsync(int mb)
        {
            using var response = await client.GetAsync($"{c.OriginUrl}{path}?mb={mb}", HttpCompletionOption.ResponseHeadersRead, c.Token).ConfigureAwait(false);
            await using var body = await response.Content.ReadAsStreamAsync(c.Token).ConfigureAwait(false);
            var buffer = new byte[1 << 20];
            long received = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, c.Token).ConfigureAwait(false)) > 0) received += read;
            if (received != (long)mb * 1024 * 1024) throw new InvalidDataException($"Short body: {received} bytes of {mb} MB.");
        }
        await FetchAsync(32).ConfigureAwait(false);
        return await MeasureTransferAsync(c, megabytes, () => FetchAsync(megabytes)).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, double>> UploadAsync(ScenarioContext c)
    {
        await c.StartHostAsync().ConfigureAwait(false);
        using var client = c.NewClient(1, TimeSpan.FromMinutes(3));
        async Task PostAsync(int mb)
        {
            var length = (long)mb * 1024 * 1024;
            using var body = new StreamContent(new ZeroStream(length));
            body.Headers.ContentLength = length;
            using var response = await client.PostAsync(c.OriginUrl + "/up", body, c.Token).ConfigureAwait(false);
            var counted = await response.Content.ReadAsStringAsync(c.Token).ConfigureAwait(false);
            if (counted != length.ToString("D12", System.Globalization.CultureInfo.InvariantCulture)) throw new InvalidDataException($"The origin counted {counted} bytes, not {length}.");
        }
        await PostAsync(16).ConfigureAwait(false);
        return await MeasureTransferAsync(c, 64, () => PostAsync(64)).ConfigureAwait(false);
    }

    private static async Task<Stream> OpenTunnelAsync(ScenarioContext c, char mode)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await socket.ConnectAsync(IPAddress.Loopback, c.Host.Port, c.Token).ConfigureAwait(false); }
        catch
        {
            socket.Dispose();
            throw;
        }

        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT 127.0.0.1:{c.Origin.TunnelPort} HTTP/1.1\r\nHost: 127.0.0.1:{c.Origin.TunnelPort}\r\n\r\n"), c.Token).ConfigureAwait(false);
            var head = new byte[4096];
            var length = 0;
            while (length < 4 || !head.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8))
            {
                if (length == head.Length) throw new InvalidDataException("The CONNECT response head is over 4 KB.");
                if (await stream.ReadAsync(head.AsMemory(length, 1), c.Token).ConfigureAwait(false) != 1) throw new EndOfStreamException("The proxy closed the connection during CONNECT.");
                length++;
            }
            var status = Encoding.ASCII.GetString(head, 0, length);
            if (!status.StartsWith("HTTP/1.1 200", StringComparison.Ordinal)) throw new InvalidDataException("CONNECT was refused: " + status.Split('\r')[0]);
            var first = new byte[Origin.PayloadBytes];
            first[0] = (byte)mode;
            await stream.WriteAsync(first, c.Token).ConfigureAwait(false);
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<Dictionary<string, double>> TunnelPingAsync(ScenarioContext c)
    {
        await c.StartHostAsync().ConfigureAwait(false);
        var millis = new List<double>();
        for (var i = 0; i < 350; i++)
        {
            var began = Stopwatch.GetTimestamp();
            await using var tunnel = await OpenTunnelAsync(c, 'P').ConfigureAwait(false);
            await tunnel.ReadExactlyAsync(new byte[Origin.PayloadBytes], c.Token).ConfigureAwait(false);
            if (i >= 50) millis.Add(Stopwatch.GetElapsedTime(began).TotalMilliseconds); // the first 50 are warm-up
        }
        return new() { ["p50_ms"] = BenchStats.Percentile(millis, 0.5), ["p99_ms"] = BenchStats.Percentile(millis, 0.99) };
    }

    private static async Task<Dictionary<string, double>> TunnelTransferAsync(ScenarioContext c, char mode)
    {
        await c.StartHostAsync().ConfigureAwait(false);
        async Task PassAsync()
        {
            await using var tunnel = await OpenTunnelAsync(c, mode).ConfigureAwait(false);
            if (mode == 'D')
            {
                var buffer = new byte[1 << 20];
                long received = 0;
                int read;
                while ((read = await tunnel.ReadAsync(buffer, c.Token).ConfigureAwait(false)) > 0) received += read;
                if (received != (long)Origin.TunnelMegabytes * 1024 * 1024) throw new InvalidDataException($"Short tunnel download: {received} bytes.");
            }
            else
            {
                var chunk = new byte[64 * 1024];
                new Random(2).NextBytes(chunk);
                for (long sent = 0; sent < (long)Origin.TunnelMegabytes * 1024 * 1024; sent += chunk.Length)
                    await tunnel.WriteAsync(chunk, c.Token).ConfigureAwait(false);
                ((NetworkStream)tunnel).Socket.Shutdown(SocketShutdown.Send);
                await tunnel.ReadExactlyAsync(new byte[Origin.PayloadBytes], c.Token).ConfigureAwait(false);
            }
        }
        await PassAsync().ConfigureAwait(false); // warm-up
        return await MeasureTransferAsync(c, Origin.TunnelMegabytes, PassAsync).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, double>> SessionsAsync(ScenarioContext c)
    {
        var host = await c.StartHostAsync().ConfigureAwait(false);
        await host.SetCapacityAsync(SessionTarget * 2, c.Token).ConfigureAwait(false);
        var url = c.OriginUrl + "/small";
        await LoadGenerator.RunAsync(c, 16, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(2), (client, ct) => client.GetAsync(url, ct)).ConfigureAwait(false);
        var baseline = await host.CollectAsync(c.Token).ConfigureAwait(false);

        using var client = c.NewClient(32, TimeSpan.FromSeconds(20));
        long next = 0, errors = 0;
        var began = Stopwatch.GetTimestamp();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            while (Interlocked.Increment(ref next) is var i && i <= SessionTarget)
            {
                try
                {
                    using var response = await client.GetAsync($"{url}?i={i}&pad=aaaaaaaaaaaaaaaaaaaaaaaa", c.Token).ConfigureAwait(false);
                    await response.Content.ReadAsByteArrayAsync(c.Token).ConfigureAwait(false);
                    if ((int)response.StatusCode != 200) Interlocked.Increment(ref errors);
                }
                catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !c.Token.IsCancellationRequested) { Interlocked.Increment(ref errors); }
            }
        }, CancellationToken.None))).ConfigureAwait(false);
        var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;

        var peak = await host.StatsAsync(c.Token).ConfigureAwait(false);
        var settled = await host.CollectAsync(c.Token).ConfigureAwait(false);
        var captured = settled["sessions"] - baseline["sessions"];
        return new()
        {
            ["rps"] = SessionTarget / seconds,
            ["errors"] = errors,
            ["sessions_captured"] = captured,
            ["session_bytes"] = captured == 0 ? 0 : Math.Max(0, settled["heap"] - baseline["heap"]) / (double)captured,
            ["heap_after_gc_mb"] = settled["heap"] / 1048576.0,
            ["peak_ws_mb"] = peak["peakws"] / 1048576.0,
            ["ws_after_gc_mb"] = settled["ws"] / 1048576.0,
        };
    }

    /// <summary>A request body of zeros that is never held in memory.</summary>
    private sealed class ZeroStream : Stream
    {
        private readonly long _length;
        private long _left;

        public ZeroStream(long length) => _left = _length = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _length - _left; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var n = (int)Math.Min(buffer.Length, _left);
            buffer[..n].Clear();
            _left -= n;
            return n;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
