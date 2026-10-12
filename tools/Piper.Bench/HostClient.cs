using System.Diagnostics;
using System.Globalization;

namespace Piper.Bench;

/// <summary>The driver's handle on one proxy host process (see <see cref="HostMode"/>). Every wait is
/// bounded by <see cref="CommandTimeout"/> and the caller's token, and disposal ends the process tree.</summary>
internal sealed class HostClient : IAsyncDisposable
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

    private readonly Process _process;
    private bool _disposed;

    private HostClient(Process process) => _process = process;

    public int Port { get; private set; }

    /// <summary>The host's own time from process entry to a listening proxy, including the CA load.</summary>
    public double StartMs { get; private set; }

    /// <summary>The build configuration the host reported (<c>Release</c>, <c>Debug</c>), or <c>unknown</c>.</summary>
    public string Configuration { get; private set; } = "unknown";

    /// <summary><paramref name="hostPath"/> is a <c>.dll</c> (run with <c>dotnet</c>) or an executable of
    /// a <c>piper-bench</c> build; the proxy it runs is whatever Piper.Core that build was made with.</summary>
    public static async Task<HostClient> StartAsync(string hostPath, string caDirectory, CancellationToken ct, IEnumerable<string>? hostArguments = null)
    {
        var isDll = hostPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo(isDll ? FindDotnet() : hostPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (isDll) startInfo.ArgumentList.Add(hostPath);
        foreach (var argument in new[] { "host", "--ca-dir", caDirectory }.Concat(hostArguments ?? [])) startInfo.ArgumentList.Add(argument);

        var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {hostPath}.");
        var client = new HostClient(process);
        process.ErrorDataReceived += (_, _) => { }; // drained so a full pipe can never block the host
        process.BeginErrorReadLine();
        try
        {
            (client.Port, client.StartMs, client.Configuration) = ParseReady(await client.ReadAsync("READY", ct).ConfigureAwait(false));
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The <c>READY port startup_ms [configuration]</c> line a host prints once it listens. The line comes from
    /// a file the user chose to run, so a short, non-numeric or out-of-range one is an <see cref="InvalidDataException"/>
    /// that quotes it (paths scrubbed, cut), never an index or format exception. A host built before the configuration
    /// was reported says nothing: unknown, not Release.</summary>
    internal static (int Port, double StartMs, string Configuration) ParseReady(string line)
    {
        var fields = line.Split(' ');
        if (fields.Length < 3 || fields[0] != "READY"
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535
            || !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var startMs) || !double.IsFinite(startMs) || startMs < 0)
            throw new InvalidDataException($"The proxy host's READY line is not 'READY port startup_ms [configuration]': {BenchHygiene.Scrub(line)}");
        return (port, startMs, fields.Length > 3 ? BenchHygiene.ConfigurationToken(fields[3]) : "unknown");
    }

    /// <summary>The <c>dotnet.exe</c> that runs a host <c>.dll</c>, as a full path: this process's own when it is
    /// <c>dotnet</c>, else the SDK's <c>DOTNET_HOST_PATH</c>, else the registered or default install. Never the bare
    /// name, which CreateProcess would resolve in the current directory first. <paramref name="registeredFolder"/>
    /// is only called when neither earlier candidate is usable.</summary>
    internal static string? FindDotnet(string? processPath, string? hostPathVariable, Func<string?> registeredFolder, string programFiles, Func<string, bool> exists)
    {
        IEnumerable<string?> Candidates()
        {
            yield return processPath;
            yield return hostPathVariable;
            if (registeredFolder() is { } folder) yield return Path.Combine(folder, "dotnet.exe");
            yield return Path.Combine(programFiles, "dotnet", "dotnet.exe");
        }
        return BenchHygiene.ChooseDotnet(Candidates(), exists);
    }

    private static string FindDotnet() => FindDotnet(
        Environment.ProcessPath, Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"), SystemInfo.RegisteredDotnetFolder,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), File.Exists)
        ?? throw new InvalidOperationException("Could not find dotnet.exe to run a host .dll; run the driver with dotnet, set DOTNET_HOST_PATH, or pass a host .exe.");

    private async Task<string> ReadAsync(string prefix, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false) is { } line)
                if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"The proxy host did not answer within {CommandTimeout.TotalSeconds:F0} s (waiting for {prefix}).");
        }
        throw new IOException($"The proxy host exited while the driver waited for {prefix}.");
    }

    private async Task<string> CommandAsync(string command, string prefix, CancellationToken ct, Action? beforeSend = null)
    {
        beforeSend?.Invoke();
        await _process.StandardInput.WriteLineAsync(command.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
        return await ReadAsync(prefix, ct).ConfigureAwait(false);
    }

    /// <summary>Process counters of the proxy: ws, peakws, heap, alloc, cpums, threads, sessions, gcpausems.
    /// The host takes them when it reads the command, a moment after <paramref name="beforeSend"/> (run just
    /// before the command is written) and before the reply returns. A caller that counts requests over the same
    /// interval flips its counter in <paramref name="beforeSend"/>, so the counters and the count share an edge
    /// to within the host's wake-up time instead of the whole round trip.</summary>
    public async Task<Dictionary<string, long>> StatsAsync(CancellationToken ct = default, Action? beforeSend = null) =>
        Parse(await CommandAsync("stats", "STATS", ct, beforeSend).ConfigureAwait(false));

    /// <summary>The same counters after a forced, compacting collection (so <c>heap</c> is what is retained).</summary>
    public async Task<Dictionary<string, long>> CollectAsync(CancellationToken ct = default) =>
        Parse(await CommandAsync("gc", "GC", ct).ConfigureAwait(false));

    public Task SetCapacityAsync(int sessions, CancellationToken ct = default) =>
        CommandAsync(FormattableString.Invariant($"capacity {sessions}"), "OK", ct);

    private static Dictionary<string, long> Parse(string line)
    {
        var values = new Dictionary<string, long>();
        foreach (var pair in line.Split(' ').Skip(1))
        {
            var kv = pair.Split('=');
            if (kv.Length == 2 && long.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)) values[kv[0]] = value;
        }
        return values;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                using var quit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await CommandAsync("quit", "BYE", quit.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or TimeoutException or InvalidOperationException)
                {
                    // Not answering: it is killed below.
                }
                if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true);
            }
        }
        finally { _process.Dispose(); }
    }
}
