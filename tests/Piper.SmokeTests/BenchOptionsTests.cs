using Piper.Bench;

// The command line of tools/Piper.Bench and what it writes into a results file (the benchmark runs are not tests).
internal static class BenchOptionsTests
{
    private const string Self = "self-build.dll";

    public static Task RunOptionsAsync(TestRunner runner) => runner.RunAsync("bench options: limits, bad values and --host", () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"piper-bench-options-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var dll = Path.Combine(folder, "build.dll");
            var exe = Path.Combine(folder, "build.EXE");
            var text = Path.Combine(folder, "build.txt");
            foreach (var path in new[] { dll, exe, text }) File.WriteAllText(path, "");

            Parse([], out var defaults, out var none);
            runner.IsTrue(none == "" && defaults is { Runs: 5 } && defaults.Duration == TimeSpan.FromSeconds(8) && defaults.Timeout == TimeSpan.FromSeconds(300), "no arguments gives the defaults");
            runner.IsTrue(defaults!.Hosts is [{ Label: "current", Path: Self }], "and the current build as the only host");

            // Each limit is accepted, and one beyond it is refused with the flag and the range named.
            foreach (var (flag, low, high) in new[] { ("--runs", 1, 1000), ("--duration", 1, 600), ("--timeout", 10, 3600) })
            {
                runner.IsTrue(Parse([flag, low.ToString()], out _, out _), $"{flag} {low} is the lowest allowed");
                runner.IsTrue(Parse([flag, high.ToString()], out _, out _), $"{flag} {high} is the highest allowed");
                foreach (var bad in new[] { (low - 1).ToString(), (high + 1).ToString(), "abc", "-5", "+5", "5.5", "1e2", " 5", "99999999999999999999" })
                {
                    var ok = Parse([flag, bad], out _, out var error);
                    runner.IsTrue(!ok && error.Contains(flag) && error.Contains($"{low} to {high}") && error.Contains($"'{bad}'"), $"{flag} '{bad}' is refused and says why");
                }
            }
            Parse(["--runs", "7", "--duration", "3", "--timeout", "20"], out var set, out _);
            runner.IsTrue(set!.Runs == 7 && set.Duration == TimeSpan.FromSeconds(3) && set.Timeout == TimeSpan.FromSeconds(20), "the values are applied");

            // A value that is itself an option is a missing value, not swallowed.
            foreach (var flag in new[] { "--runs", "--duration", "--timeout", "--scenario", "--host", "--out", "--summary", "--wait-quiet" })
            {
                runner.IsTrue(!Parse([flag, "--list"], out _, out var swallowed) && swallowed == $"{flag} needs a value.", $"{flag} followed by an option reports the missing value");
                runner.IsTrue(!Parse([flag], out _, out var missing) && missing == $"{flag} needs a value.", $"{flag} at the end reports the missing value");
            }
            runner.IsTrue(!Parse(["--out", ""], out _, out _), "an empty value is a missing value");

            runner.IsTrue(!Parse(["--wait-quiet", "0"], out _, out var quiet) && quiet.Contains("1 to 100"), "--wait-quiet 0 is refused");
            runner.IsTrue(!Parse(["--wait-quiet", "101"], out _, out _) && !Parse(["--wait-quiet", "x"], out _, out _), "--wait-quiet above 100 or not a number is refused");
            runner.IsTrue(Parse(["--wait-quiet", "1"], out var q1, out _) && q1!.WaitQuietPercent == 1 && Parse(["--wait-quiet", "100"], out _, out _), "--wait-quiet accepts 1 and 100");

            var unknownFlag = Parse(["--bogus"], out _, out var unknownError);
            runner.IsTrue(!unknownFlag && unknownError.StartsWith("Unrecognised option: --bogus", StringComparison.Ordinal), "an unknown flag is refused by name");
            runner.IsTrue(!Parse(["get_c16"], out _, out _), "a bare word is not an option");

            runner.IsTrue(!Parse(["--scenario", "get_c16,nope"], out _, out var scenarioError) && scenarioError.Contains("nope") && !scenarioError.Contains("get_c16,"), "an unknown scenario is named");
            runner.IsTrue(Parse(["--scenario", "get_c16, startup", "--scenario", "all"], out var scenarios, out _) && scenarios!.Scenarios.Count == 3, "known scenarios and 'all' are accepted, the flag repeats and entries are trimmed");

            runner.IsTrue(!Parse(["--compare", "a.jsonl"], out _, out var oneFile) && oneFile.Contains("two results files"), "--compare with one file is refused");
            runner.IsTrue(!Parse(["--compare", "a.jsonl", "--out", "x"], out _, out _), "--compare followed by an option is refused");
            runner.IsTrue(Parse(["--compare", "a.jsonl#x", "b.jsonl"], out var pair, out _) && pair!.Compare is { A: "a.jsonl#x", B: "b.jsonl" }, "--compare takes two files");

            runner.IsTrue(!Parse(["--host", "noequals"], out _, out var noEquals) && noEquals.Contains("label=path"), "--host without '=' is refused");
            runner.IsTrue(!Parse(["--host", $"={dll}"], out _, out _) && !Parse(["--host", "label="], out _, out _), "--host without a label or without a path is refused");
            runner.IsTrue(!Parse(["--host", $"x={Path.Combine(folder, "missing.dll")}"], out _, out var missingHost) && missingHost.Contains("no such file"), "--host with a path that does not exist is refused");
            runner.IsTrue(!Parse(["--host", $"x={text}"], out _, out var wrongKind) && wrongKind.Contains(".dll or .exe"), "--host only runs a .dll or .exe");
            runner.IsTrue(!Parse(["--host", $"x={folder}"], out _, out _), "--host does not take a folder");
            runner.IsTrue(!Parse(["--host", $"a={dll}", "--host", $"a={exe}"], out _, out var duplicate) && duplicate.Contains("'a'"), "two hosts with one label are refused");
            runner.IsTrue(BenchOptions.TryParse(["--host", $"a={dll}", "--host", $"b={exe}", "--host", "c=self"], dll, _ => false, out var hosts, out _)
                && hosts.Hosts.Select(h => h.Label).SequenceEqual(["a", "b", "c"]) && hosts.Hosts[0].Path == dll && hosts.Hosts[2].Path == dll, "hosts keep their order and 'self' is this build");
        }
        finally { Directory.Delete(folder, recursive: true); }
        return Task.CompletedTask;

