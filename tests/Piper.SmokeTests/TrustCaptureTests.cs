using Piper.App;
using Piper.Core.Security;

// C1: the status text only claims decryption while HTTPS is really decrypted, and a repaint never
// waits on the certificate store. The Windows certificate store is never touched.
internal static class TrustCaptureTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await RunStatusTextAsync(runner);
        await RunDisplayReadAsync(runner);
    }

    private static Task RunDisplayReadAsync(TestRunner runner) => runner.RunAsync("a repaint after the cache expired never reads the store on the calling thread", () =>
    {
        var reads = 0;
        var trusted = true;
        var clock = new FakeClock();
        var cache = new TrustStateCache(() => { reads++; return trusted; }, TimeSpan.FromSeconds(30), clock);
        var queued = 0;

        runner.IsTrue(cache.ForDisplay(() => queued++), "the first answer has to be read");
        runner.AreEqual(1, reads, "and that one read happened");

        clock.Advance(TimeSpan.FromSeconds(31));
        trusted = false;
        var shown = cache.ForDisplay(() => queued++);
        runner.IsTrue(shown, "after expiry the repaint shows the last answer");
        runner.AreEqual(1, reads, "without reading the store");
        runner.AreEqual(1, queued, "and asks for the refresh to happen elsewhere");

        runner.IsTrue(cache.TryRefresh(), "the worker's read succeeds");
        runner.IsTrue(!cache.ForDisplay(() => queued++) && queued == 1, "then the repaint shows the fresh answer and queues nothing");

        // A worker read that fails keeps the last answer, does not throw, and does not poison the cache.
        clock.Advance(TimeSpan.FromSeconds(31));
        var failing = new TrustStateCache(() => throw new System.Security.Cryptography.CryptographicException("store unavailable"), TimeSpan.FromSeconds(30), clock);
        runner.IsTrue(!failing.TryRefresh() && failing.Last is null, "a failed first read reports failure and leaves no answer");
        var fails = false;
        var flaky = new TrustStateCache(() => fails ? throw new UnauthorizedAccessException() : true, TimeSpan.FromSeconds(30), clock);
        runner.IsTrue(flaky.TryRefresh() && flaky.Last == true, "a good read is kept");
        clock.Advance(TimeSpan.FromSeconds(31));
        fails = true;
        runner.IsTrue(!flaky.TryRefresh(), "a read that then fails reports failure");
        runner.IsTrue(flaky.Last == true && flaky.NeedsRead, "the last answer is kept and a retry is still due");
        var calling = 0;
        runner.IsTrue(flaky.ForDisplay(() => calling++) && calling == 1, "a repaint still shows it and does not read");
        return Task.CompletedTask;
    });

    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    private static Task RunStatusTextAsync(TestRunner runner) => runner.RunAsync("the status text only says decryption is on while HTTPS is really decrypted", () =>
    {
        var untrusted = Strings.StatusBar.Listening("127.0.0.1:8888", HttpsState.NotTrusted);
        runner.IsTrue(untrusted.Contains("not trusted", StringComparison.Ordinal) && !untrusted.Contains("decryption on", StringComparison.Ordinal),
            $"an untrusted root is reported as such, not as decryption on ({untrusted})");
        runner.IsTrue(Strings.StatusBar.Listening("127.0.0.1:8888", HttpsState.Decrypting).Contains("decryption on", StringComparison.Ordinal),
            "decryption is reported on only when decrypting");
        runner.IsTrue(Strings.StatusBar.Listening("127.0.0.1:8888", HttpsState.Off).Contains("decryption off", StringComparison.Ordinal),
            "a switched-off option is reported as off");

        var labels = new[] { HttpsState.Decrypting, HttpsState.NotTrusted, HttpsState.Off }
            .Select(state => Strings.StatusBar.HttpsLabel(state)).ToList();
        runner.AreEqual(3, labels.Distinct().Count(), "the three HTTPS states read differently in the status bar");
        runner.IsTrue(labels[1].Contains("Not decrypted", StringComparison.Ordinal),
            "the untrusted state says HTTPS is not decrypted");
        runner.IsTrue(!labels[1].Contains("Decrypting", StringComparison.Ordinal) && !labels[2].Contains("Decrypting", StringComparison.Ordinal),
            "only the decrypting state ever says Decrypting");
        return Task.CompletedTask;
    });
}
