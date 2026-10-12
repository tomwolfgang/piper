using Piper.Bench;

// The READY line a proxy host prints, as the benchmark driver reads it from a real child process. The host is a
// one-line batch file that prints a chosen line (the real host needs a built piper-bench, which a smoke test does
// not have), so what is tested is the process start, the line scan and the parse, not the proxy.
internal static class BenchHostClientTests
{
    public static Task RunReadyLineAsync(TestRunner runner) => runner.RunAsync("bench host client: 3-field and 4-field READY lines are read, a 2-field or malformed one is a clear error", async () =>
    {
        var folder = Path.Combine(Path.GetTempPath(), $"piper-bench-client-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var count = 0;
            async Task<HostClient> Start(params string[] output)
            {
                var host = Path.Combine(folder, $"host{count++}.cmd");
                await File.WriteAllLinesAsync(host, ["@echo off", .. output.Select(line => "echo " + line)]);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                return await HostClient.StartAsync(host, folder, timeout.Token);
            }

            var current = await Start("READY 4321 12.5 Release");
            await using (current)
                runner.IsTrue(current is { Port: 4321, StartMs: 12.5, Configuration: "Release" }, $"the 4-field line gives port, start time and configuration ({current.Port} {current.StartMs} {current.Configuration})");

            var older = await Start("READY 4322 7.0");
            await using (older)
                runner.IsTrue(older is { Port: 4322, StartMs: 7.0, Configuration: "unknown" }, $"the 3-field line of an older host gives configuration unknown, not Release ({older.Port} {older.StartMs} {older.Configuration})");

            var noisy = await Start("warming up", "READY 4323 1.0 Debug");
            await using (noisy)
                runner.IsTrue(noisy is { Port: 4323, Configuration: "Debug" }, "lines before READY are skipped");

            foreach (var bad in new[] { "READY 4324", "READY", "READY abc 1.0", "READY 4324 fast", "READY 0 1.0", "READY 70000 1.0", "READY -5 1.0", "READY 4324 -1" })
            {
                try
                {
                    await using var client = await Start(bad);
                    runner.IsTrue(false, $"'{bad}' is rejected");
                }
                catch (InvalidDataException ex)
                {
                    runner.IsTrue(ex.Message.Contains("READY port startup_ms", StringComparison.Ordinal) && ex.Message.Contains(bad, StringComparison.Ordinal), $"'{bad}' is a clear error: {ex.Message}");
                }
            }

            try
            {
                await using var silent = await Start("no ready line at all");
                runner.IsTrue(false, "a host that exits without READY fails");
            }
            catch (IOException ex)
            {
                runner.IsTrue(ex.Message.Contains("exited", StringComparison.Ordinal), $"a host that exits without READY is reported as having exited: {ex.Message}");
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    });

    public static Task RunParseReadyAsync(TestRunner runner) => runner.RunAsync("bench host client: READY parsing is exact about the form, lenient about a longer one", () =>
    {
        runner.AreEqual((4321, 12.5, "Release"), HostClient.ParseReady("READY 4321 12.5 Release"), "4 fields");
        runner.AreEqual((1, 0.0, "unknown"), HostClient.ParseReady("READY 1 0"), "3 fields, the smallest values");
        runner.AreEqual((65535, 1e3, "Debug"), HostClient.ParseReady("READY 65535 1E3 Debug"), "the largest port and an exponent");
        runner.AreEqual((4321, 1.0, "Debug"), HostClient.ParseReady("READY 4321 1.0 Debug later fields"), "fields after the configuration are ignored");
        runner.AreEqual("unknown", HostClient.ParseReady("READY 4321 1.0 Rel\"ease").Configuration, "a hostile configuration is not shown or recorded");
        foreach (var bad in new[] { "", "READY", "READY 1", "READY  1 2", "READY 1  2", "ready 1 2", "READYX 1 2", " READY 1 2", "READY 65536 1", "READY 0 1", "READY +1 1", "READY 1 NaN", "READY 1 Infinity", "READY 1 -0.5", "READY 1.5 2", "READY 99999999999 1" })
        {
            try
            {
                HostClient.ParseReady(bad);
                runner.IsTrue(false, $"'{bad}' is rejected");
            }
            catch (InvalidDataException)
            {
                runner.IsTrue(true, $"'{bad}' is rejected with InvalidDataException");
            }
        }
        return Task.CompletedTask;
    });
}
