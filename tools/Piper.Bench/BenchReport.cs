using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Piper.Bench;

internal static class BenchReport
{
    // A results file is local, but a record that is not finite, or a file of millions of lines, must
    // not be able to wedge or overrun the tool that reads it.
    private const long MaxFileBytes = 256L * 1024 * 1024;
    private const int MaxRecords = 1_000_000;

    /// <summary>Parses JSONL text. Lines that are not run records (the environment header, blank or
    /// malformed lines) are skipped; <paramref name="skipped"/> counts the malformed ones.</summary>
    public static List<BenchRecord> Parse(IEnumerable<string> lines, out int skipped)
    {
        var records = new List<BenchRecord>();
        skipped = 0;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (records.Count >= MaxRecords) { skipped++; continue; }
            BenchRecord? record;
            try { record = JsonSerializer.Deserialize<BenchRecord>(line); }
            catch (JsonException) { skipped++; continue; }
            if (record is null) { skipped++; continue; }
            if (record.Kind != "run") continue;
            (record.Scenario, record.Label) = (Clean(record.Scenario), Clean(record.Label));
            var metrics = new Dictionary<string, double>();
            foreach (var (name, value) in record.Metrics ?? [])
                if (double.IsFinite(value)) metrics[Clean(name)] = value;
            record.Metrics = metrics;
            records.Add(record);
        }
        return records;
    }

    // Text from a shared file as it may be printed: control (ESC, C1) and format (bidi) characters become '?'; cut at 200.
    public static string Clean(string? text) => new((text ?? "").Take(200).Select(c => char.IsControl(c) || char.GetUnicodeCategory(c) == UnicodeCategory.Format ? '?' : c).ToArray());

    // A file whose own name contains '#' is a file, not "path#label".
    private static (string Path, string? Label) SplitSpec(string spec)
    {
        var hash = File.Exists(spec) ? -1 : spec.LastIndexOf('#');
        return hash > 0 ? (spec[..hash], spec[(hash + 1)..]) : (spec, null);
    }

    /// <summary>The header lines of a results file. A field it lacks (schema 1 has no build line)
    /// is empty or zero and never compared.</summary>
    internal sealed record BenchEnv(string Machine, string[] Scenarios, int Runs, double DurationSeconds, string Cpu, string Configuration, string PowerPlan);

    private const int EnvLinesRead = 20;

    /// <summary>The environment of a results file (<c>path#label</c> reads <c>path</c>), or null when
    /// it has no header line.</summary>
    public static BenchEnv? LoadEnv(string spec)
    {
        var path = SplitSpec(spec).Path;
        if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return null;
        return ReadEnv(File.ReadLines(path).Take(EnvLinesRead));
    }

    public static BenchEnv? ReadEnv(IEnumerable<string> lines)
    {
        BenchEnv? env = null;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kind)) continue;
                if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "env")
                {
                    env = new BenchEnv(Text(root, "machine"),
                        root.TryGetProperty("scenarios", out var names) && names.ValueKind == JsonValueKind.Array
                            ? names.EnumerateArray().Where(n => n.ValueKind == JsonValueKind.String).Select(n => Clean(n.GetString())).Take(200).ToArray() : [],
                        root.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Number && runs.TryGetInt32(out var count) ? count : 0,
                        root.TryGetProperty("duration_s", out var seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetDouble(out var duration) ? duration : 0, "", "", "");
                }
                else if (kind.ValueKind == JsonValueKind.String && kind.GetString() == "build" && env is not null)
                    env = env with { Cpu = Text(root, "cpu"), Configuration = Text(root, "configuration"), PowerPlan = Text(root, "power_plan") };
            }
            catch (JsonException)
            {
                // A damaged header line: the file is compared without it.
            }
        }
        return env;

        static string Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? Clean(value.GetString()) : "";
    }

    /// <summary>Why a comparison may mislead: the files differ in machine, CPU, scenarios,
    /// duration, runs, build configuration or power plan.</summary>
    public static List<string> EnvWarnings(BenchEnv? a, BenchEnv? b)
    {
        var warnings = new List<string>();
        if (a is null || b is null)
        {
            warnings.Add("one of the files has no environment header, so the machine and settings could not be compared.");
            return warnings;
        }
        void Differ(string what, string x, string y)
        {
            if (x.Length > 0 && y.Length > 0 && x != y) warnings.Add($"{what} differ: A '{x}', B '{y}'.");
        }
        Differ("the machines", a.Machine, b.Machine);
        Differ("the CPUs", a.Cpu, b.Cpu);
        Differ("the build configurations", a.Configuration, b.Configuration);
        Differ("the power plans", a.PowerPlan, b.PowerPlan);
        if (a.Scenarios.Length > 0 && b.Scenarios.Length > 0 && !a.Scenarios.ToHashSet().SetEquals(b.Scenarios))
            warnings.Add($"the scenarios differ: only in A: {Names(a.Scenarios.Except(b.Scenarios))}; only in B: {Names(b.Scenarios.Except(a.Scenarios))}.");
        if (a.Runs > 0 && b.Runs > 0 && a.Runs != b.Runs) warnings.Add($"the runs differ: A {a.Runs}, B {b.Runs}.");
        if (a.DurationSeconds > 0 && b.DurationSeconds > 0 && a.DurationSeconds != b.DurationSeconds)
            warnings.Add(FormattableString.Invariant($"the measured duration differs: A {a.DurationSeconds:G} s, B {b.DurationSeconds:G} s."));
        if (a.Configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase) || b.Configuration.Equals("Debug", StringComparison.OrdinalIgnoreCase))
            warnings.Add("a file was recorded by a Debug build; its numbers say little about Release.");
        return warnings;
    }

    private static string Names(IEnumerable<string> names)
    {
        var list = names.Take(16).ToList();
        return list.Count == 0 ? "none" : string.Join(", ", list.Take(15)) + (list.Count > 15 ? ", ..." : "");
    }

    /// <summary>Reads a results file; <c>path#label</c> keeps only the records of that build.</summary>
    public static List<BenchRecord> Load(string spec, out int skipped)
    {
        var (path, label) = SplitSpec(spec);
        var info = new FileInfo(path);
        if (!info.Exists) throw new FileNotFoundException($"No such results file: {path}");
        if (info.Length > MaxFileBytes) throw new InvalidDataException($"{path} is larger than {MaxFileBytes >> 20} MB.");
        var records = Parse(File.ReadLines(path), out skipped);
        if (label is null) return records;
        var chosen = records.Where(r => r.Label == label).ToList();
        if (chosen.Count == 0)
            throw new InvalidDataException($"{path} has no run labelled '{label}' (it has: {string.Join(", ", records.Select(r => r.Label).Distinct().Take(10))}).");
        return chosen;
    }

    private static Dictionary<(string Scenario, string Metric), List<double>> Collect(IEnumerable<BenchRecord> records)
    {
        var map = new Dictionary<(string, string), List<double>>();
        foreach (var record in records.Where(r => r.Error is null))
            foreach (var (metric, value) in record.Metrics)
            {
                if (!map.TryGetValue((record.Scenario, metric), out var list)) map[(record.Scenario, metric)] = list = [];
                list.Add(value);
            }
        return map;
    }

    // 1e308 from a hostile file: past a trillion the cell uses scientific notation.
    private static string Num(double v) =>
        double.IsNaN(v) ? "n/a" : Math.Abs(v) >= 1e12 ? v.ToString("0.##E+0", CultureInfo.InvariantCulture)
        : Math.Abs(v) >= 100 ? v.ToString("F0", CultureInfo.InvariantCulture)
        : Math.Abs(v) >= 10 ? v.ToString("F1", CultureInfo.InvariantCulture) : v.ToString("F2", CultureInfo.InvariantCulture);

    private static string Cell(List<double> v) =>
        $"{Num(BenchStats.Median(v))} [{Num(v.Min())}..{Num(v.Max())}]";

    private static string Failures(IEnumerable<BenchRecord> records)
    {
        var failed = records.Where(r => r.Error is not null).GroupBy(r => r.Scenario).ToList();
        return failed.Count == 0 ? "" : "Failed runs (left out of the numbers): "
            + string.Join(", ", failed.Select(g => $"{g.Key} x{g.Count()} ({g.Sum(r => r.Metrics.GetValueOrDefault("errors") + r.Metrics.GetValueOrDefault("timeouts")):F0} errors)")) + Environment.NewLine;
    }

    /// <summary>Median [min..max] of every metric, one table per build label.</summary>
    public static string Summary(IReadOnlyCollection<BenchRecord> records)
    {
        var text = new StringBuilder();
        foreach (var label in records.Select(r => r.Label).Distinct())
        {
            var mine = records.Where(r => r.Label == label).ToList();
            var load = mine.Where(r => r.BackgroundCpuPercent is not null).Select(r => r.BackgroundCpuPercent!.Value).ToList();
            text.AppendLine($"== {label}  ({mine.Select(r => r.Run).Distinct().Count()} runs) ==");
            text.AppendLine($"system CPU just before a run: median {Num(BenchStats.Median(load))}%, max {Num(load.Count == 0 ? double.NaN : load.Max())}%; ESET ekrn running in {mine.Count(r => r.EsetRunning)} of {mine.Count} runs");
            text.AppendLine($"{"scenario",-22}{"metric",-22}{"median [min..max]",-30}n");
            foreach (var ((scenario, metric), values) in Collect(mine).OrderBy(kv => kv.Key.Scenario, StringComparer.Ordinal).ThenBy(kv => kv.Key.Metric, StringComparer.Ordinal))
                text.AppendLine($"{scenario,-22}{metric,-22}{Cell(values),-30}{values.Count}");
            text.Append(Failures(mine));
            text.AppendLine();
        }
        return text.ToString();
    }

    /// <summary>Median [min..max] of A and B side by side, the change in the medians, how likely a
    /// run of B beats a run of A, and whether the ranges overlap (then the difference is noise).</summary>
    public static string Compare(IReadOnlyCollection<BenchRecord> a, IReadOnlyCollection<BenchRecord> b, string nameA, string nameB, BenchEnv? envA = null, BenchEnv? envB = null)
    {
        var left = Collect(a);
        var right = Collect(b);
        var text = new StringBuilder();
        // No header, no environment check.
        if (envA is not null || envB is not null)
            foreach (var warning in EnvWarnings(envA, envB)) text.AppendLine("WARNING: " + warning);
        text.AppendLine($"A = {Clean(nameA)}   B = {Clean(nameB)}   delta = (B - A) / A on the medians; P(B>A) = chance a B run beats an A run");
        text.AppendLine($"{"scenario",-20}{"metric",-20}{"A median [min..max]",-28}{"B median [min..max]",-28}{"delta",9}{"P(B>A)",8}  verdict");
        foreach (var key in left.Keys.Intersect(right.Keys).OrderBy(k => k.Scenario, StringComparer.Ordinal).ThenBy(k => k.Metric, StringComparer.Ordinal))
        {
            var (x, y) = (left[key], right[key]);
            if (x.Distinct().Count() == 1 && y.Distinct().Count() == 1 && x[0] == y[0]) continue; // nothing to compare
            var delta = BenchStats.DeltaPercent(BenchStats.Median(x), BenchStats.Median(y));
            var overlap = BenchStats.RangesOverlap(x, y);
            var direction = BenchStats.Direction(key.Metric);
            // With fewer than MinRunsForVerdict runs a side, ranges that do not overlap happen by chance
            // too often to call one build better or worse (33% at 2 runs each, 10% at 3).
            var verdict = overlap ? "OVERLAP (noise)"
                : direction == 0 || double.IsNaN(delta) ? "differs"
                : Math.Min(x.Count, y.Count) < BenchStats.MinRunsForVerdict ? $"separate, but too few runs (<{BenchStats.MinRunsForVerdict}) to call it"
                : (delta > 0) == (direction > 0) ? "B better" : "B worse";
            if (Math.Min(x.Count, y.Count) < 5) verdict += " [n<5]";
            var deltaText = double.IsNaN(delta) ? "n/a" : $"{delta:+0.0;-0.0;0.0}%";
            text.AppendLine($"{key.Scenario,-20}{key.Metric,-20}{Cell(x),-28}{Cell(y),-28}{deltaText,9}{BenchStats.ProbabilityGreater(y, x),8:F2}  {verdict} (n={x.Count}/{y.Count})");
        }
        text.AppendLine("Rows that are identical in every run of both builds are left out.");
        // A metric one build never produced has no row above.
        text.AppendLine($"Only in A: {Names(left.Keys.Except(right.Keys).OrderBy(k => k.Scenario, StringComparer.Ordinal).ThenBy(k => k.Metric, StringComparer.Ordinal).Select(k => k.Scenario + "/" + k.Metric))}");
        text.AppendLine($"Only in B: {Names(right.Keys.Except(left.Keys).OrderBy(k => k.Scenario, StringComparer.Ordinal).ThenBy(k => k.Metric, StringComparer.Ordinal).Select(k => k.Scenario + "/" + k.Metric))}");
        text.Append(Failures(a)).Append(Failures(b));
        return text.ToString();
    }
}
