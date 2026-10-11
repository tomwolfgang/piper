using Piper.Bench;

// The build configuration each proxy host reports in its READY line, and what a results file does with it.
internal static class BenchHostConfigTests
{
    public static Task RunHostConfigurationAsync(TestRunner runner) => runner.RunAsync("bench host configuration: reported, recorded, warned about, older files still read", () =>
    {
        runner.AreEqual("Release", BenchHygiene.ConfigurationToken("Release"), "a plain name passes");
        runner.AreEqual("Debug", BenchHygiene.ConfigurationToken("Debug"), "Debug passes");
        runner.AreEqual("Release-x64.1", BenchHygiene.ConfigurationToken("Release-x64.1"), "'.', '-' and digits are allowed");
        foreach (var hostile in new[] { "", "a b", new string('a', 17), "Rel\u001bease", "Release\"", "C:\\x", "../x", "\u202Egubed", "Rélease" })
            runner.AreEqual("unknown", BenchHygiene.ConfigurationToken(hostile), $"'{hostile.Replace("\u001b", "ESC").Replace("\u202E", "RLO")}' is not shown or recorded");
        runner.AreEqual("unknown", BenchHygiene.ConfigurationToken(null), "no value is unknown");
        runner.IsTrue(BenchHygiene.IsDebug("debug") && BenchHygiene.IsDebug("Debug") && !BenchHygiene.IsDebug("Release") && !BenchHygiene.IsDebug("unknown") && !BenchHygiene.IsDebug(null),
            "only Debug is a Debug build; unknown is not claimed to be one");

        const string build = """{"kind":"build","configuration":"Release","cpu":"c","power_plan":"balanced"}""";
        var debugHost = BenchReport.ReadEnv(["""{"schema":2,"kind":"env","machine":"m","hosts":[{"label":"before","file":"piper-bench.dll","configuration":"Release"},{"label":"after","file":"piper-bench.dll","configuration":"Debug"}],"scenarios":["a"],"runs":5,"duration_s":8}""", build])!;
        runner.IsTrue(debugHost.DebugHosts.SequenceEqual(["after"]), "the host that reported Debug is named");
        var releaseHosts = BenchReport.ReadEnv(["""{"schema":2,"kind":"env","machine":"m","hosts":[{"label":"x","file":"piper-bench.dll","configuration":"Release"}],"scenarios":["a"],"runs":5,"duration_s":8}""", build])!;
        runner.AreEqual(0, releaseHosts.DebugHosts.Length, "a Release host is not named");
        var warnings = string.Join("\n", BenchReport.EnvWarnings(releaseHosts, debugHost));
        runner.IsTrue(warnings.Contains("file B measured a Debug build of the proxy (after)") && !warnings.Contains("file A"), $"the compare warns about the file that has one: {warnings}");

        // Files from before the field existed, and hostile host lists, are read without it and without an exception.
        var older = BenchReport.ReadEnv(["""{"schema":2,"kind":"env","machine":"m","hosts":[{"label":"a","file":"piper-bench.dll"}],"scenarios":["a"],"runs":5,"duration_s":8}""", build])!;
        runner.AreEqual(0, older.DebugHosts.Length, "a schema 2 file without host configurations has no Debug host");
        runner.AreEqual(0, BenchReport.EnvWarnings(older, releaseHosts).Count, "and compares with a newer file without a warning");
        var schema1 = BenchReport.ReadEnv(["""{"schema":1,"kind":"env","machine":"old","hosts":[{"Label":"a","Path":"C:\\x"}],"scenarios":["a"],"runs":3,"duration_s":4}"""]);
        runner.IsTrue(schema1 is { Machine: "old" } && schema1.DebugHosts.Length == 0, "a schema 1 file is still read");
        foreach (var hosts in new[] { "\"x\"", "5", "null", "[1,\"Debug\",null,[]]", "{\"configuration\":\"Debug\"}", "[{\"configuration\":5,\"label\":6}]" })
        {
            var hostile = BenchReport.ReadEnv([$$"""{"kind":"env","machine":"m","hosts":{{hosts}}}"""]);
            runner.IsTrue(hostile is { Machine: "m" } && hostile.DebugHosts.Length == 0, $"hosts {hosts} give no Debug host and no exception");
        }
        return Task.CompletedTask;
    });
}
