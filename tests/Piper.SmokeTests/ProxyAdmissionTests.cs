using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Who the proxy lets in, and how long it gives them: the connection cap, the head and body
// deadlines, an Upgrade through an origin that offers h2, and connecting to a name whose first
// address does not answer.
internal static class ProxyAdmissionTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    public static async Task RunAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-ProxyAdmission-Certs"));

        // ------------------------------------------------------------------ upgrade vs h2

        await runner.RunAsync("a WebSocket upgrade reaches an origin that offers h2 in ALPN and gets its 101", async () =>
        {
            // The origin prefers h2 whenever it is offered. Piper used to offer it on an upgrade too,
            // and an h2 request has no Connection/Upgrade, so the origin answered a plain 200 and
            // the handshake could not complete.
            await using var origin = new AlpnUpgradeOrigin(ca.GetCertificateFor("127.0.0.1"));
            using var harness = new Harness(ca, o =>
            {
                o.DecryptHttps = true;
                o.ValidateUpstreamCertificates = false;
            });

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, harness.Port);
            var raw = tcp.GetStream();
            var authority = $"127.0.0.1:{origin.Port}";
            await WriteAsync(raw, $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
            var connected = await ReadAsync(raw, "\r\n\r\n", Patience);
            runner.IsTrue(connected.Text.Contains(" 200 ", StringComparison.Ordinal), "CONNECT is accepted");

            await using var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1",
                ApplicationProtocols = [SslApplicationProtocol.Http11],
                RemoteCertificateValidationCallback = (_, cert, _, _) => TrustsRoot(ca.RootCertificate, cert),
            });

            await WriteAsync(ssl,
                $"GET /chat HTTP/1.1\r\nHost: {authority}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n"
                + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n");
            var head = await ReadAsync(ssl, "\r\n\r\n", Patience);
            runner.IsTrue(head.Text.StartsWith("HTTP/1.1 101", StringComparison.Ordinal),
                $"the client gets the 101, not an h2 origin's plain answer (got: {FirstLine(head.Text)})");

            await WriteAsync(ssl, "ping");
            var echoed = await ReadAsync(ssl, "echo:ping", Patience);
            runner.IsTrue(echoed.Text.Contains("echo:ping", StringComparison.Ordinal), "and bytes flow both ways afterwards");

            runner.AreEqual(0, origin.H2Connections, "the origin was never offered h2 for the upgrade");
            var session = harness.Store.Snapshot().LastOrDefault(s => s.Path == "/chat");
            runner.AreEqual(101, session?.StatusCode ?? 0, "and the session records the 101");
        });

        await runner.RunAsync("an ordinary request to the same origin still negotiates h2 upstream", async () =>
        {
            await using var origin = new AlpnUpgradeOrigin(ca.GetCertificateFor("127.0.0.1"));
            using var harness = new Harness(ca, o =>
            {
                o.DecryptHttps = true;
                o.ValidateUpstreamCertificates = false;
            });
            using var client = harness.CreateTlsClient(ca.RootCertificate);

            var response = await client.GetAsync($"https://127.0.0.1:{origin.Port}/plain");
            var body = await response.Content.ReadAsStringAsync();
            runner.IsTrue(body.Contains("h2 origin answered", StringComparison.Ordinal), $"served over h2 (got: {body})");
            runner.AreEqual(1, origin.H2Connections, "one h2 connection upstream");
        });

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

        await runner.RunAsync("a request for the proxy's own address is refused with 508 instead of looping through it", async () =>
        {
            using var harness = new Harness(ca, o => o.MaxConcurrentConnections = 2);

            foreach (var host in new[] { "127.0.0.1", "localhost" })
            {
                using var client = await ConnectAsync(harness.Port);
                await WriteAsync(client.GetStream(),
                    $"GET http://{host}:{harness.Port}/loop HTTP/1.1\r\nHost: {host}:{harness.Port}\r\n\r\n");
                var reply = await ReadAsync(client.GetStream(), "\r\n\r\n", Patience);
                runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 508", StringComparison.Ordinal),
                    $"{host}: 508 Loop Detected (got: {FirstLine(reply.Text)})");
            }

            var session = await WaitForSessionAsync(harness.Store, s => s.Path == "/loop");
            runner.AreEqual(SessionState.Failed, session.State, "and recorded as failed");
            runner.IsTrue(session.Error?.Contains("loop", StringComparison.OrdinalIgnoreCase) == true, $"with the reason (got: {session.Error})");

            using var other = new Harness(ca, o => o.DecryptHttps = false);
            using var tunnel = await ConnectAsync(other.Port);
            await WriteAsync(tunnel.GetStream(), $"CONNECT 127.0.0.1:{other.Port} HTTP/1.1\r\nHost: 127.0.0.1:{other.Port}\r\n\r\n");
            var refused = await ReadAsync(tunnel.GetStream(), "\r\n\r\n", Patience);
            runner.IsTrue(refused.Text.StartsWith("HTTP/1.1 508", StringComparison.Ordinal),
                $"a CONNECT to the proxy itself too (got: {FirstLine(refused.Text)})");
        });

        await runner.RunAsync("a request for the proxy's own address over HTTP/2 is refused with 508 as well", async () =>
        {
            using var harness = new Harness(ca, o => o.EnableHttp2Downstream = true);
            using var client = harness.CreateTlsClient(ca.RootCertificate);
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;

            using var response = await client.GetAsync($"https://127.0.0.1:{harness.Port}/loop-h2");
            runner.AreEqual("2.0", response.Version.ToString(), "over an h2 stream");
            runner.AreEqual(508, (int)response.StatusCode, "508 Loop Detected, not a 502 and not a loop");

            var session = await WaitForSessionAsync(harness.Store, s => s.Path == "/loop-h2");
            runner.AreEqual(SessionState.Failed, session.State, "and the session is failed");
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

        await runner.RunAsync("the proxy recognises its own address, on every interface it listens on", () =>
        {
            var options = new ProxyOptions { ListeningEndpoint = new IPEndPoint(IPAddress.Any, 1234) };
            runner.IsTrue(options.IsOwnEndpoint(IPAddress.Loopback, 1234), "IPv4 loopback");
            runner.IsTrue(!options.IsOwnEndpoint(IPAddress.IPv6Loopback, 1234), "but not IPv6 loopback: 0.0.0.0 is an IPv4 socket, ::1 is another service");
            runner.IsTrue(options.IsOwnEndpoint(IPAddress.Loopback.MapToIPv6(), 1234), "IPv4-mapped loopback");

            var wildcardV6 = new ProxyOptions { ListeningEndpoint = new IPEndPoint(IPAddress.IPv6Any, 1234) };
            runner.IsTrue(wildcardV6.IsOwnEndpoint(IPAddress.IPv6Loopback, 1234), "an IPv6 wildcard listener owns ::1");
            runner.IsTrue(wildcardV6.IsOwnEndpoint(IPAddress.Loopback, 1234), "and, possibly dual-mode, 127.0.0.1");
            runner.IsTrue(!wildcardV6.IsOwnEndpoint(IPAddress.IPv6Loopback, 1235), "on its own port only");
            runner.IsTrue(!options.IsOwnEndpoint(IPAddress.Loopback, 1235), "another port is another service");
            runner.IsTrue(!options.IsOwnEndpoint(IPAddress.Parse("192.0.2.1"), 1234), "another host is another host");

            var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (own is not null) runner.IsTrue(options.IsOwnEndpoint(own, 1234), "an address of this machine's own interface");

            var loopbackOnly = new ProxyOptions { ListeningEndpoint = new IPEndPoint(IPAddress.Loopback, 1234) };
            runner.IsTrue(loopbackOnly.IsOwnEndpoint(IPAddress.Loopback, 1234), "a loopback listener is its own loopback address");
            runner.IsTrue(!loopbackOnly.IsOwnEndpoint(IPAddress.IPv6Loopback, 1234), "but not the other family's, where nothing listens");
            runner.IsTrue(!new ProxyOptions().IsOwnEndpoint(IPAddress.Loopback, 1234), "and nothing is its own while no proxy is running");
            return Task.CompletedTask;
        });

        if (Socket.OSSupportsIPv6)
        {
            await runner.RunAsync("a proxy listening on IPv6 refuses a request for its own [::1] address with 508", async () =>
            {
                using var harness = new Harness(ca, o => o.ListenAddress = IPAddress.IPv6Loopback);
                using var client = new TcpClient(AddressFamily.InterNetworkV6);
                await client.ConnectAsync(IPAddress.IPv6Loopback, harness.Port);
                await WriteAsync(client.GetStream(), $"GET http://[::1]:{harness.Port}/loop6 HTTP/1.1\r\nHost: [::1]:{harness.Port}\r\n\r\n");
                var reply = await ReadAsync(client.GetStream(), "\r\n\r\n", Patience);
                runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 508", StringComparison.Ordinal), $"(got: {FirstLine(reply.Text)})");
            });
        }

        await runner.RunAsync("an accept loop that fails unexpectedly is logged and the proxy stops reporting that it runs", async () =>
        {
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var proxy = new ProxyServer(new ProxyOptions { Port = 0 }, ca, new SessionStore());
            proxy.Log += (_, message) => lines.Enqueue(message);
            proxy.Start();
            runner.IsTrue(proxy.IsRunning, "it started");

            await proxy.RunAcceptLoopAsync(new SemaphoreSlim(1, 1),
                _ => ValueTask.FromException<TcpClient>(new InvalidOperationException("not an error the loop expects")),
                () => false, CancellationToken.None);

            runner.IsTrue(!proxy.IsRunning, "it no longer claims to be running");
            runner.IsTrue(lines.Any(l => l.Contains("Accept loop failed", StringComparison.Ordinal)), "and says why");
            await proxy.StopAsync(); // and stopping afterwards does not throw
            runner.IsTrue(true, "stopping it afterwards is clean");
        });

        await runner.RunAsync("a proxy stopped straight after it started stops cleanly and can start again", async () =>
        {
            // The accept loop used to fail with "Not listening" when Stop beat it to its first accept.
            var leftCounted = 0;
            for (var i = 0; i < 40; i++)
            {
                var proxy = new ProxyServer(new ProxyOptions { Port = 0, MaxConcurrentConnections = 1 }, ca, new SessionStore());
                proxy.Start();
                await proxy.StopAsync();
                leftCounted += proxy.ActiveConnections;
            }
            runner.AreEqual(0, leftCounted, "forty start/stop pairs end without error and with nothing left counted");

            await using var again = new ProxyServer(new ProxyOptions { Port = 0 }, ca, new SessionStore());
            again.Start();
            runner.IsTrue(again.IsRunning, "and a new one starts");
        });

        await runner.RunAsync("a failing accept gives its slot back, so errors cannot starve the gate", async () =>
        {
            using var cts = new CancellationTokenSource();
            var gate = new SemaphoreSlim(2, 2);
            var calls = 0;
            var proxy = new ProxyServer(new ProxyOptions(), ca, new SessionStore());

            // Three failed accepts, then a Stop. With two slots, a slot kept per failure would have
            // starved the loop at the third.
            await proxy.AcceptLoopAsync(gate, token =>
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
                await proxy.AcceptLoopAsync(gate, _ => ValueTask.FromException<TcpClient>(surprise), () => false, CancellationToken.None);
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

        await runner.RunAsync("a decrypted tunnel whose client never starts the TLS handshake is cut and recorded", async () =>
        {
            using var harness = new Harness(ca, o =>
            {
                o.DecryptHttps = true;
                o.RequestHeadTimeout = TimeSpan.FromMilliseconds(600);
            });

            using var client = await ConnectAsync(harness.Port);
            var stream = client.GetStream();
            await WriteAsync(stream, "CONNECT 127.0.0.1:9 HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n");
            var established = await ReadAsync(stream, "\r\n\r\n", Patience);
            runner.IsTrue(established.Text.Contains(" 200 ", StringComparison.Ordinal), "the tunnel is opened");

            var rest = await ReadAsync(stream, null, Patience);
            runner.IsTrue(rest.Eof, "then closed when no ClientHello comes");

            var session = await WaitForSessionAsync(harness.Store, s => s.IsTunnel);
            runner.AreEqual(SessionState.Failed, session.State, "and recorded as a failed handshake");
            runner.IsTrue(session.Error?.Contains("TLS handshake", StringComparison.Ordinal) == true,
                $"with the reason (got: {session.Error})");
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 0), "and its slot is given back");
        });

        // ------------------------------------------------------------------ connecting

        await runner.RunAsync("an origin that accepts the TCP connection and never answers the TLS handshake fails the request", async () =>
        {
            // Nothing bounded this handshake: the request, and the client connection slot it
            // occupied, waited for an origin that was never going to speak.
            await using var origin = new TestRawOrigin(OkAsync); // takes the connection, never writes
            using var harness = new Harness(ca, o =>
            {
                o.DecryptHttps = true;
                o.ConnectTimeout = TimeSpan.FromMilliseconds(500);
            });

            using var client = await ConnectAsync(harness.Port);
            await using var tunnel = await OpenTunnelAsync(client, $"127.0.0.1:{origin.Port}", ca.RootCertificate);
            await WriteAsync(tunnel, $"GET /silent-handshake HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n");

            var clock = Stopwatch.StartNew();
            var reply = await ReadAsync(tunnel, "\r\n\r\n", Patience);
            runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 502", StringComparison.Ordinal),
                $"the client gets a 502 (got: {FirstLine(reply.Text)})");
            runner.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(5), $"promptly ({clock.ElapsedMilliseconds}ms)");

            var session = await WaitForSessionAsync(harness.Store, s => s.Path == "/silent-handshake");
            runner.AreEqual(SessionState.Failed, session.State, "and the session is failed");
            runner.IsTrue(session.Error?.Contains("TLS handshake", StringComparison.Ordinal) == true
                          && session.Error.Contains("Timed out", StringComparison.Ordinal),
                $"with a reason that names the handshake (got: {session.Error})");
        });

        await runner.RunAsync("a name is reached over IPv4 when the origin listens only there", async () =>
        {
            // "localhost" lists ::1 as well as 127.0.0.1 on most machines, and the origin is on the
            // second only. Proves the racing connect is the one in use end to end.
            await using var origin = new TestRawOrigin(OkAsync);
            using var harness = new Harness(ca);

            using var client = await ConnectAsync(harness.Port);
            await WriteAsync(client.GetStream(), $"GET http://localhost:{origin.Port}/by-name HTTP/1.1\r\nHost: localhost:{origin.Port}\r\n\r\n");
            var reply = await ReadAsync(client.GetStream(), "ok", Patience);
            runner.IsTrue(reply.Text.Contains("200 OK", StringComparison.Ordinal), $"served (got: {FirstLine(reply.Text)})");
        });

        var v6A = IPAddress.Parse("2001:db8::1");
        var v6B = IPAddress.Parse("2001:db8::2");
        var v4A = IPAddress.Parse("192.0.2.1");
        var v4B = IPAddress.Parse("192.0.2.2");

        await runner.RunAsync("addresses alternate families, keep the resolver's first family first and drop duplicates", () =>
        {
            runner.AreEqual("2001:db8::1,192.0.2.1,2001:db8::2,192.0.2.2",
                string.Join(',', HappyEyeballs.Order([v6A, v6B, v4A, v4B])), "IPv6 first stays first");
            runner.AreEqual("192.0.2.1,2001:db8::1,192.0.2.2",
                string.Join(',', HappyEyeballs.Order([v4A, v6A, v4B])), "IPv4 first stays first");
            runner.AreEqual("192.0.2.1,192.0.2.2",
                string.Join(',', HappyEyeballs.Order([v4A, v4A, v4B, v4B])), "duplicates are dropped");
            runner.AreEqual(0, HappyEyeballs.Order([]).Count, "no addresses, no order");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a first address that never answers is passed over after the attempt delay", async () =>
        {
            var firstCancelled = false;
            var clock = Stopwatch.StartNew();

            using var winner = await HappyEyeballs.RaceAsync<Fake>([v6A, v4A], async (address, ct) =>
            {
                if (address.AddressFamily != AddressFamily.InterNetworkV6) return new Fake(address);
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { firstCancelled = true; throw; }
                return new Fake(address);
            }, TimeSpan.FromMilliseconds(100), HappyEyeballs.MaxAttempts, CancellationToken.None);

            runner.AreEqual(v4A, winner.Address, "the second address wins");
            runner.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(3), $"without waiting out the first ({clock.ElapsedMilliseconds}ms)");
            runner.IsTrue(await Poll.UntilAsync(() => firstCancelled), "and the abandoned attempt is cancelled");
        });

        await runner.RunAsync("a failed attempt starts the next one at once, not after the delay", async () =>
        {
            var clock = Stopwatch.StartNew();
            using var winner = await HappyEyeballs.RaceAsync<Fake>([v6A, v4A], (address, _) =>
                address.AddressFamily == AddressFamily.InterNetworkV6
                    ? Task.FromException<Fake>(new SocketException((int)SocketError.NetworkUnreachable))
                    : Task.FromResult(new Fake(address)),
                TimeSpan.FromSeconds(30), HappyEyeballs.MaxAttempts, CancellationToken.None);

            runner.AreEqual(v4A, winner.Address, "the second address wins");
            runner.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(3), $"straight away ({clock.ElapsedMilliseconds}ms)");
        });

        await runner.RunAsync("an attempt that succeeds after another has won is disposed, not leaked", async () =>
        {
            var slow = new Fake(v6A);
            using var winner = await HappyEyeballs.RaceAsync<Fake>([v6A, v4A], async (address, _) =>
            {
                if (address.AddressFamily != AddressFamily.InterNetworkV6) return new Fake(address);
                await Task.Delay(400); // ignores cancellation, as a connect that was already complete would
                return slow;
            }, TimeSpan.FromMilliseconds(50), HappyEyeballs.MaxAttempts, CancellationToken.None);

            runner.AreEqual(v4A, winner.Address, "the fast address wins");
            runner.IsTrue(await Poll.UntilAsync(() => slow.Disposed), "and the late connection is closed");
            runner.IsTrue(!winner.Disposed, "while the winner is left alone");
        });

        await runner.RunAsync("when every attempt fails the last error is raised and no more than the bound are tried", async () =>
        {
            var many = Enumerable.Range(1, 20).Select(i => IPAddress.Parse($"192.0.2.{i}")).ToList();
            var started = 0;
            SocketException? thrown = null;
            try
            {
                await HappyEyeballs.RaceAsync<Fake>(many, (address, _) =>
                {
                    Interlocked.Increment(ref started);
                    return Task.FromException<Fake>(new SocketException((int)SocketError.ConnectionRefused));
                }, TimeSpan.FromMilliseconds(10), 3, CancellationToken.None);
            }
            catch (SocketException ex) { thrown = ex; }

            runner.IsTrue(thrown is not null, "it fails with the socket error, not a wrapper");
            runner.AreEqual(SocketError.ConnectionRefused, thrown?.SocketErrorCode ?? SocketError.Success, "carrying the reason");
            runner.AreEqual(3, started, "after trying only the bounded number of addresses");

            SocketException? none = null;
            try
            {
                await HappyEyeballs.RaceAsync<Fake>([], (a, _) => Task.FromResult(new Fake(a)),
                    TimeSpan.FromMilliseconds(10), 3, CancellationToken.None);
            }
            catch (SocketException ex) { none = ex; }
            runner.AreEqual(SocketError.HostNotFound, none?.SocketErrorCode ?? SocketError.Success, "a name with no address is HostNotFound");
        });

        await runner.RunAsync("cancelling the connect cancels every attempt and reports the cancellation", async () =>
        {
            using var cts = new CancellationTokenSource();
            var cancelled = 0;
            var task = HappyEyeballs.RaceAsync<Fake>([v6A, v4A], async (_, ct) =>
            {
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                return new Fake(v4A);
            }, TimeSpan.FromMilliseconds(30), HappyEyeballs.MaxAttempts, cts.Token);

            await Task.Delay(200);
            await cts.CancelAsync();

            var threw = false;
            try { await task; }
            catch (OperationCanceledException) { threw = true; }
            runner.IsTrue(threw, "the caller sees OperationCanceledException");
            runner.IsTrue(await Poll.UntilAsync(() => Volatile.Read(ref cancelled) == 2), "and both attempts were cancelled");
        });
    }

    // -------------------------------------------------------------------------- helpers

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

    private sealed class Fake(IPAddress address) : IDisposable
    {
        private int _disposed;
        public IPAddress Address { get; } = address;
        public bool Disposed => Volatile.Read(ref _disposed) == 1;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
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

    /// <summary>
    /// An HTTPS origin that picks h2 whenever the client offers it and speaks HTTP/1.1 otherwise,
    /// as a real server behind a CDN does. Over h2 it answers every request with a plain 200; over
    /// HTTP/1.1 it completes a WebSocket-style upgrade and then echoes what it is sent.
    /// </summary>
    private sealed class AlpnUpgradeOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _connections = [];
        private readonly Lock _gate = new();
        private readonly Task _acceptLoop;
        private int _h2Connections;

        public AlpnUpgradeOrigin(X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int H2Connections => Volatile.Read(ref _h2Connections);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                var task = Task.Run(() => ServeAsync(client));
                lock (_gate) _connections.Add(task);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var c = client;
            c.NoDelay = true;
            var ssl = new SslStream(c.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                }, _cts.Token).ConfigureAwait(false);

                if (ssl.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
                {
                    Interlocked.Increment(ref _h2Connections);
                    var connection = new Http2Connection(ssl, (_, _) => Task.FromResult(
                        (Http2StreamResponse)HttpResponseData.Simple(200, "OK", "h2 origin answered; no upgrade was possible")));
                    await connection.RunAsync(_cts.Token).ConfigureAwait(false);
                    return;
                }

                using var reader = new HttpStreamReader(ssl);
                var request = await HttpParser.ReadRequestAsync(reader, _cts.Token).ConfigureAwait(false);
                if (request is null) return;

                if (request.Headers.HasToken("Connection", "Upgrade") && request.Headers.Contains("Upgrade"))
                {
                    await WriteAsync(ssl, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");
                    var buffer = new byte[256];
                    var n = await reader.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes("echo:" + Encoding.ASCII.GetString(buffer, 0, n)), _cts.Token)
                        .ConfigureAwait(false);
                    await ssl.FlushAsync(_cts.Token).ConfigureAwait(false);
                    await Task.Delay(200, _cts.Token).ConfigureAwait(false);
                    return;
                }

                await ssl.WriteAsync(HttpResponseData.Simple(200, "OK", "h1 origin answered").ToBytes(), _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or AuthenticationException
                                           or ObjectDisposedException or Http2ProtocolException or HttpParseException)
            {
                // The test asserts on the client side; a connection that ended early is not news.
            }
            finally
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { _listener.Stop(); } catch (SocketException) { }
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }

            Task[] pending;
            lock (_gate) pending = [.. _connections];
            await Task.WhenAll(pending).ConfigureAwait(false);
            _cts.Dispose();
        }
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
