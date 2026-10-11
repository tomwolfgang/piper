using Piper.Bench;

// The per-request division behind cpu_per_req_us and alloc_per_req_bytes: numerator and denominator are
// taken at the same window edges by LoadGenerator, and this is the arithmetic that joins them.
internal static class BenchWindowTests
{
    public static Task RunPerRequestAsync(TestRunner runner) => runner.RunAsync("bench window: per-request counters", () =>
    {
        runner.AreEqual(500.0, BenchStats.PerRequest(1000, 2000, 1000.0), "1000 ms of CPU over 2000 requests is 500 us each");
        runner.AreEqual(100.0, BenchStats.PerRequest(200_000, 2000), "200,000 bytes over 2000 requests is 100 each");
        runner.AreEqual(0.0, BenchStats.PerRequest(5000, 0), "no counted request: 0, not infinity or NaN");
        runner.AreEqual(0.0, BenchStats.PerRequest(5000, -3), "a negative count is treated as none");
        runner.AreEqual(0.0, BenchStats.PerRequest(0, 10), "no growth is 0");
        runner.IsTrue(double.IsFinite(BenchStats.PerRequest(long.MaxValue, 1, 1000.0)), "a huge delta stays finite");
        return Task.CompletedTask;
    });
}
