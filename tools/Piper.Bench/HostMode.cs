using System.Diagnostics;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

namespace Piper.Bench;

/// <summary>
/// The proxy side of a measurement: a real <see cref="ProxyServer"/> in its own process, with no UI,
/// no trust-store change and no system-proxy change (the certificate authority lives in a temporary
/// directory the driver owns). The origin and load generator run in another process so they do not
/// compete with the proxy for CPU. Only public Piper.Core API is used, so the same source builds
/// against an older checkout.
///
/// stdout: <c>READY port startup_ms</c>, then one reply per command. stdin: <c>stats</c> | <c>quit</c>;
/// when stdin closes the host stops.
/// </summary>
internal static class HostMode
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args is not ["--ca-dir", { Length: > 0 } caDirectory])
        {
            Console.Error.WriteLine("usage: piper-bench host --ca-dir <directory>");
            return 2;
        }

        var started = Stopwatch.StartNew();
        using var ca = CertificateAuthority.LoadOrCreate(caDirectory);
        var store = new SessionStore();
        var proxy = new ProxyServer(new ProxyOptions { Port = 0, DecryptHttps = false }, ca, store);
        proxy.Start();
        Console.WriteLine($"READY {proxy.Endpoint!.Port} {started.Elapsed.TotalMilliseconds:F1}");
        Console.Out.Flush();

        string? line;
        while ((line = Console.In.ReadLine()) is not null)
        {
            if (line == "quit")
            {
                await proxy.StopAsync();
                Console.WriteLine("BYE");
                return 0;
            }

            using var self = Process.GetCurrentProcess();
            Console.WriteLine(line == "stats"
                ? $"STATS ws={self.WorkingSet64} peakws={self.PeakWorkingSet64} heap={GC.GetTotalMemory(false)} " +
                  $"alloc={GC.GetTotalAllocatedBytes()} cpums={self.TotalProcessorTime.TotalMilliseconds:F0} " +
                  $"threads={self.Threads.Count} sessions={store.Count} gcpausems={GC.GetTotalPauseDuration().TotalMilliseconds:F0}"
                : "ERR unknown command");
            Console.Out.Flush();
        }

        await proxy.StopAsync();
        return 0;
    }
}
