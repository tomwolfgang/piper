using System.Net;
using System.Net.Sockets;
using Piper.Core.Http;
using Piper.Core.Http2;

// Http2Connection wired to a stubbed handler over a bare cleartext (h2c) TCP loopback socket --
// no TLS, no upstream. Proves the frame demux / per-stream concurrency / single-writer outbox
// design in isolation, before any of it is wired into ProxyServer's real TLS-terminated path.
internal static class Http2ConnectionTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        await runner.RunAsync("Http2Connection serves a single GET", async () =>
        {
            await using var harness = await Harness.StartAsync(StubHandler);
            using var client = harness.CreateClient();

            var response = await client.GetAsync($"{harness.BaseUrl}/hello");
            var body = await response.Content.ReadAsStringAsync();

            runner.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, "status");
            runner.AreEqual("2.0", response.Version.ToString(), "client negotiated h2");
            runner.IsTrue(body.Contains("GET /hello"), "body reflects the request");
        });

        await runner.RunAsync("Http2Connection serves a POST body round trip", async () =>
        {
            await using var harness = await Harness.StartAsync(StubHandler);
            using var client = harness.CreateClient();

            var response = await client.PostAsync($"{harness.BaseUrl}/echo", new StringContent("payload-data"));
            var body = await response.Content.ReadAsStringAsync();

            runner.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, "status");
            runner.IsTrue(body.Contains("POST /echo"), "method and path");
            runner.IsTrue(body.Contains("payload-data"), "request body reached the handler");
        });

        await runner.RunAsync("Http2Connection multiplexes many concurrent streams without cross-talk", async () =>
        {
            await using var harness = await Harness.StartAsync(StubHandler);
            using var client = harness.CreateClient();

            const int count = 20;
            var tasks = Enumerable.Range(0, count).Select(async i =>
            {
                var path = i % 3 == 0 ? $"/slow/{i}" : $"/fast/{i}";
                var response = await client.GetAsync($"{harness.BaseUrl}{path}");
                var body = await response.Content.ReadAsStringAsync();
                return (i, path, body);
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            foreach (var (i, path, body) in results)
                runner.AreEqual($"GET {path}", body, $"stream {i} got exactly its own response body, not another stream's");
        });

        await runner.RunAsync("Http2Connection returns a distinct status per stream", async () =>
        {
            await using var harness = await Harness.StartAsync(StubHandler);
            using var client = harness.CreateClient();

            var ok = await client.GetAsync($"{harness.BaseUrl}/hello");
            var notFound = await client.GetAsync($"{harness.BaseUrl}/status/404");

            runner.AreEqual(System.Net.HttpStatusCode.OK, ok.StatusCode, "first stream status");
            runner.AreEqual(System.Net.HttpStatusCode.NotFound, notFound.StatusCode, "second stream status, independent of the first");
        });

        await runner.RunAsync("many concurrent large bodies don't overrun the shared connection flow-control window", async () =>
        {
            // Regression test for a real bug: sending each stream's body against the *shared*
            // connection-level window via separate read-then-subtract steps let concurrent
            // streams each act on the same stale balance and, combined, send more than the peer
            // had granted -- a real HTTP/2 client (browser or HttpClient) enforces flow control
            // strictly and resets the connection when that happens. Each body here is larger than
            // the 64KB RFC-default initial window, and there are enough of them running at once
            // that their combined size can only fit if the shared window is spent atomically.
            await using var harness = await Harness.StartAsync(StubHandler);
            using var client = harness.CreateClient();

            const int count = 12;
            const int bodySize = 300_000;

            var tasks = Enumerable.Range(0, count).Select(async i =>
            {
                var response = await client.GetAsync($"{harness.BaseUrl}/big/{i}?size={bodySize}");
                var body = await response.Content.ReadAsByteArrayAsync();
                return (i, response.StatusCode, body);
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            foreach (var (i, status, body) in results)
            {
                runner.AreEqual(System.Net.HttpStatusCode.OK, status, $"stream {i} completed instead of being reset");
                runner.AreEqual(bodySize, body.Length, $"stream {i} body arrived complete, not truncated");
                runner.IsTrue(BigBodyPattern(i, bodySize).AsSpan().SequenceEqual(body), $"stream {i} body matches its own pattern, not another stream's");
            }
        });

        await runner.RunAsync("Http2Connection resets a stream whose request body passes the cap", async () =>
        {
            // Window credit is granted as DATA arrives, so without a cap one stream could grow a
            // request body until the process ran out of memory.
            const int limit = 64 * 1024;
            var handled = 0;
            var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var harness = await Harness.StartAsync((request, ct) =>
            {
                Interlocked.Increment(ref handled);
                return StubHandler(request, ct);
            }, maxRequestBodyBytes: limit, log: logged.Enqueue);
            using var client = harness.CreateClient();

            var rejected = false;
            try
            {
                using var oversized = await client.PostAsync($"{harness.BaseUrl}/echo", new ByteArrayContent(new byte[limit * 4]));
            }
            catch (HttpRequestException)
            {
                rejected = true;
            }
            runner.IsTrue(rejected, "an oversized body is refused rather than buffered");
            runner.AreEqual(0, Volatile.Read(ref handled), "the handler never saw the oversized request");
            // No session records it, so the log is what says it happened, as it does for HTTP/1.1.
            runner.AreEqual(1, logged.Count(line => line.Contains("exceeds the 65536 byte cap")), "the reset is logged once");

            var atLimit = await client.PostAsync($"{harness.BaseUrl}/echo", new ByteArrayContent(new byte[limit]));
            runner.AreEqual(System.Net.HttpStatusCode.OK, atLimit.StatusCode, "a body exactly at the cap is still served");
            var small = await client.PostAsync($"{harness.BaseUrl}/echo", new StringContent("after"));
            runner.IsTrue((await small.Content.ReadAsStringAsync()).Contains("body=after"), "the connection keeps serving other streams");
        });

        await runner.RunAsync("DATA after END_STREAM is dropped without disturbing the response", async () =>
        {
            // Once a request is dispatched its stream stays registered until the response is sent.
            // Late DATA used to pile up in the stream's buffer, and counted against the body cap it
            // would reset a response that was already on its way.
            const int limit = 1024;
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var harness = await Harness.StartAsync(async (request, ct) =>
            {
                await release.Task.WaitAsync(ct);
                return HttpResponseData.Simple(200, "OK", "done");
            }, maxRequestBodyBytes: limit);

            using var tcp = new TcpClient();
            var url = new Uri($"{harness.BaseUrl}/late");
            await tcp.ConnectAsync(IPAddress.Loopback, url.Port);
            var wire = tcp.GetStream();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await wire.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), budget.Token);
            await Http2FrameWriter.WriteAsync(wire, Http2FrameType.Settings, Http2FrameFlags.None, 0, ReadOnlyMemory<byte>.Empty, budget.Token);
            var request = new HttpRequestData { Method = "GET", RequestTarget = "/late", Url = url, HttpVersion = "HTTP/2" };
            var block = Piper.Core.Http2.Hpack.HpackEncoder.Encode(Http2MessageAdapter.ToHeaderFields(request));
            await Http2FrameWriter.WriteHeadersAsync(wire, 1, block, endStream: true, 16_384, budget.Token);
            // More than the whole 65,535-byte connection window, and each frame past the body cap:
            // dropped bytes must still be credited back, or the other streams would stall.
            const int lateFrames = 8;
            for (var i = 0; i < lateFrames; i++)
                await Http2FrameWriter.WriteAsync(wire, Http2FrameType.Data, Http2FrameFlags.None, 1, new byte[16_384], budget.Token);

            // Frames are handled in wire order, so the PING's answer means the late DATA was seen.
            await Http2FrameWriter.WriteAsync(wire, Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8], budget.Token);
            var reset = false;
            var body = new List<byte>();
            long credited = 0, creditedByPing = -1;
            try
            {
                while (true)
                {
                    var frame = await Http2FrameReader.ReadRequiredAsync(wire, 16_384, budget.Token);
                    if (frame.Type == Http2FrameType.WindowUpdate && frame.StreamId == 0)
                    {
                        var span = frame.Payload.Span;
                        credited += ((span[0] & 0x7f) << 24) | (span[1] << 16) | (span[2] << 8) | span[3];
                    }
                    if (frame.Type == Http2FrameType.Ping && frame.HasFlag(Http2FrameFlags.Ack))
                    {
                        creditedByPing = credited;
                        release.TrySetResult();
                    }
                    if (frame.StreamId != 1) continue;
                    if (frame.Type == Http2FrameType.RstStream) { reset = true; break; }
                    if (frame.Type != Http2FrameType.Data) continue;
                    body.AddRange(frame.DataPayload.ToArray());
                    if (frame.HasFlag(Http2FrameFlags.EndStream)) break;
                }
            }
            catch (OperationCanceledException) { /* reported below as a missing body */ }

            runner.IsTrue(!reset, "the stream is not reset");
            runner.AreEqual("done", System.Text.Encoding.Latin1.GetString(body.ToArray()), "the response arrives whole");
            // Credit is batched in 32 KB steps, so at most one step can still be owed.
            runner.IsTrue(creditedByPing >= lateFrames * 16_384 - 32 * 1024,
                $"the dropped bytes are credited to the connection window ({creditedByPing:N0} of {lateFrames * 16_384:N0})");
        });

        await runner.RunAsync("a stray CONTINUATION after a dispatched request neither re-dispatches it nor ends the connection", async () =>
        {
            // CONTINUATION after END_HEADERS re-completes the headers of a stream already handed to
            // its handler. Dispatching it again would run the handler twice, and dispatch releases
            // the request buffer, so a second dispatch would throw on the reader and drop the
            // connection.
            var calls = 0;
            // Held until the CONTINUATION has been read, so the stream is still registered when it
            // arrives rather than already answered and forgotten.
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var harness = await Harness.StartAsync(async (request, ct) =>
            {
                Interlocked.Increment(ref calls);
                if (request.Url!.AbsolutePath == "/once") await release.Task.WaitAsync(ct);
                return HttpResponseData.Simple(200, "OK", request.Url.AbsolutePath);
            });

            using var tcp = new TcpClient();
            var url = new Uri($"{harness.BaseUrl}/once");
            await tcp.ConnectAsync(IPAddress.Loopback, url.Port);
            var wire = tcp.GetStream();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // The encoder never writes to the dynamic table, so repeating a block cannot desynchronise
            // the decoder: what is under test is only the dispatch.
            byte[] Block(string path) => Piper.Core.Http2.Hpack.HpackEncoder.Encode(Http2MessageAdapter.ToHeaderFields(
                new HttpRequestData { Method = "GET", RequestTarget = path, Url = new Uri(url, path), HttpVersion = "HTTP/2" }));

            await wire.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), budget.Token);
            await Http2FrameWriter.WriteAsync(wire, Http2FrameType.Settings, Http2FrameFlags.None, 0, ReadOnlyMemory<byte>.Empty, budget.Token);
            await Http2FrameWriter.WriteHeadersAsync(wire, 1, Block("/once"), endStream: true, 16_384, budget.Token);
            await Http2FrameWriter.WriteAsync(wire, Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 1, ReadOnlyMemory<byte>.Empty, budget.Token);
            // Frames are handled in wire order, so the PING's answer means the CONTINUATION was seen.
            await Http2FrameWriter.WriteAsync(wire, Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8], budget.Token);
            await Http2FrameWriter.WriteHeadersAsync(wire, 3, Block("/after"), endStream: true, 16_384, budget.Token);

            var bodies = new Dictionary<int, string>();
            try
            {
                while (!bodies.ContainsKey(1) || !bodies.ContainsKey(3))
                {
                    var frame = await Http2FrameReader.ReadRequiredAsync(wire, 16_384, budget.Token);
                    if (frame.Type == Http2FrameType.Ping && frame.HasFlag(Http2FrameFlags.Ack)) release.TrySetResult();
                    if (frame.Type != Http2FrameType.Data || !frame.HasFlag(Http2FrameFlags.EndStream)) continue;
                    bodies[frame.StreamId] = System.Text.Encoding.Latin1.GetString(frame.DataPayload.Span);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { /* reported below */ }

            runner.AreEqual("/once", bodies.GetValueOrDefault(1), "the first request is answered");
            runner.AreEqual("/after", bodies.GetValueOrDefault(3), "the connection keeps serving later streams");
            runner.AreEqual(2, Volatile.Read(ref calls), "the handler ran once per request, not again for the CONTINUATION");
        });
    }

    /// <summary>Deterministic per-stream fill so cross-talk between concurrently-sent large
    /// bodies would show up as a content mismatch, not just a length mismatch.</summary>
    private static byte[] BigBodyPattern(int streamIndex, int size)
    {
        var body = new byte[size];
        for (var i = 0; i < size; i++) body[i] = (byte)(streamIndex + i);
        return body;
    }

    private static Task<HttpResponseData> StubHandler(HttpRequestData request, CancellationToken ct)
    {
        var path = request.Url!.PathAndQuery;
        if (path.StartsWith("/status/", StringComparison.Ordinal))
        {
            var code = int.Parse(path["/status/".Length..]);
            return Task.FromResult(HttpResponseData.Simple(code, "Custom", $"{request.Method} {path}"));
        }

        if (path.StartsWith("/big/", StringComparison.Ordinal))
        {
            var afterPrefix = path["/big/".Length..];
            var queryStart = afterPrefix.IndexOf('?');
            var streamIndex = int.Parse(queryStart >= 0 ? afterPrefix[..queryStart] : afterPrefix);
            var size = int.Parse(request.Url!.Query[(request.Url.Query.IndexOf("size=", StringComparison.Ordinal) + 5)..]);

            var response = new HttpResponseData { StatusCode = 200, ReasonPhrase = "OK", Body = BigBodyPattern(streamIndex, size) };
            response.Headers.Set("Content-Type", "application/octet-stream");
            return Task.FromResult(response);
        }

        return SlowOrFastAsync(request, path, ct);
    }

    private static async Task<HttpResponseData> SlowOrFastAsync(HttpRequestData request, string path, CancellationToken ct)
    {
        if (path.StartsWith("/slow/", StringComparison.Ordinal))
            await Task.Delay(80, ct).ConfigureAwait(false);

        var bodyText = request.Body.Length > 0
            ? $"{request.Method} {path} body={request.BodyAsText()}"
            : $"{request.Method} {path}";
        return HttpResponseData.Simple(200, "OK", bodyText);
    }

    /// <summary>Bare TCP loopback listener speaking cleartext HTTP/2, so the demux/concurrency
    /// design can be exercised with a real <see cref="HttpClient"/> without any TLS involved.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _acceptLoop;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _connections = [];
        private readonly Lock _gate = new();

        private readonly long? _maxRequestBodyBytes;
        private readonly Action<string>? _log;

        private Harness(TcpListener listener, Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler,
            long? maxRequestBodyBytes, Action<string>? log)
        {
            _listener = listener;
            _maxRequestBodyBytes = maxRequestBodyBytes;
            _log = log;
            _acceptLoop = AcceptLoopAsync(handler);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public static Task<Harness> StartAsync(
            Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler, long? maxRequestBodyBytes = null,
            Action<string>? log = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new Harness(listener, handler, maxRequestBodyBytes, log));
        }

        public HttpClient CreateClient() => new(new SocketsHttpHandler
        {
            // Cleartext h2 ("h2c"): no ALPN/TLS negotiation, so this must be forced explicitly.
        })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10),
        };

        private async Task AcceptLoopAsync(Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                var task = Task.Run(async () =>
                {
                    using var c = client;
                    c.NoDelay = true;
                    var connection = _maxRequestBodyBytes is { } max
                        ? new Http2Connection(c.GetStream(), async (r, t) => await handler(r, t).ConfigureAwait(false)) { MaxRequestBodyBytes = max, Log = _log }
                        : new Http2Connection(c.GetStream(), async (r, t) => await handler(r, t).ConfigureAwait(false)) { Log = _log };
                    try { await connection.RunAsync(_cts.Token).ConfigureAwait(false); }
                    catch { /* test asserts on the client side */ }
                }, _cts.Token);

                lock (_gate) _connections.Add(task);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try { await _acceptLoop.ConfigureAwait(false); } catch { }

            Task[] pending;
            lock (_gate) pending = _connections.ToArray();
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        }
    }
}
