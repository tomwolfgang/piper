using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// How a relayed connection ends, and how one that never speaks again is given up on.
internal static class ConnectionLifetimeTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("the idle timeout measures silence, not elapsed time", async () =>
        {
            // The property the whole timeout depends on: a peer that keeps sending, slowly, must
            // never trip it, however long the message takes in total. Driven through a stream that
            // drips one byte per gap, so the timing belongs to the test and not to the network.
            using var reader = new HttpStreamReader(new DripStream(TimeSpan.FromMilliseconds(120), 10))
            {
                IdleTimeout = TimeSpan.FromMilliseconds(400),
            };

            var clock = Stopwatch.StartNew();
            var body = await reader.ReadExactlyAsync(10, CancellationToken.None);

            runner.AreEqual(10, body.Length, "every byte arrives");
            runner.IsTrue(clock.ElapsedMilliseconds > 400,
                $"even though the read as a whole outlasted the timeout ({clock.ElapsedMilliseconds}ms)");

            // And the other half: unbroken silence does trip it.
            using var silent = new HttpStreamReader(new DripStream(Timeout.InfiniteTimeSpan, 1))
            {
                IdleTimeout = TimeSpan.FromMilliseconds(300),
            };
            var threw = false;
            try { await silent.ReadExactlyAsync(1, CancellationToken.None); }
            catch (HttpParseException) { threw = true; }
            runner.IsTrue(threw, "a peer that says nothing at all is given up on");
        });

        await runner.RunAsync("a tunnel keeps delivering after the client half-closes its send side", async () =>
        {
            // The shape of a download: the client finishes sending, shuts down its send side, and
            // waits. Tearing the tunnel down on the first direction to finish killed the transfer
            // the tunnel existed for -- so the origin here answers only *after* it has seen
            // end-of-stream from the client, which is the exact moment the old code gave up.
            const string Reply = "PAYLOAD-SENT-AFTER-CLIENT-WENT-QUIET";

            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                var drain = new byte[64];
                while (await stream.ReadAsync(drain, ct) > 0) { }     // wait for the client's FIN
                await TestRawOrigin.WriteAsync(stream, Reply, ct);
                return false;
            });

            // Decryption off, so CONNECT yields a blind byte tunnel rather than a TLS handshake
            // this deliberately raw client would never complete.
            using var harness = new ProxyHarness(o => o.DecryptHttps = false);
            using var raw = new TcpClient();
            await raw.ConnectAsync(IPAddress.Loopback, harness.Port);
            var toProxy = raw.GetStream();

            await TestRawOrigin.WriteAsync(toProxy,
                $"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n",
                CancellationToken.None);
            var established = await ReadUntilAsync(toProxy, "Connection Established");
            runner.IsTrue(established.Contains("200", StringComparison.Ordinal), "the tunnel is established");

            await TestRawOrigin.WriteAsync(toProxy, "GET / HTTP/1.1\r\n\r\n", CancellationToken.None);
            raw.Client.Shutdown(SocketShutdown.Send);

            var relayed = await ReadUntilAsync(toProxy, Reply);
            runner.IsTrue(relayed.Contains(Reply, StringComparison.Ordinal),
                "the origin's reply still reaches the client after its send side closed");
        });

        await runner.RunAsync("an origin that goes silent fails with a reason instead of hanging", async () =>
        {
            await using var origin = new TestRawOrigin(async (_, _, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return false;
            });

            using var harness = new ProxyHarness(o => o.UpstreamIdleTimeout = TimeSpan.FromMilliseconds(400));
            using var client = harness.CreateClient();

            var clock = Stopwatch.StartNew();
            var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/never-answers");

            runner.AreEqual(HttpStatusCode.BadGateway, response.StatusCode,
                "the client is told the origin failed rather than left waiting");
            runner.IsTrue(clock.Elapsed < TimeSpan.FromSeconds(15),
                $"and told promptly rather than at its own timeout ({clock.ElapsedMilliseconds}ms)");

            var session = harness.Store.Snapshot()
                .LastOrDefault(s => s.Url.Contains("never-answers", StringComparison.Ordinal));
            runner.IsTrue(session is not null, "the attempt is captured");
            runner.AreEqual(SessionState.Failed, session!.State, "and recorded as failed");
            runner.IsTrue(session.Error?.Contains("stalled", StringComparison.OrdinalIgnoreCase) == true,
                $"with a reason naming the stall (got: {session.Error})");
        });

        await runner.RunAsync("a request with an unreadable Content-Length gets a 400 and a closed connection", async () =>
        {
            // RFC 9112 6.3 rule 6. Taken as "no body", the bytes after such a head would be parsed
            // as a second request that a front proxy never saw -- so nothing may reach the origin.
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n", ct);
                return false;
            });
            using var harness = new ProxyHarness();
            var authority = $"127.0.0.1:{origin.Port}";

            // Plain HTTP, then the same request inside a decrypted CONNECT tunnel: the two read
            // requests in separate loops, and each has to answer on its own stream.
            foreach (var tunnelled in new[] { false, true })
            foreach (var framing in new[] { "Content-Length: +5", "Content-Length: 5\r\nContent-Length: 7" })
            {
                using var raw = new TcpClient();
                await raw.ConnectAsync(IPAddress.Loopback, harness.Port);
                Stream toProxy = raw.GetStream();
                var target = $"http://{authority}/smuggle";

                if (tunnelled)
                {
                    await TestRawOrigin.WriteAsync(raw.GetStream(),
                        $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n", CancellationToken.None);
                    await ReadUntilAsync(raw.GetStream(), "\r\n\r\n");

                    // Trust is not what this test is about; the peer is Piper itself, on loopback.
                    var ssl = new SslStream(toProxy, leaveInnerStreamOpen: false, (_, _, _, _) => true);
                    await ssl.AuthenticateAsClientAsync("127.0.0.1");
                    toProxy = ssl;
                    target = "/smuggle";
                }

                // No body bytes follow the head: bytes left unread when the proxy closes would make
                // the close a reset, which can discard the 400 before this test reads it.
                await toProxy.WriteAsync(Encoding.Latin1.GetBytes(
                    $"POST {target} HTTP/1.1\r\nHost: {authority}\r\n{framing}\r\n\r\n"));

                var (reply, closed) = await ReadToCloseAsync(toProxy);
                var what = (tunnelled ? "tunnelled " : "") + framing.Replace("\r\n", " + ", StringComparison.Ordinal);
                runner.IsTrue(reply.StartsWith("HTTP/1.1 400 ", StringComparison.Ordinal),
                    $"'{what}' is answered with a 400 (got: {reply.Split('\r')[0]})");
                runner.IsTrue(reply.Contains("\r\nConnection: close\r\n", StringComparison.OrdinalIgnoreCase),
                    $"which tells the client the connection will not be reused after '{what}'");
                runner.IsTrue(closed, $"and the connection is closed rather than left hanging after '{what}'");
                await toProxy.DisposeAsync();
            }

            runner.AreEqual(0, origin.ConnectionCount, "the origin is never contacted");
        });
    }

    /// <summary>Reads until the peer closes, or gives up after 10 seconds. The flag says which.</summary>
    private static async Task<(string Text, bool Closed)> ReadToCloseAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = new StringBuilder();
        var buffer = new byte[1024];
        while (true)
        {
            int n;
            try { n = await stream.ReadAsync(buffer, timeout.Token); }
            catch (OperationCanceledException) { return (text.ToString(), false); }
            catch (IOException) { return (text.ToString(), true); } // closed with a reset
            if (n == 0) return (text.ToString(), true);
            text.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }
    }

    private sealed class ProxyHarness : IDisposable
    {
        private readonly CertificateAuthority _ca;
        private readonly ProxyServer _proxy;

        public ProxyHarness(Action<ProxyOptions>? configure = null)
        {
            _ca = CertificateAuthority.LoadOrCreate(
                Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Lifetime-Certs"));
            Store = new SessionStore();
            var options = new ProxyOptions { Port = 0 };
            configure?.Invoke(options);
            _proxy = new ProxyServer(options, _ca, Store);
            _proxy.Start();
            Port = _proxy.Endpoint!.Port;
        }

        public int Port { get; }

        public SessionStore Store { get; }

        public HttpClient CreateClient() => new(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{Port}", BypassOnLocal: false),
            UseProxy = true,
        })
        { Timeout = TimeSpan.FromSeconds(30) };

        public void Dispose()
        {
            _proxy.StopAsync().GetAwaiter().GetResult();
            _ca.Dispose();
        }
    }

    /// <summary>Hands back one byte per gap, so a test owns the timing rather than the network.</summary>
    private sealed class DripStream(TimeSpan gap, int count) : Stream
    {
        private int _remaining = count;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_remaining == 0) return 0;
            await Task.Delay(gap, ct);
            _remaining--;
            buffer.Span[0] = (byte)'x';
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<string> ReadUntilAsync(NetworkStream stream, string expect)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var text = new StringBuilder();
        var buffer = new byte[1024];
        while (!text.ToString().Contains(expect, StringComparison.Ordinal))
        {
            int n;
            try { n = await stream.ReadAsync(buffer, timeout.Token); }
            catch (OperationCanceledException) { break; }
            if (n == 0) break;
            text.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }
        return text.ToString();
    }
}
