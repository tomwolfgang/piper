namespace Piper.Bench;

/// <summary>The arithmetic behind the reported numbers. Pure, so the smoke tests can link this file.</summary>
internal static class BenchStats
{
    /// <summary>Nearest-rank percentile of <paramref name="values"/>; NaN when there are none.</summary>
    public static double Percentile(IReadOnlyList<double> values, double quantile)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.OrderBy(v => v).ToArray();
        var rank = (int)Math.Ceiling(Math.Clamp(quantile, 0, 1) * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}
