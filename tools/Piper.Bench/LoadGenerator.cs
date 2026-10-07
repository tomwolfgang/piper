using System.Diagnostics;

namespace Piper.Bench;

/// <summary>A closed-loop load: <c>concurrency</c> workers each send, read the whole response and
/// send again, for a fixed time, so throughput and latency are measured at a stated concurrency.</summary>
internal static class LoadGenerator
{
    // Bounds the memory a long --duration can use; the count of completed requests is not capped.
    private const int MaxSamplesPerWorker = 250_000;

    public static async Task<Dictionary<string, double>> RunAsync(ScenarioContext context, int concurrency, TimeSpan ramp, TimeSpan duration,
        Func<HttpClient, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var ct = context.Token;
        var host = context.Host;
        using var client = context.NewClient(concurrency);
        context.ResetConnectionCounts();

        long errors = 0, timeouts = 0;
        var measuring = false;
        var stop = false;
        var latencies = new List<long>[concurrency];
        var completed = new long[concurrency];
        var workers = Enumerable.Range(0, concurrency).Select(w => Task.Run(async () =>
        {
            var samples = latencies[w] = new List<long>(8192);
            while (!Volatile.Read(ref stop))
            {
                var began = Stopwatch.GetTimestamp();
                try
                {
                    using var response = await send(client, ct).ConfigureAwait(false);
                    await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                    if ((int)response.StatusCode != 200) Interlocked.Increment(ref errors);
                    else if (Volatile.Read(ref measuring))
                    {
                        completed[w]++;
                        if (samples.Count < MaxSamplesPerWorker) samples.Add(Stopwatch.GetTimestamp() - began);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Interlocked.Increment(ref timeouts); }
                catch (HttpRequestException) { Interlocked.Increment(ref errors); }
            }
        }, CancellationToken.None)).ToArray();

        try
        {
            await Task.Delay(ramp, ct).ConfigureAwait(false);
            Interlocked.Exchange(ref errors, 0);
            Interlocked.Exchange(ref timeouts, 0);
            var before = await host.StatsAsync(ct).ConfigureAwait(false);
            var began = Stopwatch.GetTimestamp();
            Volatile.Write(ref measuring, true);
            await Task.Delay(duration, ct).ConfigureAwait(false);
            Volatile.Write(ref measuring, false);
            var seconds = Stopwatch.GetElapsedTime(began).TotalSeconds;
            var after = await host.StatsAsync(ct).ConfigureAwait(false);
            Volatile.Write(ref stop, true);
            await Task.WhenAll(workers).ConfigureAwait(false);

            var total = completed.Sum();
            var millis = latencies.SelectMany(l => l).Select(t => t * 1000.0 / Stopwatch.Frequency).ToArray();
            return new Dictionary<string, double>
            {
                ["rps"] = total / seconds,
                ["p50_ms"] = BenchStats.Percentile(millis, 0.50),
                ["p99_ms"] = BenchStats.Percentile(millis, 0.99),
                ["errors"] = Interlocked.Read(ref errors),
                ["timeouts"] = Interlocked.Read(ref timeouts),
                ["cpu_per_req_us"] = total == 0 ? 0 : (after["cpums"] - before["cpums"]) * 1000.0 / total,
                ["alloc_per_req_bytes"] = total == 0 ? 0 : (after["alloc"] - before["alloc"]) / (double)total,
                ["client_conns"] = context.ClientConnections,
                ["upstream_conns"] = context.Origin.ConnectionCount,
            };
        }
        finally
        {
            Volatile.Write(ref stop, true);
            // Workers in flight end on the 10 s client timeout, or at once when the run is cancelled.
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }
}
