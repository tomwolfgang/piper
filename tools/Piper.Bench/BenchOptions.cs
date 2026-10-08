using System.Globalization;

namespace Piper.Bench;

internal sealed record HostBuild(string Label, string Path);

internal sealed class BenchOptions
{
    public const string Usage = """
        piper-bench: measure Piper's proxy against a local origin, in a separate process from the proxy.

          piper-bench [options]                    run the scenarios, write one JSON line per run, print a summary
          piper-bench --list                       list the scenarios
          piper-bench --summary results.jsonl      print median [min..max] per build
          piper-bench --compare A.jsonl B.jsonl    print A against B (either may be file.jsonl#label); warns when the
                                                   files differ in machine, scenarios, duration or runs, and lists
                                                   the metrics only one of them has

        options:
          --scenario a,b,...   scenarios to run (default: all)
          --runs N             runs of every scenario, 1..1000 (default 5; use 15 for large transfers)
          --duration S         seconds measured by the get_* and hdr_* scenarios, 1..600 (default 8)
          --host label=path    a piper-bench build to measure (its .dll or .exe); repeat to alternate
                               builds A,B,B,A; "self" is this build (default: current=self)
          --out file           results file (default: piper-bench-<UTC time>.jsonl)
          --timeout S          longest one scenario run may take, 10..3600 (default 300)
          --wait-quiet PCT     before each run, wait up to 60 s for system CPU to fall under PCT
        """;

    public List<string> Scenarios { get; } = [];
    public int Runs { get; private set; } = 5;
    public TimeSpan Duration { get; private set; } = TimeSpan.FromSeconds(8);
    public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(300);
    public double? WaitQuietPercent { get; private set; }
    public string? Out { get; private set; }
    public List<HostBuild> Hosts { get; } = [];
    public bool List { get; private set; }
    public string? Summary { get; private set; }
    public (string A, string B)? Compare { get; private set; }

    public static bool TryParse(string[] args, string self, out BenchOptions options, out string error)
    {
        options = new BenchOptions();
        error = "";
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (name)
            {
                case "--list": options.List = true; break;
                case "--scenario" when Next() is { } v: options.Scenarios.AddRange(v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                case "--runs" when Next() is { } v && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 1000: options.Runs = n; break;
                case "--duration" when Next() is { } v && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var s) && s is >= 1 and <= 600: options.Duration = TimeSpan.FromSeconds(s); break;
                case "--timeout" when Next() is { } v && int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var t) && t is >= 10 and <= 3600: options.Timeout = TimeSpan.FromSeconds(t); break;
                case "--wait-quiet" when Next() is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p is >= 1 and <= 100: options.WaitQuietPercent = p; break;
                case "--summary" when Next() is { Length: > 0 } v: options.Summary = v; break;
                case "--compare" when Next() is { Length: > 0 } a && Next() is { Length: > 0 } b: options.Compare = (a, b); break;
                case "--out" when Next() is { Length: > 0 } v: options.Out = v; break;
                case "--host" when Next() is { } v:
                    var eq = v.IndexOf('=');
                    var label = eq > 0 ? v[..eq] : "";
                    var path = eq > 0 ? v[(eq + 1)..] : "";
                    if (path == "self") path = self;
                    if (label.Length == 0 || !File.Exists(path)) { error = $"--host expects label=path of an existing file, got '{v}'."; return false; }
                    if (options.Hosts.Any(h => h.Label == label)) { error = $"Two --host values share the label '{label}'."; return false; }
                    options.Hosts.Add(new HostBuild(label, Path.GetFullPath(path)));
                    break;
                default:
                    error = $"Unrecognised or invalid option: {name}{Environment.NewLine}{Usage}";
                    return false;
            }
        }

        var unknown = options.Scenarios.Where(s => s != "all" && !Piper.Bench.Scenarios.All.Any(x => x.Name == s)).ToList();
        if (unknown.Count > 0)
        {
            error = $"Unknown scenario: {string.Join(", ", unknown)}. Use --list.";
            return false;
        }
        if (options.Hosts.Count == 0) options.Hosts.Add(new HostBuild("current", self));
        return true;
    }
}
