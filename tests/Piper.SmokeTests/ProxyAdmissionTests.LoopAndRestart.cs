using System.Net;
using System.Net.Sockets;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Part of the admission tests (see ProxyAdmissionTests.cs, whose helpers it shares): the proxy
// refusing to connect to itself, the accept loop failing and the proxy being started again, the
// eviction bookkeeping, and the edges of the body progress floor.
internal static partial class ProxyAdmissionTests
{
    public static async Task RunLoopAndRestartAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-ProxyLoopRestart-Certs"));

        // ------------------------------------------------- the proxy must not connect to itself

        await runner.RunAsync("a request for the proxy's own address is refused with 508 and holds no slot", async () =>
        {
            using var harness = new Harness(ca, o => o.MaxConcurrentConnections = 2);

            // More requests than the cap: if each one looped back in, the two slots would be gone by
            // the third. The URL carries a query and a cookie to show nothing of the request is echoed.
            foreach (var host in new[] { "127.0.0.1", "localhost", "127.0.0.1", "127.0.0.1", "127.0.0.1" })
            {
                using var client = await ConnectAsync(harness.Port);
                await WriteAsync(client.GetStream(),
                    $"GET http://{host}:{harness.Port}/loop?token=hunter2 HTTP/1.1\r\nHost: {host}:{harness.Port}\r\nCookie: sid=abc\r\n\r\n");
                var reply = await ReadAsync(client.GetStream(), null, Patience);
                runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 508", StringComparison.Ordinal),
                    $"{host}: 508 Loop Detected (got: {FirstLine(reply.Text)})");
                runner.IsTrue(reply.Text.Contains($"{host}:{harness.Port}", StringComparison.Ordinal)
                              && !reply.Text.Contains("hunter2", StringComparison.Ordinal)
                              && !reply.Text.Contains("sid=abc", StringComparison.Ordinal)
                              && !reply.Text.Contains("/loop", StringComparison.Ordinal),
                    "which names the host and port and nothing else of the request");
            }

            var session = await WaitForSessionAsync(harness.Store, s => s.Path == "/loop");
            runner.AreEqual(SessionState.Failed, session.State, "recorded as failed");
            runner.IsTrue(session.Error?.Contains("loop", StringComparison.OrdinalIgnoreCase) == true, $"with the reason (got: {session.Error})");
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 0), "and no connection or slot is left held");
        });

        await runner.RunAsync("a CONNECT to the proxy itself is refused with 508, blind or decrypted", async () =>
        {
            using var blind = new Harness(ca, o => o.DecryptHttps = false);
            using (var tunnel = await ConnectAsync(blind.Port))
            {
                await WriteAsync(tunnel.GetStream(), $"CONNECT 127.0.0.1:{blind.Port} HTTP/1.1\r\nHost: 127.0.0.1:{blind.Port}\r\n\r\n");
                var refused = await ReadAsync(tunnel.GetStream(), "\r\n\r\n", Patience);
                runner.IsTrue(refused.Text.StartsWith("HTTP/1.1 508", StringComparison.Ordinal),
                    $"a blind tunnel (got: {FirstLine(refused.Text)})");
            }
            runner.IsTrue(await Poll.UntilAsync(() => blind.Proxy.ActiveConnections == 0), "which holds nothing");

            // Decrypted: the tunnel itself is served by the proxy, so it is the request inside that is refused.
            using var decrypting = new Harness(ca);
            using var client = await ConnectAsync(decrypting.Port);
            await using var inside = await OpenTunnelAsync(client, $"127.0.0.1:{decrypting.Port}", ca.RootCertificate);
            await WriteAsync(inside, $"GET /inner HTTP/1.1\r\nHost: 127.0.0.1:{decrypting.Port}\r\n\r\n");
            var reply = await ReadAsync(inside, null, Patience);
            runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 508", StringComparison.Ordinal),
                $"a decrypted tunnel (got: {FirstLine(reply.Text)})");
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

        await runner.RunAsync("the proxy recognises its own address, and only that: an IPv4 wildcard listener does not own the IPv6 loopback", () =>
        {
            const int Port = 1234;
            var v4Any = new ProxyOptions { ListeningEndpoint = new IPEndPoint(IPAddress.Any, Port) };
            runner.IsTrue(v4Any.IsOwnEndpoint(IPAddress.Loopback, Port), "0.0.0.0 owns 127.0.0.1");
            runner.IsTrue(v4Any.IsOwnEndpoint(IPAddress.Loopback.MapToIPv6(), Port), "and its IPv4-mapped form");
            runner.IsTrue(!v4Any.IsOwnEndpoint(IPAddress.IPv6Loopback, Port), "but not ::1, where the same port may be another service");
            runner.IsTrue(!v4Any.IsOwnEndpoint(IPAddress.Loopback, Port + 1), "on its own port only");
            runner.IsTrue(!v4Any.IsOwnEndpoint(IPAddress.Parse("192.0.2.1"), Port), "another host is another host");

            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(u => u.Address).ToList();
            var someV4 = interfaces.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            if (someV4 is not null) runner.IsTrue(v4Any.IsOwnEndpoint(someV4, Port), "an IPv4 address of this machine's own interface");
            var someV6 = interfaces.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(a));
            if (someV6 is not null) runner.IsTrue(!v4Any.IsOwnEndpoint(someV6, Port), "but not an IPv6 one");

            var v6Any = new ProxyOptions { ListeningEndpoint = new IPEndPoint(IPAddress.IPv6Any, Port) };
            runner.IsTrue(v6Any.IsOwnEndpoint(IPAddress.IPv6Loopback, Port), "a (v6-only) :: listener owns ::1");
            runner.IsTrue(!v6Any.IsOwnEndpoint(IPAddress.Loopback, Port), "but not 127.0.0.1 unless its socket is dual-mode");
            runner.IsTrue(!v6Any.IsOwnEndpoint(IPAddress.Loopback.MapToIPv6(), Port), "nor the mapped form of it");
            v6Any.ListeningDualMode = true;
            runner.IsTrue(v6Any.IsOwnEndpoint(IPAddress.Loopback, Port), "a dual-mode one does");
            runner.IsTrue(v6Any.IsOwnEndpoint(IPAddress.Loopback.MapToIPv6(), Port), "also by its mapped form");

            var loopbackV4 = new ProxyOptions { ListeningEndpoint = new IPEndPoint(IPAddress.Loopback, Port) };
            runner.IsTrue(loopbackV4.IsOwnEndpoint(IPAddress.Loopback, Port), "a loopback listener is its own address");
            runner.IsTrue(!loopbackV4.IsOwnEndpoint(IPAddress.Parse("127.0.0.2"), Port), "but not the rest of 127/8");
            runner.IsTrue(!loopbackV4.IsOwnEndpoint(IPAddress.IPv6Loopback, Port), "nor the other family's loopback");
            var loopbackV6 = new ProxyOptions { ListeningEndpoint = new IPEndPoint(IPAddress.IPv6Loopback, Port) };
            runner.IsTrue(loopbackV6.IsOwnEndpoint(IPAddress.IPv6Loopback, Port), "and so is an IPv6 one");
            runner.IsTrue(!loopbackV6.IsOwnEndpoint(IPAddress.Loopback, Port), "without owning 127.0.0.1");

            runner.IsTrue(!new ProxyOptions().IsOwnEndpoint(IPAddress.Loopback, Port), "and nothing is its own while no proxy is running");
            return Task.CompletedTask;
        });

        await runner.RunAsync("the connect drops the addresses that are the proxy and refuses when none is left", async () =>
        {
            var timeout = TimeSpan.FromSeconds(30);
            var delay = TimeSpan.FromMilliseconds(250);
            var other = IPAddress.Parse("192.0.2.7");
            bool IsOwn(IPAddress address, int port) => port == 8888 && address.Equals(IPAddress.Loopback);

            var dialled = new System.Collections.Concurrent.ConcurrentQueue<IPAddress>();
            Task<Socket> Dial(IPAddress address, int port, CancellationToken ct)
            {
                dialled.Enqueue(address);
                return Task.FromResult(new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp));
            }

            string Dialled() => string.Join(",", dialled);

            using (await HappyEyeballs.ConnectAsync("mixed.test", 8888, timeout, delay,
                       (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback, other]), Dial, CancellationToken.None, IsOwn))
            {
            }
            runner.AreEqual("192.0.2.7", Dialled(), "a name with another address is dialled there only, never at the proxy");

            dialled.Clear();
            using (await HappyEyeballs.ConnectAsync("same.test", 9999, timeout, delay,
                       (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]), Dial, CancellationToken.None, IsOwn))
            {
            }
            runner.AreEqual("127.0.0.1", Dialled(), "the same address on another port is somebody else's");

            dialled.Clear();
            var loop = await CaptureAsync<ProxyLoopException>(() => HappyEyeballs.ConnectAsync("localhost", 8888, timeout, delay,
                (_, _) => Task.FromResult<IPAddress[]>([IPAddress.Loopback]), Dial, CancellationToken.None, IsOwn));
            runner.IsTrue(loop is not null, "a name that is only the proxy is a loop");
            runner.AreEqual("", Dialled(), "and nothing is dialled for it");
            runner.AreEqual("localhost:8888 is this proxy itself; forwarding the request there would loop.", loop?.Message,
                "named by host and port alone");

            var leftover = await CaptureAsync<ProxyLoopException>(() => HappyEyeballs.ConnectAsync("localhost", 8888, timeout, delay,
                (_, _) => Task.FromResult<IPAddress[]>([IPAddress.IPv6Loopback, IPAddress.Loopback]),
                (_, _, _) => throw new SocketException((int)SocketError.ConnectionRefused), CancellationToken.None, IsOwn));
            runner.IsTrue(leftover is not null, "and so is one whose other addresses refuse: the loop is the more useful thing to say");

            var refused = await CaptureAsync<IOException>(() => HappyEyeballs.ConnectAsync("down.test", 8888, timeout, delay,
                (_, _) => Task.FromResult<IPAddress[]>([other]),
                (_, _, _) => throw new SocketException((int)SocketError.ConnectionRefused), CancellationToken.None, IsOwn));
            runner.IsTrue(refused is not null and not ProxyLoopException, "while a refusal with no proxy address among them stays a refusal");
        });

        // ----------------------------------------------------- the accept loop failing and restarting

        await runner.RunAsync("a failed accept loop is reported, the proxy stops claiming to run, and Start tells the old connections to go", async () =>
        {
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var options = new ProxyOptions { Port = 0, MaxConcurrentConnections = 8 };
            await using var proxy = new ProxyServer(options, ca, new SessionStore());
            proxy.Log += (_, message) => lines.Enqueue(message);
            var fail = false;
            proxy.WrapAccept = real => ct => fail
                ? ValueTask.FromException<TcpClient>(new InvalidOperationException("the listener broke"))
                : real(ct);
            proxy.Start();

            using var inFlight = await ConnectAsync(proxy.Endpoint!.Port);
            runner.IsTrue(await Poll.UntilAsync(() => proxy.ActiveConnections == 1), "a connection is being served");

            fail = true;
            using var trigger = await ConnectAsync(proxy.Endpoint!.Port); // the accept after this one fails
            runner.IsTrue(await Poll.UntilAsync(() => !proxy.IsRunning), "the proxy stops claiming to run");
            runner.IsTrue(await Poll.UntilAsync(() => lines.Any(l => l.Contains("Accept loop failed", StringComparison.Ordinal)
                                                                   && l.Contains("the listener broke", StringComparison.Ordinal))),
                "and says why"); // the line follows the flag by a moment
            var refused = false;
            try { using var late = await ConnectAsync(proxy.Endpoint!.Port); }
            catch (SocketException) { refused = true; }
            runner.IsTrue(refused, "and the listener is closed, so nobody queues behind a loop that is gone");

            fail = false;
            proxy.Start(); // a new run: the old one's connections must be told to stop first
            runner.IsTrue(proxy.IsRunning, "it starts again");
            var closed = await ReadAsync(inFlight.GetStream(), null, Patience);
            runner.IsTrue(closed.Eof, "and the connection of the failed run is closed, not left on a token nobody cancelled");
            runner.IsTrue(await Poll.UntilAsync(() => proxy.ActiveConnections == 0), "every one of that run's connections unwound");

            await using var origin = new TestRawOrigin(OkAsync);
            using var fresh = await ConnectAsync(proxy.Endpoint!.Port);
            await WriteAsync(fresh.GetStream(), Get(origin.Port, "/after-restart"));
            var reply = await ReadAsync(fresh.GetStream(), "ok", Patience);
            runner.IsTrue(reply.Text.Contains("200 OK", StringComparison.Ordinal), $"and the new run serves (got: {FirstLine(reply.Text)})");
        });

        await runner.RunAsync("stopping after the accept loop failed completes, cleans up and frees the port", async () =>
        {
            var options = new ProxyOptions { Port = 0, MaxConcurrentConnections = 8 };
            var proxy = new ProxyServer(options, ca, new SessionStore());
            var fail = false;
            proxy.WrapAccept = real => ct => fail
                ? ValueTask.FromException<TcpClient>(new InvalidOperationException("the listener broke"))
                : real(ct);
            proxy.Start();
            var port = proxy.Endpoint!.Port;

            using var inFlight = await ConnectAsync(port);
            runner.IsTrue(await Poll.UntilAsync(() => proxy.ActiveConnections == 1), "a connection is being served");
            fail = true;
            using var trigger = await ConnectAsync(port);
            runner.IsTrue(await Poll.UntilAsync(() => !proxy.IsRunning), "the loop failed");

            var stopping = proxy.StopAsync();
            runner.IsTrue(await Task.WhenAny(stopping, Task.Delay(Patience)) == stopping, "the stop finishes");
            await stopping; // and does not rethrow what the loop died of
            runner.IsTrue(!proxy.IsRunning, "not running");
            var closed = await ReadAsync(inFlight.GetStream(), null, Patience);
            runner.IsTrue(closed.Eof, "its connections were told to stop");

            var probe = new TcpListener(IPAddress.Loopback, port);
            try
            {
                probe.Start();
                runner.IsTrue(true, "and the listener is gone: the port can be bound again");
            }
            catch (SocketException ex)
            {
                runner.IsTrue(false, $"and the listener is gone: the port can be bound again ({ex.SocketErrorCode})");
            }
            finally { probe.Stop(); }

            await proxy.StopAsync(); // a second stop is harmless
            await proxy.DisposeAsync();
        });

        await runner.RunAsync("a persistent accept error backs off instead of spinning", async () =>
        {
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var proxy = new ProxyServer(new ProxyOptions(), ca, new SessionStore());
            proxy.Log += (_, message) => lines.Enqueue(message);
            var admission = new ProxyServer.Admission(2);
            var calls = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));

            await proxy.AcceptLoopAsync(admission, _ =>
            {
                Interlocked.Increment(ref calls);
                return ValueTask.FromException<TcpClient>(new SocketException((int)SocketError.TooManyOpenSockets));
            }, () => false, cts.Token);

            // 50, 100, 200, 400, 800 ms: six or seven attempts in the time a spinning loop makes millions.
            runner.IsTrue(calls is >= 2 and <= 30, $"{calls} attempts in 1.5 s");
            runner.IsTrue(lines.Count <= 30, $"and as many log lines ({lines.Count})");
            runner.AreEqual(2, admission.Gate.CurrentCount, "and no slot is kept by a failure");
        });

        // --------------------------------------------------------------- eviction and capacity

        await runner.RunAsync("eviction is asynchronous, so a poll does not close another connection while the last one is still unwinding", async () =>
        {
            var proxy = new ProxyServer(new ProxyOptions(), ca, new SessionStore());
            var admission = new ProxyServer.Admission(1);
            admission.Gate.Wait(0); // the only slot is held by a connection that is not part of this

            var states = new[] { new ProxyServer.ConnectionState(), new ProxyServer.ConnectionState(), new ProxyServer.ConnectionState() };
            var tokens = states.Select(s => s.BeginIdle()).ToArray();
            foreach (var state in states) admission.Add(state);

            using var cts = new CancellationTokenSource();
            var loop = proxy.AcceptLoopAsync(admission, async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return null!;
            }, () => true, cts.Token);

            // Fifteen or so polls of a gate that stays full, with a client always waiting.
            await Task.Delay(1800);
            runner.AreEqual(1, tokens.Count(t => t.IsCancellationRequested), "one idle connection was closed, not one per poll");
            runner.AreEqual(1L, proxy.EvictedIdleConnections, "and counted once");

            // It unwinds (its slot is still held, so the gate stays full): the next poll may take the next.
            var evicted = Array.FindIndex(tokens, t => t.IsCancellationRequested);
            admission.Remove(states[evicted]);
            await Task.Delay(1500);
            runner.AreEqual(2, tokens.Count(t => t.IsCancellationRequested), "once that one has gone, the next is closed");
            runner.AreEqual(2L, proxy.EvictedIdleConnections, "and counted");

            await cts.CancelAsync();
            await loop;
        });

        await runner.RunAsync("the saturation log names the capacity the run started with, not the option's present value", async () =>
        {
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            ProxyOptions? options = null;
            using var harness = new Harness(ca, o =>
            {
                options = o;
                o.MaxConcurrentConnections = 2;
            }, lines);
            options!.MaxConcurrentConnections = 50; // read at Start: this changes nothing for the run in progress

            using var first = await ConnectAsync(harness.Port);
            using var second = await ConnectAsync(harness.Port);
            runner.IsTrue(await Poll.UntilAsync(() => harness.Proxy.ActiveConnections == 2), "two connections hold the two slots");
            using var third = await ConnectAsync(harness.Port);
            runner.IsTrue(await Poll.UntilAsync(() => lines.Any(l => l.Contains("Connection limit", StringComparison.Ordinal))), "the limit is reported");
            runner.IsTrue(lines.Any(l => l.Contains("Connection limit (2)", StringComparison.Ordinal)), "as the 2 it is, not 50");
        });

        // ------------------------------------------------------------------ the progress floor

        await runner.RunAsync("a small slow body is not cut for being small: the floor never asks for more than is left", async () =>
        {
            runner.IsTrue(!await StalledAsync(minimum: 1024, bodyLength: 10, buffered: 0, arriving: 10),
                "a 10-byte body that arrives complete after the window is not a stall, whatever the minimum");
            runner.IsTrue(await StalledAsync(minimum: 1024, bodyLength: 10, buffered: 0, arriving: 5),
                "but one that is not yet complete when the window ends still is");
            runner.IsTrue(await StalledAsync(minimum: 1024, bodyLength: -1, buffered: 0, arriving: 5),
                "and a body of unknown length keeps the whole minimum");
        });

        await runner.RunAsync("body bytes that came in with the head count as progress", async () =>
        {
            runner.IsTrue(!await StalledAsync(minimum: 100, bodyLength: 1000, buffered: 99, arriving: 1),
                "99 buffered plus 1 read meets a minimum of 100");
            runner.IsTrue(await StalledAsync(minimum: 100, bodyLength: 1000, buffered: 0, arriving: 1),
                "where one byte alone does not");
            runner.IsTrue(!await StalledAsync(minimum: 1024, bodyLength: 100, buffered: 50, arriving: 25),
                "and a small body is asked only for what was still to come: 50 left, 25 arrived, 50 buffered");
            runner.IsTrue(await StalledAsync(minimum: 1024, bodyLength: 100, buffered: 0, arriving: 25),
                "where without the buffered half it is a stall");
        });

        await runner.RunAsync("a small body that arrives slowly through a tight idle timeout is served, not answered 408", async () =>
        {
            // Idle 2 s, so the window is 2 s and the floor 1,024 bytes; the 10-byte body arrives in two
            // pieces, the last after the window has ended. In a tunnel, as the other drip tests are.
            using var harness = new Harness(ca, o => o.IdleTimeout = TimeSpan.FromSeconds(2));
            using var client = await ConnectAsync(harness.Port);
            await using var tunnel = await OpenTunnelAsync(client, "127.0.0.1:9", ca.RootCertificate);
            await WriteAsync(tunnel, "POST /small HTTP/1.1\r\nHost: 127.0.0.1:9\r\nContent-Length: 10\r\n\r\n");
            await Task.Delay(1300);
            await WriteAsync(tunnel, "abcd");
            await Task.Delay(1300);
            await WriteAsync(tunnel, "efghij");

            var session = await WaitForSessionAsync(harness.Store, s => s.Path == "/small");
            runner.AreEqual(10, session.Request!.Body.Length, "the whole body was received");
            runner.IsTrue(session.Error?.Contains("stopped sending", StringComparison.OrdinalIgnoreCase) != true,
                $"and the client is not accused of stalling (got: {session.Error})");
        });

        await runner.RunAsync("a guarded stream disposed asynchronously disposes what it wraps, and its own base too", async () =>
        {
            var inner = new TrackingStream();
            await using (new GuardedClientStream(inner, TimeSpan.FromSeconds(1))) { }
            runner.AreEqual(1, inner.AsyncDisposals, "the wrapped stream is disposed asynchronously");
            runner.IsTrue(inner.SyncDisposals >= 1, "and the base class's DisposeAsync ran (it disposes through Dispose)");
        });
    }

    /// <summary>Arms a floor with a 100 ms window, lets the window pass, then reads
    /// <paramref name="arriving"/> bytes. True when that read is reported as a stall.</summary>
    private static async Task<bool> StalledAsync(long minimum, long bodyLength, long buffered, int arriving)
    {
        using var stream = new GuardedClientStream(new MemoryStream(new byte[100]), TimeSpan.FromSeconds(30));
        stream.ArmProgressFloor(TimeSpan.FromMilliseconds(100), minimum, bodyLength, buffered);
        await Task.Delay(300);
        try
        {
            var read = await stream.ReadAsync(new byte[arriving]);
            return read < 0;
        }
        catch (HttpStalledException)
        {
            return true;
        }
    }

    private static async Task<TException?> CaptureAsync<TException>(Func<Task> body) where TException : Exception
    {
        try { await body(); }
        catch (TException ex) { return ex; }
        catch (Exception) { return null; }
        return null;
    }

    /// <summary>Counts how it is disposed.</summary>
    private sealed class TrackingStream : Stream
    {
        public int AsyncDisposals;
        public int SyncDisposals;

        public override ValueTask DisposeAsync()
        {
            AsyncDisposals++;
            return ValueTask.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) SyncDisposals++;
            base.Dispose(disposing);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}
