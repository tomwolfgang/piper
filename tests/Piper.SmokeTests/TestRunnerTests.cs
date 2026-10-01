using System.Diagnostics;

/// <summary>
/// The runner itself: it has to fail a hung test instead of hanging, honour --filter, find every test
/// group without a hand-kept list, and keep each process's files to itself.
/// </summary>
internal static class TestRunnerTests
{
    /// <summary>Short enough to keep the suite quick, long enough that an idle pool never trips it.</summary>
    private static readonly TimeSpan HangTimeout = TimeSpan.FromMilliseconds(400);

    private static readonly TimeSpan DetectedWithin = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("a test that never finishes fails at the timeout and the run carries on", async () =>
        {
            var child = new TestRunner(TextWriter.Null, testTimeout: HangTimeout);
            var stopwatch = Stopwatch.StartNew();
            await child.RunAsync("hangs", () => new TaskCompletionSource().Task);
            var elapsed = stopwatch.Elapsed;
            await child.RunAsync("passes afterwards", () =>
            {
                child.IsTrue(true, "the next test ran");
                return Task.CompletedTask;
            });

            runner.AreEqual(1, child.Failed, "the hung test is one failure");
            runner.IsTrue(child.Failures[0].Contains("timed out after", StringComparison.Ordinal), $"and the failure says it timed out ({child.Failures[0]})");
            runner.IsTrue(elapsed < DetectedWithin, $"it was reported within seconds, not left hanging ({elapsed.TotalMilliseconds:0} ms)");
            runner.AreEqual(1, child.Passed, "the assertion of the following test still counted");
            runner.AreEqual(2, child.Timings.Count, "both tests have a timing");
            runner.AreEqual(1, child.Summarize(), "a run holding a hung test exits non-zero");
        });

        await runner.RunAsync("a test that blocks its thread before its first await is timed out too", async () =>
        {
            var child = new TestRunner(TextWriter.Null, testTimeout: HangTimeout);
            using var release = new ManualResetEventSlim();
            var stopwatch = Stopwatch.StartNew();
            await child.RunAsync("blocks", () =>
            {
                release.Wait();
                return Task.CompletedTask;
            });
            var elapsed = stopwatch.Elapsed;
            release.Set();

            runner.AreEqual(1, child.Failed, "the blocked test fails");
            runner.IsTrue(elapsed < DetectedWithin, $"and the runner got control back ({elapsed.TotalMilliseconds:0} ms)");
        });

        await runner.RunAsync("assertions from a test the runner gave up on are not counted", async () =>
        {
            var child = new TestRunner(TextWriter.Null, testTimeout: HangTimeout);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await child.RunAsync("wakes up too late", async () =>
            {
                await release.Task;
                child.IsTrue(false, "late failing assertion");
                child.IsTrue(true, "late passing assertion");
                finished.SetResult();
            });
            release.SetResult();
            await finished.Task.WaitAsync(DetectedWithin);

            runner.AreEqual(1, child.Failed, "only the timeout counts, not the late failure");
            runner.AreEqual(0, child.Passed, "and the late pass is not credited to a later test");
        });

        await runner.RunAsync("an exception that happens to be a TimeoutException is reported as the test's own", async () =>
        {
            var child = new TestRunner(TextWriter.Null, testTimeout: HangTimeout);
            await child.RunAsync("throws", () => throw new TimeoutException("from the test body"));

            runner.AreEqual(1, child.Failed, "one failure");
            runner.IsTrue(child.Failures[0].Contains("threw TimeoutException", StringComparison.Ordinal)
                && child.Failures[0].Contains("from the test body", StringComparison.Ordinal), $"it names the exception, not a runner timeout ({child.Failures[0]})");
        });

        await runner.RunAsync("--filter runs the tests whose name matches, ignoring case, and skips the rest", async () =>
        {
            var ran = new List<string>();
            var child = new TestRunner(TextWriter.Null, filter: "KEEP");
            await child.RunAsync("please keep this", () => { ran.Add("kept"); return Task.CompletedTask; });
            await child.RunAsync("drop this", () => { ran.Add("dropped"); return Task.CompletedTask; });

            runner.AreEqual("kept", string.Join(",", ran), "only the matching body ran");
            runner.AreEqual(1, child.Skipped, "the other is counted as skipped");
            runner.AreEqual(1, child.Timings.Count, "and left out of the timings");
            runner.AreEqual(0, child.Summarize(), "a filtered run that passes exits zero");
        });

        await runner.RunAsync("--filter naming a group runs all of its tests; a filter matching nothing fails the run", async () =>
        {
            var ran = new List<string>();
            var child = new TestRunner(TextWriter.Null, filter: "somegroup.runthing");
            await child.RunGroupAsync("Some.SomeGroup.RunThingAsync", async r =>
            {
                await r.RunAsync("first", () => { ran.Add("first"); return Task.CompletedTask; });
                await r.RunAsync("second", () => { ran.Add("second"); return Task.CompletedTask; });
            });
            await child.RunGroupAsync("Some.OtherGroup.RunOtherAsync", async r =>
                await r.RunAsync("third", () => { ran.Add("third"); return Task.CompletedTask; }));

            runner.AreEqual("first,second", string.Join(",", ran), "the whole matching group ran and nothing else");

            var nothing = new TestRunner(TextWriter.Null, filter: "no such test");
            await nothing.RunAsync("something", () => Task.CompletedTask);
            runner.AreEqual(1, nothing.Summarize(), "a typo in --filter is an error, not a green run");
        });

