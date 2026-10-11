using Piper.Bench;

// When a time-limited streamed download (rtt50_h1, rtt50_h2) is a measurement and when it is a failed run.
internal static class BenchDownloadTests
{
    public static Task RunDownloadFailureAsync(TestRunner runner) => runner.RunAsync("bench download: headers without body bytes, and short bodies, are failures", () =>
    {
        const long gigabyte = 1024L * 1048576;
        runner.IsTrue(BenchStats.DownloadFailure(0, gigabyte, windowEnded: true) is not null, "the window ended with no body byte: a stall, not mbps=0");
        runner.IsTrue(BenchStats.DownloadFailure(0, gigabyte, windowEnded: false) is not null, "the body ended empty: a failure");
        runner.IsTrue(BenchStats.DownloadFailure(-1, gigabyte, windowEnded: true) is not null, "a negative count is not a measurement");
        runner.IsTrue(BenchStats.DownloadFailure(1, gigabyte, windowEnded: true) is null, "one byte when the window ended is a (very slow) rate");
        runner.IsTrue(BenchStats.DownloadFailure(gigabyte / 2, gigabyte, windowEnded: true) is null, "part of the body when the window ended is a rate");
        runner.IsTrue(BenchStats.DownloadFailure(gigabyte - 1, gigabyte, windowEnded: false) is { } shortBody && shortBody.Contains("1073741823 of 1073741824"), "a body that ended before the window with a byte missing fails, with the counts");
        runner.IsTrue(BenchStats.DownloadFailure(gigabyte, gigabyte, windowEnded: false) is null, "the whole body before the window ended is a measurement");
        runner.IsTrue(BenchStats.DownloadFailure(gigabyte + 1, gigabyte, windowEnded: false) is null, "more than expected is not a short body");
        return Task.CompletedTask;
    });
}
