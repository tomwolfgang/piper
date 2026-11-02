using System.Net;
using System.Net.Sockets;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// HTTP/1.1 field strictness and the per-body caps on chunk-size lines and trailers (roadmap S13).
//
// Piper re-emits every header it parsed with HeaderCollection.ToRawString, so a field the next hop
// reads differently from Piper (a lone CR it ends a line on, a NUL, a name with a space in it) is a
// smuggling vector. The proxy path (client requests, origin responses) refuses them, with the same
// rules the HTTP/2 and HTTP/3 adapter already applies; the Composer, SAZ import and AutoResponder
// response files stay lenient, because what a user typed or archived is the point there.
internal static class HeaderStrictnessTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    // Mirrors HttpParser's constants, spelled out so a change to a cap has to change a test too.
    private const int ChunkLineFreeBytes = 32;
    private const int MaxChunkExtensionBytes = 16 * 1024;
    private const int MaxTrailerBytes = 64 * 1024;

    /// <summary>Header lines (as they sit in a request head, CRLF excluded) every strict parser refuses,
    /// with the HTTP/2 field each one would be on that wire.</summary>
    private static readonly (string Line, string H2Name, string H2Value, string What)[] Malformed =
    [
        ("X: a\rTransfer-Encoding: chunked", "x", "a\rTransfer-Encoding: chunked", "a lone CR in a value"),
        ("X: a\0b", "x", "a\0b", "a NUL in a value"),
        ("X: \0", "x", "\0", "a value that is only a NUL"),
        ("Bad Name: v", "bad name", "v", "a space inside a name"),
        ("X(Y): v", "x(y)", "v", "a delimiter in a name"),
        ("X/Y: v", "x/y", "v", "a slash in a name"),
        ("X\u0001: v", "x\u0001", "v", "a control character in a name"),
        ("X\u00e9: v", "x\u00e9", "v", "a non-ASCII byte in a name"),
        ("\"X\": v", "\"x\"", "v", "a quoted name"),
        (":x: v", ":x", "v", "a name that starts with a colon"),
        ("X\r: v", "x\r", "v", "a CR at the end of a name"),
    ];

    public static async Task RunAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-HeaderStrictness-Certs"));

        // ------------------------------------------------------------------ field rules

        await runner.RunAsync("a strict request parse refuses CR or NUL in a value and a name that is not a token", async () =>
        {
            foreach (var (line, _, _, what) in Malformed)
            {
                runner.IsTrue(await ThrowsParseAsync(() => ReadRequestAsync($"GET / HTTP/1.1\r\nHost: h\r\n{line}\r\n\r\n")),
                    $"request with {what} is refused");
                runner.IsTrue(await ThrowsParseAsync(() => ReadResponseAsync($"HTTP/1.1 200 OK\r\n{line}\r\nContent-Length: 0\r\n\r\n")),
                    $"response with {what} is refused");
            }

            // RFC 9112 5.1: whitespace between a request's field name and the colon is refused (400).
            runner.IsTrue(await ThrowsParseAsync(() => ReadRequestAsync("GET / HTTP/1.1\r\nHost: h\r\nX : v\r\n\r\n")),
                "a request with whitespace before the colon is refused");
            runner.IsTrue(await ThrowsParseAsync(() => ReadRequestAsync("GET / HTTP/1.1\r\nHost : h\r\n\r\n")),
                "a request Host with whitespace before the colon is refused");

            // ...and a proxy removes it from a response instead (same section).
            var response = await ReadResponseAsync("HTTP/1.1 200 OK\r\nX-Spaced \t: v\r\nContent-Length: 0\r\n\r\n");
            runner.AreEqual("v", response.Headers["X-Spaced"], "a response's whitespace before the colon is removed");
            runner.IsTrue(!response.HeadAsText().Contains("X-Spaced ", StringComparison.Ordinal), "and never re-emitted");
        });

        await runner.RunAsync("a strict parse keeps every legal field as it was sent", async () =>
        {
            var request = await ReadRequestAsync(
                "GET / HTTP/1.1\r\nHost: h\r\nX-Tab: a\tb\r\nX-Obs: caf\u00e9\r\nX-Empty:\r\n!#$%&'*+-.^_`|~09azAZ: tchar\r\n"
                + "X-Ctl: a\u0001b\u007f\r\nX-Pad:   v   \r\n\r\n");
            runner.AreEqual("a\tb", request.Headers["X-Tab"], "a tab inside a value");
            runner.AreEqual("caf\u00e9", request.Headers["X-Obs"], "obs-text in a value");
            runner.AreEqual("", request.Headers["X-Empty"], "an empty value");
            runner.AreEqual("tchar", request.Headers["!#$%&'*+-.^_`|~09azAZ"], "every tchar in a name");
            // Other controls are not field-vchar either, but RFC 9110 5.5 names only CR, LF and NUL as
            // the ones a recipient must act on, and the HTTP/2 adapter lets the same set through.
            runner.AreEqual("a\u0001b\u007f", request.Headers["X-Ctl"], "other control characters, as on HTTP/2");
            runner.AreEqual("v", request.Headers["X-Pad"], "surrounding whitespace is OWS, not part of the value");
        });

        await runner.RunAsync("obs-fold is still replaced with a space in a strict parse, and a folded CR or NUL is refused", async () =>
        {
            // RFC 9112 5.2 lets a server or proxy either refuse obs-fold (400) or replace it with SP. Piper
            // has always replaced it; that stays, on purpose, so old clients keep working.
            var request = await ReadRequestAsync("GET / HTTP/1.1\r\nHost: h\r\nX-Fold: a\r\n  b\r\n\tc\r\n\r\n");
            runner.AreEqual("a b c", request.Headers["X-Fold"], "folded lines join with one space");
            runner.IsTrue(request.Headers.ToRawString().Contains("X-Fold: a b c\r\n", StringComparison.Ordinal), "and are re-emitted on one line");

            runner.IsTrue(await ThrowsParseAsync(() => ReadRequestAsync("GET / HTTP/1.1\r\nHost: h\r\nX-Fold: a\r\n b\rTransfer-Encoding: chunked\r\n\r\n")),
                "a CR in a folded continuation is refused");
            runner.IsTrue(await ThrowsParseAsync(() => ReadRequestAsync("GET / HTTP/1.1\r\nHost: h\r\nX-Fold: a\r\n b\0\r\n\r\n")),
                "a NUL in a folded continuation is refused");
            runner.IsTrue(await ThrowsParseAsync(() => ReadRequestAsync("GET / HTTP/1.1\r\n Host: h\r\n\r\n")),
                "a head that starts with a folded line is still refused");
        });

        await runner.RunAsync("the HTTP/2 adapter refuses the same fields as the HTTP/1.1 parser", () =>
        {
            foreach (var (_, name, value, what) in Malformed)
            {
                runner.AreEqual("HttpParseException", ExceptionName(() => Http2MessageAdapter.ToRequest(
                [
                    (":method", "GET"), (":scheme", "https"), (":authority", "h"), (":path", "/"), (name, value),
                ])), $"an h2 request with {what} is refused");

                // A response's unknown pseudo-header is dropped rather than refused (never relayed): C8b.
                if (name.StartsWith(':')) continue;
                runner.AreEqual("HttpParseException", ExceptionName(() => Http2MessageAdapter.ToResponse(
                    [(":status", "200"), (name, value)])), $"an h2 response with {what} is refused");
            }

            runner.AreEqual("a\u0001b", Http2MessageAdapter.ToRequest(
                [(":method", "GET"), (":scheme", "https"), (":authority", "h"), (":path", "/"), ("x-ctl", "a\u0001b")])
                .Headers["x-ctl"], "and lets the same other control characters through");
            return Task.CompletedTask;
        });

        await runner.RunAsync("lenient parsing (Composer, SAZ import, response files) keeps what the user wrote", () =>
        {
            runner.IsTrue(HeaderCollection.TryParse("Bad Name: v\nX: a\rb\nY: c\0d", out var headers, out var error), $"parses ({error})");
            runner.AreEqual("v", headers["Bad Name"], "a name with a space");
            runner.AreEqual("a\rb", headers["X"], "a CR in a value");
            runner.AreEqual("c\0d", headers["Y"], "a NUL in a value");

            runner.IsTrue(RequestExecutor.TryParseRaw("GET http://h/ HTTP/1.1\r\nBad Name: v\r\n\r\n", out var raw, out error),
                $"the Composer's raw editor still takes a non-token name ({error})");
            runner.AreEqual("v", raw.Headers["Bad Name"], "and keeps it");

            runner.IsTrue(HttpWireFormat.TryParseResponse(Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\nBad Name: v\r\n\r\n"), out var file, out error),
                $"a response file with a non-token name still loads ({error})");
            runner.AreEqual("v", file.Headers["Bad Name"], "and keeps it");
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------------ the proxy path

        await runner.RunAsync("a client request with a malformed field is answered 400, recorded as failed and never forwarded", async () =>
        {
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok", ct);
                return true;
            });
            using var harness = new Harness(ca);

            var lines = Malformed.Select(m => m.Line).Append("X : v").ToArray();
            foreach (var line in lines)
            {
                using var client = await ConnectAsync(harness.Port);
                await WriteAsync(client.GetStream(),
                    $"POST http://127.0.0.1:{origin.Port}/s HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n{line}\r\n"
                    + "Content-Length: 5\r\n\r\n0\r\n\r\n" + Get(origin.Port, "/smuggled"));
                var reply = await ReadAsync(client.GetStream(), null, Patience);
                runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 400", StringComparison.Ordinal),
                    $"'{Show(line)}' is answered 400 (got: {FirstLine(reply.Text)})");
                runner.IsTrue(reply.Eof && !reply.Text.Contains("200 OK", StringComparison.Ordinal), $"and closed after '{Show(line)}'");
            }

            runner.IsTrue(await Poll.UntilAsync(() => harness.Store.Snapshot().Count(s => s.State == SessionState.Failed) == lines.Length),
                $"each one is a failed session ({harness.Store.Snapshot().Count(s => s.State == SessionState.Failed)} of {lines.Length})");
            var failed = harness.Store.Snapshot().First(s => s.State == SessionState.Failed);
            runner.AreEqual(400, failed.Response?.StatusCode ?? 0, "carrying the 400 that was sent");
            runner.IsTrue(failed.Error?.Contains("malformed", StringComparison.OrdinalIgnoreCase) == true, $"and a clear error ({failed.Error})");
            runner.IsTrue(harness.Store.Snapshot().All(s => s.Error?.Contains('\0') != true && s.Error?.Contains('\r') != true),
                "no error text carries the hostile bytes");
            runner.AreEqual(0, origin.ConnectionCount, "and nothing reached the origin");
        });

        await runner.RunAsync("an origin response with a malformed field is a 502 and never reaches the client", async () =>
        {
            var next = 0;
            var lines = Malformed.Select(m => m.Line).ToArray();
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                var line = lines[Interlocked.Increment(ref next) - 1];
                await TestRawOrigin.WriteAsync(stream, $"HTTP/1.1 200 OK\r\n{line}\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok", ct);
                return false;
            });
            using var harness = new Harness(ca);

            foreach (var line in lines)
            {
                using var client = await ConnectAsync(harness.Port);
                await WriteAsync(client.GetStream(), Get(origin.Port, "/r"));
                var reply = await ReadAsync(client.GetStream(), null, Patience);
                runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 502", StringComparison.Ordinal),
                    $"'{Show(line)}' from the origin is a 502 (got: {FirstLine(reply.Text)})");
                runner.IsTrue(!reply.Text.Contains("200 OK", StringComparison.Ordinal), "and the origin's head is not relayed");
            }

            runner.IsTrue(await Poll.UntilAsync(() => harness.Store.Snapshot().Count(s => s.State == SessionState.Failed) == lines.Length),
                "each one is a failed session");
        });

        await runner.RunAsync("the Composer still sends a lenient request and shows a lenient response", async () =>
        {
            string? seen = null;
            await using var origin = new TestRawOrigin(async (head, stream, ct) =>
            {
                seen = head;
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nBad Name: v\r\nX-Cr: a\rb\r\nContent-Length: 2\r\n\r\nok", ct);
                return false;
            });

            var store = new SessionStore();
            var executor = new RequestExecutor(new ProxyOptions(), store);
            runner.IsTrue(RequestExecutor.TryParseRaw(
                $"GET http://127.0.0.1:{origin.Port}/c HTTP/1.1\r\nOdd Name: x\r\nX-Nul: a\0b\r\n\r\n", out var request, out var error),
                $"the raw request parses ({error})");
            runner.AreEqual("a\0b", request.Headers["X-Nul"], "the raw request keeps the NUL");

            // The NUL is checked on the bytes the Composer writes, not on what reaches the test origin:
            // a loopback HTTP scanner (ESET on the dev machine) drops a NUL and what follows it.
            var written = new MemoryStream();
            await request.Clone().WriteOriginFormAsync(written, CancellationToken.None);
            runner.IsTrue(Encoding.Latin1.GetString(written.ToArray()).Contains("\r\nX-Nul: a\0b\r\n", StringComparison.Ordinal),
                "the Composer writes the NUL as typed");

            var session = await executor.ExecuteAsync(request);
            runner.AreEqual(SessionState.Complete, session.State, $"the exchange completes ({session.Error})");
            runner.IsTrue(seen?.Contains("\r\nOdd Name: x\r\n", StringComparison.Ordinal) == true, "the non-token name went out as typed");
            runner.AreEqual("v", session.Response?.Headers["Bad Name"], "the response's non-token name is shown");
            runner.AreEqual("a\rb", session.Response?.Headers["X-Cr"], "and so is its CR");
        });

        // ------------------------------------------------------------------ chunk-size lines

        await runner.RunAsync("chunk-size line bytes are capped per body, extensions and padding included", async () =>
        {
            runner.AreEqual(ChunkLineFreeBytes, HttpParser.ChunkLineFreeBytes, "the free bytes per size line are the ones tested here");
            runner.AreEqual(MaxChunkExtensionBytes, HttpParser.MaxChunkExtensionBytes, "and so is the extension cap");
            runner.AreEqual(MaxTrailerBytes, HttpParser.MaxTrailerBytes, "and the trailer cap");

            // Four chunks whose lines carry exactly the cap between them, then the same plus one byte.
            var atCap = ChunkedBody(MaxChunkExtensionBytes / 4, MaxChunkExtensionBytes / 4, MaxChunkExtensionBytes / 4, MaxChunkExtensionBytes / 4);
            var overCap = ChunkedBody(MaxChunkExtensionBytes / 4, MaxChunkExtensionBytes / 4, MaxChunkExtensionBytes / 4, MaxChunkExtensionBytes / 4 + 1);
            var oneLineOver = ChunkedBody(MaxChunkExtensionBytes + 1);

            var request = await ReadRequestAsync(ChunkedRequest(atCap));
            runner.AreEqual("abcd", Encoding.Latin1.GetString(request.Body), "extensions adding up to the cap are read");
            runner.IsTrue(await ThrowsParseAsync(() => ReadRequestAsync(ChunkedRequest(overCap))), "one byte more is refused");
            runner.IsTrue(await ThrowsParseAsync(() => ReadRequestAsync(ChunkedRequest(oneLineOver))), "and so is one line over the cap");

            runner.AreEqual("abcd", Encoding.Latin1.GetString((await RelayAsync(atCap)).Captured), "the relay reads the cap");
            runner.IsTrue(await ThrowsParseAsync(() => RelayAsync(overCap)), "and refuses one byte more");
            runner.IsTrue(await ThrowsParseAsync(() => RelayAsync(oneLineOver)), "and one line over the cap");

            // Leading zeros and padding are part of the same budget, so neither is a way round it.
            var zeros = string.Concat(Enumerable.Repeat(new string('0', 8 * 1024) + "1\r\nx\r\n", 3)) + "0\r\n\r\n";
            runner.IsTrue(await ThrowsParseAsync(() => ReadRequestAsync(ChunkedRequest(zeros))), "leading zeros count");
            var padded = string.Concat(Enumerable.Repeat("1" + new string(' ', 8 * 1024) + "\r\nx\r\n", 3)) + "0\r\n\r\n";
            runner.IsTrue(await ThrowsParseAsync(() => RelayAsync(padded)), "padding counts");

            // A long stream of ordinary chunks is never cut: a plain size line costs nothing.
            var many = string.Concat(Enumerable.Repeat("0000000000000001\r\nx\r\n", 20_000)) + "0\r\n\r\n";
            runner.AreEqual(20_000L, (await RelayAsync(many)).TotalBytes, "20,000 plain chunks relay whole");
            runner.AreEqual(20_000, (await ReadRequestAsync(ChunkedRequest(many))).Body.Length, "and read whole");
        });

        await runner.RunAsync("trailer bytes are capped per body", async () =>
        {
            var underCap = Trailers(MaxTrailerBytes);
            var overCap = Trailers(MaxTrailerBytes + 1);

            var request = await ReadRequestAsync(ChunkedRequest("1\r\nx\r\n0\r\n" + underCap + "\r\n"));
            runner.AreEqual("x", Encoding.Latin1.GetString(request.Body), "trailers adding up to the cap are read and dropped");
            runner.AreEqual("HttpTooLargeException", await ExceptionNameAsync(() => ReadRequestAsync(ChunkedRequest("1\r\nx\r\n0\r\n" + overCap + "\r\n"))),
                "one byte more is refused as too large");
            runner.AreEqual(1L, (await RelayAsync("1\r\nx\r\n0\r\n" + underCap + "\r\n")).TotalBytes, "the relay reads the cap");
            runner.IsTrue(await ThrowsParseAsync(() => RelayAsync("1\r\nx\r\n0\r\n" + overCap + "\r\n")), "and refuses one byte more");
        });

        // ------------------------------------------------------------------ statuses for over-cap requests

        await runner.RunAsync("an over-cap request gets the status RFC 9110 names for it", async () =>
        {
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok", ct);
                return true;
            });
            using var harness = new Harness(ca);
            var target = $"http://127.0.0.1:{origin.Port}/big";
            var host = $"Host: 127.0.0.1:{origin.Port}\r\n";

            var cases = new (string Wire, int Status, string What)[]
            {
                ($"GET {target}?{new string('q', 70_000)} HTTP/1.1\r\n{host}\r\n", 414, "a request line over the head cap"),
                ($"GET {target} HTTP/1.1\r\n{host}X-Big: {new string('v', 70_000)}\r\n\r\n", 431, "one header line over the head cap"),
                ($"GET {target} HTTP/1.1\r\n{host}{Headers(40, 2_000)}\r\n", 431, "a head over its byte cap"),
                ($"GET {target} HTTP/1.1\r\n{host}{Headers(201, 1)}\r\n", 431, "more headers than the cap"),
                ($"POST {target} HTTP/1.1\r\n{host}Transfer-Encoding: chunked\r\n\r\n1\r\nx\r\n0\r\n{Trailers(MaxTrailerBytes + 1)}\r\n", 431, "trailers over their cap"),
                ($"POST {target} HTTP/1.1\r\n{host}Content-Length: 999999999999\r\n\r\n", 413, "a Content-Length no array holds"),
                ($"POST {target} HTTP/1.1\r\n{host}Transfer-Encoding: chunked\r\n\r\n{new string('F', 10)}\r\n", 413, "a chunk no array holds"),
                ($"POST {target} HTTP/1.1\r\n{host}Transfer-Encoding: chunked\r\n\r\n{ChunkedBody(MaxChunkExtensionBytes + 1)}", 400, "chunk extensions over their cap"),
            };

            for (var i = 0; i < cases.Length; i++)
            {
                var (wire, status, what) = cases[i];
                using var client = await ConnectAsync(harness.Port);
                try { await WriteAsync(client.GetStream(), wire); }
                catch (IOException) { /* the proxy may refuse before the whole request is written */ }
                var reply = await ReadAsync(client.GetStream(), null, Patience);

                // The status is checked on the session, which records the reply that was sent. On the wire
                // the reply can be lost: the proxy refuses as soon as a cap is crossed and closes with the
                // rest of the request unread, and Windows then resets the connection, which discards a
                // reply the client has not read yet (a ProxyServer behaviour for every refusal; see the PR).
                var count = i + 1;
                runner.IsTrue(await Poll.UntilAsync(() => harness.Store.Snapshot().Count(s => s.State == SessionState.Failed) == count),
                    $"{what} is a failed session");
                var session = harness.Store.Snapshot().Last(s => s.State == SessionState.Failed);
                runner.AreEqual(status, session.Response?.StatusCode ?? 0, $"{what} is answered {status}");
                runner.IsTrue(reply.Text.Length == 0 || reply.Text.StartsWith($"HTTP/1.1 {status}", StringComparison.Ordinal),
                    $"and the client sees that or nothing (got: {FirstLine(reply.Text)})");
            }

            runner.AreEqual(0, origin.ConnectionCount, "nothing reached the origin");
        });
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>A chunked body of one-byte chunks ("a", "b", ...), the n-th size line carrying
    /// <c>extras[n]</c> bytes past <see cref="ChunkLineFreeBytes"/>.</summary>
    private static string ChunkedBody(params int[] extras)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < extras.Length; i++)
        {
            var line = "1;e=" + new string('x', ChunkLineFreeBytes + extras[i] - 4);
            sb.Append(line).Append("\r\n").Append((char)('a' + i)).Append("\r\n");
        }
        return sb.Append("0\r\n\r\n").ToString();
    }

    /// <summary>Trailer lines adding up to exactly <paramref name="bytes"/>, CRLFs included.</summary>
    private static string Trailers(int bytes)
    {
        var sb = new StringBuilder();
        var left = bytes;
        for (var i = 0; left > 0; i++)
        {
            var prefix = $"T{i}: ";
            var line = Math.Min(left, 16 * 1024);
            if (left - line is > 0 and < 16) line -= 16; // never leave a remainder too short for a line
            sb.Append(prefix).Append('v', line - prefix.Length - 2).Append("\r\n");
            left -= line;
        }
        return sb.ToString();
    }

    private static string Headers(int count, int valueLength) =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"X-H-{i}: {new string('v', valueLength)}\r\n"));

    private static string ChunkedRequest(string body) =>
        $"POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n{body}";

    private static async Task<HttpRequestData> ReadRequestAsync(string wire)
    {
        using var reader = new HttpStreamReader(new MemoryStream(Encoding.Latin1.GetBytes(wire)));
        return await HttpParser.ReadRequestAsync(reader, CancellationToken.None)
               ?? throw new InvalidOperationException("no request");
    }

    private static async Task<HttpResponseData> ReadResponseAsync(string wire)
    {
        using var reader = new HttpStreamReader(new MemoryStream(Encoding.Latin1.GetBytes(wire)));
        var (head, _) = await HttpParser.ReadResponseHeadAsync(reader, "GET", CancellationToken.None);
        return head;
    }

    private static async Task<HttpBodyRelay.Result> RelayAsync(string body)
    {
        using var reader = new HttpStreamReader(new MemoryStream(Encoding.Latin1.GetBytes(body)));
        return await HttpBodyRelay.RelayAsync(reader, HttpBodyDescriptor.Chunked, Stream.Null,
            rechunkDownstream: false, captureLimit: long.MaxValue, onProgress: null, CancellationToken.None);
    }

    private static async Task<bool> ThrowsParseAsync(Func<Task> action)
    {
        try { await action(); return false; }
        catch (HttpParseException) { return true; }
    }

    private static string ExceptionName(Action action)
    {
        try { action(); return "(none)"; }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    private static async Task<string> ExceptionNameAsync(Func<Task> action)
    {
        try { await action(); return "(none)"; }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    private static string Show(string text)
    {
        var shown = text.Replace("\r", "\\r").Replace("\0", "\\0");
        return shown.Length <= 32 ? shown : shown[..32] + "...";
    }

    private sealed class Harness : IDisposable
    {
        public Harness(CertificateAuthority ca)
        {
            Store = new SessionStore();
            Proxy = new ProxyServer(new ProxyOptions { Port = 0 }, ca, Store);
            Proxy.Start();
            Port = Proxy.Endpoint!.Port;
        }

        public ProxyServer Proxy { get; }
        public int Port { get; }
        public SessionStore Store { get; }

        public void Dispose() => Proxy.StopAsync().GetAwaiter().GetResult();
    }

    private static string Get(int port, string path) =>
        $"GET http://127.0.0.1:{port}{path} HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\n\r\n";

    private static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }

    private static Task WriteAsync(Stream stream, string text) =>
        stream.WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

    private static string FirstLine(string text) =>
        text.Split('\n', 2)[0].TrimEnd('\r') is { Length: > 0 } line ? line : "(nothing)";

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
            catch (IOException) { return (text.ToString(), true); }
            if (n == 0) return (text.ToString(), true);
            text.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }
        return (text.ToString(), false);
    }
}
