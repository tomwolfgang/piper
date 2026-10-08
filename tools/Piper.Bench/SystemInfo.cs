using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
            .Select(p => (Name: p.Key.Name, Percent: (p.Value - before[p.Key]).TotalSeconds / seconds / Environment.ProcessorCount * 100))
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

    /// <summary>The second line of a results file (kind "build"): what built and ran the driver. Read
    /// from files and two read-only registry values; no process is started; no name, no path.</summary>
    public static Dictionary<string, string> Provenance() => new()
    {
        ["kind"] = "build",
#if DEBUG
        ["configuration"] = "Debug",
#else
        ["configuration"] = "Release",
#endif
        ["git_sha"] = GitCommit(),
        ["runtime"] = $"{RuntimeInformation.FrameworkDescription} {RuntimeInformation.ProcessArchitecture}",
        ["gc"] = $"{(System.Runtime.GCSettings.IsServerGC ? "server" : "workstation")}, {(!AppContext.TryGetSwitch("System.GC.Concurrent", out var concurrent) || concurrent ? "concurrent" : "non-concurrent")}",
        ["cpu"] = Cpu(),
        ["power_plan"] = PowerPlan(),
    };

    // The commit of the checkout the driver was built in, from .git/HEAD and its ref (a
    // worktree's .git is a file pointing at the real folder); "unknown" outside a checkout.
    private static string GitCommit()
    {
        try
        {
            for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            {
                var dot = Path.Combine(folder.FullName, ".git");
                var gitDir = Directory.Exists(dot) ? dot
                    : ReadSmall(dot) is { } pointer && pointer.StartsWith("gitdir:", StringComparison.Ordinal) ? Path.GetFullPath(Path.Combine(folder.FullName, pointer[7..].Trim())) : null;
                if (gitDir is null) continue;
                var head = ReadSmall(Path.Combine(gitDir, "HEAD"));
                if (head is null) return "unknown";
                if (!head.StartsWith("ref: refs/", StringComparison.Ordinal)) return IsSha(head) ? head[..12] : "unknown";
                var reference = head[5..].Trim();
                if (reference.Contains("..", StringComparison.Ordinal)) return "unknown";
                var common = ReadSmall(Path.Combine(gitDir, "commondir")) is { } relative ? Path.GetFullPath(Path.Combine(gitDir, relative)) : gitDir;
                var loose = ReadSmall(Path.Combine(common, reference.Replace('/', Path.DirectorySeparatorChar)));
                if (loose is not null) return IsSha(loose) ? loose[..12] : "unknown";
                var packed = Path.Combine(common, "packed-refs");
                if (File.Exists(packed) && new FileInfo(packed).Length <= (16 << 20))
                    foreach (var line in File.ReadLines(packed).Take(200_000))
                        if (line.Length > 41 && line[40] == ' ' && line.AsSpan(41).SequenceEqual(reference) && IsSha(line[..40])) return line[..12];
                return "unknown";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // An unreadable .git folder: no commit recorded.
        }
        return "unknown";

        static bool IsSha(string text) => text.Length >= 40 && text[..40].All(Uri.IsHexDigit);
    }

    // A file of at most 4 KB, trimmed; null when missing or larger.
    private static string? ReadSmall(string path)
    {
        var info = new FileInfo(path);
        return info.Exists && info.Length <= 4096 ? File.ReadAllText(path).Trim() : null;
    }

    private static string Cpu() => ReadMachineValue(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") is { Length: > 0 } name
        ? string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries)) : "unknown";

    private static string PowerPlan() => ReadMachineValue(@"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes", "ActivePowerScheme") switch
    {
        null or "" => "unknown",
        "381b4222-f694-41f0-9685-ff5bb260df2e" => "balanced",
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "high performance",
        "a1841308-3541-4fab-bc81-f71556f20b4a" => "power saver",
        "e9a42b02-d5df-448d-aa00-03f14749eb61" => "ultimate performance",
        _ => "custom",
    };

    // One read-only HKEY_LOCAL_MACHINE string value.
    private static string? ReadMachineValue(string subKey, string name)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return Read(subKey, name); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }

        [SupportedOSPlatform("windows")]
        static string? Read(string subKey, string name)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
            return (key?.GetValue(name) as string)?.Trim();
        }
    }

    public static string Describe()
    {
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);
        return $"{Environment.ProcessorCount} logical cores, {memory:F1} GB RAM, {RuntimeInformation.OSDescription}, "
            + $"{RuntimeInformation.FrameworkDescription}, ekrn {(IsEsetRunning() ? "running" : "not running")}";
    }
}
