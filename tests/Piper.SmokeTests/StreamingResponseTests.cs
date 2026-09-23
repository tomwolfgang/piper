using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// That a response body is forwarded as it arrives rather than once it is whole.
//
// The ordering assertions are enforced by program order, not by a stopwatch: the source is held on
// a gate the test alone releases, so "the destination had bytes before the source sent the rest" is
// a fact about sequence and cannot go flaky on a loaded machine. Against code that buffers, these
// do not merely run slowly -- they cannot finish at all, because the source waits on a gate the
// test only opens once the destination has been written to. The waits are bounded so that failure
// arrives as a failed assertion rather than as a hung suite.
internal static class StreamingResponseTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("a length-framed body is forwarded before the rest of it exists", async () =>
        {
            // Driven at the relay rather than through sockets. What is being asserted is an ordering
            // between a read and a write, and loopback TCP is free to coalesce and split writes
            // however it likes, which would decide the result for reasons that have nothing to do
            // with the code under test.
            var first = Encoding.Latin1.GetBytes(new string('a', 4096));
            var second = Encoding.Latin1.GetBytes(new string('b', 4096));
            var gate = new TaskCompletionSource();
            var released = false;

            using var source = new HttpStreamReader(new GatedStream(first, gate.Task, second));
            var destination = new RecordingStream();

            var relay = HttpBodyRelay.RelayAsync(
                source, HttpBodyDescriptor.OfLength(first.Length + second.Length), destination,
                rechunkDownstream: false, captureLimit: long.MaxValue, CancellationToken.None);

            await destination.WaitForAsync(first.Length).WaitAsync(TimeSpan.FromSeconds(10));

            runner.IsTrue(!released, "the first part is forwarded while the origin is still gated");
            runner.AreEqual(first.Length, (int)destination.Length, "and only the part that had arrived");

            released = true;
            gate.SetResult();

            var result = await relay.WaitAsync(TimeSpan.FromSeconds(10));

            runner.AreEqual((long)(first.Length + second.Length), result.TotalBytes, "the whole body is relayed");
            runner.AreEqual(Convert.ToHexString(SHA256.HashData([.. first, .. second])),
                Convert.ToHexString(SHA256.HashData(destination.ToArray())), "byte-exact, in order");
            runner.IsTrue(result.Complete, "and captured whole");
        });

        await runner.RunAsync("a chunked body is relayed chunk by chunk and stays chunked", async () =>
        {
            // The same property end to end, over real sockets, where chunk framing makes the
            // boundaries explicit rather than something TCP gets to decide.
            var gate = new TaskCompletionSource();
            var released = false;

            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream,
                    "HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\n\r\n"
                    + "5\r\nfirst\r\n", ct);
                await gate.Task;
                await TestRawOrigin.WriteAsync(stream, "6\r\nsecond\r\n0\r\n\r\n", ct);
                return false;
            });

            using var harness = new ProxyHarness();
            using var client = harness.CreateClient();

            var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/events",
                HttpCompletionOption.ResponseHeadersRead);

            runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "the head arrives");
            runner.IsTrue(response.Headers.TransferEncodingChunked == true,
                "and stays chunked downstream, since there is no length to give instead");

            await using var body = await response.Content.ReadAsStreamAsync();
            var chunk = new byte[5];
            await ReadExactlyAsync(body, chunk);

            runner.IsTrue(!released, "the first chunk reached the client before the origin sent the second");
            runner.AreEqual("first", Encoding.Latin1.GetString(chunk), "with the right bytes");

            released = true;
            gate.SetResult();

            var rest = new byte[6];
            await ReadExactlyAsync(body, rest);
            runner.AreEqual("second", Encoding.Latin1.GetString(rest), "the later chunk follows");

            var session = LastSessionFor(harness, "/events");
            runner.AreEqual(11L, session.ResponseSize, "the session records the decoded body length");
            runner.AreEqual("firstsecond", Encoding.Latin1.GetString(session.Response!.Body),
                "and captures the de-chunked body");
        });

        await runner.RunAsync("a body far past the old cap is no longer refused before it starts", async () =>
        {
            // A Content-Length over 256 MB used to be rejected by the parser before a single body
            // byte had been read, which reached the client as a 502. Nothing that large is moved
            // here: what is asserted is that the declared size alone no longer decides the outcome.
            const long Declared = 5_000_000_000;
            var sent = new string('z', 64 * 1024);
            var clientHasIt = new TaskCompletionSource();

            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream,
                    $"HTTP/1.1 200 OK\r\nContent-Length: {Declared}\r\n\r\n{sent}", ct);
                // Then closes, far short of what it promised -- but only once the client has read
                // what did arrive. Ending short resets the client's connection, and a reset may
                // discard bytes the client has not read yet.
                await clientHasIt.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                return false;
            });

            using var harness = new ProxyHarness();
            using var client = harness.CreateClient();

            var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/enormous",
                HttpCompletionOption.ResponseHeadersRead);

            runner.AreEqual(HttpStatusCode.OK, response.StatusCode,
                "the response is relayed rather than refused for its size");
            runner.AreEqual(Declared, response.Content.Headers.ContentLength ?? -1,
                "with the length the origin declared");

            await using var body = await response.Content.ReadAsStreamAsync();
            var got = new byte[sent.Length];
            await ReadExactlyAsync(body, got);
            clientHasIt.SetResult();
            runner.AreEqual(sent, Encoding.Latin1.GetString(got), "and the bytes it did send arrive");
        });

        await runner.RunAsync("time to first byte measures the head, not the whole download", async () =>
        {
            var gate = new TaskCompletionSource();

            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 4\r\n\r\n", ct);
                await gate.Task;
                await Task.Delay(400, ct);
                await TestRawOrigin.WriteAsync(stream, "done", ct);
                return false;
            });

            using var harness = new ProxyHarness();
            using var client = harness.CreateClient();

            var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/ttfb",
                HttpCompletionOption.ResponseHeadersRead);
            gate.SetResult();
            runner.AreEqual("done", await response.Content.ReadAsStringAsync(), "the body arrives");

            var session = LastSessionFor(harness, "/ttfb");
            runner.IsTrue(session.TimeToFirstByte < TimeSpan.FromMilliseconds(400),
                $"time to first byte excludes the body ({session.TimeToFirstByte?.TotalMilliseconds:0}ms)");
            runner.IsTrue(session.Duration >= TimeSpan.FromMilliseconds(350),
                "while the session's duration still covers the whole exchange");
        });

        await runner.RunAsync("a relayed body is captured byte-exact", async () =>
        {
            // A relay that is fast but corrupting would be worse than the buffering it replaces, so
            // the bytes are checked by hash rather than by length.
            var payload = RandomNumberGenerator.GetBytes(512 * 1024);
            var expected = Convert.ToHexString(SHA256.HashData(payload));

            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream,
                    $"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\n\r\n", ct);
                await stream.WriteAsync(payload, ct);
                return false;
            });

            using var harness = new ProxyHarness();
            using var client = harness.CreateClient();

            var got = await client.GetByteArrayAsync($"http://127.0.0.1:{origin.Port}/blob");

            runner.AreEqual(payload.Length, got.Length, "every byte arrives");
            runner.AreEqual(expected, Convert.ToHexString(SHA256.HashData(got)), "and none is altered in transit");

            var session = LastSessionFor(harness, "/blob");
            runner.AreEqual((long)payload.Length, session.ResponseSize, "the session reports the true size");
            runner.AreEqual(expected, Convert.ToHexString(SHA256.HashData(session.Response!.Body)),
                "and captures the body it relayed");
            runner.IsTrue(session.Response.IsBodyComplete, "flagged as a complete capture");
        });

        // Once the head has gone out, the only honest way to report a failed body is to break the
        // connection. Writing a 502 there would land inside the response in flight: counted as body
        // bytes, read as a chunk-size line, or -- on a close-delimited body -- appended to the file
        // with nothing to tell it apart from what the origin sent.
        foreach (var (name, head, resets) in new[]
                 {
                     ("a Content-Length body", "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\nhello", false),
                     ("a chunked body", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n", false),
                     ("a close-delimited body", "HTTP/1.1 200 OK\r\nConnection: close\r\n\r\nhello", true),
                 })
        {
            await runner.RunAsync($"an origin failing inside {name} breaks the client connection instead of answering twice", async () =>
            {
                var headSeen = new TaskCompletionSource();

                await using var origin = new TestRawOrigin(async (_, stream, ct) =>
                {
                    await TestRawOrigin.WriteAsync(stream, head, ct);
                    // Held until the client has the head, so the failure is certain to come after
                    // it rather than racing it.
                    await headSeen.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    // A close-delimited body ends legitimately on an orderly close; only a reset
                    // makes it a failure. Closed on the socket itself: disposing the stream would
                    // shut it down first, sending the FIN this is meant to avoid.
                    if (resets)
                    {
                        stream.Socket.LingerState = new LingerOption(true, 0);
                        stream.Socket.Close();
                    }
                    return false;
                });

                using var harness = new ProxyHarness();
                var (text, reset) = await RawExchangeAsync(harness, origin, "/cut", "HTTP/1.1", headSeen);
                runner.IsTrue(text.StartsWith("HTTP/1.1 200 OK\r\n", StringComparison.Ordinal),
                    "the origin's head reached the client");
                var tail = text[(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
                runner.IsTrue(!tail.Contains("HTTP/", StringComparison.Ordinal) && !tail.Contains("Bad Gateway", StringComparison.Ordinal),
                    $"nothing but body bytes follows it (got \"{tail.ReplaceLineEndings("\\n")}\")");
                runner.IsTrue(reset, "and the client sees the transfer fail, not end");
                runner.AreEqual(SessionState.Failed, LastSessionFor(harness, "/cut").State,
                    "the session is recorded as failed");
            });
        }

        await runner.RunAsync("a Content-Length beside chunked is not relayed alongside it", async () =>
        {
            // Chunked wins at Piper (RFC 9112 6.3), so the length describes nothing that is relayed,
            // and a next hop that framed on it instead would desync.
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream,
                    "HTTP/1.1 200 OK\r\nContent-Length: 999\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n", ct);
                return false;
            });

            using var harness = new ProxyHarness();
            var (text, _) = await RawExchangeAsync(harness, origin, "/both", "HTTP/1.1");
            var head = text[..(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)];

            runner.IsTrue(!head.Contains("Content-Length", StringComparison.OrdinalIgnoreCase),
                $"the length is dropped ({head.ReplaceLineEndings("|")})");
            runner.IsTrue(head.Contains("Transfer-Encoding: chunked", StringComparison.Ordinal), "and the body stays chunked");
            runner.AreEqual("5\r\nhello\r\n0\r\n\r\n", text[head.Length..], "carrying exactly the relayed bytes");
        });

        await runner.RunAsync("a response with an unreadable Content-Length is refused, not relayed", async () =>
        {
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: +5\r\n\r\nhello", ct);
                return false;
            });

            using var harness = new ProxyHarness();
            using var client = harness.CreateClient();
            var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/signed");

            runner.AreEqual(HttpStatusCode.BadGateway, response.StatusCode,
                "the client gets a 502 before any of it is relayed");
            runner.AreEqual(SessionState.Failed, LastSessionFor(harness, "/signed").State, "and the session says so");
        });

        await runner.RunAsync("an HTTP/1.0 client is given a chunked body de-chunked", async () =>
        {
            // HTTP/1.0 has no chunked coding: such a client would take the chunk-size lines for
            // content. It gets the body delimited by the connection closing instead.
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream,
                    "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n", ct);
                return false;
            });

            using var harness = new ProxyHarness();
            var (text, _) = await RawExchangeAsync(harness, origin, "/old", "HTTP/1.0");
            var head = text[..(text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)];

            runner.IsTrue(!head.Contains("Transfer-Encoding", StringComparison.OrdinalIgnoreCase),
                $"no chunked coding is announced ({head.ReplaceLineEndings("|")})");
            runner.IsTrue(head.Contains("Connection: close", StringComparison.Ordinal), "the close delimits the body");
            runner.AreEqual("hello world", text[head.Length..], "which arrives de-chunked");
        });

        await runner.RunAsync("a relay toward a leg that cannot half-close ends when either side does", async () =>
        {
            // A TLS leg is passed with no socket, because half-closing TCP under TLS would look like
            // a truncation attack. Without a half-close to pass on, one side finishing must end the
            // pair, or the peer waits on the other direction for ever.
            using var clientPair = await SocketPair.CreateAsync();
            using var originPair = await SocketPair.CreateAsync();

            var relay = ProxyServer.RelayBothWaysAsync(
                clientPair.Far, null, originPair.Near, null, TimeSpan.FromMinutes(5), CancellationToken.None);

            originPair.Far.Dispose();   // the origin ends; the client never sends another byte

            // Awaited rather than merely raced, so the relay has to *complete*: a fault here would
            // reach the 101 caller's catch, and an ordinary WebSocket close would be recorded as a
            // failure and answered with a reset.
            string outcome;
            try
            {
                await relay.WaitAsync(TimeSpan.FromSeconds(10));
                outcome = "completed";
            }
            catch (TimeoutException) { outcome = "still waiting on the silent direction"; }
            catch (Exception ex) { outcome = $"faulted with {ex.GetType().Name}"; }

            runner.AreEqual("completed", outcome, "the relay ends cleanly when one side does");
        });

        await runner.RunAsync("a half-closed relay ends once the other side goes silent", async () =>
        {
            // Both legs plaintext, so the origin's close is passed on as a FIN. A client that takes
            // the FIN and never closes its own half would otherwise hold the pair open for ever.
            using var clientPair = await SocketPair.CreateAsync();
            using var originPair = await SocketPair.CreateAsync();

            var relay = ProxyServer.RelayBothWaysAsync(
                clientPair.Far, clientPair.Far.Socket, originPair.Near, originPair.Near.Socket,
                TimeSpan.FromMilliseconds(200), CancellationToken.None);

            originPair.Far.Socket.Shutdown(SocketShutdown.Send);   // the origin is done sending

            string outcome;
            try
            {
                await relay.WaitAsync(TimeSpan.FromSeconds(10));
                outcome = "completed";
            }
            catch (TimeoutException) { outcome = "still held open by the silent client"; }

            runner.AreEqual("completed", outcome, "the relay gives up on the silent direction");
        });
    }

    /// <summary>
    /// Sends one GET through the proxy on a raw socket and reads until the proxy ends the
    /// connection, reporting whether it ended with a reset rather than an orderly close.
    /// </summary>
    private static async Task<(string Text, bool Reset)> RawExchangeAsync(
        ProxyHarness harness, TestRawOrigin origin, string path, string version,
        TaskCompletionSource? headSeen = null)
    {
        using var socket = new TcpClient();
        await socket.ConnectAsync(IPAddress.Loopback, harness.Port);
        var proxy = socket.GetStream();
        await proxy.WriteAsync(Encoding.Latin1.GetBytes(
            $"GET http://127.0.0.1:{origin.Port}{path} {version}\r\nHost: 127.0.0.1:{origin.Port}\r\nConnection: close\r\n\r\n"));

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var received = new List<byte>();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var n = await proxy.ReadAsync(buffer, budget.Token);
                if (n == 0) return (Encoding.Latin1.GetString(received.ToArray()), false);
                received.AddRange(buffer.AsSpan(0, n));
                if (headSeen is { Task.IsCompleted: false }
                    && Encoding.Latin1.GetString(received.ToArray()).Contains("\r\n\r\n"))
                    headSeen.SetResult();
            }
        }
        catch (IOException)
        {
            return (Encoding.Latin1.GetString(received.ToArray()), true);
        }
    }

    /// <summary>Both ends of one loopback TCP connection, as streams.</summary>
    private sealed class SocketPair : IDisposable
    {
        private SocketPair(NetworkStream near, NetworkStream far) => (Near, Far) = (near, far);

        public NetworkStream Near { get; }

        public NetworkStream Far { get; }

        public static async Task<SocketPair> CreateAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var near = new TcpClient();
                await near.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
                var far = await listener.AcceptTcpClientAsync();
                return new SocketPair(near.GetStream(), far.GetStream());
            }
            finally
            {
                listener.Stop();
            }
        }

        public void Dispose()
        {
            Near.Dispose();
            Far.Dispose();
        }
    }

    private sealed class ProxyHarness : IDisposable
    {
        private readonly CertificateAuthority _ca;
        private readonly ProxyServer _proxy;

        public ProxyHarness()
        {
            _ca = CertificateAuthority.LoadOrCreate(
                Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Streaming-Certs"));
            Store = new SessionStore();
            _proxy = new ProxyServer(new ProxyOptions { Port = 0 }, _ca, Store);
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

    /// <summary>Hands over the first part of a body, then waits for the test before the rest.</summary>
    private sealed class GatedStream(byte[] first, Task gate, byte[] second) : Stream
    {
        private int _delivered;
        private bool _waited;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_delivered < first.Length)
            {
                var take = Math.Min(buffer.Length, first.Length - _delivered);
                first.AsMemory(_delivered, take).CopyTo(buffer);
                _delivered += take;
                return take;
            }

            if (!_waited)
            {
                await gate.WaitAsync(ct);
                _waited = true;
            }

            var offset = _delivered - first.Length;
            if (offset >= second.Length) return 0;

            var rest = Math.Min(buffer.Length, second.Length - offset);
            second.AsMemory(offset, rest).CopyTo(buffer);
            _delivered += rest;
            return rest;
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

    /// <summary>Records what it is written and lets a test wait for a given amount of it.</summary>
    private sealed class RecordingStream : Stream
    {
        private readonly MemoryStream _written = new();
        private readonly Lock _gate = new();
        private TaskCompletionSource? _waiter;
        private long _awaited;

        public override long Length
        {
            get { lock (_gate) return _written.Length; }
        }

        public byte[] ToArray()
        {
            lock (_gate) return _written.ToArray();
        }

        public Task WaitForAsync(long bytes)
        {
            lock (_gate)
            {
                if (_written.Length >= bytes) return Task.CompletedTask;
                _awaited = bytes;
                _waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                return _waiter.Task;
            }
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            TaskCompletionSource? reached = null;
            lock (_gate)
            {
                _written.Write(buffer.Span);
                if (_waiter is not null && _written.Length >= _awaited)
                {
                    reached = _waiter;
                    _waiter = null;
                }
            }
            reached?.TrySetResult();
            return ValueTask.CompletedTask;
        }

        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static Session LastSessionFor(ProxyHarness harness, string path) =>
        harness.Store.Snapshot().Last(s => s.Url.Contains(path, StringComparison.Ordinal));

    /// <summary>
    /// Fills <paramref name="destination"/>, failing loudly rather than hanging for ever if the
    /// bytes never come -- which is what a proxy that buffers would do here.
    /// </summary>
    private static async Task ReadExactlyAsync(Stream source, byte[] destination)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var offset = 0;
        while (offset < destination.Length)
        {
            var n = await source.ReadAsync(destination.AsMemory(offset), budget.Token);
            if (n == 0) throw new IOException($"stream ended after {offset} of {destination.Length} bytes");
            offset += n;
        }
    }
}
