using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Bounds on everything the HTTP/1.x parsers read from a hostile peer or file: chunk sizes, header
// counts, folded lines, start lines and status codes. Discovered by reflection like every other
// group (public static Task Run*Async(TestRunner)); there is no registration line to look for.
//
// One shared implementation sits behind every caller (HttpSyntax and HeaderCollection), so each
// caller gets the same malformed input here: the proxy's request and response paths, the Composer
// and AutoResponder parsers, the SAZ importer, the multipart inspector and the HTTP/2 adapter.
internal static class ParserBoundsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    // The most headers any caller accepts. Mirrors HeaderCollection.MaxParsedHeaders, spelled out so a
    // change to the cap has to change a test too.
    private const int Cap = 200;

    public static async Task RunAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-ParserBounds-Certs"));

        // ----------------------------------------------------------------- chunk sizes

        await runner.RunAsync("a malformed chunk size is a parse error, never an overflow or a bad allocation", async () =>
        {
            // "FFFFFFFF" and "80000000" used to parse (as hex, into an int) to a negative number, which
            // then reached `new byte[-1]` and threw OverflowException: not a parse error, so nothing
            // downstream recorded or answered it.
            var sizes = new[]
            {
                "FFFFFFFF", "80000000", "-1", "-0", "+5", "zz", "", " ", "0x10", "1_0", "1 0", "g",
                "FFFFFFFFFFFFFFFF", "7FFFFFFFFFFFFFFF", "10000000000000000", "10000000001",
                "7FFFFFFF", "100000000", new string('0', 70_000) + "5",
            };

            foreach (var size in sizes)
            {
                var wire = $"POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n{size}\r\nabc\r\n0\r\n\r\n";
                runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
                {
                    using var reader = ReaderFor(wire);
                    await HttpParser.ReadRequestAsync(reader, CancellationToken.None);
                }), $"request body, chunk size '{Show(size)}'");

                runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
                {
                    using var reader = ReaderFor($"{size}\r\nabc\r\n0\r\n\r\n");
                    await HttpBodyRelay.RelayAsync(reader, HttpBodyDescriptor.Chunked, Stream.Null,
                        rechunkDownstream: false, captureLimit: long.MaxValue, onProgress: null, CancellationToken.None);
                }), $"relayed body, chunk size '{Show(size)}'");
            }

            // The legal spellings still read: extension, padding, either case, leading zeros.
            foreach (var (wire, expected) in new[]
            {
                ("5\r\nhello\r\n0\r\n\r\n", "hello"),
                ("05;ext=1\r\nhello\r\n0\r\n\r\n", "hello"),
                (" 5 \t\r\nhello\r\n0\r\n\r\n", "hello"),
                ("A\r\n0123456789\r\n0\r\n\r\n", "0123456789"),
                ("a\r\n0123456789\r\n0000\r\n\r\n", "0123456789"),
            })
            {
                using var reader = ReaderFor($"POST / HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\n{wire}");
                var request = await HttpParser.ReadRequestAsync(reader, CancellationToken.None);
                runner.AreEqual(expected, Encoding.Latin1.GetString(request!.Body), $"chunk size line in '{Show(wire)}'");
            }
        });

        await runner.RunAsync("the shared chunk-size parser accepts exactly hex digits up to 1 TiB", () =>
        {
            foreach (var (line, expected) in new (string, long)[]
            {
                ("0", 0), ("00", 0), ("1", 1), ("a", 10), ("A", 10), ("ff", 255), ("FfFf", 0xFFFF), ("0000005", 5),
                ("FFFFFFFF", 0xFFFFFFFFL), ("80000000", 0x80000000L), ("10000000000", 1L << 40),
                ("5;name=value", 5), ("5 ;x", 5), ("5\t", 5), (" \t5 \t;ext", 5), ("5;", 5),
            })
                runner.IsTrue(HttpSyntax.TryParseChunkSize(line, out var size) && size == expected, $"'{line}' is {expected}");

            foreach (var line in new[]
            {
                "", " ", ";", ";5", "-1", "+1", "0x1", "1g", "g", "1 2", "1,2", "²", "１",
                "10000000001", "FFFFFFFFFFFF", "7FFFFFFFFFFFFFFF", "8000000000000000", "FFFFFFFFFFFFFFFF", "10000000000000000",
                "100000000000000000000000000000000000000000000000000000000000000000000000",
            })
                runner.IsTrue(!HttpSyntax.TryParseChunkSize(line, out var size) && size == 0, $"'{Show(line)}' is refused");

            runner.IsTrue(!HttpSyntax.TryParseStatusCode(null, out _), "no status text is refused");
            foreach (var (text, expected) in new[] { ("100", 100), ("101", 101), ("200", 200), ("599", 599) })
                runner.IsTrue(HttpSyntax.TryParseStatusCode(text, out var code) && code == expected, $"status '{text}'");
            foreach (var text in new[] { "099", "600", "000", "1", "10", "1000", "-10", "1 0", " 99", "99 ", "1e2", "0x1" })
                runner.IsTrue(!HttpSyntax.TryParseStatusCode(text, out var code) && code == 0, $"status '{text}' is refused");

            return Task.CompletedTask;
        });

        await runner.RunAsync("a client request with a malformed chunk size is answered 400 and recorded as a failed session", async () =>
        {
            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok", ct);
                return true;
            });
            using var harness = new Harness(ca, lines);

            foreach (var size in new[] { "FFFFFFFF", "80000000", "-1", "zz", "FFFFFFFFFFFFFFFF" })
            {
                using var client = await ConnectAsync(harness.Port);
                await WriteAsync(client.GetStream(),
                    $"POST http://127.0.0.1:{origin.Port}/up HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n"
                    + $"Transfer-Encoding: chunked\r\n\r\n{size}\r\nabc");
                var reply = await ReadAsync(client.GetStream(), null, Patience);

                runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 400", StringComparison.Ordinal),
                    $"chunk size '{size}' is answered 400 (got: {FirstLine(reply.Text)})");
                runner.IsTrue(reply.Eof, $"and the connection is closed after chunk size '{size}'");
            }

            runner.IsTrue(await Poll.UntilAsync(() => harness.Store.Snapshot().Count(s => s.State == SessionState.Failed) == 5),
                $"each of them left a failed session ({harness.Store.Snapshot().Length} sessions, "
                + $"{harness.Store.Snapshot().Count(s => s.State == SessionState.Failed)} failed)");
            var failed = harness.Store.Snapshot().First(s => s.State == SessionState.Failed);
            runner.AreEqual("POST", failed.Request?.Method,"the failed session carries the request");
            runner.AreEqual(400, failed.Response?.StatusCode ?? 0, "and the 400 that was sent");
            runner.IsTrue(!string.IsNullOrEmpty(failed.Error), "and says why");
            runner.AreEqual(0, origin.ConnectionCount, "nothing was forwarded to the origin");

            using var after = await ConnectAsync(harness.Port);
            await WriteAsync(after.GetStream(), Get(origin.Port, "/after"));
            var ok = await ReadAsync(after.GetStream(), "ok", Patience);
            runner.IsTrue(ok.Text.Contains("200 OK", StringComparison.Ordinal), "the proxy still serves the next client");
        });

        await runner.RunAsync("an origin's malformed chunk size fails the session cleanly and leaves the proxy serving", async () =>
        {
            var next = 0;
            var bodies = new[] { "zz\r\nabc\r\n", "-1\r\nabc\r\n", "FFFFFFFFFFFFFFFF\r\n", "FFFFFFFF\r\nabc" };
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                var body = bodies[Interlocked.Increment(ref next) - 1];
                await TestRawOrigin.WriteAsync(stream,
                    "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" + body, ct);
                return false;
            });
            using var harness = new Harness(ca);

            foreach (var body in bodies)
            {
                using var client = await ConnectAsync(harness.Port);
                await WriteAsync(client.GetStream(), Get(origin.Port, "/chunked"));
                var reply = await ReadAsync(client.GetStream(), null, Patience);
                runner.IsTrue(reply.Eof, $"the client's connection ends after '{Show(body)}'");
                runner.IsTrue(!reply.Text.EndsWith("0\r\n\r\n", StringComparison.Ordinal),
                    $"and is never handed a well-formed end of body after '{Show(body)}'");
            }

            runner.IsTrue(await Poll.UntilAsync(() => harness.Store.Snapshot().Count(s => s.State == SessionState.Failed) == bodies.Length),
                "every one of them is recorded as failed");
        });

        // ------------------------------------------------------------- header counts

        await runner.RunAsync("every header parser stops at the same header count", () =>
        {
            var atCap = HeaderBlock(Cap);
            var overCap = HeaderBlock(Cap + 1);

            runner.AreEqual(Cap, HeaderCollection.Parse(atCap).Count, "Parse accepts exactly the cap");
            runner.AreEqual("HttpParseException", ExceptionName(() => HeaderCollection.Parse(overCap)),
                "Parse refuses one more");
            runner.IsTrue(HeaderCollection.TryParse(atCap, out var parsed, out _) && parsed.Count == Cap,
                "TryParse accepts exactly the cap");
            runner.IsTrue(!HeaderCollection.TryParse(overCap, out _, out var error) && error.Length > 0,
                "TryParse refuses one more, with a reason");

            // The raw-wire parsers return a reason instead of throwing: their input is a file or a
            // text box the user supplied.
            var response = Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\n" + overCap + "\r\nbody");
            runner.IsTrue(!HttpWireFormat.TryParseResponse(response, out _, out var wireError) && wireError.Length > 0,
                "a response file with too many headers is refused with a reason");
            runner.IsTrue(HttpWireFormat.TryParseResponse(
                Encoding.Latin1.GetBytes("HTTP/1.1 200 OK\r\n" + atCap + "\r\nbody"), out var okResponse, out _)
                && okResponse.Headers.Count == Cap, "and one at the cap is read");

            runner.IsTrue(!RequestExecutor.TryParseRaw("GET http://h.test/ HTTP/1.1\n" + overCap.Replace("\r\n", "\n"),
                out _, out var rawError) && rawError.Length > 0,
                "the Composer's raw request with too many headers is refused with a reason");
            runner.IsTrue(RequestExecutor.TryParseRaw("GET http://h.test/ HTTP/1.1\n" + atCap.Replace("\r\n", "\n"),
                out var rawRequest, out _) && rawRequest.Headers.Count == Cap, "and one at the cap is read");

            // A multipart part is skipped rather than blowing up the inspector.
            var multipart = $"--B\r\nContent-Disposition: form-data; name=a\r\n{overCap}\r\nx\r\n"
                          + "--B\r\nContent-Disposition: form-data; name=b\r\n\r\ny\r\n--B--\r\n";
            var form = new HttpRequestData { Body = Encoding.Latin1.GetBytes(multipart) };
            form.Headers.Add("Content-Type", "multipart/form-data; boundary=B");
            var fields = WebFormParser.Parse(form);
            runner.AreEqual(1, fields.Count, "a multipart part with too many headers is skipped");
            runner.AreEqual("b", fields[0].Name, "and the others still read");

            return Task.CompletedTask;
        });

        await runner.RunAsync("the stream parser and the SAZ importer refuse too many headers too", async () =>
        {
            var overCap = HeaderBlock(Cap + 1);
            runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
            {
                using var reader = ReaderFor("GET /x HTTP/1.1\r\nHost: h\r\n" + overCap + "\r\n");
                await HttpParser.ReadRequestAsync(reader, CancellationToken.None);
            }), "a request with too many headers");
            runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
            {
                using var reader = ReaderFor("HTTP/1.1 200 OK\r\n" + overCap + "\r\n");
                await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None);
            }), "a response with too many headers");

            const string Get = "GET https://api.example.test/v1 HTTP/1.1\r\nHost: api.example.test\r\n\r\n";
            var path = SazImportHardeningTests.Write(SazImportHardeningTests.Zip(archive =>
            {
                SazImportHardeningTests.Add(archive, "raw/1_c.txt", "GET https://a.test/ HTTP/1.1\r\n" + overCap + "\r\n");
                SazImportHardeningTests.Add(archive, "raw/1_s.txt", "HTTP/1.1 200 OK\r\n\r\nok");
                SazImportHardeningTests.Add(archive, "raw/2_c.txt", Get);
                SazImportHardeningTests.Add(archive, "raw/2_s.txt", "HTTP/1.1 200 OK\r\n" + overCap + "\r\nok");
                SazImportHardeningTests.Add(archive, "raw/3_c.txt", Get);
                SazImportHardeningTests.Add(archive, "raw/3_s.txt", "HTTP/1.1 200 OK\r\nX: y\r\n\r\nok");
            }));
            try
            {
                var result = SazImporter.Import(path);
                runner.AreEqual(2, result.Sessions.Count, "the session whose request is bad is skipped, the rest import");
                runner.AreEqual(SazImportFailure.None, result.Failure, "the archive is not rejected");
                runner.IsTrue(result.Sessions.Any(s => s.Response is null), "a bad response degrades to no response");
                runner.IsTrue(result.Warnings.Count >= 2, "and both are reported");
            }
            finally { File.Delete(path); }
        });

        // --------------------------------------------------------------- folded lines

        await runner.RunAsync("a block of folded header lines is parsed in linear time and a head has a size cap", async () =>
        {
            // Appending each continuation to the previous value copied the whole value every time:
            // the work grew with the square of the line count.
            var folded = "X-Long: a\n" + string.Concat(Enumerable.Repeat(" a\n", 80_000));
            var watch = Stopwatch.StartNew();
            var parsed = HeaderCollection.Parse(folded);
            watch.Stop();
            runner.AreEqual(1, parsed.Count, "the folds join the one header");
            runner.AreEqual(1 + 2 * 80_000, parsed["X-Long"]!.Length, "and keep every line, separated by one space");
            runner.IsTrue(watch.ElapsedMilliseconds < 1500, $"in linear time ({watch.ElapsedMilliseconds} ms)");

            // A response head had no size bound at all: folds are not counted as headers, so an origin
            // could keep one header growing for ever. It is bounded like a request head now.
            watch.Restart();
            using (var reader = ReaderFor("HTTP/1.1 200 OK\r\nX-Long: a\r\n" + string.Concat(Enumerable.Repeat(" a\r\n", 60_000)) + "\r\n"))
            {
                var response = await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None);
                runner.AreEqual(1 + 2 * 60_000, response.Headers["X-Long"]!.Length, "a long folded response header within the cap is read");
            }
            watch.Stop();
            runner.IsTrue(watch.ElapsedMilliseconds < 1500, $"in linear time ({watch.ElapsedMilliseconds} ms)");

            runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
            {
                using var reader = ReaderFor("HTTP/1.1 200 OK\r\nX-Long: a\r\n" + string.Concat(Enumerable.Repeat(" a\r\n", 200_000)) + "\r\n");
                await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None);
            }), "a response head past the cap is refused");

            runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
            {
                using var reader = ReaderFor("GET / HTTP/1.1\r\nX-Long: a\r\n" + string.Concat(Enumerable.Repeat(" a\r\n", 40_000)) + "\r\n");
                await HttpParser.ReadRequestAsync(reader, CancellationToken.None);
            }), "and so is a request head past its own, smaller, cap");

            runner.AreEqual("HttpParseException", ExceptionName(() => HeaderCollection.Parse(
                "X: a\n" + string.Concat(Enumerable.Repeat(" a\n", 400_000)))), "Parse refuses a block past its cap");
        });

        // ----------------------------------------------------------------- start lines

        await runner.RunAsync("a status code outside 100-599 is refused by every response parser", async () =>
        {
            foreach (var bad in new[] { "99", "20", "0", "000", "099", "600", "999", "1000", "2000", "+200", "-1", "2x0", "²²²", "0200" })
            {
                runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
                {
                    using var reader = ReaderFor($"HTTP/1.1 {bad} Reason\r\nContent-Length: 0\r\n\r\n");
                    await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None);
                }), $"the stream parser refuses status '{bad}'");

                runner.IsTrue(!HttpWireFormat.TryParseResponse(
                    Encoding.Latin1.GetBytes($"HTTP/1.1 {bad} Reason\r\n\r\nbody"), out _, out var error) && error.Length > 0,
                    $"a response file with status '{bad}' is refused with a reason");
            }

            foreach (var good in new[] { "200", "204", "299", "404", "500", "599" })
            {
                using var reader = ReaderFor($"HTTP/1.1 {good} Reason\r\nContent-Length: 0\r\n\r\n");
                var response = await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None);
                runner.AreEqual(int.Parse(good), response.StatusCode, $"status '{good}' reads");
                runner.IsTrue(HttpWireFormat.TryParseResponse(
                    Encoding.Latin1.GetBytes($"HTTP/1.1 {good} Reason\r\n\r\nbody"), out var file, out _) && file.StatusCode == int.Parse(good),
                    $"and so does a file with status '{good}'");
            }

            // 1xx are interim: the next response is the one returned.
            using (var reader = ReaderFor("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n"))
                runner.AreEqual(200, (await HttpParser.ReadResponseAsync(reader, "POST", CancellationToken.None)).StatusCode,
                    "an interim 100 is still skipped");

            // The SAZ importer reports the entry instead of importing a 99999 or a 0.
            var path = SazImportHardeningTests.Write(SazImportHardeningTests.Zip(archive =>
            {
                var number = 0;
                foreach (var bad in new[] { "99", "600", "99999", "0" })
                {
                    number++;
                    SazImportHardeningTests.Add(archive, $"raw/{number}_c.txt", "GET https://a.test/ HTTP/1.1\r\nHost: a.test\r\n\r\n");
                    SazImportHardeningTests.Add(archive, $"raw/{number}_s.txt", $"HTTP/1.1 {bad} X\r\n\r\nok");
                }
            }));
            try
            {
                var result = SazImporter.Import(path);
                runner.AreEqual(4, result.Sessions.Count, "every request still imports");
                runner.IsTrue(result.Sessions.All(s => s.Response is null), "but no response with an out-of-range status is kept");
                runner.AreEqual(4, result.Warnings.Count, "and each is reported");
            }
            finally { File.Delete(path); }
        });

        await runner.RunAsync("a malformed start line is refused by every request and response parser", async () =>
        {
            var badRequests = new[]
            {
                "G@T / HTTP/1.1", "GE T / HTTP/1.1 junk", "GET /a\u0001b HTTP/1.1", "GET /a\u007Fb HTTP/1.1",
                "GET / HTTP/x", "GET / http/1.1", "GET / HTTP/1.1 junk", "GET / HTTP/11", "GET / HTTP/1.", "GET",
                "(GET) / HTTP/1.1", "GETé / HTTP/1.1",
            };
            foreach (var line in badRequests)
                runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
                {
                    using var reader = ReaderFor(line + "\r\nHost: h\r\n\r\n");
                    await HttpParser.ReadRequestAsync(reader, CancellationToken.None);
                }), $"request line '{Show(line)}'");

            foreach (var line in new[] { "GET / HTTP/1.1", "GET / HTTP/1.0", "OPTIONS * HTTP/1.1", "CONNECT h:443 HTTP/1.1", "M-SEARCH * HTTP/1.1", "GET /", "GET   /a   HTTP/1.1" })
            {
                using var reader = ReaderFor(line + "\r\nHost: h\r\n\r\n");
                runner.IsTrue(await HttpParser.ReadRequestAsync(reader, CancellationToken.None) is not null, $"request line '{line}' still reads");
            }

            foreach (var line in new[] { "HTTP/x 200 OK", "http/1.1 200 OK", "HTTP/1.1 200 O\u0001K", "HTTP/1.1 200 O\rK", "HTTP/1.1", "200 OK", "FOO 200 OK" })
                runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
                {
                    using var reader = ReaderFor(line + "\r\nContent-Length: 0\r\n\r\n");
                    await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None);
                }), $"status line '{Show(line)}'");

            foreach (var line in new[] { "HTTP/1.1 200 OK", "HTTP/1.1 200", "HTTP/1.0 404 Not Found At All", "HTTP/1.1 200 " })
            {
                using var reader = ReaderFor(line + "\r\nContent-Length: 0\r\n\r\n");
                runner.IsTrue((await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None)).StatusCode >= 200,
                    $"status line '{line}' still reads");
            }

            // The Composer is lenient about the version and the spacing, but not about the method or
            // the target.
            runner.IsTrue(!RequestExecutor.TryParseRaw("G@T http://h.test/ HTTP/1.1\nHost: h.test", out _, out var error) && error.Length > 0,
                "the Composer refuses a method that is not a token");
            runner.IsTrue(!RequestExecutor.TryParseRaw("GET http://h.test/a\u0001b HTTP/1.1\nHost: h.test", out _, out _),
                "and a target with a control character");
            runner.IsTrue(RequestExecutor.TryParseRaw("get   http://h.test/    whatever extra\nHost: h.test", out var composed, out _)
                          && composed.Method == "GET", "but takes odd spacing, a made-up version and the lowercase method");

            // SAZ: a bad method or status line skips that message, not the archive.
            var path = SazImportHardeningTests.Write(SazImportHardeningTests.Zip(archive =>
            {
                SazImportHardeningTests.Add(archive, "raw/1_c.txt", "G@T https://a.test/ HTTP/1.1\r\n\r\n");
                SazImportHardeningTests.Add(archive, "raw/2_c.txt", "GET https://a.test/ HTTP/1.1\r\n\r\n");
                SazImportHardeningTests.Add(archive, "raw/2_s.txt", "HTTP/1.1 200 O\u0001K\r\n\r\nok");
                SazImportHardeningTests.Add(archive, "raw/3_c.txt", "GET https://a.test/ HTTP/1.1\r\n\r\n");
                SazImportHardeningTests.Add(archive, "raw/3_s.txt", "HTTP/1.1 200 OK\r\n\r\nok");
            }));
            try
            {
                var result = SazImporter.Import(path);
                runner.AreEqual(2, result.Sessions.Count, "SAZ skips the session with the bad method");
                runner.AreEqual(1, result.Sessions.Count(s => s.Response is not null), "and drops only the response with the bad status line");
            }
            finally { File.Delete(path); }
        });

        await runner.RunAsync("an HTTP/2 :status outside 100-599 is refused", () =>
        {
            foreach (var bad in new[] { "000", "099", "600", "999", "20", "2000", "+20", "٢٠٠", "２００", " 20", "" })
                runner.AreEqual("HttpParseException", ExceptionName(() => Http2MessageAdapter.ToResponse([(":status", bad)])),
                    $":status '{bad}'");

            foreach (var good in new[] { "100", "200", "404", "599" })
                runner.AreEqual(int.Parse(good), Http2MessageAdapter.ToResponse([(":status", good)]).StatusCode, $":status '{good}'");

            return Task.CompletedTask;
        });
    }

    // ------------------------------------------------------------------ helpers

    private static string HeaderBlock(int count) =>
        string.Concat(Enumerable.Range(0, count).Select(i => $"X-H-{i}: v{i}\r\n"));

    private static HttpStreamReader ReaderFor(string wire) => new(new MemoryStream(Encoding.Latin1.GetBytes(wire)));

    private static string Show(string text) => text.Length <= 24 ? text : text[..24] + "...";

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

    private sealed class Harness : IDisposable
    {
        public Harness(CertificateAuthority ca, System.Collections.Concurrent.ConcurrentQueue<string>? log = null)
        {
            Store = new SessionStore();
            Proxy = new ProxyServer(new ProxyOptions { Port = 0 }, ca, Store);
            if (log is not null) Proxy.Log += (_, message) => log.Enqueue(message);
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

    /// <summary>Reads until <paramref name="until"/> has arrived (or, when null, until the peer closes),
    /// the limit passes, or the connection ends. Never throws on a closed peer.</summary>
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
}
