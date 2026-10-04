using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Who the proxy lets in, and how long it gives them: the connection cap and the slots it hands out,
// idle eviction when the gate is full, the first-byte, head and body deadlines, the progress floor
// on request bodies, the write deadline on client responses, the request head size cap, and the
// validation of the options that drive them.
internal static partial class ProxyAdmissionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    public static async Task RunAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-ProxyAdmission-Certs"));

        // ------------------------------------------------------------- connection admission

        await runner.RunAsync("connections over the cap wait for a free slot instead of being served at once", async () =>
        {
            // The two served connections are busy (waiting on an origin that is held), so there is
            // no idle one to close for the third and it has to wait.
            var release = new TaskCompletionSource();
            await using var origin = new TestRawOrigin(async (head, stream, ct) =>
            {
                if (head.Contains("/hold", StringComparison.Ordinal)) await release.Task.WaitAsync(ct);
                return await OkAsync(head, stream, ct);
            });
            using var harness = new Harness(ca, o => o.MaxConcurrentConnections = 2);

            using var first = await ConnectAsync(harness.Port);
            using var second = await ConnectAsync(harness.Port);
            await WriteAsync(first.GetStream(), Get(origin.Port, "/hold-1"));
            await WriteAsync(second.GetStream(), Get(origin.Port, "/hold-2"));
            runner.IsTrue(await Poll.UntilAsync(() => harness.Store.Snapshot().Count(s => s.State == SessionState.AwaitingResponse) == 2),
                "two connections are being served, each waiting on the origin");

            using var third = await ConnectAsync(harness.Port);
            await WriteAsync(third.GetStream(), Get(origin.Port, "/queued"));
            var early = await ReadAsync(third.GetStream(), null, TimeSpan.FromMilliseconds(700));
            runner.AreEqual("", early.Text, "a third connection is not served while two are open");
            runner.IsTrue(harness.Proxy.ActiveConnections <= 2, $"the cap holds ({harness.Proxy.ActiveConnections} served)");

            release.SetResult(); // the origin answers, the two connections end, slots free up
            var late = await ReadAsync(third.GetStream(), "ok", Patience);
            runner.IsTrue(late.Text.Contains("200 OK", StringComparison.Ordinal),
                $"and is served as soon as one ends (got: {FirstLine(late.Text)})");
            runner.IsTrue(harness.Proxy.ActiveConnections <= 2, "still within the cap");

            first.Dispose();
            second.Dispose();
            third.Dispose();
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 0),
                $"every slot is given back ({harness.Proxy.ActiveConnections} still counted)");

            using var fresh = await ConnectAsync(harness.Port);
            await WriteAsync(fresh.GetStream(), Get(origin.Port, "/after"));
            var after = await ReadAsync(fresh.GetStream(), "ok", Patience);
            runner.IsTrue(after.Text.Contains("200 OK", StringComparison.Ordinal), "and a later connection is served");
        });

        await runner.RunAsync("a full gate closes the connection idle longest to make room, and says so once", async () =>
        {
            // Idle sockets must not be able to starve real clients: every local process connects from
            // 127.0.0.1, so there is nothing per address to count, but an idle connection is cheap to lose.
            await using var origin = new TestRawOrigin(OkAsync);
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var harness = new Harness(ca, o => o.MaxConcurrentConnections = 2, lines);

            using var first = await ConnectAsync(harness.Port);
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 1), "the first connection is served");
            await Task.Delay(50); // so that it is unambiguously the older of the two
            using var second = await ConnectAsync(harness.Port);
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 2), "and the second");

            using var third = await ConnectAsync(harness.Port);
            await WriteAsync(third.GetStream(), Get(origin.Port, "/third"));
            var clock = Stopwatch.StartNew();
            var served = await ReadAsync(third.GetStream(), "ok", Patience);
            runner.IsTrue(served.Text.Contains("200 OK", StringComparison.Ordinal),
                $"a third client is served though both slots were taken (got: {FirstLine(served.Text)})");
            runner.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(5), $"promptly ({clock.ElapsedMilliseconds}ms)");

            var oldest = await ReadAsync(first.GetStream(), null, Patience);
            runner.IsTrue(oldest.Eof && oldest.Text == "", "by closing the connection that had been idle longest");
            var newer = await ReadAsync(second.GetStream(), null, TimeSpan.FromMilliseconds(300));
            runner.IsTrue(!newer.Eof, "and only that one");

            runner.AreEqual(1L, harness.Proxy.EvictedIdleConnections, "one eviction is counted");
            runner.AreEqual(1L, harness.Proxy.SaturationEpisodes, "one saturation episode is counted");
            runner.AreEqual(1, lines.Count(l => l.Contains("connection limit", StringComparison.OrdinalIgnoreCase)),
                "and logged once, without naming a host or an address");
        });

        await runner.RunAsync("a body trickled at a byte per window is cut even though it is never silent", async () =>
        {
            // Content-Length 1000, one byte every 300 ms: each gap is inside the 500 ms idle timeout,
            // so silence alone never trips; the progress floor is what ends it. In a tunnel, because
            // some antivirus loopback filters hold back an unfinished plaintext message.
            using var harness = new Harness(ca, o =>
            {
                o.DecryptHttps = true;
                o.IdleTimeout = TimeSpan.FromMilliseconds(500);
            });

            using var client = await ConnectAsync(harness.Port);
            await using var tunnel = await OpenTunnelAsync(client, "127.0.0.1:9", ca.RootCertificate);
            await WriteAsync(tunnel, "POST /drip HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 1000\r\n\r\n");

            using var stop = new CancellationTokenSource();
            var dripping = Task.Run(async () =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        await Task.Delay(300, stop.Token);
                        await tunnel.WriteAsync("a"u8.ToArray(), stop.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException
                                               or InvalidOperationException)
                {
                    // The proxy closed on us, which is the point; or the test ended.
                }
            });

            var clock = Stopwatch.StartNew();
            var reply = await ReadAsync(tunnel, null, TimeSpan.FromSeconds(8));
            await stop.CancelAsync();
            await dripping;

            runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 408", StringComparison.Ordinal),
                $"it is told 408 (got: {FirstLine(reply.Text)})");
            runner.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(6), $"within a couple of windows ({clock.ElapsedMilliseconds}ms)");

            var session = await WaitForSessionAsync(harness.Store, s => s.Path == "/drip");
            runner.AreEqual(SessionState.Failed, session.State, "and recorded as failed");
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 0), "and its slot is given back");
        });

        await runner.RunAsync("a client that asks for a download and never reads it is cut, not waited on for ever", async () =>
        {
            const long Size = 256L * 1024 * 1024;
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Length: {Size}\r\nConnection: close\r\n\r\n", ct);
                var chunk = new byte[64 * 1024];
                for (long sent = 0; sent < Size; sent += chunk.Length) await stream.WriteAsync(chunk, ct);
                return false;
            });
            using var harness = new Harness(ca, o => o.IdleTimeout = TimeSpan.FromMilliseconds(800));

            using var client = await ConnectAsync(harness.Port);
            await WriteAsync(client.GetStream(), Get(origin.Port, "/never-read")); // and then reads nothing

            runner.IsTrue(await Poll.UntilAsync(() =>
                    harness.Store.Snapshot().Any(s => s.Path == "/never-read" && s.State == SessionState.Failed)),
                "the transfer is failed once the client has stopped reading for the idle timeout");
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 0), "and its slot is given back");
        });

        await runner.RunAsync("a request head has a total size cap, not only per line and per header count", async () =>
        {
            await using var origin = new TestRawOrigin(OkAsync);
            using var harness = new Harness(ca);

            string WithHeaders(int count) => Get(origin.Port, "/big-head")[..^2]
                + string.Concat(Enumerable.Range(0, count).Select(i => $"X-Pad-{i}: {new string('a', 20_000)}\r\n")) + "\r\n";

            using var fits = await ConnectAsync(harness.Port);
            await WriteAsync(fits.GetStream(), WithHeaders(3));
            var served = await ReadAsync(fits.GetStream(), "ok", Patience);
            runner.IsTrue(served.Text.Contains("200 OK", StringComparison.Ordinal), $"60 KB of headers is served (got: {FirstLine(served.Text)})");

            var before = origin.ConnectionCount;
            using var tooBig = await ConnectAsync(harness.Port);
            try { await WriteAsync(tooBig.GetStream(), WithHeaders(6)); }
            catch (IOException) { /* the proxy may close before the whole head is sent */ }
            var refused = await ReadAsync(tooBig.GetStream(), null, Patience);
            runner.IsTrue(refused.Eof, "120 KB of headers gets the connection closed");
            runner.AreEqual(before, origin.ConnectionCount, "and is never forwarded");
        });

        await runner.RunAsync("the guarded client stream enforces its write deadline and progress floor on the sync surface too", async () =>
        {
            async Task<Type?> ThrownAsync(Func<Task> action)
            {
                try { await action(); return null; }
                catch (Exception ex) { return ex.GetType(); }
            }

            using var stalled = new GuardedClientStream(new StallingStream(), TimeSpan.FromMilliseconds(200));
            var clock = Stopwatch.StartNew();
            runner.AreEqual(typeof(IOException), await ThrownAsync(() => stalled.WriteAsync(new byte[1]).AsTask()), "an async write that never completes ends in an IOException");
            runner.AreEqual(typeof(IOException), await ThrownAsync(() => Task.Run(() => stalled.Write(new byte[1], 0, 1))), "and so does a synchronous one");
            runner.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(5), $"promptly ({clock.ElapsedMilliseconds}ms)");

            stalled.ArmProgressFloor(TimeSpan.FromMilliseconds(50), 1000);
            await Task.Delay(120);
            runner.AreEqual(typeof(HttpStalledException), await ThrownAsync(async () => _ = await stalled.ReadAsync(new byte[8]).AsTask()),
                "a read that completes a window short of the minimum is a stall");
            stalled.ArmProgressFloor(TimeSpan.FromMilliseconds(50), 1000);
            await Task.Delay(120);
            runner.AreEqual(typeof(HttpStalledException), await ThrownAsync(() => Task.Run(() => _ = stalled.Read(new byte[8], 0, 8))),
                "on the synchronous surface too");
            stalled.DisarmProgressFloor();
            runner.IsTrue(await ThrownAsync(async () => _ = await stalled.ReadAsync(new byte[8]).AsTask()) is null, "and a disarmed floor never throws");
        });

        await runner.RunAsync("blank lines before a request count against the head cap", async () =>
        {
            await using var origin = new TestRawOrigin(OkAsync);
            using var harness = new Harness(ca);

            using var client = await ConnectAsync(harness.Port);
            var before = origin.ConnectionCount;
            try { await WriteAsync(client.GetStream(), string.Concat(Enumerable.Repeat("\r\n", 40_000)) + Get(origin.Port, "/after-blanks")); }
            catch (IOException) { /* the proxy may close before all of it is sent */ }
            var reply = await ReadAsync(client.GetStream(), null, Patience);
            runner.IsTrue(!reply.Text.Contains("200 OK", StringComparison.Ordinal), "80 KB of blank lines is not tolerated");
            runner.AreEqual(before, origin.ConnectionCount, "and the request behind them is never forwarded");
        });

        await runner.RunAsync("an eviction aimed at one idle wait never closes the connection's next one", () =>
        {
            var state = new ProxyServer.ConnectionState();

            var first = state.BeginIdle();
            state.EndIdle(); // a first byte arrived: the connection is busy
            runner.IsTrue(!state.TryEvict(), "a busy connection cannot be evicted");

            var second = state.BeginIdle();
            runner.IsTrue(!second.IsCancellationRequested && !first.IsCancellationRequested,
                "and a refused eviction leaves the next idle wait alone");
            runner.IsTrue(state.TryEvict(), "an idle one can");
            runner.IsTrue(second.IsCancellationRequested, "by cancelling that wait");
            runner.IsTrue(!state.TryEvict(), "once, not twice");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a failing accept gives its slot back, so errors cannot starve the gate", async () =>
        {
            using var cts = new CancellationTokenSource();
            var admission = new ProxyServer.Admission(2);
            var gate = admission.Gate;
            var calls = 0;
            var proxy = new ProxyServer(new ProxyOptions(), ca, new SessionStore());

            // Three failed accepts, then a Stop. With two slots, a slot kept per failure would have
            // starved the loop at the third.
            await proxy.AcceptLoopAsync(admission, token =>
            {
                if (Interlocked.Increment(ref calls) < 4)
                    return ValueTask.FromException<TcpClient>(new SocketException((int)SocketError.TooManyOpenSockets));
                cts.Cancel();
                return ValueTask.FromException<TcpClient>(new OperationCanceledException(token));
            }, () => false, cts.Token);

            runner.AreEqual(4, calls, "the loop kept accepting after each failure");
            runner.AreEqual(2, gate.CurrentCount, "and every slot was given back");

            var surprise = new InvalidOperationException("not an error the loop expects");
            InvalidOperationException? escaped = null;
            try
            {
                await proxy.AcceptLoopAsync(admission, _ => ValueTask.FromException<TcpClient>(surprise), () => false, CancellationToken.None);
            }
            catch (InvalidOperationException ex) { escaped = ex; }
            runner.IsTrue(ReferenceEquals(escaped, surprise), "an unexpected failure ends the loop rather than being hidden");
            runner.AreEqual(2, gate.CurrentCount, "and it too gives its slot back");
        });

        await runner.RunAsync("the cap is clamped to a usable range", () =>
        {
            var options = new ProxyOptions { MaxConcurrentConnections = 0 };
            runner.AreEqual(1, options.MaxConcurrentConnections, "zero becomes one, not a gate nobody can pass");
            options.MaxConcurrentConnections = int.MinValue;
            runner.AreEqual(1, options.MaxConcurrentConnections, "negative becomes one");
            options.MaxConcurrentConnections = int.MaxValue;
            runner.AreEqual(100_000, options.MaxConcurrentConnections, "and it is bounded above");
            runner.IsTrue(new ProxyOptions().MaxConcurrentConnections >= 256, "the default leaves room for a busy application");
            return Task.CompletedTask;
        });

        // ---------------------------------------------------------- deadlines for the client

        await runner.RunAsync("a slow upload that keeps sending is not cut for taking long", async () =>
        {
            // The old read wrapped line, headers and body in one budget of IdleTimeout, so this
            // 3 second upload died at 1.5 seconds, silently. Every byte now re-arms the timer. The
            // progress floor is turned right down: this is about a slow client that is still moving.
            await using var origin = new TestRawOrigin(OkAsync);
            using var harness = new Harness(ca, o =>
            {
                o.IdleTimeout = TimeSpan.FromMilliseconds(1500);
                o.RequestHeadTimeout = TimeSpan.FromMilliseconds(1700);
                o.MinRequestBodyBytesPerWindow = 1;
            });

            using var client = await ConnectAsync(harness.Port);
            var stream = client.GetStream();
            await WriteAsync(stream, Post(origin.Port, "/slow-upload", 12));

            var clock = Stopwatch.StartNew();
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(250);
                await stream.WriteAsync("x"u8.ToArray());
            }

            var reply = await ReadAsync(stream, "ok", Patience);
            runner.IsTrue(clock.ElapsedMilliseconds > 1500, $"the upload outlasted the idle timeout ({clock.ElapsedMilliseconds}ms)");
            runner.IsTrue(reply.Text.Contains("200 OK", StringComparison.Ordinal),
                $"and still got its answer (got: {FirstLine(reply.Text)})");

            var session = await WaitForSessionAsync(harness.Store, s => s.Path == "/slow-upload");
            runner.AreEqual(SessionState.Complete, session.State, "recorded as a success");
            runner.AreEqual(12, session.Request!.Body.Length, "with the whole body");
        });

        await runner.RunAsync("a client that goes silent in the middle of a body is cut, told 408 and recorded", async () =>
        {
            await using var origin = new TestRawOrigin(OkAsync);
            using var harness = new Harness(ca, o => o.IdleTimeout = TimeSpan.FromMilliseconds(500));

            using var client = await ConnectAsync(harness.Port);
            var stream = client.GetStream();
            await WriteAsync(stream, Post(origin.Port, "/stalled-upload", 100));
            await WriteAsync(stream, "12345");

            var clock = Stopwatch.StartNew();
            var reply = await ReadAsync(stream, null, Patience);
            runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 408", StringComparison.Ordinal),
                $"the client is told why (got: {FirstLine(reply.Text)})");
            runner.IsTrue(reply.Eof, "and the connection is closed");
            runner.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(5), $"promptly ({clock.ElapsedMilliseconds}ms)");

            var session = await WaitForSessionAsync(harness.Store, s => s.Path == "/stalled-upload");
            runner.AreEqual(SessionState.Failed, session.State, "the request is recorded as failed");
            runner.AreEqual("POST", session.Request!.Method, "with what was received of it");
            runner.IsTrue(session.Error?.Contains("body", StringComparison.OrdinalIgnoreCase) == true,
                $"and a reason that names the body (got: {session.Error})");
            runner.AreEqual(408, session.StatusCode, "and the answer it was given");
        });

        await runner.RunAsync("a client that says nothing at all is closed quietly after the request head timeout", async () =>
        {
            // The idle timeout stays at its default: a connection's first request waits only the
            // (shorter) head timeout, so a flood of silent sockets cannot hold slots for two minutes.
            using var harness = new Harness(ca, o => o.RequestHeadTimeout = TimeSpan.FromMilliseconds(400));

            using var client = await ConnectAsync(harness.Port);
            var reply = await ReadAsync(client.GetStream(), null, Patience);
            runner.AreEqual("", reply.Text, "nothing is written to a connection that never asked anything");
            runner.IsTrue(reply.Eof, "it is simply closed");
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 0), "and its slot is given back");
            runner.AreEqual(0, harness.Store.Count, "no session is made for it");
        });

        await runner.RunAsync("request headers dripped one byte at a time are cut by the head deadline", async () =>
        {
            // Every byte arrives well inside the idle timeout, so only a deadline on the head as a
            // whole can stop this; without one the connection (and now its slot) is held for ever.
            // Sent inside a decrypted tunnel: an unfinished plaintext head is held back by some
            // antivirus loopback filters, which would test the filter rather than the proxy.
            using var harness = new Harness(ca, o =>
            {
                o.DecryptHttps = true;
                o.IdleTimeout = TimeSpan.FromSeconds(30);
                o.RequestHeadTimeout = TimeSpan.FromMilliseconds(700);
            });

            using var client = await ConnectAsync(harness.Port);
            await using var stream = await OpenTunnelAsync(client, "127.0.0.1:9", ca.RootCertificate);
            using var stop = new CancellationTokenSource();
            var dripping = Task.Run(async () =>
            {
                try
                {
                    await WriteAsync(stream, "GET /never HTTP/1.1\r\nHost: 127.0.0.1:9\r\nX-Slow: ");
                    while (!stop.IsCancellationRequested)
                    {
                        await Task.Delay(150, stop.Token);
                        await stream.WriteAsync("a"u8.ToArray(), stop.Token);
                    }
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException
                                               or InvalidOperationException)
                {
                    // The proxy closed on us, which is the point; or the test ended.
                }
            });

            var clock = Stopwatch.StartNew();
            var reply = await ReadAsync(stream, null, TimeSpan.FromSeconds(6));
            await stop.CancelAsync();
            await dripping;

            runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 408", StringComparison.Ordinal),
                $"the slow client is told 408 (got: {FirstLine(reply.Text)})");
            runner.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(4), $"well before the idle timeout ({clock.ElapsedMilliseconds}ms)");
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 0), "and its slot is given back");
        });

        await runner.RunAsync("a fresh connection waits the head timeout for its first byte, a kept-alive one only the idle timeout", async () =>
        {
            // Idle is short and the head timeout long: if the first byte of a fresh connection were
            // held to IdleTimeout it would be closed at once; the head timeout is what it gets.
            using var quiet = new Harness(ca, o =>
            {
                o.IdleTimeout = TimeSpan.FromMilliseconds(300);
                o.RequestHeadTimeout = TimeSpan.FromSeconds(6);
            });
            using var fresh = await ConnectAsync(quiet.Port);
            var early = await ReadAsync(fresh.GetStream(), null, TimeSpan.FromMilliseconds(1800));
            runner.IsTrue(!early.Eof, "a silent fresh connection is still open well after the idle timeout");
            var late = await ReadAsync(fresh.GetStream(), null, Patience);
            runner.IsTrue(late.Eof && late.Text == "", "and is closed quietly once the head timeout has passed");

            // The other half: between requests of a kept-alive connection, IdleTimeout applies.
            await using var origin = new TestRawOrigin(KeepAliveAsync);
            using var harness = new Harness(ca, o =>
            {
                o.IdleTimeout = TimeSpan.FromMilliseconds(600);
                o.RequestHeadTimeout = TimeSpan.FromSeconds(60);
            });
            using var client = await ConnectAsync(harness.Port);
            await WriteAsync(client.GetStream(), Get(origin.Port, "/one"));
            var first = await ReadAsync(client.GetStream(), "ok", Patience);
            runner.IsTrue(first.Text.Contains("200 OK", StringComparison.Ordinal), "the first request is served");

            var clock = Stopwatch.StartNew();
            var closed = await ReadAsync(client.GetStream(), null, Patience);
            runner.IsTrue(closed.Eof, $"then the idle connection is closed ({clock.ElapsedMilliseconds}ms), long before the 60s head timeout");
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 0), "and its slot is given back");
        });

        await runner.RunAsync("a full gate also closes a connection that is idle between kept-alive requests", async () =>
        {
            await using var origin = new TestRawOrigin(KeepAliveAsync);
            using var harness = new Harness(ca, o => o.MaxConcurrentConnections = 1);

            using var first = await ConnectAsync(harness.Port);
            await WriteAsync(first.GetStream(), Get(origin.Port, "/kept"));
            var served = await ReadAsync(first.GetStream(), "ok", Patience);
            runner.IsTrue(served.Text.Contains("200 OK", StringComparison.Ordinal), "the first client is served and the connection kept");

            using var second = await ConnectAsync(harness.Port);
            await WriteAsync(second.GetStream(), Get(origin.Port, "/second"));
            var reply = await ReadAsync(second.GetStream(), "ok", Patience);
            runner.IsTrue(reply.Text.Contains("200 OK", StringComparison.Ordinal),
                $"a second client is served though the only slot was taken (got: {FirstLine(reply.Text)})");

            var closed = await ReadAsync(first.GetStream(), null, Patience);
            runner.IsTrue(closed.Eof, "by closing the idle kept-alive connection");
            runner.AreEqual(1L, harness.Proxy.EvictedIdleConnections, "counted once");
        });

        await runner.RunAsync("every way a connection can end gives its slot back: a cap of one is never exhausted", async () =>
        {
            await using var origin = new TestRawOrigin(OkAsync);
            await using var dead = new TestRawOrigin((_, _, _) => Task.FromResult(false)); // closes at once, answers nothing
            using var harness = new Harness(ca, o =>
            {
                o.MaxConcurrentConnections = 1;
                o.DecryptHttps = false; // so the CONNECT below is a blind tunnel, refused at once
                o.IdleTimeout = TimeSpan.FromMilliseconds(500);
                o.RequestHeadTimeout = TimeSpan.FromMilliseconds(500);
            });

            async Task<bool> ServedAsync(string label)
            {
                using var client = await ConnectAsync(harness.Port);
                await WriteAsync(client.GetStream(), Get(origin.Port, "/slot-" + label));
                var reply = await ReadAsync(client.GetStream(), "ok", Patience);
                return reply.Text.Contains("200 OK", StringComparison.Ordinal);
            }

            runner.IsTrue(await ServedAsync("start"), "a request is served on the only slot");

            using (var closedAtOnce = await ConnectAsync(harness.Port)) { }          // client closes without a byte
            runner.IsTrue(await ServedAsync("after-close"), "after a client that left without a byte");

            using (var reset = await ConnectAsync(harness.Port))                       // client resets mid-head
            {
                await WriteAsync(reset.GetStream(), "GET http://127.0.0.1/ HT");
                reset.Client.LingerState = new LingerOption(true, 0);
            }
            runner.IsTrue(await ServedAsync("after-reset"), "after a client that reset in the middle of a head");

            using (var silent = await ConnectAsync(harness.Port))                      // silent until the head timeout
            {
                var gone = await ReadAsync(silent.GetStream(), null, Patience);
                runner.IsTrue(gone.Eof, "a silent client is cut");
            }
            runner.IsTrue(await ServedAsync("after-silence"), "after a client that was cut for silence");

            using (var bad = await ConnectAsync(harness.Port))                         // malformed request
            {
                await WriteAsync(bad.GetStream(), "NOT A REQUEST\r\n\r\n");
                await ReadAsync(bad.GetStream(), null, Patience);
            }
            runner.IsTrue(await ServedAsync("after-malformed"), "after a malformed request");

            using (var unreachable = await ConnectAsync(harness.Port))                 // upstream that answers nothing
            {
                await WriteAsync(unreachable.GetStream(), Get(dead.Port, "/dead"));
                var bad502 = await ReadAsync(unreachable.GetStream(), null, Patience);
                runner.IsTrue(bad502.Text.Contains(" 502 ", StringComparison.Ordinal), "an origin that hangs up gives a 502");
            }
            runner.IsTrue(await ServedAsync("after-502"), "after a failed upstream");

            using (var refused = await ConnectAsync(harness.Port))                     // CONNECT to a closed port, blind tunnel
            {
                await WriteAsync(refused.GetStream(), "CONNECT 127.0.0.1:1 HTTP/1.1\r\nHost: 127.0.0.1:1\r\n\r\n");
                await ReadAsync(refused.GetStream(), null, Patience);
            }
            runner.IsTrue(await ServedAsync("after-connect"), "after a CONNECT that could not reach its target");

            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 0), "and nothing is left counted");
        });

        await runner.RunAsync("stopping a proxy that is at its cap with a client waiting completes cleanly", async () =>
        {
            var release = new TaskCompletionSource();
            await using var origin = new TestRawOrigin(async (head, stream, ct) =>
            {
                await release.Task.WaitAsync(ct);
                return await OkAsync(head, stream, ct);
            });
            var harness = new Harness(ca, o => o.MaxConcurrentConnections = 1);
            using var busy = await ConnectAsync(harness.Port);
            await WriteAsync(busy.GetStream(), Get(origin.Port, "/hold"));
            runner.IsTrue(await Poll.UntilAsync(() => harness.Store.Snapshot().Any(s => s.State == SessionState.AwaitingResponse)),
                "the only slot is busy");
            using var waiting = await ConnectAsync(harness.Port);
            await WriteAsync(waiting.GetStream(), Get(origin.Port, "/waiting"));
            await Task.Delay(300);

            var stopping = harness.Proxy.StopAsync();
            runner.IsTrue(await Task.WhenAny(stopping, Task.Delay(Patience)) == stopping, "the stop finishes while the accept loop is polling a full gate");
            await stopping; // and it did not fail
            release.SetResult();
            harness.Dispose();
        });

        await runner.RunAsync("the timeouts are clamped, so a negative, zero, infinite or huge value cannot throw where it is used", async () =>
        {
            var options = new ProxyOptions
            {
                IdleTimeout = TimeSpan.FromSeconds(-5),
                RequestHeadTimeout = TimeSpan.Zero,
                ConnectTimeout = TimeSpan.MinValue,
                TlsHandshakeTimeout = TimeSpan.FromMilliseconds(-2),
            };
            runner.AreEqual(TimeSpan.FromMilliseconds(1), options.IdleTimeout, "negative becomes the smallest usable value");
            runner.AreEqual(TimeSpan.FromMilliseconds(1), options.RequestHeadTimeout, "and so does zero");
            runner.AreEqual(TimeSpan.FromMilliseconds(1), options.ConnectTimeout, "and the minimum");
            runner.AreEqual(TimeSpan.FromMilliseconds(1), options.TlsHandshakeTimeout, "on every timeout");

            options.IdleTimeout = Timeout.InfiniteTimeSpan;
            runner.AreEqual(TimeSpan.FromDays(1), options.IdleTimeout, "infinite becomes a day, not an unbounded hold");
            options.RequestHeadTimeout = TimeSpan.MaxValue;
            runner.AreEqual(TimeSpan.FromDays(1), options.RequestHeadTimeout, "a huge value is bounded");
            options.TlsHandshakeTimeout = TimeSpan.FromDays(400);
            runner.AreEqual(TimeSpan.FromDays(1), options.TlsHandshakeTimeout, "past what CancelAfter accepts too");

            options.MinRequestBodyBytesPerWindow = -7;
            runner.AreEqual(0L, options.MinRequestBodyBytesPerWindow, "a negative progress floor is no floor");
            runner.AreEqual(TimeSpan.FromSeconds(120), new ProxyOptions().IdleTimeout, "the defaults are unchanged");
            runner.AreEqual(TimeSpan.FromSeconds(30), new ProxyOptions().RequestHeadTimeout, "the head timeout default");

            // And the proxy runs with such values rather than throwing from CancelAfter on the first request.
            await using var origin = new TestRawOrigin(OkAsync);
            using var harness = new Harness(ca, o =>
            {
                o.IdleTimeout = TimeSpan.MaxValue;
                o.RequestHeadTimeout = TimeSpan.MaxValue;
                o.MinRequestBodyBytesPerWindow = long.MinValue;
            });
            using var client = await ConnectAsync(harness.Port);
            await WriteAsync(client.GetStream(), Post(origin.Port, "/extreme", 3) + "abc");
            var reply = await ReadAsync(client.GetStream(), "ok", Patience);
            runner.IsTrue(reply.Text.Contains("200 OK", StringComparison.Ordinal), $"a request is served (got: {FirstLine(reply.Text)})");
        });
    }

    // -------------------------------------------------------------------------- helpers

    private static async Task<bool> KeepAliveAsync(string head, NetworkStream stream, CancellationToken ct)
    {
        await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok", ct);
        return true;
    }

    /// <summary>Hands out one byte per read at once, and never completes a write.</summary>
    private sealed class StallingStream : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            buffer.Span[0] = 1;
            return ValueTask.FromResult(1);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            buffer[offset] = 1;
            return 1;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            await Task.Delay(Timeout.Infinite, cancellationToken);

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class Harness : IDisposable
    {
        public Harness(CertificateAuthority ca, Action<ProxyOptions>? configure = null,
            System.Collections.Concurrent.ConcurrentQueue<string>? log = null)
        {
            Store = new SessionStore();
            var options = new ProxyOptions { Port = 0 };
            configure?.Invoke(options);
            Proxy = new ProxyServer(options, ca, Store);
            if (log is not null) Proxy.Log += (_, message) => log.Enqueue(message);
            Proxy.Start();
            Port = Proxy.Endpoint!.Port;
        }

        public ProxyServer Proxy { get; }
        public int Port { get; }
        public SessionStore Store { get; }

        public HttpClient CreateTlsClient(X509Certificate2 trustedRoot) => new(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{Port}", BypassOnLocal: false),
            UseProxy = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) => TrustsRoot(trustedRoot, cert),
            },
        })
        { Timeout = TimeSpan.FromSeconds(20) };

        public void Dispose() => Proxy.StopAsync().GetAwaiter().GetResult();
    }

    private static async Task<bool> OkAsync(string head, NetworkStream stream, CancellationToken ct)
    {
        // Reads the body the head announces: closing with it unread would reset the connection
        // and could take the reply with it.
        var announced = Regex.Match(head, @"Content-Length:\s*(\d+)", RegexOptions.IgnoreCase);
        if (announced.Success)
        {
            var left = int.Parse(announced.Groups[1].Value);
            var scratch = new byte[256];
            while (left > 0)
            {
                var n = await stream.ReadAsync(scratch.AsMemory(0, Math.Min(left, scratch.Length)), ct);
                if (n == 0) return false;
                left -= n;
            }
        }

        await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", ct);
        return false;
    }

    private static string Get(int port, string path) =>
        $"GET http://127.0.0.1:{port}{path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n";

    private static string Post(int port, string path, int length) =>
        $"POST http://127.0.0.1:{port}{path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Length: {length}\r\n\r\n";

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    /// <summary>CONNECTs through the proxy and completes the TLS handshake with its minted leaf.</summary>
    private static async Task<SslStream> OpenTunnelAsync(TcpClient client, string authority, X509Certificate2 root)
    {
        var raw = client.GetStream();
        await WriteAsync(raw, $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
        var connected = await ReadAsync(raw, "\r\n\r\n", Patience);
        if (!connected.Text.Contains(" 200 ", StringComparison.Ordinal))
            throw new IOException($"CONNECT was not accepted: {FirstLine(connected.Text)}");

        var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            RemoteCertificateValidationCallback = (_, cert, _, _) => TrustsRoot(root, cert),
        });
        return ssl;
    }

    private static Task WriteAsync(Stream stream, string text) =>
        stream.WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

    /// <summary>Reads until <paramref name="until"/> has arrived (or, when null, until the peer
    /// closes), the limit passes, or the connection ends. Never throws on a closed peer.</summary>
    private static async Task<(string Text, bool Eof)> ReadAsync(Stream stream, string? until, TimeSpan limit)
    {
        using var cts = new CancellationTokenSource(limit);
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (until is null || !text.ToString().Contains(until, StringComparison.Ordinal))
        {
            int n;
            try { n = await stream.ReadAsync(buffer, cts.Token); }
            catch (OperationCanceledException) { return (text.ToString(), false); }
            catch (IOException) { return (text.ToString(), true); } // a reset is as closed as a FIN
            if (n == 0) return (text.ToString(), true);
            text.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }
        return (text.ToString(), false);
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\r');
        return end < 0 ? text : text[..end];
    }

    private static async Task<Session> WaitForSessionAsync(SessionStore store, Func<Session, bool> match)
    {
        Session? found = null;
        await Poll.UntilAsync(() => (found = store.Snapshot().LastOrDefault(s => match(s) && s.Completed is not null)) is not null);
        return found ?? throw new TimeoutException("No matching session was completed.");
    }

    private static bool TrustsRoot(X509Certificate2 root, X509Certificate? presented)
    {
        if (presented is null) return false;
        using var leaf = new X509Certificate2(presented);
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        return chain.Build(leaf)
               && chain.ChainElements.Cast<X509ChainElement>().Any(e => e.Certificate.Thumbprint == root.Thumbprint);
    }
}
