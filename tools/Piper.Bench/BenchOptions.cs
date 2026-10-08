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
          piper-bench --compare A.jsonl B.jsonl    print A against B (either may be file.jsonl#label)

        options:
          --scenario a,b,...   scenarios to run (default: all)
          --runs N             runs of every scenario, 1..1000 (default 5; use 15 for large transfers)
          --duration S         seconds measured by the get_* and hdr_* scenarios, 1..600 (default 8)
          --host label=path    a piper-bench build to measure (its .dll or .exe); repeat to alternate
                               builds A,B,B,A; "self" is this build (default: current=self). The file is
                               RUN, with your rights: name only builds you made or trust.
          --out file           new results file (default: piper-bench-<UTC time>.jsonl); an existing
                               file is never overwritten
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

    /// <summary>Parses the command line. <paramref name="isScenario"/> says whether a scenario name
    /// exists, so this file stays free of the scenarios (and of the proxy) and the smoke tests can link it.</summary>
    public static bool TryParse(string[] args, string self, Func<string, bool> isScenario, out BenchOptions options, out string error)
    {
        options = new BenchOptions();
        error = "";
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            string text;
            int whole;
            switch (name)
            {
                case "--list": options.List = true; break;
                case "--scenario":
                    if (!TryValue(args, ref i, out text, out error)) return false;
                    options.Scenarios.AddRange(text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--runs":
                    if (!TryWhole(args, ref i, 1, 1000, out whole, out error)) return false;
                    options.Runs = whole;
                    break;
                case "--duration":
                    if (!TryWhole(args, ref i, 1, 600, out whole, out error)) return false;
                    options.Duration = TimeSpan.FromSeconds(whole);
                    break;
                case "--timeout":
                    if (!TryWhole(args, ref i, 10, 3600, out whole, out error)) return false;
                    options.Timeout = TimeSpan.FromSeconds(whole);
                    break;
                case "--wait-quiet":
                    if (!TryValue(args, ref i, out text, out error)) return false;
                    if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent) || percent is < 1 or > 100)
                    {
                        error = $"--wait-quiet takes a percentage from 1 to 100, got '{text}'.";
                        return false;
                    }
                    options.WaitQuietPercent = percent;
                    break;
                case "--summary":
                    if (!TryValue(args, ref i, out text, out error)) return false;
                    options.Summary = text;
                    break;
                case "--compare":
                    if (!TryValue(args, ref i, out var first, out error)) return false;
                    if (!TryValue(args, ref i, out var second, out error)) { error = "--compare needs two results files: --compare A.jsonl B.jsonl."; return false; }
                    options.Compare = (first, second);
                    break;
                case "--out":
                    if (!TryValue(args, ref i, out text, out error)) return false;
                    options.Out = text;
                    break;
                case "--host":
                    if (!TryValue(args, ref i, out text, out error)) return false;
                    if (!TryHost(text, self, options.Hosts, out var build, out error)) return false;
                    options.Hosts.Add(build!);
                    break;
                default:
                    error = $"Unrecognised option: {name}{Environment.NewLine}{Usage}";
                    return false;
            }
        }

        var unknown = options.Scenarios.Where(s => s != "all" && !isScenario(s)).ToList();
        if (unknown.Count > 0)
        {
            error = $"Unknown scenario: {string.Join(", ", unknown)}. Use --list.";
            return false;
        }
        if (options.Hosts.Count == 0) options.Hosts.Add(new HostBuild("current", self));
        return true;
    }

    // The value after args[i]. A value that is itself an option ("--runs --scenario x") is a missing
    // value, not something to swallow: the next loop turn must still see that option.
    private static bool TryValue(string[] args, ref int i, out string value, out string error)
    {
        value = "";
        error = "";
        if (i + 1 >= args.Length || args[i + 1].Length == 0 || args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            error = $"{args[i]} needs a value.";
            return false;
        }
        value = args[++i];
        return true;
    }

    private static bool TryWhole(string[] args, ref int i, int min, int max, out int number, out string error)
    {
        number = 0;
        var flag = args[i];
        if (!TryValue(args, ref i, out var text, out error)) return false;
        // NumberStyles.None: digits only, so no sign, space or exponent gets through.
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number >= min && number <= max) return true;
        error = $"{flag} takes a whole number from {min} to {max}, got '{text}'.";
        return false;
    }

    // --host runs the file it names, so only the two kinds of file a piper-bench build is are accepted.
    private static bool TryHost(string text, string self, List<HostBuild> existing, out HostBuild? build, out string error)
    {
        build = null;
        error = "";
        var eq = text.IndexOf('=');
        var label = eq > 0 ? text[..eq] : "";
        var path = eq > 0 ? text[(eq + 1)..] : "";
        if (path == "self") path = self;
        if (label.Length == 0 || path.Length == 0) { error = $"--host expects label=path, got '{text}'."; return false; }
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            error = $"--host runs the file it names, and only a piper-bench .dll or .exe is accepted, got '{path}'.";
            return false;
        }
        if (!File.Exists(path)) { error = $"--host '{label}': no such file: {path}"; return false; }
        if (existing.Any(h => h.Label == label)) { error = $"Two --host values share the label '{label}'."; return false; }
        build = new HostBuild(label, Path.GetFullPath(path));
        return true;
    }
}
