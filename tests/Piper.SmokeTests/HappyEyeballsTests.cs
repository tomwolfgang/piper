using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Connecting to an origin: RFC 8305 staggered attempts, and the bound on a TLS handshake. The race is
// driven with injected attempts (a black-holed IPv6 path cannot be reproduced portably), so the
// assertions are about order, cancellation and disposal rather than about elapsed time; a generous
// token bounds any test that would otherwise wait for a stagger that never comes.
internal static class HappyEyeballsTests
{
    private static readonly IPAddress V6A = IPAddress.Parse("2001:db8::1");
    private static readonly IPAddress V6B = IPAddress.Parse("2001:db8::2");
    private static readonly IPAddress V4A = IPAddress.Parse("192.0.2.1");
    private static readonly IPAddress V4B = IPAddress.Parse("192.0.2.2");

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public static async Task RunHappyEyeballsAsync(TestRunner runner)
    {
        await runner.RunAsync("addresses alternate families, keep the resolver's first family first and drop duplicates", () =>
        {
            runner.AreEqual("2001:db8::1,192.0.2.1,2001:db8::2,192.0.2.2",
                string.Join(',', HappyEyeballs.Order([V6A, V6B, V4A, V4B])), "IPv6 first stays first");
            runner.AreEqual("192.0.2.1,2001:db8::1,192.0.2.2",
                string.Join(',', HappyEyeballs.Order([V4A, V6A, V4B])), "IPv4 first stays first");
            runner.AreEqual("192.0.2.1,192.0.2.2",
                string.Join(',', HappyEyeballs.Order([V4A, V4A, V4B, V4B])), "duplicates are dropped");
            runner.AreEqual(0, HappyEyeballs.Order([]).Count, "no addresses, no order");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a first address that never answers is passed over after the attempt delay", async () =>
        {
            var firstCancelled = false;
            using var guard = new CancellationTokenSource(Patience);

            using var winner = await HappyEyeballs.RaceAsync<Fake>([V6A, V4A], async (address, ct) =>
            {
                if (address.AddressFamily != AddressFamily.InterNetworkV6) return new Fake(address);
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { firstCancelled = true; throw; }
                return new Fake(address);
            }, TimeSpan.FromMilliseconds(100), HappyEyeballs.MaxAttempts, guard.Token);

            runner.AreEqual(V4A, winner.Address, "the second address wins while the first is still waiting");
            runner.IsTrue(await Poll.UntilAsync(() => firstCancelled), "and the abandoned attempt is cancelled");
        });

        await runner.RunAsync("a failed attempt starts the next one at once, not after the delay", async () =>
        {
            // The delay is longer than the guard: only an immediate start can finish in time.
            using var guard = new CancellationTokenSource(Patience);
            using var winner = await HappyEyeballs.RaceAsync<Fake>([V6A, V4A], (address, _) =>
                address.AddressFamily == AddressFamily.InterNetworkV6
                    ? Task.FromException<Fake>(new SocketException((int)SocketError.NetworkUnreachable))
                    : Task.FromResult(new Fake(address)),
                TimeSpan.FromHours(1), HappyEyeballs.MaxAttempts, guard.Token);

            runner.AreEqual(V4A, winner.Address, "the second address wins");
        });

        await runner.RunAsync("a single address connects with no stagger", async () =>
        {
            using var winner = await HappyEyeballs.RaceAsync<Fake>([V4A], (address, _) => Task.FromResult(new Fake(address)),
                TimeSpan.FromHours(1), HappyEyeballs.MaxAttempts, CancellationToken.None);
            runner.AreEqual(V4A, winner.Address, "the one address is used");
            runner.IsTrue(!winner.Disposed, "and handed over alive");
        });

        await runner.RunAsync("an attempt that succeeds after another has won is disposed, the winner is kept", async () =>
        {
            var slow = new Fake(V6A);
            var release = new TaskCompletionSource();
            using var winner = await HappyEyeballs.RaceAsync<Fake>([V6A, V4A], async (address, _) =>
            {
                if (address.AddressFamily != AddressFamily.InterNetworkV6) return new Fake(address);
                await release.Task; // ignores cancellation, as a connect that had already completed would
                return slow;
            }, TimeSpan.FromMilliseconds(50), HappyEyeballs.MaxAttempts, CancellationToken.None);

            runner.AreEqual(V4A, winner.Address, "the fast address wins");
            release.SetResult();
            runner.IsTrue(await Poll.UntilAsync(() => slow.Disposed), "and the late connection is closed");
            runner.IsTrue(!winner.Disposed, "while the winner is left alone");
        });

        await runner.RunAsync("a losing attempt that fails after the winner leaves no unobserved task exception", async () =>
        {
            var unobserved = new List<string>();
            void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
            {
                lock (unobserved)
                    foreach (var inner in e.Exception.Flatten().InnerExceptions) unobserved.Add(inner.Message);
            }

            TaskScheduler.UnobservedTaskException += OnUnobserved;
            try
            {
                var release = new TaskCompletionSource();
                using var winner = await HappyEyeballs.RaceAsync<Fake>([V6A, V4A], async (address, _) =>
                {
                    if (address.AddressFamily != AddressFamily.InterNetworkV6) return new Fake(address);
                    await release.Task;
                    throw new InvalidOperationException("loser-failure-marker");
                }, TimeSpan.FromMilliseconds(50), HappyEyeballs.MaxAttempts, CancellationToken.None);

                release.SetResult();
                await Task.Delay(300); // let the loser fail
                for (var i = 0; i < 3; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }

                lock (unobserved)
                    runner.IsTrue(!unobserved.Contains("loser-failure-marker"),
                        $"the loser's failure was observed (unobserved: {string.Join(';', unobserved)})");
            }
            finally { TaskScheduler.UnobservedTaskException -= OnUnobserved; }
        });

        await runner.RunAsync("no more attempts are made than the bound, whatever the resolver returns", async () =>
        {
            var many = Enumerable.Range(1, 20).Select(i => IPAddress.Parse($"192.0.2.{i}")).ToList();
            var started = 0;
            var thrown = await CaptureAsync<SocketException>(() => HappyEyeballs.RaceAsync<Fake>(many, (_, _) =>
            {
                Interlocked.Increment(ref started);
                return Task.FromException<Fake>(new SocketException((int)SocketError.ConnectionRefused));
            }, TimeSpan.FromMilliseconds(10), HappyEyeballs.MaxAttempts, CancellationToken.None));

            runner.AreEqual(SocketError.ConnectionRefused, thrown?.SocketErrorCode ?? SocketError.Success, "the failure carries its reason");
            runner.AreEqual(6, started, "and only six addresses were tried");
            runner.AreEqual(6, HappyEyeballs.MaxAttempts, "which is the documented bound");

            var none = await CaptureAsync<SocketException>(() => HappyEyeballs.RaceAsync<Fake>([],
                (a, _) => Task.FromResult(new Fake(a)), TimeSpan.FromMilliseconds(10), 3, CancellationToken.None));
            runner.AreEqual(SocketError.HostNotFound, none?.SocketErrorCode ?? SocketError.Success, "a name with no address is HostNotFound");
        });

        await runner.RunAsync("the most informative failure is reported, not simply the last", async () =>
        {
            static Task<Fake> Fail(SocketError error) => Task.FromException<Fake>(new SocketException((int)error));

            // A timeout on the first address, a refusal on the second: the timeout says more.
            var timeoutFirst = await CaptureAsync<SocketException>(() => HappyEyeballs.RaceAsync<Fake>([V4A, V4B],
                (address, _) => Fail(address.Equals(V4A) ? SocketError.TimedOut : SocketError.ConnectionRefused),
                TimeSpan.FromMilliseconds(10), HappyEyeballs.MaxAttempts, CancellationToken.None));
            runner.AreEqual(SocketError.TimedOut, timeoutFirst?.SocketErrorCode ?? SocketError.Success, "a timeout beats a later refusal");

            // And whichever order they arrive in.
            var timeoutLast = await CaptureAsync<SocketException>(() => HappyEyeballs.RaceAsync<Fake>([V4A, V4B],
                (address, _) => Fail(address.Equals(V4A) ? SocketError.ConnectionRefused : SocketError.TimedOut),
                TimeSpan.FromMilliseconds(10), HappyEyeballs.MaxAttempts, CancellationToken.None));
            runner.AreEqual(SocketError.TimedOut, timeoutLast?.SocketErrorCode ?? SocketError.Success, "also when it comes last");

            // "Network unreachable" from a family this path cannot use says least of all.
            var unreachableLast = await CaptureAsync<SocketException>(() => HappyEyeballs.RaceAsync<Fake>([V4A, V6A],
                (address, _) => Fail(address.Equals(V4A) ? SocketError.ConnectionRefused : SocketError.NetworkUnreachable),
                TimeSpan.FromMilliseconds(10), HappyEyeballs.MaxAttempts, CancellationToken.None));
            runner.AreEqual(SocketError.ConnectionRefused, unreachableLast?.SocketErrorCode ?? SocketError.Success,
                "a refusal beats a later unreachable network");
        });

        await runner.RunAsync("cancelling the connect cancels every attempt and reports the cancellation", async () =>
        {
            using var cts = new CancellationTokenSource();
            var started = 0;
            var cancelled = 0;
            var task = HappyEyeballs.RaceAsync<Fake>([V6A, V4A], async (_, ct) =>
            {
                Interlocked.Increment(ref started);
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                return new Fake(V4A);
            }, TimeSpan.FromMilliseconds(30), HappyEyeballs.MaxAttempts, cts.Token);

            runner.IsTrue(await Poll.UntilAsync(() => Volatile.Read(ref started) == 2), "both attempts are under way");
            await cts.CancelAsync();

            var threw = await CaptureAsync<OperationCanceledException>(() => task);
            runner.IsTrue(threw is not null, "the caller sees OperationCanceledException");
            runner.IsTrue(await Poll.UntilAsync(() => Volatile.Read(ref cancelled) == 2), "and both attempts were cancelled");
        });
    }

    public static async Task RunHappyEyeballsConnectAsync(TestRunner runner)
    {
        await runner.RunAsync("an IP literal is connected to without a lookup", async () =>
        {
            var resolved = 0;
            using var socket = await HappyEyeballs.ConnectAsync("192.0.2.1", 443, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250),
                (_, _) => { Interlocked.Increment(ref resolved); return Task.FromResult<IPAddress[]>([]); },
                (address, _, _) => Task.FromResult(new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)),
                CancellationToken.None);
            runner.AreEqual(0, resolved, "the resolver was not asked");

            using var bracketed = await HappyEyeballs.ConnectAsync("[2001:db8::1]", 443, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250),
                (_, _) => { Interlocked.Increment(ref resolved); return Task.FromResult<IPAddress[]>([]); },
                (address, _, _) => Task.FromResult(new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)),
                CancellationToken.None);
            runner.AreEqual(0, resolved, "nor for a bracketed IPv6 literal");
        });

        await runner.RunAsync("a name with one address connects, and the winner is the socket returned", async () =>
        {
            Socket? made = null;
            using var socket = await HappyEyeballs.ConnectAsync("origin.test", 8080, TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(250),
                (_, _) => Task.FromResult<IPAddress[]>([V4A]),
                (address, port, _) =>
                {
                    runner.AreEqual(8080, port, "the requested port is dialled");
                    return Task.FromResult(made = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp));
                },
                CancellationToken.None);
            runner.IsTrue(ReferenceEquals(made, socket), "the connection made is the one handed back");
        });

        await runner.RunAsync("the connect budget bounds resolution and every attempt and names the target and the wait", async () =>
        {
            var attemptCancelled = false;
            var clock = Stopwatch.StartNew();
            var failure = await CaptureAsync<IOException>(() => HappyEyeballs.ConnectAsync("origin.test", 8443,
                TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(50),
                (_, _) => Task.FromResult<IPAddress[]>([V6A, V4A]),
                async (_, _, ct) =>
                {
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException) { attemptCancelled = true; throw; }
                    return null!;
                },
                CancellationToken.None));

            runner.IsTrue(failure is not null, "a timeout is an IOException");
            runner.IsTrue(failure?.Message.Contains("origin.test:8443", StringComparison.Ordinal) == true
                          && failure.Message.Contains("Timed out after 0.4s", StringComparison.Ordinal),
                $"naming the host, the port and the wait (got: {failure?.Message})");
            runner.IsTrue(clock.Elapsed < Patience, "and it did not wait for the attempts to give up themselves");
            runner.IsTrue(await Poll.UntilAsync(() => attemptCancelled), "every attempt was cancelled");

            var slowLookup = await CaptureAsync<IOException>(() => HappyEyeballs.ConnectAsync("slow-dns.test", 80,
                TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(50),
                async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return []; },
                (_, _, _) => throw new InvalidOperationException("no address was ever resolved"),
                CancellationToken.None));
            runner.IsTrue(slowLookup?.Message.Contains("slow-dns.test:80", StringComparison.Ordinal) == true,
                $"a name that does not resolve in time is bounded too (got: {slowLookup?.Message})");
        });

        await runner.RunAsync("the caller's cancellation is an OperationCanceledException, not a timeout", async () =>
        {
            using var cts = new CancellationTokenSource();
            var task = HappyEyeballs.ConnectAsync("origin.test", 80, TimeSpan.FromMinutes(5), TimeSpan.FromMilliseconds(50),
                (_, _) => Task.FromResult<IPAddress[]>([V4A]),
                async (_, _, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; },
                cts.Token);
            await Task.Delay(100);
            await cts.CancelAsync();
            runner.IsTrue(await CaptureAsync<OperationCanceledException>(() => task) is not null, "cancellation propagates as itself");
        });

        await runner.RunAsync("every attempt refused is an IOException naming the target, with the socket error inside", async () =>
        {
            var failure = await CaptureAsync<IOException>(() => HappyEyeballs.ConnectAsync("origin.test", 8081,
                TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(10),
                (_, _) => Task.FromResult<IPAddress[]>([V4A, V4B]),
                (_, _, _) => Task.FromException<Socket>(new SocketException((int)SocketError.ConnectionRefused)),
                CancellationToken.None));

            runner.IsTrue(failure?.Message.Contains("origin.test:8081", StringComparison.Ordinal) == true, "the message names the target");
            runner.IsTrue(failure?.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused },
                "and the reason travels as the inner exception");
        });

        await runner.RunAsync("a name reaches an origin that listens only on IPv4", async () =>
        {
            // "localhost" lists ::1 as well as 127.0.0.1 on most machines and the listener is on the
            // second only: the first attempt is refused and the second must still connect.
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptSocketAsync();

            using var socket = await HappyEyeballs.ConnectAsync("localhost", port, new ProxyOptions(), CancellationToken.None);
            using var peer = await accepted.WaitAsync(Patience);
            runner.IsTrue(socket.Connected, "connected");
            runner.IsTrue(socket.NoDelay, "with Nagle off, as every proxy connection is");

            using var adopted = HappyEyeballs.Adopt(socket);
            runner.IsTrue(ReferenceEquals(adopted.Client, socket) && adopted.Connected,
                "and a TcpClient adopts it as the connection, not a socket of its own");
        });
    }

    public static async Task RunHandshakeTimeoutAsync(TestRunner runner)
    {
        await runner.RunAsync("an origin that accepts the TCP connection and never answers the TLS handshake is an IOException", async () =>
        {
            using var silent = new SilentListener();
            var options = new ProxyOptions { TlsHandshakeTimeout = TimeSpan.FromMilliseconds(500) };

            var failure = await CaptureAsync<IOException>(() =>
                UpstreamConnection.ConnectAsync("127.0.0.1", silent.Port, isTls: true, options, CancellationToken.None));

            runner.IsTrue(failure is not null, "the handshake is bounded");
            runner.IsTrue(failure?.Message.Contains("TLS handshake", StringComparison.Ordinal) == true
                          && failure.Message.Contains($"127.0.0.1:{silent.Port}", StringComparison.Ordinal)
                          && failure.Message.Contains("Timed out after 0.5s", StringComparison.Ordinal),
                $"and says which handshake, with whom and for how long (got: {failure?.Message})");
            runner.IsTrue(await Poll.UntilAsync(() => silent.Accepted == 1), "after the connection itself had succeeded");
            runner.IsTrue(await silent.ClosedByPeerAsync(), "and the half-open connection is closed, not leaked");
        });

        await runner.RunAsync("cancelling during the upstream handshake is a cancellation, not a handshake timeout", async () =>
        {
            using var silent = new SilentListener();
            var options = new ProxyOptions { TlsHandshakeTimeout = TimeSpan.FromMinutes(5) };
            using var cts = new CancellationTokenSource();

            var connecting = UpstreamConnection.ConnectAsync("127.0.0.1", silent.Port, isTls: true, options, cts.Token);
            runner.IsTrue(await Poll.UntilAsync(() => silent.Accepted == 1), "the handshake is under way");
            await cts.CancelAsync();

            var cancelled = await CaptureAsync<OperationCanceledException>(() => connecting);
            runner.IsTrue(cancelled is not null, "OperationCanceledException propagates");
        });

        await runner.RunAsync("a plain connection to a name reaches an IPv4-only listener through UpstreamConnection", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accepted = listener.AcceptSocketAsync();

            using var upstream = await UpstreamConnection.ConnectAsync("localhost", port, isTls: false, new ProxyOptions(), CancellationToken.None);
            using var peer = await accepted.WaitAsync(Patience);
            runner.IsTrue(upstream.IsUsable, "the upstream is connected and usable");
            runner.AreEqual("localhost", upstream.Host, "and remembers the name it was asked for");
        });

        await runner.RunAsync("a decrypted tunnel whose client never starts the TLS handshake is cut and recorded", async () =>
        {
            using var ca = CertificateAuthority.LoadOrCreate(
                Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-HappyEyeballs-Certs"));
            var store = new SessionStore();
            var options = new ProxyOptions
            {
                Port = 0,
                DecryptHttps = true,
                TlsHandshakeTimeout = TimeSpan.FromMilliseconds(600),
            };
            var proxy = new ProxyServer(options, ca, store);
            proxy.Start();
            try
            {
                using var client = new TcpClient { NoDelay = true };
                await client.ConnectAsync(IPAddress.Loopback, proxy.Endpoint!.Port);
                var stream = client.GetStream();
                await stream.WriteAsync(Encoding.Latin1.GetBytes("CONNECT 127.0.0.1:9 HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n"));

                var established = await ReadAsync(stream, "\r\n\r\n");
                runner.IsTrue(established.Text.Contains(" 200 ", StringComparison.Ordinal), "the tunnel is opened");

                var rest = await ReadAsync(stream, null);
                runner.IsTrue(rest.Eof, "then closed when no ClientHello comes");

                Session? session = null;
                await Poll.UntilAsync(() => (session = store.Snapshot().LastOrDefault(s => s.IsTunnel && s.Completed is not null)) is not null);
                runner.AreEqual(SessionState.Failed, session?.State ?? SessionState.Complete, "and recorded as a failed handshake");
                runner.IsTrue(session?.Error?.Contains("TLS handshake with client failed", StringComparison.Ordinal) == true
                              && session.Error.Contains("no handshake within", StringComparison.Ordinal),
                    $"with the reason (got: {session?.Error})");
            }
            finally { await proxy.StopAsync(); }
        });
    }

    // -------------------------------------------------------------------------- helpers

    private static async Task<TException?> CaptureAsync<TException>(Func<Task> body) where TException : Exception
    {
        try { await body(); }
        catch (TException ex) { return ex; }
        return null;
    }

    private static async Task<(string Text, bool Eof)> ReadAsync(Stream stream, string? until)
    {
        using var cts = new CancellationTokenSource(Patience);
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

    private sealed class Fake(IPAddress address) : IDisposable
    {
        private int _disposed;
        public IPAddress Address { get; } = address;
        public bool Disposed => Volatile.Read(ref _disposed) == 1;
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
    }

    /// <summary>Accepts TCP connections and never writes a byte, so a TLS handshake with it waits for ever.</summary>
    private sealed class SilentListener : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<Socket> _held = [];
        private readonly CancellationTokenSource _stop = new();
        private int _accepted;

        public SilentListener()
        {
            _listener.Start();
            _ = Task.Run(AcceptAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public int Accepted => Volatile.Read(ref _accepted);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var socket = await _listener.AcceptSocketAsync(_stop.Token);
                    lock (_held) _held.Add(socket);
                    Interlocked.Increment(ref _accepted);
                }
            }
            catch (OperationCanceledException) { /* stopped */ }
            catch (ObjectDisposedException) { /* stopped */ }
        }

        /// <summary>True once the first accepted connection has been closed by the other side.
        /// The ClientHello it was sent is read and discarded first.</summary>
        public async Task<bool> ClosedByPeerAsync()
        {
            Socket socket;
            lock (_held) socket = _held[0];
            using var cts = new CancellationTokenSource(Patience);
            var buffer = new byte[4096];
            try
            {
                while (await socket.ReceiveAsync(buffer, cts.Token) > 0) { }
                return true;
            }
            catch (SocketException) { return true; }
            catch (OperationCanceledException) { return false; }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            lock (_held) foreach (var socket in _held) socket.Dispose();
            _stop.Dispose();
        }
    }
}
