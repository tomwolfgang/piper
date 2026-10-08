using Piper.Bench;

// How tools/Piper.Bench reads a results file and judges two of them (the benchmark runs are not tests).
internal static class BenchComparisonTests
{
    public static Task RunMedianAndVerdictAsync(TestRunner runner) => runner.RunAsync("bench statistics: run-level median, few-run verdicts, no-success runs", () =>
    {
        runner.AreEqual(2.5, BenchStats.Median([1, 2, 3, 4]), "the median of four runs is the mean of the middle two, not the lower one");
        runner.AreEqual(3.5, BenchStats.Median([6, 1, 5, 2, 4, 3]), "and of six");
        runner.AreEqual(2.0, BenchStats.Median([3, 1, 2]), "an odd count takes the middle run");
        runner.AreEqual(9.0, BenchStats.Median([9]), "one run is its own median");
        runner.AreEqual(5.0, BenchStats.Median([5, 5]), "two equal runs");
        runner.IsTrue(double.IsNaN(BenchStats.Median([])), "no runs gives NaN");
        runner.IsTrue(double.IsFinite(BenchStats.Median([double.MaxValue, double.MaxValue])), "two huge runs do not overflow");
        runner.AreEqual(2.0, BenchStats.Percentile([1, 2, 3, 4], 0.5), "a latency percentile stays nearest-rank");

        // A load run fails when more than 1% of what it attempted (succeeded + errors + timeouts) did not succeed.
        static Dictionary<string, double> Load(double requests, double errors, double timeouts = 0, double rps = 100) =>
            new() { ["rps"] = rps, ["requests"] = requests, ["errors"] = errors, ["timeouts"] = timeouts };
        foreach (var (metrics, failed, what) in new[]
        {
            (Load(0, 200, rps: 0), true, "a rate of zero"), (Load(99, 1), false, "exactly 1% failed"), (Load(9900, 100), false, "exactly 1% of 10000"), (Load(9899, 101), true, "just over 1% failed"),
            (Load(99, 0, 1), false, "1 timeout in 100"), (Load(98, 0, 2), true, "2 timeouts in 100"), (Load(1, 2999, rps: 0.439), true, "0.439 rps, 2999 errors"), (Load(0, 0, rps: 5), true, "nothing attempted"),
            (Load(100, 0, rps: double.NaN), true, "a NaN rate"), (Load(100, double.NaN), true, "a NaN count"), (Load(100, -5), true, "a negative count"), (new Dictionary<string, double> { ["rps"] = 0.5 }, false, "uncounted successes"),
        })
            runner.IsTrue((BenchStats.FailureReason(metrics) is not null) == failed, $"a load run with {what} is {(failed ? "a failure" : "kept")}");

        var four = new[] { 1.0, 2, 3, 4 };
        runner.IsTrue(BenchReport.Summary(four.Select((v, i) => Run("a", i, v)).ToList()).Contains("2.50 [1.00..4.00]"), "the summary shows 2.5 as the median of [1,2,3,4]");

        var fast = new[] { 120.0, 122, 121, 119 };
        var slow = new[] { 100.0, 102, 101, 99 };
        runner.IsTrue(BenchReport.Compare(Runs("a", slow), Runs("b", fast), "a", "b").Contains("B better"), "four runs a side with disjoint ranges are called");
        var three = BenchReport.Compare(Runs("a", slow.Take(3)), Runs("b", fast.Take(3)), "a", "b");
        runner.IsTrue(!three.Contains("B better") && !three.Contains("B worse") && three.Contains("too few runs"), "three runs a side are not called better or worse");
        runner.IsTrue(BenchReport.Compare(Runs("a", slow.Take(2)), Runs("b", [90, 130]), "a", "b").Contains("OVERLAP"), "overlapping ranges are still noise");

        var huge = BenchReport.Summary([Run("a", 0, 1e308), Run("a", 1, double.MaxValue)]);
        runner.IsTrue(huge.Split('\n').All(line => line.Length < 200) && huge.Contains("E+"), "a value of 1e308 does not print 300 digits");
        return Task.CompletedTask;
    });

