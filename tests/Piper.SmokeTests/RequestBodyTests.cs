using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Request bodies: MaxBodyBytes applies to them (413, a failed session), a declared length is not allocated
// before its bytes arrive, and the origin gets the bytes that were sent. Discovered by reflection like
// every other group (public static Task Run*Async(TestRunner)); there is no registration line to look for.
internal static class RequestBodyTests
{
    private const int Limit = 4 * 1024 * 1024;

    public static async Task RunAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-RequestBody-Certs"));

        await runner.RunAsync("a request body over MaxBodyBytes is answered 413 and recorded as a failed session", async () =>
        {
            runner.AreEqual(256L * 1024 * 1024, new ProxyOptions().MaxBodyBytes, "the default keeps the 256 MiB request bodies always had");
            await using var origin = new RecordingOrigin();
            using var proxy = new Proxy(ca);

            // None of these sends the body, or the terminating chunk: the proxy has to answer from the
            // length or the chunk header that crosses the limit. 1 GiB fits an array, so it is a size being
            // declined; one no array can hold stays the parse error it was (400, see ParserBoundsTests).
            var half = $"{Limit / 2:X}\r\n{new string('a', Limit / 2)}\r\n";
            var attempts = new (string Name, string Framing, string Body)[]
            {
                ("a Content-Length one over", $"Content-Length: {Limit + 1}", ""),
                ("a Content-Length of 1 GiB", "Content-Length: 1073741824", ""),
                ("a chunk one over", "Transfer-Encoding: chunked", $"{Limit + 1:X}\r\n"),
                ("a chunk of 1 GiB", "Transfer-Encoding: chunked", "40000000\r\n"),
                ("chunks that add up to one over", "Transfer-Encoding: chunked", half + half + "1\r\n"),
            };
            foreach (var (name, framing, body) in attempts)
            {
                using var client = await ConnectAsync(proxy.Port);
                await WriteAsync(client.GetStream(), Head(origin.Port, framing) + body);
                var reply = await ReadAsync(client.GetStream(), null);
                runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 413", StringComparison.Ordinal), $"{name}: answered 413 (got: {FirstLine(reply.Text)})");
                runner.IsTrue(reply.Eof, $"{name}: and the connection is closed");
            }

            runner.IsTrue(await Poll.UntilAsync(() => proxy.Store.Snapshot().Count(s => s.State == SessionState.Failed) == attempts.Length),
                "each left a failed session");
            var failed = proxy.Store.Snapshot().First(s => s.State == SessionState.Failed);
            runner.AreEqual("POST", failed.Request?.Method, "the failed session carries the request");
            runner.AreEqual(413, failed.Response?.StatusCode ?? 0, "and the 413 that was sent");
            runner.IsTrue(!string.IsNullOrEmpty(failed.Error), "and says why");
            runner.AreEqual(0, origin.ConnectionCount, "nothing was forwarded to the origin");
        });

        await runner.RunAsync("the origin receives every request body byte for byte, up to exactly MaxBodyBytes", async () =>
        {
            await using var origin = new RecordingOrigin();
            using var proxy = new Proxy(ca);

            var cases = new List<(string Name, byte[] Body, int[]? Chunks)>();
            foreach (var size in new[] { 0, 1, 16 * 1024 - 1, 16 * 1024, 16 * 1024 + 1, 200_001, 1024 * 1024, Limit })
                cases.Add(($"{size} B", Pattern(size), null));
            cases.Add(("chunked 1+5000+70000+3", Pattern(75_004), [1, 5000, 70_000, 3]));
            cases.Add(("chunked 16 KiB", Pattern(16 * 1024), [16 * 1024]));
            cases.Add(($"chunked {Limit} B", Pattern(Limit), [Limit / 2, Limit / 4, Limit / 4 - 1, 1]));

            foreach (var (name, body, chunks) in cases)
            {
                origin.Requests.Clear();
                using var client = await ConnectAsync(proxy.Port);
                var stream = client.GetStream();
                if (chunks is null)
                {
                    await WriteAsync(stream, Head(origin.Port, $"Content-Length: {body.Length}"));
                    await stream.WriteAsync(body);
                }
                else
                {
                    await WriteAsync(stream, Head(origin.Port, "Transfer-Encoding: chunked"));
                    var offset = 0;
                    foreach (var size in chunks)
                    {
                        await WriteAsync(stream, $"{size:X}\r\n");
                        await stream.WriteAsync(body.AsMemory(offset, size));
                        await WriteAsync(stream, "\r\n");
                        offset += size;
                    }
                    await WriteAsync(stream, "0\r\n\r\n");
                }

                runner.IsTrue((await ReadAsync(stream, "ok")).Text.Contains("200 OK", StringComparison.Ordinal), $"{name}: answered");
                runner.AreEqual(1, origin.Requests.Count, $"{name}: reached the origin once");
                if (origin.Requests.Count != 1) continue;
                var (head, received) = origin.Requests.Single();
                runner.IsTrue(received.AsSpan().SequenceEqual(body), $"{name}: the origin received the same {body.Length} bytes");
                runner.IsTrue(head.Contains($"Content-Length: {body.Length}\r\n", StringComparison.OrdinalIgnoreCase), $"{name}: framed by length");
            }

            runner.IsTrue(await Poll.UntilAsync(() => proxy.Store.Snapshot().Length == cases.Count), "every request was recorded");
            var sessions = proxy.Store.Snapshot().OrderBy(s => s.Id).ToArray();
            for (var i = 0; i < cases.Count; i++)
                runner.IsTrue(sessions[i].Request!.Body.AsSpan().SequenceEqual(cases[i].Body), $"{cases[i].Name}: the session holds what was sent");
        });

