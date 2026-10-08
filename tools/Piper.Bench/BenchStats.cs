namespace Piper.Bench;

/// <summary>The arithmetic behind the summary and compare tables. Pure, so the smoke tests link
/// this file and check it without starting a proxy.</summary>
internal static class BenchStats
{
    /// <summary>The median of the runs of one metric: the middle value, or the mean of the two middle
    /// ones when the count is even (a failed run leaves 4 or 6). NaN when there are none. Latency
    /// percentiles stay nearest-rank (<see cref="Percentile"/>): they must be a measured value.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        var middle = sorted.Length / 2;
        // Halved before they are added, so two values near double.MaxValue do not overflow.
        return sorted.Length % 2 == 1 ? sorted[middle] : sorted[middle - 1] / 2 + sorted[middle] / 2;
    }

    /// <summary>The fewest runs per build for which a "better" or "worse" verdict is given. Complete
    /// separation of the ranges happens by chance 2/C(n+m,n) of the time: 33% at 2 runs each, 10% at 3,
    /// 2.9% at 4.</summary>
    public const int MinRunsForVerdict = 4;

    /// <summary>The share of a load run's attempts (<c>requests</c> + <c>errors</c> + <c>timeouts</c>) that may
    /// fail: beyond it the proxy was refusing or stalling and per-request CPU and memory divide by too few successes.</summary>
    public const int MaxFailedPercent = 1;

    /// <summary>Why a load run (one with <c>rps</c>) failed, or null: no success (rate zero or NaN), nothing
    /// attempted, a count that is not a number, or over <see cref="MaxFailedPercent"/> percent failed.</summary>
    public static string? FailureReason(IReadOnlyDictionary<string, double> metrics)
    {
        if (!metrics.TryGetValue("rps", out var rps)) return null;
        var failed = metrics.GetValueOrDefault("errors") + metrics.GetValueOrDefault("timeouts");
        if (!(rps > 0)) return FormattableString.Invariant($"no request succeeded ({failed:F0} errors and timeouts)");
        if (!metrics.TryGetValue("requests", out var succeeded)) return null; // not counted by this scenario or file
        var attempted = succeeded + failed;
        if (!(succeeded >= 0 && failed >= 0 && attempted > 0)) return "the request counts are not usable";
        return failed * 100 > attempted * MaxFailedPercent ? FormattableString.Invariant($"{failed:F0} of {attempted:F0} requests failed (over {MaxFailedPercent}%)") : null;
    }

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
        // For each B run, the A runs below it count 1 and the equal ones 1/2: two binary searches over
        // the sorted A, so a results file full of one key costs n log n, not n x m.
        var sorted = a.OrderBy(v => v).ToArray();
        double wins = 0;
        foreach (var x in b)
        {
            var below = LowerBound(sorted, x);
            wins += below + 0.5 * (LowerBound(sorted, x, upper: true) - below);
        }
        return wins / ((double)a.Count * b.Count);
    }

    // The first index whose value is >= x (or > x when upper), in a sorted array.
    private static int LowerBound(double[] sorted, double x, bool upper = false)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (upper ? sorted[mid] <= x : sorted[mid] < x) lo = mid + 1;
            else hi = mid;
        }
        return lo;
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
