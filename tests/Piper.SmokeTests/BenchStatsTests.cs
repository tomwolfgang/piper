using Piper.Bench;

// The percentile arithmetic of tools/Piper.Bench at its boundaries (the table tests are in BenchReportTests).
internal static class BenchStatsTests
{
    public static Task RunPercentileAsync(TestRunner runner) => runner.RunAsync("bench percentile: boundaries of the nearest-rank rule", () =>
    {
        runner.IsTrue(double.IsNaN(BenchStats.Percentile([], 0)) && double.IsNaN(BenchStats.Percentile([], 1)), "no samples gives NaN at both ends");
        foreach (var quantile in new[] { 0, 0.5, 0.99, 1 })
            runner.AreEqual(7.0, BenchStats.Percentile([7], quantile), $"one sample is every percentile (q={quantile})");

        double[] four = [4, 1, 3, 2];
        runner.AreEqual(1.0, BenchStats.Percentile(four, 0), "q=0 is the minimum");
        runner.AreEqual(2.0, BenchStats.Percentile(four, 0.5), "q=0.5 of an even count is the lower middle sample (nearest rank)");
        runner.AreEqual(3.0, BenchStats.Percentile(four, 0.75), "q=0.75 of four is the third");
        runner.AreEqual(4.0, BenchStats.Percentile(four, 0.99), "q=0.99 of four is the maximum");
        runner.AreEqual(4.0, BenchStats.Percentile(four, 1), "q=1 is the maximum");
        runner.AreEqual(3.0, BenchStats.Percentile([1, 2, 3], 0.5 + 0.2), "q=0.7 of three rounds the rank up");
        runner.AreEqual(2.0, BenchStats.Percentile([1, 2, 3], 0.5), "q=0.5 of an odd count is the middle sample");
        runner.IsTrue(four[0] == 4 && four[1] == 1, "the input is not reordered");
        runner.AreEqual(5.0, BenchStats.Percentile([5, 5, 5, 5], 0.99), "equal samples");
        runner.AreEqual(1.0, BenchStats.Percentile([1, 2], 0.0001), "a tiny quantile still takes the first sample");

        var survived = true;
        try { _ = BenchStats.Percentile([1, 2, 3], double.NaN); _ = BenchStats.Percentile([1, 2, 3], double.PositiveInfinity); }
        catch (Exception) { survived = false; }
        runner.IsTrue(survived, "a quantile that is NaN or infinite does not throw");
        return Task.CompletedTask;
    });
}
