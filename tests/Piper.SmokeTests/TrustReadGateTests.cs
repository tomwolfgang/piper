using Piper.Core.Security;

// The gate that keeps one background trust read running at a time. The bug it prevents: a read whose
// result never reaches the UI (BeginInvoke threw, or the store hung) left the "in flight" flag set, so
// the periodic check stopped for the whole session and a root removed outside Piper was never noticed.
internal static class TrustReadGateTests
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Abandon = TimeSpan.FromSeconds(120);

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("only one trust read runs at a time, and a success lets the next start", () =>
        {
            var gate = new TrustReadGate(Retry, Abandon);
            var first = gate.TryBegin(0);
            runner.IsTrue(first != 0, "the first read may start");
            runner.AreEqual(0, gate.TryBegin(1_000), "a second one waits while the first is running");
            gate.End(first, failed: false, nowMs: 2_000);
            runner.IsTrue(gate.TryBegin(2_001) != 0, "after it came back the next may start at once");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a failed read cools down for one cache lifetime", () =>
        {
            var gate = new TrustReadGate(Retry, Abandon);
            var id = gate.TryBegin(0);
            gate.End(id, failed: true, nowMs: 1_000);
            runner.AreEqual(0, gate.TryBegin(30_999), "no new read inside the cool-down");
            runner.IsTrue(gate.TryBegin(31_000) != 0, "and one is allowed once it has passed");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a read whose result could not be delivered does not wedge the refresh", () =>
        {
            var gate = new TrustReadGate(Retry, Abandon);
            var id = gate.TryBegin(0);
            gate.Abort(id); // BeginInvoke threw: no handle yet, being recreated
            runner.IsTrue(gate.TryBegin(1) != 0, "the next tick starts a new read straight away, with no failure penalty");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a read that never returns is abandoned, and its late result cannot clear its successor", () =>
        {
            var gate = new TrustReadGate(Retry, Abandon);
            var hung = gate.TryBegin(0);
            runner.AreEqual(0, gate.TryBegin(119_999), "still waiting inside the abandon window");
            var next = gate.TryBegin(120_000);
            runner.IsTrue(next != 0 && next != hung, "after the window a new read starts with a new id");
            gate.End(hung, failed: false, nowMs: 125_000); // the hung read finally returns
            runner.AreEqual(0, gate.TryBegin(125_001), "the late result of the abandoned read does not release the running one");
            gate.Abort(hung);
            runner.AreEqual(0, gate.TryBegin(125_002), "nor does a late abort");
            gate.End(next, failed: false, nowMs: 126_000);
            runner.IsTrue(gate.TryBegin(126_001) != 0, "the current read's own result does");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a failed read that was already abandoned does not start a cool-down for its successor", () =>
        {
            var gate = new TrustReadGate(Retry, Abandon);
            var hung = gate.TryBegin(0);
            var next = gate.TryBegin(120_000);
            gate.End(hung, failed: true, nowMs: 121_000);
            gate.End(next, failed: false, nowMs: 122_000);
            runner.IsTrue(gate.TryBegin(122_001) != 0, "only the current read's failure schedules a retry");
            return Task.CompletedTask;
        });
    }
}
