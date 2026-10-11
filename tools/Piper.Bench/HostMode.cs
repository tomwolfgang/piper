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
/// <c>host --ca-dir D [--decrypt] [--insecure-upstream] [--remap "ip name;ip name"]</c>: decryption on (the
/// driver's client trusts the temporary CA in-process, nothing is installed), origin certificates not
/// validated (the benchmark origin's is self-signed), and host remapping so many names reach the origin.
///
/// stdout: <c>READY port startup_ms</c>, then one reply per command. stdin: <c>stats</c> | <c>gc</c> (the
/// same counters after a forced, compacting collection) | <c>capacity N</c> | <c>quit</c>; when stdin
/// closes the host stops.
/// </summary>
internal static class HostMode
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? caDirectory = null, remap = null;
        bool decrypt = false, insecureUpstream = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ca-dir" when i + 1 < args.Length: caDirectory = args[++i]; break;
                case "--remap" when i + 1 < args.Length: remap = args[++i]; break;
                case "--decrypt": decrypt = true; break;
                case "--insecure-upstream": insecureUpstream = true; break;
                default: caDirectory = null; i = args.Length; break;
            }
        }

        if (string.IsNullOrWhiteSpace(caDirectory))
        {
            Console.Error.WriteLine("usage: piper-bench host --ca-dir <directory> [--decrypt] [--insecure-upstream] [--remap \"ip name;ip name\"]");
            return 2;
        }

        var started = Stopwatch.StartNew();
        using var ca = CertificateAuthority.LoadOrCreate(caDirectory);
        var store = new SessionStore();
        var options = new ProxyOptions { Port = 0, DecryptHttps = decrypt, ValidateUpstreamCertificates = !insecureUpstream };
        if (remap is not null) options.HostRemapping.Apply(new HostRemappingSettings { Enabled = true, Mappings = remap.Replace(';', '\n') });
        var proxy = new ProxyServer(options, ca, store);
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

            if (line.StartsWith("capacity ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(9), out var capacity) && capacity > 0)
            {
                store.Capacity = capacity;
                Console.WriteLine("OK");
            }
            else if (line is "stats" or "gc") Console.WriteLine(Stats(line, store));
            else Console.WriteLine("ERR unknown command");
            Console.Out.Flush();
        }

        await proxy.StopAsync();
        return 0;
    }

    private static string Stats(string command, SessionStore store)
    {
        if (command == "gc")
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        }

        using var self = Process.GetCurrentProcess();
        return $"{(command == "gc" ? "GC" : "STATS")} ws={self.WorkingSet64} peakws={self.PeakWorkingSet64} heap={GC.GetTotalMemory(false)} " +
            $"alloc={GC.GetTotalAllocatedBytes()} cpums={self.TotalProcessorTime.TotalMilliseconds:F0} " +
            $"threads={self.Threads.Count} sessions={store.Count} gcpausems={GC.GetTotalPauseDuration().TotalMilliseconds:F0}";
    }
}
