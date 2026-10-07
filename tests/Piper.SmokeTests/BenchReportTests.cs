using Piper.Bench;

// The arithmetic and the file format behind tools/Piper.Bench's tables. The benchmark runs themselves
// are not tests; what is checked here is that a result file is read back as it was written, that
// hostile lines cannot break the reader, and that the compare verdicts follow the numbers.
internal static class BenchReportTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("bench report: statistics, file format and verdicts", () =>
    {
        // Percentile is nearest-rank, and total over empty, single and out-of-range input.
        var hundred = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        runner.AreEqual(50.0, BenchStats.Percentile(hundred, 0.5), "p50 of 1..100");
        runner.AreEqual(99.0, BenchStats.Percentile(hundred, 0.99), "p99 of 1..100");
        runner.AreEqual(100.0, BenchStats.Percentile(hundred, 7), "a quantile above 1 clamps to the maximum");
        runner.AreEqual(1.0, BenchStats.Percentile(hundred, -1), "a quantile below 0 clamps to the minimum");
        runner.AreEqual(5.0, BenchStats.Percentile([5.0], 0.99), "one sample is every percentile");
        runner.IsTrue(double.IsNaN(BenchStats.Percentile([], 0.5)), "no samples gives NaN, not an exception");
        runner.AreEqual(2.0, BenchStats.Median([3.0, 1.0, 2.0]), "the median does not depend on input order");

        runner.AreEqual(10.0, BenchStats.DeltaPercent(100, 110), "delta of +10%");
        runner.AreEqual(0.0, BenchStats.DeltaPercent(0, 0), "0 to 0 is no change");
        runner.IsTrue(double.IsNaN(BenchStats.DeltaPercent(0, 5)), "a change from zero has no percentage");
        runner.IsTrue(BenchStats.RangesOverlap([1, 5], [5, 9]), "ranges that touch overlap");
        runner.IsTrue(!BenchStats.RangesOverlap([1, 2], [3, 4]), "disjoint ranges do not");
        runner.AreEqual(1.0, BenchStats.ProbabilityGreater([10, 11], [1, 2]), "every B run above every A run");
        runner.AreEqual(0.5, BenchStats.ProbabilityGreater([1, 2], [1, 2]), "identical samples tie at one half");
        runner.AreEqual(1, BenchStats.Direction("rps"), "throughput: higher is better");
        runner.AreEqual(-1, BenchStats.Direction("p99_ms"), "latency: lower is better");
        runner.AreEqual(0, BenchStats.Direction("client_conns"), "a connection count has no direction");

        // The file format: one line out, the same record back; the header, blanks and junk are skipped.
        var written = new BenchRecord { Scenario = "get_c16", Run = 3, Label = "main", BackgroundCpuPercent = 4.5, EsetRunning = true, Metrics = { ["rps"] = 3000.5 } };
        var line = written.ToLine();
        runner.IsTrue(line.Contains("\"scenario\":\"get_c16\"") && line.Contains("\"metrics\":{\"rps\":3000.5}") && !line.Contains("error"), "a record is one compact line without null fields");
        var parsed = BenchReport.Parse([line, "", """{"kind":"env","machine":"x"}""", "not json", """{"kind":"run","metrics":{"rps":1e999}}""", "[1,2]", "null"], out var skipped);
        runner.AreEqual(2, parsed.Count, "the run record and the one whose only metric is not a finite number survive");
        runner.AreEqual(3, skipped, "malformed lines are counted, not thrown");
        runner.AreEqual(0, parsed[1].Metrics.Count, "a metric that is not finite is dropped");
        runner.AreEqual(3000.5, parsed[0].Metrics["rps"], "the metric round trips");
        runner.AreEqual("main", parsed[0].Label, "the label round trips");
        runner.IsTrue(parsed[0].EsetRunning && parsed[0].BackgroundCpuPercent == 4.5, "the noise fields round trip");

        var file = Path.Combine(Path.GetTempPath(), $"piper-bench-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllLines(file, [Record("a", 1, 100).ToLine(), Record("b", 1, 200).ToLine()]);
            runner.AreEqual(1, BenchReport.Load(file + "#a", out _).Count, "file#label keeps one build");
            runner.AreEqual(2, BenchReport.Load(file, out _).Count, "no label keeps all");
            var missing = false;
            try { BenchReport.Load(file + ".missing", out _); } catch (FileNotFoundException) { missing = true; }
            runner.IsTrue(missing, "a missing file is reported, not an empty table");
        }
        finally { File.Delete(file); }

        // Compare: verdicts follow the direction of the metric and the overlap of the ranges.
        var a = new[] { 100.0, 102, 101, 99, 100 }.Select((v, i) => Record("a", i, v)).ToList();
        var b = new[] { 120.0, 122, 121, 119, 120 }.Select((v, i) => Record("b", i, v)).ToList();
        var faster = BenchReport.Compare(a, b, "a", "b");
        runner.IsTrue(faster.Contains("+20.0%") && faster.Contains("B better"), "throughput up 20% with disjoint ranges is better");
        var slower = BenchReport.Compare(b, a, "b", "a");
        runner.IsTrue(slower.Contains("B worse"), "and the reverse is worse");

        var noisy = new[] { 90.0, 130, 100, 115, 95 }.Select((v, i) => Record("b", i, v)).ToList();
        runner.IsTrue(BenchReport.Compare(a, noisy, "a", "b").Contains("OVERLAP"), "overlapping ranges are flagged as noise");
        runner.IsTrue(BenchReport.Compare(a.Take(2).ToList(), b.Take(2).ToList(), "a", "b").Contains("[n<5]"), "fewer than five runs is flagged");

        var failed = Record("b", 9, 500);
        failed.Error = "boom";
        var withFailure = BenchReport.Compare(a, b.Append(failed).ToList(), "a", "b");
        runner.IsTrue(withFailure.Contains("(n=5/5)") && withFailure.Contains("scenario x1"), "a failed run is reported and left out of the numbers");

        var same = BenchReport.Compare(a, a, "a", "a2");
        runner.IsTrue(same.Contains("OVERLAP"), "the same data overlaps itself");
        var constant = new[] { Constant("a"), Constant("a") };
        runner.IsTrue(!BenchReport.Compare(constant, constant, "a", "b").Contains("errors"), "a metric that never varies is left out");
        runner.IsTrue(BenchReport.Summary(a).Contains("median"), "the summary prints");
        return Task.CompletedTask;

        static BenchRecord Record(string label, int run, double rps) => new() { Scenario = "scenario", Run = run, Label = label, Metrics = { ["rps"] = rps } };
        static BenchRecord Constant(string label) => new() { Scenario = "s", Label = label, Metrics = { ["errors"] = 0 } };
    });
}
