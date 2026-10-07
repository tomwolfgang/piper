namespace Piper.Bench;

/// <summary>The arithmetic behind the summary and compare tables. Pure, so the smoke tests link
/// this file and check it without starting a proxy.</summary>
internal static class BenchStats
{
    public static double Median(IReadOnlyList<double> values) => Percentile(values, 0.5);

    /// <summary>Nearest-rank percentile of <paramref name="values"/>; NaN when there are none.</summary>
    public static double Percentile(IReadOnlyList<double> values, double quantile)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        var rank = (int)Math.Ceiling(Math.Clamp(quantile, 0, 1) * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    /// <summary>Percentage change from <paramref name="baseline"/> to <paramref name="candidate"/>;
    /// NaN when the baseline is zero and the candidate is not.</summary>
    public static double DeltaPercent(double baseline, double candidate) =>
        baseline == 0 ? (candidate == 0 ? 0 : double.NaN) : (candidate - baseline) / Math.Abs(baseline) * 100;

    /// <summary>Whether the min..max ranges of two sample sets share a point. Overlapping ranges mean
    /// the run-to-run noise is as large as the difference, so the medians cannot be told apart.</summary>
    public static bool RangesOverlap(IReadOnlyList<double> a, IReadOnlyList<double> b) =>
        a.Count > 0 && b.Count > 0 && a.Min() <= b.Max() && b.Min() <= a.Max();

    /// <summary>The chance that a random run of <paramref name="b"/> is larger than a random run of
    /// <paramref name="a"/> (ties count half). 0.5 means no difference; 0.9 or 0.1 is a clear one.</summary>
    public static double ProbabilityGreater(IReadOnlyList<double> b, IReadOnlyList<double> a)
    {
        if (a.Count == 0 || b.Count == 0) return double.NaN;
        double wins = 0;
        foreach (var x in b)
            foreach (var y in a)
                wins += x > y ? 1 : x == y ? 0.5 : 0;
        return wins / ((double)a.Count * b.Count);
    }

    /// <summary>+1 when a larger value is better (throughput), -1 when a smaller one is (latency,
    /// CPU, memory), 0 when the metric has no direction (a count of connections).</summary>
    public static int Direction(string metric)
    {
        if (metric.EndsWith("_rps", StringComparison.Ordinal) || metric.EndsWith("_mbps", StringComparison.Ordinal)
            || metric is "rps" or "mbps") return 1;
        if (metric.EndsWith("_ms", StringComparison.Ordinal) || metric.EndsWith("_us", StringComparison.Ordinal)
            || metric.EndsWith("_mb", StringComparison.Ordinal) || metric.EndsWith("_bytes", StringComparison.Ordinal)
            || metric is "errors" or "timeouts") return -1;
        return 0;
    }
}
