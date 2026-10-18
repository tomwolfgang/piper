using System.Net.Sockets;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Failed client TLS handshakes (X2 slice 2): repeated failures collapse into one counted row, bounded, and a
// trimmed or cleared row is replaced. The aggregator runs on a fake clock; the proxy cases are real loopback,
// no node or openssl.
internal static class TlsFailureCollapseTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public static async Task RunTlsFailureCollapseAsync(TestRunner runner)
    {
        await runner.RunAsync("aggregator: 1,000 failures from one key are one row; process, port or kind make another", () =>
        {
            var (store, _, aggregator) = Make();
            for (var i = 0; i < 1000; i++) Fail(aggregator, "Launcher.exe", i % 2 == 0 ? "Host.example" : "host.example.");
            Fail(aggregator, "launcher.EXE", "host.example");
            Fail(aggregator, "other.exe", "host.example");
            Fail(aggregator, "Launcher.exe", "host.example", port: 8443);
            Fail(aggregator, "Launcher.exe", "host.example", TlsFailureKind.ClosedEarly);
            var rows = Rows(store);
            runner.AreEqual(4, rows.Length, "one row per process, port and kind: case and a trailing dot do not split one");
            runner.AreEqual(1001, rows[0].TlsFailure!.Attempts, "the first counts every failure from it");
            runner.AreEqual(4, aggregator.KeyCount, "four keys");
            return Task.CompletedTask;
        });

        await runner.RunAsync("aggregator: the count reaches the grid, at most a few times a second, and ends on the last count", async () =>
        {
            var (store, _, aggregator) = Make();
            var updates = new List<int>();
            store.SessionUpdated += (_, e) => { lock (updates) updates.Add(e.Session.TlsFailure!.Attempts); };
            for (var i = 0; i < 1000; i++) Fail(aggregator, "p.exe", "h.test");
            runner.IsTrue(await Poll.UntilAsync(() => { lock (updates) return updates.Count > 0 && updates[^1] == 1000; }, 5000), "the final count is announced");
            await Task.Delay(600);
            lock (updates) runner.IsTrue(updates.Count <= 3, $"1,000 failures made {updates.Count} announcements");
        });

        await runner.RunAsync("aggregator: Stop announces the pending count at once and leaves nothing for later", async () =>
        {
            var (store, _, aggregator) = Make();
            var updates = new List<int>();
            store.SessionUpdated += (_, e) => { lock (updates) updates.Add(e.Session.TlsFailure!.Attempts); };
            for (var i = 0; i < 100; i++) Fail(aggregator, "p.exe", "h.test");
            aggregator.Stop();
            lock (updates) runner.IsTrue(updates is [100], $"announced once, with the count ({string.Join(",", updates)})");
            await Task.Delay(600);
            lock (updates) runner.AreEqual(1, updates.Count, "and nothing after it");
        });

        await runner.RunAsync("aggregator: a client the store does not admit cannot use up the allowance of the ones it does", () =>
        {
            var (store, _, aggregator) = Make();
            store.CaptureFilter = session => session.ProcessName != "excluded.exe";
            for (var i = 0; i < 50; i++) Fail(aggregator, "excluded.exe", "same.test");
            for (var i = 0; i < 30; i++) Fail(aggregator, "excluded.exe", $"other{i}.test");
            for (var i = 0; i < TlsFailureAggregator.MaxNewRowsPerWindow; i++) Fail(aggregator, "p.exe", $"h{i}.test");
            var rows = Rows(store);
            runner.IsTrue(rows.All(r => r.ProcessName == "p.exe" && !r.TlsFailure!.IsOverflow), "no row for the excluded client and none of the overflow kind");
            runner.AreEqual(TlsFailureAggregator.MaxNewRowsPerWindow, rows.Length, "and each in-scope host still gets a row of its own");
            return Task.CompletedTask;
        });

        await runner.RunAsync("aggregator: 300 distinct hosts stay within the new-row limit; the rest are counted per process", () =>
        {
            var (store, clock, aggregator) = Make();
            for (var i = 0; i < 300; i++) Fail(aggregator, "cycler.exe", $"h{i}.test");
            var rows = Rows(store);
            var overflow = rows.Where(r => r.TlsFailure!.IsOverflow).ToArray();
            runner.AreEqual(TlsFailureAggregator.MaxNewRowsPerWindow + 1, rows.Length, "20 rows of their own and one overflow row");
            runner.AreEqual(1, overflow.Length, "one overflow row");
            runner.AreEqual(280, overflow[0].TlsFailure!.Attempts, "counting the other 280");
            runner.IsTrue(overflow[0].ProcessName == "cycler.exe" && overflow[0].Request?.Url is null, "named for the process, with no host");
            runner.AreEqual(0L, aggregator.SuppressedCount, "none dropped");

            clock.Advance(TimeSpan.FromSeconds(11));
            Fail(aggregator, "cycler.exe", "later.test");
            runner.AreEqual(TlsFailureAggregator.MaxNewRowsPerWindow + 2, Rows(store).Length, "a new window allows a new row");
            return Task.CompletedTask;
        });

        await runner.RunAsync("aggregator: past the overflow rows' allowance a failure is only counted as suppressed", () =>
        {
            var (store, _, aggregator) = Make();
            for (var i = 0; i < 100; i++) Fail(aggregator, $"p{i}.exe", $"h{i}.test");
            runner.AreEqual(2 * TlsFailureAggregator.MaxNewRowsPerWindow, Rows(store).Length, "20 rows and 20 overflow rows");
            runner.AreEqual(60L, aggregator.SuppressedCount, "60 had nowhere to go");
            runner.AreEqual(2 * TlsFailureAggregator.MaxNewRowsPerWindow, aggregator.KeyCount, "and cost no key");
            return Task.CompletedTask;
        });

        await runner.RunAsync("aggregator: the map never exceeds 256 keys, and an evicted key starts a new row", () =>
        {
            var (store, clock, aggregator) = Make();
            var peak = 0;
            for (var window = 0; window < 40; window++)
            {
                clock.Advance(TimeSpan.FromSeconds(11));
                for (var i = 0; i < 20; i++) Fail(aggregator, "p.exe", $"w{window}h{i}.test");
                peak = Math.Max(peak, aggregator.KeyCount);
            }
            runner.AreEqual(TlsFailureAggregator.MaxKeys, peak, "800 keys were offered, 256 are kept");
            var before = store.Count;
            clock.Advance(TimeSpan.FromSeconds(11));
            Fail(aggregator, "p.exe", "w0h0.test"); // the oldest, long evicted
            runner.AreEqual(before + 1, store.Count, "it gets a row again");
            runner.AreEqual(1, Rows(store).Last().TlsFailure!.Attempts, "counted from one");
            Fail(aggregator, "p".PadRight(100_000, 'x'), new string('h', 100_000));
            runner.IsTrue(aggregator.KeyCount <= TlsFailureAggregator.MaxKeys, "hostile 100,000-character names are cut, and cannot widen the map");
            return Task.CompletedTask;
        });

        await runner.RunAsync("aggregator: an episode ends after ten minutes", () =>
        {
            var (store, clock, aggregator) = Make();
            Fail(aggregator, "p.exe", "h.test");
            clock.Advance(TimeSpan.FromMinutes(9));
            Fail(aggregator, "p.exe", "h.test");
            runner.AreEqual(1, Rows(store).Length, "still the same row at nine minutes");
            clock.Advance(TimeSpan.FromMinutes(2));
            Fail(aggregator, "p.exe", "h.test");
            runner.AreEqual(2, Rows(store).Length, "a new row at eleven");
            runner.AreEqual(1, Rows(store)[1].TlsFailure!.Attempts, "counted from one");
            return Task.CompletedTask;
        });

        await runner.RunAsync("aggregator: Clear, RemoveAll and a capacity trim each start a fresh row", () =>
        {
            var (store, clock, aggregator) = Make();
            Fail(aggregator, "p.exe", "h.test");
            var first = Rows(store).Single();
            store.RemoveAll(s => s.ProcessName == "other.exe");
            Fail(aggregator, "p.exe", "h.test");
            runner.AreEqual(2, first.TlsFailure!.Attempts, "a RemoveAll that spares the row keeps counting into it");

            store.Clear();
            clock.Advance(TimeSpan.FromSeconds(11));
            Fail(aggregator, "p.exe", "h.test");
            var afterClear = Rows(store).Single();
            runner.IsTrue(!ReferenceEquals(first, afterClear) && afterClear.TlsFailure!.Attempts == 1, "after Clear: a new row");

            store.RemoveAll(s => s.TlsFailure is not null);
            clock.Advance(TimeSpan.FromSeconds(11));
            Fail(aggregator, "p.exe", "h.test");
            var afterRemove = Rows(store).Single();
            runner.IsTrue(!ReferenceEquals(afterClear, afterRemove) && afterRemove.TlsFailure!.Attempts == 1, "after RemoveAll of it: a new row");

            store.Capacity = 3;
            for (var i = 0; i < 3; i++) store.Add(new Session { Request = new HttpRequestData { Method = "GET" } });
            runner.IsTrue(!store.IsRetained(afterRemove), "the row was trimmed for capacity");
            clock.Advance(TimeSpan.FromSeconds(11));
            Fail(aggregator, "p.exe", "h.test");
            runner.IsTrue(Rows(store) is [var fresh] && !ReferenceEquals(fresh, afterRemove) && fresh.TlsFailure!.Attempts == 1, "after a trim: a new row, counted from one");
            return Task.CompletedTask;
        });

        await runner.RunAsync("aggregator: concurrent failures from one key are counted exactly, in one row", async () =>
        {
            var (store, _, aggregator) = Make();
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 500; i++) Fail(aggregator, "p.exe", "h.test");
            })));
            runner.AreEqual(1, Rows(store).Length, "one row");
            runner.AreEqual(4000, Rows(store)[0].TlsFailure!.Attempts, "4,000 attempts");
        });

        // ------------------------------------------------------------ the real proxy, real loopback

        using var ca = CertificateAuthority.LoadOrCreate(Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-TlsCollapse-Certs"));

        await runner.RunAsync("twelve connections that close before TLS starts, from one process to one host, are one row with twelve attempts", () =>
            TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
            {
                for (var i = 0; i < 12; i++)
                {
                    using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                runner.IsTrue(await Poll.UntilAsync(() => Rows(store) is [{ TlsFailure.Attempts: 12 }]), $"one row, twelve attempts (got {string.Join(",", Rows(store).Select(r => r.TlsFailure!.Attempts))})");
                runner.AreEqual(1, store.Count, "and nothing else in the store");
            }));

        await runner.RunAsync("a flood of 300 connections to 300 hosts adds a handful of rows, and every connection ends", () =>
            TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
            {
                for (var batch = 0; batch < 6; batch++)
                    await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
                        TlsFailureEvidenceTests.Reset(await TlsFailureEvidenceTests.OpenTunnelAsync(port, $"flood{batch * 50 + i}.test:443"))));
                runner.IsTrue(await Poll.UntilAsync(() => Rows(store).Sum(r => r.TlsFailure!.Attempts) == 300, 30_000), "every failure is counted somewhere");
                runner.IsTrue(Rows(store).Length <= 2 * (TlsFailureAggregator.MaxNewRowsPerWindow + 1), $"{Rows(store).Length} rows for 300 failing hosts");
                runner.IsTrue(Rows(store).Any(r => r.TlsFailure!.IsOverflow), "the rest are counted in an overflow row");
            })); // and WithProxyAsync fails unless every connection ended
    }

    // -------------------------------------------------------------------------- helpers

    private static Session[] Rows(SessionStore store) => store.Snapshot().Where(s => s.TlsFailure is not null).ToArray();

    private static (SessionStore Store, FakeTime Clock, TlsFailureAggregator Aggregator) Make()
    {
        var store = new SessionStore();
        var clock = new FakeTime();
        return (store, clock, new TlsFailureAggregator(store, clock));
    }

    private static void Fail(TlsFailureAggregator aggregator, string process, string host,
        TlsFailureKind kind = TlsFailureKind.Rejected, int port = 443)
    {
        var info = new TlsFailureInfo(kind, TlsFailurePhase.InHandshake) { LastSeen = DateTimeOffset.Now };
        aggregator.Record(process, host, port, info, () => new Session
        {
            Request = new HttpRequestData { Method = "CONNECT", RequestTarget = $"{host}:{port}" },
            IsTunnel = true,
            IsHttps = true,
            State = SessionState.Failed,
            ProcessName = process,
            ServerEndpoint = $"{host}:{port}",
            TlsFailure = info,
            Completed = DateTimeOffset.Now,
        });
    }

    /// <summary>A clock that moves only when told to; millisecond resolution.</summary>
    private sealed class FakeTime : TimeProvider
    {
        private long _now;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _now);

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Interlocked.Read(ref _now));

        public void Advance(TimeSpan by) => Interlocked.Add(ref _now, (long)by.TotalMilliseconds);
    }
}