    public static Task RunEnvironmentAsync(TestRunner runner) => runner.RunAsync("bench compare: environment header, differences and one-sided metrics", () =>
    {
        const string env = """{"schema":2,"kind":"env","machine":"32 cores, 31.6 GB","hosts":[{"label":"a","file":"piper-bench.dll"}],"scenarios":["get_c16","startup"],"runs":5,"duration_s":8}""";
        const string build = """{"kind":"build","configuration":"Release","git_sha":"abcdef123456","cpu":"Test CPU","power_plan":"balanced"}""";
        var read = BenchReport.ReadEnv([env, build, """{"kind":"run","scenario":"x"}"""]);

        // The three lines of a real file (env, build, run) give one record and nothing counted as unreadable.
        var roundTrip = BenchReport.Parse([env, build, new BenchRecord { Scenario = "get_c16", Label = "a", Metrics = { ["rps"] = 5 } }.ToLine()], out var unreadable);
        runner.IsTrue(roundTrip.Count == 1 && unreadable == 0 && roundTrip[0].Scenario == "get_c16", "env + build + run parses to exactly one record, none skipped");
        runner.IsTrue(read is { Machine: "32 cores, 31.6 GB", Runs: 5, DurationSeconds: 8, Cpu: "Test CPU", Configuration: "Release", PowerPlan: "balanced" } && read.Scenarios.SequenceEqual(["get_c16", "startup"]), "the env and build lines are read");

        var old = BenchReport.ReadEnv(["""{"schema":1,"kind":"env","machine":"old","hosts":[{"Label":"a","Path":"C:\\x"}],"scenarios":["a"],"runs":3,"duration_s":4}"""]);
        runner.IsTrue(old is { Machine: "old", Runs: 3, Cpu: "" }, "a schema 1 file (no build line, PascalCase hosts) is still read");
        runner.IsTrue(BenchReport.ReadEnv([]) is null && BenchReport.ReadEnv(["not json", "[1]", "null", """{"kind":1}""", """{"kind":"env","scenarios":"x","runs":"y","machine":5}"""]) is { Machine: "", Runs: 0 }, "hostile or missing headers give nothing or empty fields, never an exception");

        var a = BenchReport.ReadEnv([env, build])!;
        runner.AreEqual(0, BenchReport.EnvWarnings(a, a).Count, "the same environment has no warning");
        var other = a with { Machine = "8 cores", Scenarios = ["get_c16", "download_cl"], Runs = 15, DurationSeconds = 4, Cpu = "Other CPU", Configuration = "Debug", PowerPlan = "power saver" };
        var warnings = string.Join("\n", BenchReport.EnvWarnings(a, other));
        runner.IsTrue(new[] { "machines differ", "CPUs differ", "scenarios differ", "only in A: startup", "only in B: download_cl", "runs differ", "duration differs", "power plans differ", "Debug build" }.All(warnings.Contains), $"every difference is named: {warnings}");

        // A results file is shared: ESC, C1 and bidi characters must not reach a terminal through a table.
        runner.IsTrue(BenchReport.Clean("x\u001b[2J\u009b\u202Ey") == "x?[2J??y" && BenchReport.Clean(null) == "" && BenchReport.Clean(new string('a', 500)).Length == 200, "control and format characters are replaced, long text is cut");
        var poisoned = BenchReport.Parse(["""{"kind":"run","scenario":"s\u001b[31m","label":"l\u202E","error":"e\u001b","metrics":{"m\u001b":1,"m\u0007":2}}"""], out _);
        var printed = BenchReport.Summary(poisoned) + BenchReport.Compare(poisoned, poisoned, "n\u001b", "n\u009b", BenchReport.ReadEnv(["""{"kind":"env","machine":"m\u001b"}"""]), a);
        runner.IsTrue(poisoned.Count == 1 && !printed.Any(c => char.IsControl(c) && c is not ('\n' or '\r')) && !printed.Contains('\u202E'), "no control or bidi character reaches a summary or comparison");
        runner.IsTrue(BenchReport.EnvWarnings(a, null).Count == 1 && BenchReport.EnvWarnings(null, null).Count == 1, "a file without a header is a warning");
        runner.AreEqual(0, BenchReport.EnvWarnings(a, a with { Cpu = "", PowerPlan = "" }).Count, "a field one file does not have is not a difference");

        var left = Runs("a", [1.0, 2, 3, 4, 5]);
        var right = Runs("b", [1.0, 2, 3, 4, 5]).Concat([new BenchRecord { Scenario = "extra", Label = "b", Metrics = { ["rps"] = 1 } }]).ToList();
        var table = BenchReport.Compare(left, right, "a", "b", a, other);
        runner.IsTrue(table.StartsWith("WARNING: ", StringComparison.Ordinal) && table.Contains("Only in B: extra/rps") && table.Contains("Only in A: none"), "the table opens with the warnings and ends with the metrics only one file has");
        runner.IsTrue(!BenchReport.Compare(left, left, "a", "a").Contains("WARNING"), "no headers given, no environment check");
        return Task.CompletedTask;
    });

    public static Task RunTempFoldersAsync(TestRunner runner) => runner.RunAsync("bench temp folders: stale run folders are removed, nothing else", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), $"piper-bench-temp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string Make(string name, bool old)
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "Piper-Root.pfx"), "x");
                if (old) Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-3));
                return path;
            }

            var stale = Make("piper-bench-" + new string('a', 32), old: true);
            var fresh = Make("piper-bench-" + new string('b', 32), old: false);
            var otherName = Make("piper-bench-options-" + new string('c', 32), old: true);
            var asFile = Path.Combine(root, "piper-bench-" + new string('e', 32) + ".jsonl");
            File.WriteAllText(asFile, "x");
            File.SetCreationTimeUtc(asFile, DateTime.UtcNow.AddDays(-3));

            runner.AreEqual(1, BenchTemp.DeleteStaleFolders(root, TimeSpan.FromDays(1)), "one old run folder is removed");
            runner.IsTrue(!Directory.Exists(stale), "the folder older than a day is gone");
            runner.IsTrue(Directory.Exists(fresh), "a folder from today is kept (another run may be using it)");
            runner.IsTrue(Directory.Exists(otherName) && File.Exists(asFile), "names that are not a run folder's, and files, are never touched");
            runner.AreEqual(0, BenchTemp.DeleteStaleFolders(Path.Combine(root, "missing"), TimeSpan.FromDays(1)), "a missing temp folder is not an error");
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    });

    private static BenchRecord Run(string label, int run, double rps) => new() { Scenario = "scenario", Run = run, Label = label, Metrics = { ["rps"] = rps } };

    private static List<BenchRecord> Runs(string label, IEnumerable<double> values) => values.Select((v, i) => Run(label, i, v)).ToList();
}
