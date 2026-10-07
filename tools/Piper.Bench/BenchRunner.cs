using System.Globalization;
using System.Text.Json;

namespace Piper.Bench;

/// <summary>Runs scenarios against one or more builds and writes a JSON line per scenario run. A run
/// that fails is recorded with its error and the next one still runs: a benchmark that stops at the
/// first stall hides how often it stalls.</summary>
internal static class BenchRunner
{
    private static readonly TimeSpan QuietPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan QuietGiveUp = TimeSpan.FromSeconds(60);

    public static async Task<int> RunAsync(BenchOptions options, CancellationToken ct)
    {
        var scenarios = options.Scenarios.Count == 0 || options.Scenarios.Contains("all")
            ? Scenarios.All.ToList()
            : Scenarios.All.Where(s => options.Scenarios.Contains(s.Name)).ToList();
        var outPath = options.Out ?? $"piper-bench-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl";
        // One certificate authority folder for every host process: in the temp folder, deleted at
        // the end, never the user's real one, never added to any trust store.
        var caDirectory = Path.Combine(Path.GetTempPath(), "piper-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(caDirectory);
        var records = new List<BenchRecord>();
        var failures = 0;

        try
        {
            await using var origin = await Origin.StartAsync().ConfigureAwait(false);
            await using var file = new StreamWriter(new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
            var machine = SystemInfo.Describe();
            Console.WriteLine($"Machine: {machine}");
            Console.WriteLine($"Results: {Path.GetFullPath(outPath)}");
            await file.WriteLineAsync(JsonSerializer.Serialize(new
            {
                schema = BenchRecord.SchemaVersion, kind = "env", utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture), machine,
                hosts = options.Hosts, scenarios = scenarios.Select(s => s.Name), runs = options.Runs, duration_s = options.Duration.TotalSeconds,
            })).ConfigureAwait(false);

            // The first start of a build creates the certificate authority and reads its files from disk.
            // That is paid here, once, so the startup scenario measures a normal start.
            foreach (var build in options.Hosts)
            {
                Console.WriteLine($"Priming {build.Label}...");
                try { await using var primed = await HostClient.StartAsync(build.Path, caDirectory, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is TimeoutException or IOException or FormatException or IndexOutOfRangeException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // A build that cannot start, or does not say READY, is a usage problem: say which one.
                    Console.Error.WriteLine($"Could not start the proxy host of '{build.Label}' ({build.Path}): {ex.Message}");
                    return 2;
                }
            }

            for (var run = 1; run <= options.Runs; run++)
            {
                // A,B then B,A: a drift over the session (thermal, background tasks) does not favour one build.
                var order = run % 2 == 1 ? options.Hosts : Enumerable.Reverse(options.Hosts).ToList();
                foreach (var scenario in scenarios)
                    foreach (var build in order)
                    {
                        ct.ThrowIfCancellationRequested();
                        var record = await RunOnceAsync(options, origin, build, scenario, run, caDirectory, ct).ConfigureAwait(false);
                        records.Add(record);
                        if (record.Error is not null) failures++;
                        await file.WriteLineAsync(record.ToLine()).ConfigureAwait(false);
                        Console.WriteLine($"[{run}/{options.Runs}] {scenario.Name,-17}{build.Label,-12}"
                            + (record.Error is null ? string.Join(" ", record.Metrics.Select(m => FormattableString.Invariant($"{m.Key}={m.Value:G6}"))) : "FAILED: " + record.Error)
                            + FormattableString.Invariant($"   (bg cpu {record.BackgroundCpuPercent:F1}%{(record.BackgroundTop?.Length > 0 ? " " + record.BackgroundTop : "")})"));
                    }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("Cancelled; the results so far are in " + outPath);
            return 130;
        }
        finally
        {
            try { Directory.Delete(caDirectory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A straggling host process still holds a file; the OS temp cleanup takes the folder.
            }
        }

        Console.WriteLine();
        Console.Write(BenchReport.Summary(records));
        return failures == 0 ? 0 : 1;
    }

    private static async Task<BenchRecord> RunOnceAsync(BenchOptions options, Origin origin, HostBuild build, Scenario scenario, int run, string caDirectory, CancellationToken ct)
    {
        var (cpu, top) = await WaitForQuietAsync(options, ct).ConfigureAwait(false);
        var record = new BenchRecord
        {
            Scenario = scenario.Name, Run = run, Label = build.Label, Utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            BackgroundCpuPercent = double.IsNaN(cpu) ? null : Math.Round(cpu, 1), BackgroundTop = top, EsetRunning = SystemInfo.IsEsetRunning(),
        };

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(options.Timeout);
        await using var context = new ScenarioContext(options, origin, build.Path, caDirectory, limit.Token);
        try
        {
            var metrics = await scenario.Run(context).ConfigureAwait(false);
            record.Metrics = metrics.Where(m => double.IsFinite(m.Value)).ToDictionary(m => m.Key, m => Math.Round(m.Value, 3));
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            // Ctrl+C ends the host, so the scenario may fail with something other than a cancellation:
            // report it as the cancellation it is, not as a failed run or an unhandled exception.
            ct.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex)
        {
            // Recovery is to record the failure (a timeout is one) and carry on with the next run.
            record.Error = ex is OperationCanceledException ? $"timed out after {options.Timeout.TotalSeconds:F0} s" : $"{ex.GetType().Name}: {ex.Message}";
        }
        return record;
    }

    private static async Task<(double Cpu, string Top)> WaitForQuietAsync(BenchOptions options, CancellationToken ct)
    {
        var waited = TimeSpan.Zero;
        while (true)
        {
            var sample = await SystemInfo.SampleBackgroundAsync(QuietPoll, ct).ConfigureAwait(false);
            if (options.WaitQuietPercent is not { } limit || sample.CpuPercent < limit || waited >= QuietGiveUp) return sample;
            waited += QuietPoll;
        }
    }
}
