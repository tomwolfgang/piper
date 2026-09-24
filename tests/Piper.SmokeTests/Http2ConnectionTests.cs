using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Http2.Hpack;

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

        await runner.RunAsync("Http2Connection treats trailing HEADERS as the end of the request, not a new one", async () =>
        {
            // RFC 9113 §8.1: HEADERS, DATA..., then a trailing HEADERS with END_STREAM. The trailers
            // used to become a phantom second request while the real one, and its body, were lost.
            await using var peer = await RawPeer.StartAsync();

            await peer.SendHeadersAsync(1, endStream: false, Get("/upload", "POST"));
            await peer.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "hello-"u8.ToArray());
            await peer.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "world"u8.ToArray());
            await peer.SendHeadersAsync(1, endStream: true, HpackEncoder.Encode([("x-checksum", "abc")]));

            var end = await peer.ReadUntilAsync(f => f.StreamId == 1 && f.HasFlag(Http2FrameFlags.EndStream));
            runner.IsTrue(end is not null, "the request was answered");
            runner.AreEqual(1, peer.Requests.Count, "exactly one request reached the handler");

            var request = peer.Requests.Single();
            runner.AreEqual("POST", request.Method, "the original request, not the trailers, was dispatched");
            runner.AreEqual("/upload", request.RequestTarget, "path");
            runner.AreEqual("hello-world", request.BodyAsText(), "the whole body arrived");
            runner.IsTrue(!request.Headers.GetValues("x-checksum").Any(), "trailers are discarded, as HTTP/1.1 trailers are");
        });

        foreach (var reusedId in new[] { 1, 3 })
        {
            await runner.RunAsync($"Http2Connection rejects HEADERS reusing closed stream id {reusedId} with PROTOCOL_ERROR", async () =>
            {
                await using var peer = await RawPeer.StartAsync();

                await peer.SendHeadersAsync(3, endStream: true, Get("/first"));
                runner.IsTrue(await peer.ReadUntilAsync(f => f.StreamId == 3 && f.HasFlag(Http2FrameFlags.EndStream)) is not null,
                    "stream 3 completed");

                await peer.SendHeadersAsync(reusedId, endStream: true, Get("/again"));
                var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);

                // Stream 3's slot is released just after its last frame is queued, so a reuse can
                // land while it is still half-closed; STREAM_CLOSED is then the right answer.
                var code = GoAwayCode(goAway);
                runner.IsTrue(code == Http2ErrorCode.ProtocolError || (reusedId == 3 && code == Http2ErrorCode.StreamClosed),
                    $"connection error PROTOCOL_ERROR (RFC 9113 §5.1.1), got {code}");
                runner.AreEqual(1, peer.Requests.Count, "the reused id never reached the handler");
            });
        }

        await runner.RunAsync("Http2Connection resets a request missing :path but keeps HPACK in sync", async () =>
        {
            await using var peer = await RawPeer.StartAsync();

            // The malformed block inserts "x-sync: 1" into the dynamic table (literal with
            // incremental indexing, new name). The next stream refers to it by index 62; that only
            // decodes if the rejected block was decoded too.
            var malformed = HpackEncoder.Encode([(":method", "GET"), (":scheme", "https"), (":authority", "example.com")])
                .Concat(new byte[] { 0x40, 6, (byte)'x', (byte)'-', (byte)'s', (byte)'y', (byte)'n', (byte)'c', 1, (byte)'1' })
                .ToArray();
            await peer.SendHeadersAsync(1, endStream: true, malformed);
            await peer.SendHeadersAsync(3, endStream: true, Get("/ok").Append((byte)(0x80 | 62)).ToArray());

            var reset = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.RstStream && f.StreamId == 1);
            runner.AreEqual(Http2ErrorCode.ProtocolError, reset is null ? (Http2ErrorCode?)null : ErrorCodeAt(reset.Value, 0),
                "the malformed stream is reset with PROTOCOL_ERROR");
            runner.IsTrue(await peer.ReadUntilAsync(f => f.StreamId == 3 && f.HasFlag(Http2FrameFlags.EndStream)) is not null,
                "the next stream is still served");

            runner.AreEqual(1, peer.Requests.Count, "only the well-formed request reached the handler");
            runner.AreEqual("1", peer.Requests.Single().Headers["x-sync"], "dynamic-table entry from the rejected block resolved");
        });

        await runner.RunAsync("Http2Connection resets a stream whose trailers carry a pseudo-header", async () =>
        {
            await using var peer = await RawPeer.StartAsync();

            await peer.SendHeadersAsync(1, endStream: false, Get("/upload", "POST"));
            await peer.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "body"u8.ToArray());
            await peer.SendHeadersAsync(1, endStream: true, HpackEncoder.Encode([(":path", "/evil")]));
            await peer.SendHeadersAsync(3, endStream: true, Get("/after"));

            var reset = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.RstStream && f.StreamId == 1);
            runner.IsTrue(reset is not null, "stream 1 is reset");
            runner.IsTrue(await peer.ReadUntilAsync(f => f.StreamId == 3 && f.HasFlag(Http2FrameFlags.EndStream)) is not null,
                "the connection keeps serving other streams");
            runner.AreEqual("/after", peer.Requests.Single().RequestTarget, "only stream 3 reached the handler");
        });

        await runner.RunAsync("Http2Connection resets a stream whose trailers do not end it", async () =>
        {
            await using var peer = await RawPeer.StartAsync();

            await peer.SendHeadersAsync(1, endStream: false, Get("/upload", "POST"));
            await peer.SendHeadersAsync(1, endStream: false, HpackEncoder.Encode([("x-trailer", "1")]));

            var reset = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.RstStream && f.StreamId == 1);
            runner.IsTrue(reset is not null, "stream 1 is reset");
            runner.AreEqual(0, peer.Requests.Count, "nothing reached the handler");
        });

        await runner.RunAsync("Http2Connection rejects HEADERS after the request already ended", async () =>
        {
            // A slow handler keeps stream 1 open (half-closed remote) while a second HEADERS arrives.
            await using var peer = await RawPeer.StartAsync(delay: TimeSpan.FromSeconds(1));

            await peer.SendHeadersAsync(1, endStream: true, Get("/slow"));
            await peer.SendHeadersAsync(1, endStream: true, HpackEncoder.Encode([("x-late", "1")]));

            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.StreamClosed, GoAwayCode(goAway), "connection error STREAM_CLOSED");
            runner.AreEqual(1, peer.Requests.Count, "no second request");
        });

        await runner.RunAsync("Http2Connection keeps a reset stream counted until its handler stops (Rapid Reset)", async () =>
        {
            // Handlers that ignore cancellation stand in for work that has not yet noticed the reset.
            await using var peer = await RawPeer.StartAsync(delay: TimeSpan.FromSeconds(2), honourCancellation: false);

            const int pairs = 150;
            var cancel = new byte[] { 0, 0, 0, (byte)Http2ErrorCode.Cancel };
            for (var id = 1; id < pairs * 2; id += 2)
            {
                await peer.SendHeadersAsync(id, endStream: true, Get($"/r/{id}"));
                await peer.SendAsync(Http2FrameType.RstStream, Http2FrameFlags.None, id, cancel);
            }

            var refused = 0;
            await peer.ReadUntilAsync(f =>
                f.Type == Http2FrameType.RstStream && ErrorCodeAt(f, 0) == Http2ErrorCode.RefusedStream && ++refused == pairs - 100);

            runner.AreEqual(pairs - 100, refused, "streams beyond MaxConcurrentStreams are refused");
            runner.AreEqual(100, peer.Requests.Count, "no more than MaxConcurrentStreams handlers started");
        });

        await runner.RunAsync("Http2Connection rejects a header block interrupted by another frame", async () =>
        {
            await using var peer = await RawPeer.StartAsync();

            await peer.SendAsync(Http2FrameType.Headers, Http2FrameFlags.None, 1, Get("/x"));
            await peer.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "x"u8.ToArray());

            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.ProtocolError, GoAwayCode(goAway), "RFC 9113 §6.10 PROTOCOL_ERROR");
            runner.AreEqual(0, peer.Requests.Count, "nothing reached the handler");
        });

        await runner.RunAsync("Http2Connection rejects CONTINUATION without HEADERS", async () =>
        {
            await using var peer = await RawPeer.StartAsync();

            await peer.SendAsync(Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 1, Get("/x"));

            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.ProtocolError, GoAwayCode(goAway), "PROTOCOL_ERROR");
        });

        await runner.RunAsync("Http2Connection bounds a header block that never ends", async () =>
        {
            await using var peer = await RawPeer.StartAsync();

            await peer.SendAsync(Http2FrameType.Headers, Http2FrameFlags.None, 1, Get("/x"));
            var filler = new byte[16_000];
            Http2Frame? goAway = null;
            for (var i = 0; i < 8 && goAway is null; i++)
            {
                try { await peer.SendAsync(Http2FrameType.Continuation, Http2FrameFlags.None, 1, filler); }
                catch (IOException) { break; } // the server already hung up
            }
            goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);

            runner.IsTrue(goAway is not null, "the connection is ended rather than buffering without limit");
            runner.AreEqual(0, peer.Requests.Count, "nothing reached the handler");
        });
    }

    private static byte[] Get(string path, string method = "GET") => HpackEncoder.Encode(
        [(":method", method), (":scheme", "https"), (":authority", "example.com"), (":path", path)]);

    private static Http2ErrorCode? GoAwayCode(Http2Frame? goAway) => goAway is null ? null : ErrorCodeAt(goAway.Value, 4);

    private static Http2ErrorCode ErrorCodeAt(Http2Frame frame, int offset)
    {
        var s = frame.Payload.Span[offset..];
        return (Http2ErrorCode)(((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3]);
    }

    /// <summary>A hand-driven HTTP/2 client over loopback TCP, for frame sequences HttpClient will
    /// never produce. Records every request the connection hands to its handler.</summary>
    private sealed class RawPeer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly TcpClient _server;
        private readonly Task _run;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(15));

        public ConcurrentQueue<HttpRequestData> Requests { get; } = new();
        private NetworkStream Wire => _client.GetStream();

        private RawPeer(TcpClient client, TcpClient server, TimeSpan delay, bool honourCancellation)
        {
            _client = client;
            _server = server;
            var connection = new Http2Connection(server.GetStream(), async (request, ct) =>
            {
                Requests.Enqueue(request);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, honourCancellation ? ct : CancellationToken.None).ConfigureAwait(false);
                return (Http2StreamResponse)HttpResponseData.Simple(200, "OK", "ok");
            });
            _run = Task.Run(async () =>
            {
                try { await connection.RunAsync(_cts.Token).ConfigureAwait(false); }
                catch { /* assertions are made on what the peer sees */ }
            });
        }

        public static async Task<RawPeer> StartAsync(TimeSpan delay = default, bool honourCancellation = true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient { NoDelay = true };
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port).ConfigureAwait(false);
            var server = await accept.ConfigureAwait(false);
            listener.Stop();

            var peer = new RawPeer(client, server, delay, honourCancellation);
            await peer.Wire.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
            await peer.SendAsync(Http2FrameType.Settings, Http2FrameFlags.None, 0, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            return peer;
        }

        public Task SendAsync(Http2FrameType type, Http2FrameFlags flags, int streamId, ReadOnlyMemory<byte> payload) =>
            Http2FrameWriter.WriteAsync(Wire, type, flags, streamId, payload, _cts.Token);

        public Task SendHeadersAsync(int streamId, bool endStream, byte[] block) =>
            SendAsync(Http2FrameType.Headers,
                Http2FrameFlags.EndHeaders | (endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None), streamId, block);

        /// <summary>Reads frames until one matches, returning null if the connection ends first.</summary>
        public async Task<Http2Frame?> ReadUntilAsync(Func<Http2Frame, bool> match)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                while (await Http2FrameReader.ReadAsync(Wire, 1 << 24, timeout.Token).ConfigureAwait(false) is { } frame)
                    if (match(frame)) return frame;
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            return null;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _cts.CancelAsync().ConfigureAwait(false);
            try { await _run.ConfigureAwait(false); } catch { }
            _server.Dispose();
            _cts.Dispose();
        }
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

        private Harness(TcpListener listener, Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
        {
            _listener = listener;
            _acceptLoop = AcceptLoopAsync(handler);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public static Task<Harness> StartAsync(Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new Harness(listener, handler));
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
                    var connection = new Http2Connection(c.GetStream(), async (r, t) => await handler(r, t).ConfigureAwait(false));
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
