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
            record.Metrics = record.Metrics?.Where(kv => double.IsFinite(kv.Value)).ToDictionary(kv => kv.Key, kv => kv.Value) ?? [];
            records.Add(record);
        }
        return records;
    }

    /// <summary>Reads a results file; <c>path#label</c> keeps only the records of that build.</summary>
    public static List<BenchRecord> Load(string spec, out int skipped)
    {
        // A file whose own name contains '#' is a file, not "path#label".
        var hash = File.Exists(spec) ? -1 : spec.LastIndexOf('#');
        var path = hash > 0 ? spec[..hash] : spec;
        var label = hash > 0 ? spec[(hash + 1)..] : null;
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

    private static string Num(double v) =>
        double.IsNaN(v) ? "n/a" : Math.Abs(v) >= 100 ? v.ToString("F0", CultureInfo.InvariantCulture)
        : Math.Abs(v) >= 10 ? v.ToString("F1", CultureInfo.InvariantCulture) : v.ToString("F2", CultureInfo.InvariantCulture);

    private static string Cell(List<double> v) =>
        $"{Num(BenchStats.Median(v))} [{Num(v.Min())}..{Num(v.Max())}]";

    private static string Failures(IEnumerable<BenchRecord> records)
    {
        var failed = records.Where(r => r.Error is not null).GroupBy(r => r.Scenario).ToList();
        return failed.Count == 0 ? "" : "Failed runs (left out of the numbers): "
            + string.Join(", ", failed.Select(g => $"{g.Key} x{g.Count()}")) + Environment.NewLine;
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
    public static string Compare(IReadOnlyCollection<BenchRecord> a, IReadOnlyCollection<BenchRecord> b, string nameA, string nameB)
    {
        var left = Collect(a);
        var right = Collect(b);
        var text = new StringBuilder();
        text.AppendLine($"A = {nameA}   B = {nameB}   delta = (B - A) / A on the medians; P(B>A) = chance a B run beats an A run");
        text.AppendLine($"{"scenario",-20}{"metric",-20}{"A median [min..max]",-28}{"B median [min..max]",-28}{"delta",9}{"P(B>A)",8}  verdict");
        foreach (var key in left.Keys.Intersect(right.Keys).OrderBy(k => k.Scenario, StringComparer.Ordinal).ThenBy(k => k.Metric, StringComparer.Ordinal))
        {
            var (x, y) = (left[key], right[key]);
            if (x.Distinct().Count() == 1 && y.Distinct().Count() == 1 && x[0] == y[0]) continue; // nothing to compare
            var delta = BenchStats.DeltaPercent(BenchStats.Median(x), BenchStats.Median(y));
            var overlap = BenchStats.RangesOverlap(x, y);
            var direction = BenchStats.Direction(key.Metric);
            var verdict = overlap ? "OVERLAP (noise)"
                : direction == 0 || double.IsNaN(delta) ? "differs"
                : (delta > 0) == (direction > 0) ? "B better" : "B worse";
            if (Math.Min(x.Count, y.Count) < 5) verdict += " [n<5]";
            var deltaText = double.IsNaN(delta) ? "n/a" : $"{delta:+0.0;-0.0;0.0}%";
            text.AppendLine($"{key.Scenario,-20}{key.Metric,-20}{Cell(x),-28}{Cell(y),-28}{deltaText,9}{BenchStats.ProbabilityGreater(y, x),8:F2}  {verdict} (n={x.Count}/{y.Count})");
        }
        text.AppendLine("Rows that are identical in every run of both builds are left out.");
        text.Append(Failures(a)).Append(Failures(b));
        return text.ToString();
    }
}