        static bool Parse(string[] args, out BenchOptions? options, out string error)
        {
            var ok = BenchOptions.TryParse(args, Self, name => name is "get_c16" or "startup", out var parsed, out error);
            options = parsed;
            return ok;
        }
    });

    public static Task RunResultsHygieneAsync(TestRunner runner) => runner.RunAsync("bench results file: no paths, no process names, no overwrite", () =>
    {
        runner.AreEqual("ekrn", BenchHygiene.ProcessLabel("ekrn"), "a known scanner is named");
        runner.AreEqual("msmpeng", BenchHygiene.ProcessLabel("msmpeng"), "the match ignores case");
        runner.AreEqual("other", BenchHygiene.ProcessLabel("my-secret-project"), "any other process is not named");
        runner.AreEqual("other", BenchHygiene.ProcessLabel(""), "an empty name is not named");

        var scrubbed = BenchHygiene.Scrub("IOException: Could not find file 'C:\\Users\\jane.doe\\bench\\a.dll'.");
        runner.IsTrue(!scrubbed.Contains("jane") && !scrubbed.Contains("C:\\") && scrubbed.StartsWith("IOException: Could not find file '<path>'", StringComparison.Ordinal), $"a quoted drive path is replaced: {scrubbed}");
        scrubbed = BenchHygiene.Scrub("FileNotFoundException: C:/Users/jane doe/My Builds/piper-bench.dll was not found");
        runner.IsTrue(!scrubbed.Contains("jane") && !scrubbed.Contains("Builds") && scrubbed.Contains("<path>"), $"an unquoted path with spaces and slashes goes whole: {scrubbed}");
        scrubbed = BenchHygiene.Scrub("IOException: \\\\fileserver\\share\\jane\\x.dll is busy");
        runner.IsTrue(!scrubbed.Contains("fileserver") && !scrubbed.Contains("jane"), $"a UNC path is replaced: {scrubbed}");
        runner.AreEqual("a  b c", BenchHygiene.Scrub("a\r\nb\tc"), "line breaks and control characters become spaces");
        runner.AreEqual("", BenchHygiene.Scrub(""), "an empty message stays empty");
        var long1 = BenchHygiene.Scrub(new string('x', 5000));
        runner.IsTrue(long1.Length <= 203 && long1.EndsWith("...", StringComparison.Ordinal), "a long message is cut");
        runner.AreEqual("timed out after 5 s", BenchHygiene.Scrub("timed out after 5 s"), "a message without a path is unchanged");

        var folder = Path.Combine(Path.GetTempPath(), $"piper-bench-results-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "baseline.jsonl");
            File.WriteAllText(file, "the only copy");
            var refused = false;
            try { BenchHygiene.CreateResultsFile(file).Dispose(); }
            catch (IOException ex) { refused = ex.Message.Contains("already exists"); }
            runner.IsTrue(refused && File.ReadAllText(file) == "the only copy", "an existing results file is refused and left as it was");

            var fresh = Path.Combine(folder, "new.jsonl");
            using (var stream = BenchHygiene.CreateResultsFile(fresh)) stream.WriteByte((byte)'x');
            runner.AreEqual("x", File.ReadAllText(fresh), "a new results file is created and written");
        }
        finally { Directory.Delete(folder, recursive: true); }
        return Task.CompletedTask;
    });
}
