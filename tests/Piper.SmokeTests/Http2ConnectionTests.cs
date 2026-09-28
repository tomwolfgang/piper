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
                var answer = await peer.ReadUntilAsync(f =>
                    f.Type == Http2FrameType.GoAway || (f.Type == Http2FrameType.RstStream && f.StreamId == reusedId));

                // Stream 3's slot is released just after its last frame is queued, so a reuse can
                // land while it is still half-closed; resetting just that stream with STREAM_CLOSED
                // is then the right answer.
                var outcome = answer switch
                {
                    { Type: Http2FrameType.GoAway } g => $"GOAWAY {GoAwayCode(g)}",
                    { Type: Http2FrameType.RstStream } r => $"RST_STREAM {ErrorCodeAt(r, 0)}",
                    _ => "nothing",
                };
                runner.IsTrue(outcome == $"GOAWAY {Http2ErrorCode.ProtocolError}"
                              || (reusedId == 3 && outcome == $"RST_STREAM {Http2ErrorCode.StreamClosed}"),
                    $"connection error PROTOCOL_ERROR (RFC 9113 §5.1.1), got {outcome}");
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

        await runner.RunAsync("Http2Connection resets only the stream that sends HEADERS after its request ended", async () =>
        {
            // A slow handler keeps stream 1 open (half-closed remote) while a second HEADERS arrives.
            await using var peer = await RawPeer.StartAsync(delay: TimeSpan.FromSeconds(1));

            await peer.SendHeadersAsync(1, endStream: true, Get("/slow"));
            await peer.SendHeadersAsync(1, endStream: true, HpackEncoder.Encode([("x-late", "1")]));

            var reset = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway || (f.Type == Http2FrameType.RstStream && f.StreamId == 1));
            runner.IsTrue(reset is { Type: Http2FrameType.RstStream }, "a stream error, not the end of the connection");
            runner.AreEqual(Http2ErrorCode.StreamClosed, reset is { Type: Http2FrameType.RstStream } r ? ErrorCodeAt(r, 0) : (Http2ErrorCode?)null,
                "RFC 9113 §5.1 STREAM_CLOSED");

            // The other streams on the connection carry on.
            await peer.SendHeadersAsync(3, endStream: true, Get("/after"));
            var after = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway || (f.StreamId == 3 && f.HasFlag(Http2FrameFlags.EndStream)));
            runner.IsTrue(after is { Type: not Http2FrameType.GoAway }, "a later stream is still answered");
            runner.AreEqual(2, peer.Requests.Count, "no second request for stream 1: /slow and /after only");
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
            await Poll.UntilAsync(() => peer.Requests.Count >= 100);
            await Task.Delay(200);
            runner.AreEqual(100, peer.Requests.Count, "no more than MaxConcurrentStreams handlers started");
        });

        await runner.RunAsync("Http2Connection discards trailers the peer sent before seeing REFUSED_STREAM", async () =>
        {
            await using var peer = await RawPeer.StartAsync();

            // 100 streams left open (no END_STREAM) fill MaxConcurrentStreams; 201 is refused. Its
            // block inserts "x-sync: 1" into the dynamic table, and stream 1's trailers refer to it
            // by index 62, which only decodes if the refused block was decoded too.
            for (var id = 1; id <= 199; id += 2)
                await peer.SendHeadersAsync(id, endStream: false, Get($"/open/{id}", "POST"));
            await peer.SendHeadersAsync(201, endStream: false, [.. Get("/refused", "POST"), .. LiteralIndexed("x-sync", "1")]);
            await peer.SendHeadersAsync(201, endStream: true, HpackEncoder.Encode([("x-trailer", "1")]));
            await peer.SendHeadersAsync(1, endStream: true, [0x80 | 62]);

            var outcome = await peer.ReadUntilAsync(f =>
                f.Type == Http2FrameType.GoAway || (f.StreamId == 1 && f.HasFlag(Http2FrameFlags.EndStream)));
            runner.IsTrue(outcome is { Type: not Http2FrameType.GoAway }, "the connection survives, HPACK in sync, and stream 1 is answered");
            runner.AreEqual("/open/1", peer.Requests.Single().RequestTarget, "the refused stream never reached the handler");
        });

        await runner.RunAsync("Http2Connection bounds the decoded header list, not just the block", async () =>
        {
            await using var peer = await RawPeer.StartAsync();

            // ~4 KiB of block: one 4000-byte entry added to the dynamic table, then referenced 20
            // times by one-byte index, decoding to ~80 KiB against the 64 KiB advertised limit.
            // The table is in sync afterwards, so only that stream is reset. Stream 3's oversized
            // trailers (the same entry, referenced 20 times) are reset the same way, and stream 5
            // proves the connection carries on.
            byte[] amplify = [.. Enumerable.Repeat((byte)(0x80 | 62), 20)];
            await peer.SendHeadersAsync(1, endStream: true, [.. Get("/x"), .. LiteralIndexed("x-big", new string('a', 4000)), .. amplify]);
            await peer.SendHeadersAsync(3, endStream: false, Get("/upload", "POST"));
            await peer.SendHeadersAsync(3, endStream: true, amplify);
            await peer.SendHeadersAsync(5, endStream: true, Get("/after"));

            var resets = new Dictionary<int, Http2ErrorCode>();
            var answered = await peer.ReadUntilAsync(f =>
            {
                if (f.Type == Http2FrameType.RstStream) resets[f.StreamId] = ErrorCodeAt(f, 0);
                return f.Type == Http2FrameType.GoAway || (f.StreamId == 5 && f.HasFlag(Http2FrameFlags.EndStream));
            });
            runner.IsTrue(answered is { Type: not Http2FrameType.GoAway }, "the connection survives and stream 5 is answered");
            runner.AreEqual(Http2ErrorCode.EnhanceYourCalm, resets.GetValueOrDefault(1), "the oversized request is reset with ENHANCE_YOUR_CALM");
            runner.AreEqual(Http2ErrorCode.EnhanceYourCalm, resets.GetValueOrDefault(3), "the oversized trailers are reset with ENHANCE_YOUR_CALM");
            runner.AreEqual("/after", peer.Requests.Single().RequestTarget, "only stream 5 reached the handler");
        });

        await runner.RunAsync("Http2Connection discards the rest of a malformed request it reset", async () =>
        {
            await using var peer = await RawPeer.StartAsync();

            await peer.SendHeadersAsync(1, endStream: false,
                HpackEncoder.Encode([(":method", "POST"), (":scheme", "https"), (":authority", "example.com")]));
            await peer.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "body"u8.ToArray());
            await peer.SendHeadersAsync(1, endStream: true, HpackEncoder.Encode([("x-trailer", "1")]));
            await peer.SendHeadersAsync(3, endStream: true, Get("/after"));

            var outcome = await peer.ReadUntilAsync(f =>
                f.Type == Http2FrameType.GoAway || (f.StreamId == 3 && f.HasFlag(Http2FrameFlags.EndStream)));
            runner.IsTrue(outcome is { Type: not Http2FrameType.GoAway }, "the connection survives and stream 3 is answered");
            runner.AreEqual("/after", peer.Requests.Single().RequestTarget, "only stream 3 reached the handler");
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

        await runner.RunAsync("Http2Connection bounds request bodies across a connection's streams, not only per stream", async () =>
        {
            // Each stream stays under its own cap; together they would pass the connection's. Only
            // the stream whose DATA would carry the total past it is reset.
            var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var harness = await Harness.StartAsync(
                (request, ct) => Task.FromResult(HttpResponseData.Simple(200, "OK", request.Body.Length.ToString())),
                maxRequestBodyBytes: 64 * 1024, log: logged.Enqueue, maxBufferedRequestBytes: 96 * 1024);

            using var tcp = new TcpClient();
            var url = new Uri($"{harness.BaseUrl}/up");
            await tcp.ConnectAsync(IPAddress.Loopback, url.Port);
            var wire = tcp.GetStream();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var block = Piper.Core.Http2.Hpack.HpackEncoder.Encode(Http2MessageAdapter.ToHeaderFields(
                new HttpRequestData { Method = "POST", RequestTarget = "/up", Url = url, HttpVersion = "HTTP/2" }));
            Task Data(int stream, int size, bool end = false) => Http2FrameWriter.WriteAsync(
                wire, Http2FrameType.Data, end ? Http2FrameFlags.EndStream : Http2FrameFlags.None, stream, new byte[size], budget.Token);

            await wire.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), budget.Token);
            await Http2FrameWriter.WriteAsync(wire, Http2FrameType.Settings, Http2FrameFlags.None, 0, ReadOnlyMemory<byte>.Empty, budget.Token);
            foreach (var stream in new[] { 1, 3 })
            {
                await Http2FrameWriter.WriteHeadersAsync(wire, stream, block, endStream: false, 16_384, budget.Token);
                await Data(stream, 16_384);
                await Data(stream, 16_384);
                await Data(stream, 8_192);
            }
            await Http2FrameWriter.WriteHeadersAsync(wire, 5, block, endStream: false, 16_384, budget.Token);
            await Data(5, 16_384); // 96 KB buffered: at the connection's cap, not past it
            await Data(5, 16_384); // would be 112 KB
            await Data(1, 0, end: true);
            await Data(3, 0, end: true);

            var bodies = new Dictionary<int, string>();
            var reset = new HashSet<int>();
            try
            {
                while (!bodies.ContainsKey(1) || !bodies.ContainsKey(3) || !reset.Contains(5))
                {
                    var frame = await Http2FrameReader.ReadRequiredAsync(wire, 16_384, budget.Token);
                    if (frame.Type == Http2FrameType.RstStream) reset.Add(frame.StreamId);
                    if (frame.Type == Http2FrameType.Data && frame.HasFlag(Http2FrameFlags.EndStream))
                        bodies[frame.StreamId] = System.Text.Encoding.Latin1.GetString(frame.DataPayload.Span);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { /* reported below */ }

            runner.IsTrue(reset.SetEquals([5]), $"only the stream that passed the connection's cap is reset ({string.Join(",", reset)})");
            runner.AreEqual("40960", bodies.GetValueOrDefault(1), "the first stream's body arrives whole");
            runner.AreEqual("40960", bodies.GetValueOrDefault(3), "and so does the second's");
            runner.AreEqual(1, logged.Count(line => line.Contains("request bodies on this connection exceed the 98304 byte cap")),
                "the reset is logged once, naming the connection's cap");
        });

        await runner.RunAsync("DATA after END_STREAM resets only that stream, once, and is still credited", async () =>
        {
            // Once a request is dispatched its stream stays registered until the response is sent.
            // Late DATA used to pile up in the stream's buffer. It is a stream error (RFC 9113 §5.1):
            // that stream is reset, and only once however much follows, while the bytes are still
            // credited to the connection window so the other streams are not stalled.
            const int limit = 1024;
            var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var harness = await Harness.StartAsync(async (request, ct) =>
            {
                if (request.Url!.AbsolutePath != "/late") return HttpResponseData.Simple(200, "OK", "after");
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { handlerCancelled.TrySetResult(); throw; }
                return HttpResponseData.Simple(200, "OK", "unreachable");
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
            var after = Piper.Core.Http2.Hpack.HpackEncoder.Encode(Http2MessageAdapter.ToHeaderFields(
                new HttpRequestData { Method = "GET", RequestTarget = "/after", Url = new Uri(url, "/after"), HttpVersion = "HTTP/2" }));
            await Http2FrameWriter.WriteHeadersAsync(wire, 3, after, endStream: true, 16_384, budget.Token);

            var resets = new List<Http2ErrorCode>();
            var goAway = false;
            string? afterBody = null;
            long credited = 0, creditedByPing = -1;
            try
            {
                while (afterBody is null || creditedByPing < 0)
                {
                    var frame = await Http2FrameReader.ReadRequiredAsync(wire, 16_384, budget.Token);
                    if (frame.Type == Http2FrameType.GoAway) { goAway = true; break; }
                    if (frame.Type == Http2FrameType.WindowUpdate && frame.StreamId == 0)
                    {
                        var span = frame.Payload.Span;
                        credited += ((span[0] & 0x7f) << 24) | (span[1] << 16) | (span[2] << 8) | span[3];
                    }
                    if (frame.Type == Http2FrameType.Ping && frame.HasFlag(Http2FrameFlags.Ack)) creditedByPing = credited;
                    if (frame.Type == Http2FrameType.RstStream && frame.StreamId == 1)
                        resets.Add((Http2ErrorCode)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.Span));
                    if (frame.Type == Http2FrameType.Data && frame.StreamId == 3 && frame.HasFlag(Http2FrameFlags.EndStream))
                        afterBody = System.Text.Encoding.Latin1.GetString(frame.DataPayload.Span);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException) { /* reported below */ }

            runner.IsTrue(!goAway, "the connection is not ended");
            runner.AreEqual($"{Http2ErrorCode.StreamClosed}", string.Join(",", resets), "stream 1 is reset once, with STREAM_CLOSED");
            runner.IsTrue(handlerCancelled.Task.Wait(TimeSpan.FromSeconds(5)), "its handler is cancelled");
            runner.AreEqual("after", afterBody, "a later stream is still answered");
            // Credit is batched in 32 KB steps, so at most one step can still be owed.
            runner.IsTrue(creditedByPing >= lateFrames * 16_384 - 32 * 1024,
                $"the dropped bytes are credited to the connection window ({creditedByPing:N0} of {lateFrames * 16_384:N0})");
        });

        await runner.RunAsync("a malformed request is reset with PROTOCOL_ERROR and never reaches the handler", async () =>
        {
            // The handler is what forwards upstream in ProxyServer, so a request that never reaches
            // it never reaches an HTTP/1.1 origin either.
            var seen = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var harness = await Harness.StartAsync((request, ct) =>
            {
                seen.Enqueue(request.RequestTarget);
                return StubHandler(request, ct);
            });

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, harness.Port);
            var stream = tcp.GetStream();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ct = timeout.Token;

            await stream.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray(), ct);
            await Http2FrameWriter.WriteAsync(stream, Http2FrameType.Settings, Http2FrameFlags.None, 0, ReadOnlyMemory<byte>.Empty, ct);

            static List<(string, string)> Request(string path, params (string, string)[] extra) =>
                [(":method", "GET"), (":scheme", "http"), (":authority", "127.0.0.1"), (":path", path), .. extra];

            var malformed = new[]
            {
                Request("/crlf", ("x-a", "1\r\nx-injected: yes")),
                Request("/upper", ("X-A", "1")),
                Request("/te", ("transfer-encoding", "chunked")),
                Request("/path\r\nx-injected: yes"),
            };
            var streamId = 1;
            foreach (var fields in malformed)
            {
                await Http2FrameWriter.WriteHeadersAsync(stream, streamId, HpackEncoder.Encode(fields), endStream: true, 16384, ct);
                streamId += 2;
            }

            var resets = new Dictionary<int, uint>();
            var validAnswered = false;
            var pingAcked = false;
            long connectionCredit = 0;
            var valid = -1;
            async Task<bool> ReadOneAsync()
            {
                var frame = await Http2FrameReader.ReadRequiredAsync(stream, 1 << 20, ct);
                runner.IsTrue(frame.Type != Http2FrameType.GoAway, "the connection is not torn down");
                if (frame.Type == Http2FrameType.GoAway) return false;
                if (frame.Type == Http2FrameType.RstStream)
                    resets[frame.StreamId] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.Span);
                if (frame.Type == Http2FrameType.WindowUpdate && frame.StreamId == 0)
                    connectionCredit += System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(frame.Payload.Span) & 0x7fffffff;
                if (frame.Type == Http2FrameType.Ping && frame.HasFlag(Http2FrameFlags.Ack)) pingAcked = true;
                if (frame.Type == Http2FrameType.Headers && frame.StreamId == valid) validAnswered = true;
                return true;
            }

            // The outbox is FIFO, so once this PING is acked every stream-0 credit sent before the
            // DATA below (an initial window bump, say) has been counted, and only the delta is
            // attributed to the discarded body.
            await Http2FrameWriter.WriteAsync(stream, Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8], ct);
            while (!pingAcked) if (!await ReadOneAsync()) return;
            var creditBeforeData = connectionCredit;

            // Headers without END_STREAM, then a body filling the whole RFC-default connection
            // window: the DATA is dropped with the stream, but the connection window must still be
            // credited back, or one malformed request would stall every later stream.
            var withBody = streamId;
            var junk = new byte[65535];
            "GET /smuggled HTTP/1.1\r\n\r\n"u8.CopyTo(junk);
            await Http2FrameWriter.WriteHeadersAsync(stream, withBody, HpackEncoder.Encode(Request("/body", ("x-a", "\r\n"))), endStream: false, 16384, ct);
            await Http2FrameWriter.WriteDataAsync(stream, withBody, junk, endStream: true, 16384, ct);
            streamId += 2;

            valid = streamId;
            await Http2FrameWriter.WriteHeadersAsync(stream, valid, HpackEncoder.Encode(Request("/ok")), endStream: true, 16384, ct);

            // Credit goes out in 32 KiB steps (Http2Connection.WindowUpdateThreshold), so up to one
            // step short of the full body may still be pending; without the credit it would be 0.
            const int creditStep = 32 * 1024;
            long credited() => connectionCredit - creditBeforeData;
            while (!validAnswered || resets.Count < malformed.Length + 1 || credited() <= junk.Length - creditStep)
                if (!await ReadOneAsync()) return;
            runner.IsTrue(credited() > junk.Length - creditStep, "DATA on a reset stream is credited back to the connection window");

            for (var id = 1; id < valid; id += 2)
                runner.AreEqual((uint)Http2ErrorCode.ProtocolError, resets.GetValueOrDefault(id), $"stream {id} reset with PROTOCOL_ERROR");
            runner.IsTrue(!resets.ContainsKey(valid), "the valid stream is not reset");
            runner.AreEqual("/ok", string.Join(",", seen), "only the valid request reached the handler");
        });
    }

    private static byte[] Get(string path, string method = "GET") => HpackEncoder.Encode(
        [(":method", method), (":scheme", "https"), (":authority", "example.com"), (":path", path)]);

    /// <summary>HPACK literal with incremental indexing and a new name (RFC 7541 §6.2.1), no
    /// Huffman: the one representation that adds to the peer's dynamic table.</summary>
    private static byte[] LiteralIndexed(string name, string value)
    {
        var bytes = new List<byte> { 0x40 };
        foreach (var s in new[] { name, value })
        {
            // 7-bit-prefix integer (RFC 7541 §5.1) for the length, then the raw octets.
            var n = s.Length;
            if (n < 127) bytes.Add((byte)n);
            else
            {
                bytes.Add(127);
                for (n -= 127; n >= 128; n >>= 7) bytes.Add((byte)((n & 0x7f) | 0x80));
                bytes.Add((byte)n);
            }
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(s));
        }
        return [.. bytes];
    }

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

        private readonly long? _maxRequestBodyBytes;
        private readonly long? _maxBufferedRequestBytes;
        private readonly Action<string>? _log;

        private Harness(TcpListener listener, Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler,
            long? maxRequestBodyBytes, long? maxBufferedRequestBytes, Action<string>? log)
        {
            _listener = listener;
            _maxRequestBodyBytes = maxRequestBodyBytes;
            _maxBufferedRequestBytes = maxBufferedRequestBytes;
            _log = log;
            _acceptLoop = AcceptLoopAsync(handler);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public static Task<Harness> StartAsync(
            Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler, long? maxRequestBodyBytes = null,
            Action<string>? log = null, long? maxBufferedRequestBytes = null)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new Harness(listener, handler, maxRequestBodyBytes, maxBufferedRequestBytes, log));
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
                    var connection = new Http2Connection(c.GetStream(), async (r, t) => await handler(r, t).ConfigureAwait(false))
                    {
                        MaxRequestBodyBytes = _maxRequestBodyBytes ?? Http2Connection.DefaultMaxRequestBodyBytes,
                        MaxBufferedRequestBytes = _maxBufferedRequestBytes ?? Http2Connection.DefaultMaxBufferedRequestBytes,
                        Log = _log,
                    };
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