        await runner.RunAsync("a group that throws outside any test is one failure and the next group still runs", async () =>
        {
            var child = new TestRunner(TextWriter.Null);
            await child.RunGroupAsync("Some.Broken.RunAsync", _ => throw new InvalidOperationException("setup broke"));
            await child.RunGroupAsync("Some.Fine.RunAsync", r => r.RunAsync("fine", () =>
            {
                child.IsTrue(true, "ran");
                return Task.CompletedTask;
            }));

            runner.AreEqual(1, child.Failed, "the broken group is reported");
            runner.IsTrue(child.Failures[0].Contains("setup broke", StringComparison.Ordinal), "with its reason");
            runner.AreEqual(1, child.Passed, "and the following group ran");
        });

        await runner.RunAsync("command-line options parse and reject bad input", () =>
        {
            runner.IsTrue(TestOptions.TryParse([], out var none, out _), "no arguments is fine");
            runner.AreEqual((string?)null, none.Filter, "no filter by default");
            runner.AreEqual(TestOptions.DefaultTimeout, none.Timeout, "default timeout");

            runner.IsTrue(TestOptions.TryParse(["--filter", "Http2", "--timeout", "5", "--list"], out var full, out _), "all options together");
            runner.AreEqual("Http2", full.Filter, "--filter value");
            runner.AreEqual(TimeSpan.FromSeconds(5), full.Timeout, "--timeout value");
            runner.IsTrue(full.List, "--list");

            runner.IsTrue(TestOptions.TryParse(["--filter=a=b", "--timeout=9"], out var inline, out _), "--name=value form");
            runner.AreEqual("a=b", inline.Filter, "only the first = splits");
            runner.AreEqual(TimeSpan.FromSeconds(9), inline.Timeout, "--timeout=value");

            foreach (var bad in new[]
            {
                new[] { "--filter" }, new[] { "--filter=" }, new[] { "--timeout" }, new[] { "--timeout", "0" },
                new[] { "--timeout", "3601" }, new[] { "--timeout", "soon" }, new[] { "--list=yes" },
                new[] { "--list", "extra" }, new[] { "--bogus" }, new[] { "stray" },
            })
            {
                runner.IsTrue(!TestOptions.TryParse(bad, out _, out var error) && error.StartsWith("usage:", StringComparison.Ordinal),
                    $"rejected: {string.Join(' ', bad)}");
            }

            return Task.CompletedTask;
        });

        await runner.RunAsync("test groups are discovered without a hand-kept list, in a stable order", () =>
        {
            var ids = TestDiscovery.Groups.Select(group => group.Id).ToArray();

            runner.IsTrue(ids.Contains("TestRunnerTests.RunAsync"), "this class is discovered");
            runner.IsTrue(ids.Contains("SazImportLimitTests.RunAsync"), "a class the old hand-kept list forgot is discovered");
            runner.IsTrue(ids.Contains("DiagnosticsBundleTests.RunLogTrimmingAsync"), "so is a second Run method in one class");
            runner.AreEqual(ids.Length, ids.Distinct().Count(), "no group appears twice");
            runner.IsTrue(ids.SequenceEqual(ids.OrderBy(id => id, StringComparer.Ordinal)), "the order is the ordinal name order");
            runner.IsTrue(TestDiscovery.Discover(typeof(TestRunner).Assembly).Select(group => group.Id).SequenceEqual(ids),
                "discovering again gives the same list in the same order");

            var fixture = typeof(NotGroups);
            foreach (var method in fixture.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                runner.IsTrue(!TestDiscovery.IsGroup(method), $"{method.Name} is not a group");
            }

            return Task.CompletedTask;
        });

        await runner.RunAsync("this process runs in its own temp folder", () =>
        {
            var temp = Path.GetTempPath();
            runner.IsTrue(temp.Contains($"{TestOptions.SandboxPrefix}{Environment.ProcessId}-", StringComparison.Ordinal),
                "Path.GetTempPath() is the per-process folder, so no fixed temp name can collide with another run");
            runner.IsTrue(Directory.Exists(temp), "and it exists");
            return Task.CompletedTask;
        });
    }

    /// <summary>Near misses that discovery must leave alone.</summary>
    private sealed class NotGroups
    {
        public static Task RunNoParameterAsync() => Task.CompletedTask;
        public static Task RunWrongParameterAsync(int count) => Task.CompletedTask;
        public static Task RunTwoParametersAsync(TestRunner runner, int count) => Task.CompletedTask;
        public static void RunWrongReturnAsync(TestRunner runner) { }
        public static Task<int> RunGenericReturnAsync(TestRunner runner) => Task.FromResult(0);
        public static Task NotNamedRunAsync(TestRunner runner) => Task.CompletedTask;
        public static Task RunNotNamedAsyncSuffix(TestRunner runner) => Task.CompletedTask;
        public static Task Run_Async_Middle_Wrong(TestRunner runner) => Task.CompletedTask;
        internal static Task RunInternalAsync(TestRunner runner) => Task.CompletedTask;
        private static Task RunPrivateAsync(TestRunner runner) => Task.CompletedTask;
        public Task RunInstanceAsync(TestRunner runner) => Task.CompletedTask;
    }
}