        await runner.RunAsync("a body announced but not sent costs a small buffer, not the announced size", async () =>
        {
            const int announced = 200 * 1024 * 1024;
            const long bound = 16L * 1024 * 1024;

            // Ten bytes of a 200 MiB body, declared by Content-Length and then by a chunk header, and silence.
            foreach (var (name, framing) in new[] { ("Content-Length", $"Content-Length: {announced}\r\n\r\n"),
                                                    ("chunk size", $"Transfer-Encoding: chunked\r\n\r\n{announced:X}\r\n") })
            {
                var pipe = new Pipe(); // its reader waits for more until cancelled; nothing completes the writer
                await pipe.Writer.WriteAsync(Encoding.Latin1.GetBytes($"POST / HTTP/1.1\r\nHost: h\r\n{framing}0123456789"));
                using var reader = new HttpStreamReader(pipe.Reader.AsStream());
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
                var before = GC.GetTotalAllocatedBytes(precise: true);
                try { await HttpParser.ReadRequestAsync(reader, cts.Token); }
                catch (OperationCanceledException) { }
                var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
                runner.IsTrue(allocated < bound, $"a 200 MiB {name} that stalls allocated {allocated:N0} bytes (bound {bound:N0})");
            }

            // And through the proxy, where the first request on a connection is what a client can send for free.
            await using var origin = new RecordingOrigin();
            using var proxy = new Proxy(ca, 256L * 1024 * 1024);
            using var client = await ConnectAsync(proxy.Port);
            var start = GC.GetTotalAllocatedBytes(precise: true);
            await WriteAsync(client.GetStream(), Head(origin.Port, $"Content-Length: {announced}"));
            await Task.Delay(700); // the head is parsed and the body read begun well inside this
            var viaProxy = GC.GetTotalAllocatedBytes(precise: true) - start;
            runner.IsTrue(viaProxy < bound, $"the proxy holding a declared 200 MiB body that never comes allocated {viaProxy:N0} bytes (bound {bound:N0})");
        });

        await runner.RunAsync("the buffer a body is read into grows with the data and ends exactly full", () =>
        {
            var source = Pattern(300_000);
            foreach (var declared in new[] { 300_000, 5, 0 }) // exact, understated (as an HTTP/2 peer may send), unknown
            {
                var buffer = new BodyBuffer(declared);
                var written = 0;
                while (written < source.Length)
                {
                    var room = buffer.Reserve(source.Length - written);
                    runner.IsTrue(room.Length > 0 && room.Length <= Math.Max(written * 4 + 4, BodyBuffer.InitialCapacity),
                        $"declared {declared}: at {written} bytes received, room for {room.Length} is at most four times that");
                    source.AsSpan(written, room.Length).CopyTo(room.Span);
                    buffer.Advance(room.Length);
                    written += room.Length;
                }
                runner.IsTrue(buffer.ToArray().AsSpan().SequenceEqual(source), $"declared {declared}: every byte comes out as it went in");
            }
            runner.AreEqual(0, new BodyBuffer().ToArray().Length, "an empty body is an empty array");
            return Task.CompletedTask;
        });
    }

    private static byte[] Pattern(int size)
    {
        var bytes = new byte[size];
        new Random(size).NextBytes(bytes);
        return bytes;
    }

    private static string Head(int originPort, string framing) =>
        $"POST http://127.0.0.1:{originPort}/up HTTP/1.1\r\nHost: 127.0.0.1:{originPort}\r\n{framing}\r\n\r\n";

    private sealed class Proxy : IDisposable
    {
        private readonly ProxyServer _server;

        public Proxy(CertificateAuthority ca, long maxBodyBytes = Limit)
        {
            Store = new SessionStore();
            _server = new ProxyServer(new ProxyOptions { Port = 0, MaxBodyBytes = maxBodyBytes }, ca, Store);
            _server.Start();
            Port = _server.Endpoint!.Port;
        }

        public int Port { get; }
        public SessionStore Store { get; }

        public void Dispose() => _server.StopAsync().GetAwaiter().GetResult();
    }

    /// <summary>An origin that reads each request's body (by Content-Length, which is how the proxy always
    /// frames what it forwards) and keeps it, then answers "ok".</summary>
    private sealed class RecordingOrigin : IAsyncDisposable
    {
        private static readonly Regex ContentLength = new(@"\r\nContent-Length:\s*(\d+)\r\n", RegexOptions.IgnoreCase);
        private readonly TestRawOrigin _origin;

        public RecordingOrigin()
        {
            _origin = new TestRawOrigin(async (head, stream, ct) =>
            {
                var body = new byte[ContentLength.Match(head) is { Success: true } match ? int.Parse(match.Groups[1].Value) : 0];
                await stream.ReadExactlyAsync(body, ct);
                Requests.Enqueue((head, body));
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok", ct);
                return true;
            });
        }

        public ConcurrentQueue<(string Head, byte[] Body)> Requests { get; } = new();
        public int Port => _origin.Port;
        public int ConnectionCount => _origin.ConnectionCount;

        public ValueTask DisposeAsync() => _origin.DisposeAsync();
    }

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static Task WriteAsync(Stream stream, string text) => stream.WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

    private static string FirstLine(string text) => text.Split('\n', 2)[0].TrimEnd('\r') is { Length: > 0 } line ? line : "(nothing)";

    /// <summary>Reads until <paramref name="until"/> has arrived (or, when null, until the peer closes) or
    /// eight seconds pass. Never throws on a closed peer.</summary>
    private static async Task<(string Text, bool Eof)> ReadAsync(Stream stream, string? until)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
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
}
