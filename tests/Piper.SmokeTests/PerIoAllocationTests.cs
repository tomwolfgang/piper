using Piper.Core.Http;
using Piper.Core.Proxy;

// The deadline on each client write and idle-bounded read used to cost a linked CancellationTokenSource
// and a timer-queue entry per call, on every request in both directions. These tests count what a call
// allocates, so that coming back is a failure.
internal static class PerIoAllocationTests
{
    private const int Calls = 4000;

    // A call that completes on the spot should allocate nothing; the old code spent 144 bytes on its
    // linked source alone.
    private const long SyncBudget = 48;

    public static async Task RunPerIoAllocationAsync(TestRunner runner)
    {
        await runner.RunAsync("a client write that completes on the spot allocates no deadline", async () =>
        {
            await using var stream = new GuardedClientStream(new ScriptedStream(), TimeSpan.FromSeconds(30));
            var buffer = new byte[16];
            var perCall = await PerCallAsync(() => stream.WriteAsync(buffer).AsTask());
            runner.IsTrue(perCall <= SyncBudget, $"a write that completes at once allocated {perCall} bytes (budget {SyncBudget})");
        });

        await runner.RunAsync("a read under the idle timeout that completes on the spot allocates no deadline", async () =>
        {
            using var reader = new HttpStreamReader(new ScriptedStream()) { IdleTimeout = TimeSpan.FromSeconds(30) };
            var buffer = new byte[1];
            var perCall = await PerCallAsync(async () => await reader.ReadAsync(buffer, CancellationToken.None));
            runner.IsTrue(perCall <= SyncBudget, $"a read that completes at once allocated {perCall} bytes (budget {SyncBudget})");
        });
    }

    /// <summary>Bytes allocated per call on this thread, after a warm-up.</summary>
    private static async Task<long> PerCallAsync(Func<Task> call)
    {
        for (var i = 0; i < 200; i++) await call();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < Calls; i++) await call();
        return (GC.GetAllocatedBytesForCurrentThread() - before) / Calls;
    }
}
