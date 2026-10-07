using Piper.Bench;

// The arithmetic and file format behind tools/Piper.Bench's tables (the benchmark runs are not tests).
internal static class BenchReportTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("bench report: statistics, file format and verdicts", () =>
    {
        var hundred = Enumerable.Range(1, 100).Select(i => (double)i).ToArray();
        runner.AreEqual(50.0, BenchStats.Percentile(hundred, 0.5), "p50 of 1..100");
        runner.AreEqual(99.0, BenchStats.Percentile(hundred, 0.99), "p99 of 1..100");
        runner.AreEqual(100.0, BenchStats.Percentile(hundred, 7), "a quantile above 1 clamps to the maximum");
        runner.AreEqual(1.0, BenchStats.Percentile(hundred, -1), "a quantile below 0 clamps to the minimum");
        runner.IsTrue(double.IsNaN(BenchStats.Percentile([], 0.5)), "no samples gives NaN, not an exception");

        runner.AreEqual(10.0, BenchStats.DeltaPercent(100, 110), "delta of +10%");
        runner.IsTrue(double.IsNaN(BenchStats.DeltaPercent(0, 5)), "a change from zero has no percentage");
        runner.IsTrue(BenchStats.RangesOverlap([1, 5], [5, 9]), "ranges that touch overlap");
        runner.IsTrue(!BenchStats.RangesOverlap([1, 2], [3, 4]), "disjoint ranges do not");
        runner.AreEqual(0.5, BenchStats.ProbabilityGreater([1, 2], [1, 2]), "identical samples tie at one half");
        runner.AreEqual(0.375, BenchStats.ProbabilityGreater([1, 2, 2, 5], [2, 3]), "ties and a mix of wins count as in the pairwise definition");
        // Equal to the pairwise definition on random small inputs, ties included.
        var random = new Random(7);
        for (var round = 0; round < 200; round++)
        {
            var left = Enumerable.Range(0, random.Next(1, 12)).Select(_ => (double)random.Next(0, 6)).ToArray();
            var right = Enumerable.Range(0, random.Next(1, 12)).Select(_ => (double)random.Next(0, 6)).ToArray();
            double wins = 0;
            foreach (var x in right)
                foreach (var y in left)
                    wins += x > y ? 1 : x == y ? 0.5 : 0;
            if (Math.Abs(BenchStats.ProbabilityGreater(right, left) - wins / (left.Length * right.Length)) > 1e-12) { runner.IsTrue(false, $"P(B>A) differs from the pairwise count for A=[{string.Join(",", left)}] B=[{string.Join(",", right)}]"); break; }
        }
        // A hostile file can put a million runs under one key: not a million squared.
        var many = Enumerable.Range(0, 1_000_000).Select(i => (double)(i % 1000)).ToArray();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var chance = BenchStats.ProbabilityGreater(many, many);
        runner.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(10) && Math.Abs(chance - 0.5) < 1e-9, $"a million against a million runs finishes quickly ({clock.ElapsedMilliseconds} ms)");
        runner.AreEqual(1, BenchStats.Direction("rps"), "throughput: higher is better");
        runner.AreEqual(-1, BenchStats.Direction("p99_ms"), "latency: lower is better");
        runner.AreEqual(0, BenchStats.Direction("client_conns"), "a connection count has no direction");

        var written = new BenchRecord { Scenario = "get_c16", Run = 3, Label = "main", BackgroundCpuPercent = 4.5, EsetRunning = true, Metrics = { ["rps"] = 3000.5 } };
        var line = written.ToLine();
        runner.IsTrue(line.Contains("\"scenario\":\"get_c16\"") && line.Contains("\"metrics\":{\"rps\":3000.5}") && !line.Contains("error"), "a record is one compact line without null fields");
        var parsed = BenchReport.Parse([line, "", """{"kind":"env","machine":"x"}""", "not json", """{"kind":"run","metrics":{"rps":1e999}}""", "[1,2]", "null"], out var skipped);
        // 1e999 is either rejected (line skipped) or read as infinity (metric dropped): either way no infinity gets through.
        runner.AreEqual(5, parsed.Count + skipped, "every non-blank, non-header line is either parsed or counted as skipped");
        runner.IsTrue(parsed.Count >= 1 && skipped >= 3, "junk, an array and null are skipped");
        runner.IsTrue(parsed.All(r => r.Metrics.Values.All(double.IsFinite)), "a metric that is not finite never reaches a table");
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
            var unknown = false;
            try { BenchReport.Load(file + "#nope", out _); } catch (InvalidDataException) { unknown = true; }
            runner.IsTrue(unknown, "a label that matches nothing is an error, not an empty table");
            var hashed = Path.Combine(Path.GetTempPath(), $"piper#bench-{Guid.NewGuid():N}.jsonl");
            File.Copy(file, hashed);
            try { runner.AreEqual(2, BenchReport.Load(hashed, out _).Count, "a '#' in the file's own name is part of the name"); }
            finally { File.Delete(hashed); }
        }
        finally { File.Delete(file); }

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

        var constant = new[] { Constant("a"), Constant("a") };
        runner.IsTrue(!BenchReport.Compare(constant, constant, "a", "b").Contains("errors"), "a metric that never varies is left out");
        runner.IsTrue(BenchReport.Summary(a).Contains("median"), "the summary prints");
        return Task.CompletedTask;

        static BenchRecord Record(string label, int run, double rps) => new() { Scenario = "scenario", Run = run, Label = label, Metrics = { ["rps"] = rps } };
        static BenchRecord Constant(string label) => new() { Scenario = "s", Label = label, Metrics = { ["errors"] = 0 } };
    });
}
