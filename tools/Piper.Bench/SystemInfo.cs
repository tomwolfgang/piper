using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Piper.Bench;

/// <summary>What the machine was doing, recorded beside each result: a number taken while a browser
/// or an antivirus scan was busy is a different number.</summary>
internal static class SystemInfo
{
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    /// <summary>System-wide CPU use over <paramref name="window"/>, and the busiest other processes
    /// in that time ("name:3.1%", at most three, only those above 1%).</summary>
    public static async Task<(double CpuPercent, string Top)> SampleBackgroundAsync(TimeSpan window, CancellationToken ct)
    {
        var before = Snapshot();
        var cpu0 = GetSystemTimes(out var i0, out var k0, out var u0);
        var started = Stopwatch.GetTimestamp();
        await Task.Delay(window, ct).ConfigureAwait(false);
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var cpu1 = GetSystemTimes(out var i1, out var k1, out var u1);
        var after = Snapshot();

        double percent = double.NaN;
        if (cpu0 && cpu1)
        {
            var total = (double)(k1 - k0) + (u1 - u0); // kernel time includes idle time
            if (total > 0) percent = 100 * (total - (i1 - i0)) / total;
        }

        var self = Environment.ProcessId;
        var busy = after
            .Where(p => p.Key.Id != self && before.ContainsKey(p.Key))
            // The results file is shared: a process is named only when it is a known noisy one, else "other".
            .Select(p => (Name: BenchHygiene.ProcessLabel(p.Key.Name), Percent: (p.Value - before[p.Key]).TotalSeconds / seconds / Environment.ProcessorCount * 100))
            .Where(p => p.Percent >= 1)
            .GroupBy(p => p.Name)
            .Select(g => (Name: g.Key, Percent: g.Sum(p => p.Percent)))
            .OrderByDescending(p => p.Percent).Take(3);
        return (percent, string.Join(",", busy.Select(p => FormattableString.Invariant($"{p.Name}:{p.Percent:F1}%"))));
    }

    private static Dictionary<(string Name, int Id), TimeSpan> Snapshot()
    {
        var result = new Dictionary<(string, int), TimeSpan>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { result[(process.ProcessName, process.Id)] = process.TotalProcessorTime; }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // A protected or already-exited process: it simply does not appear in the sample.
                }
            }
        }
        return result;
    }

    /// <summary>ESET's real-time scanner (ekrn) buffers loopback HTTP, which stalls tiny payloads
    /// and adds latency with or without Piper in the path, so its presence is part of every result.</summary>
    public static bool IsEsetRunning() => IsRunning("ekrn");

    private static bool IsRunning(string name)
    {
        var found = Process.GetProcessesByName(name);
        foreach (var process in found) process.Dispose();
        return found.Length > 0;
    }

    public static string Describe()
    {
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        return $"{Environment.ProcessorCount} logical cores, {memory:F1} GB RAM, {RuntimeInformation.OSDescription}, "
            + $"{RuntimeInformation.FrameworkDescription}, ekrn {(IsEsetRunning() ? "running" : "not running")}";
    }
}
