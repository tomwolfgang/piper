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

            var hugeHead = $"--B\r\nContent-Disposition: form-data; name=a\r\nX-Pad: {new string('p', 300_000)}\r\n\r\nx\r\n"
                         + "--B\r\nContent-Disposition: form-data; name=b\r\n\r\ny\r\n--B--\r\n";
            var huge = new HttpRequestData { Body = Encoding.Latin1.GetBytes(hugeHead) };
            huge.Headers.Add("Content-Type", "multipart/form-data; boundary=B");
            runner.AreEqual(1, WebFormParser.Parse(huge).Count, "and so is a part whose head is past the size cap");

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
            var parsed = HeaderCollection.Parse(folded);
            runner.AreEqual(1, parsed.Count, "the folds join the one header");
            runner.AreEqual(1 + 2 * 80_000, parsed["X-Long"]!.Length, "and keep every line, separated by one space");

            // Judged by how the time grows, not by a wall-clock limit that a slow machine would trip:
            // four times the lines takes about four times as long when linear and about sixteen times
            // as long when quadratic. The best of three runs each keeps a stray GC pause out of it.
            static double BestMilliseconds(int lines)
            {
                var block = "X-Long: a\n" + string.Concat(Enumerable.Repeat(" a\n", lines));
                var best = double.MaxValue;
                for (var run = 0; run < 3; run++)
                {
                    var clock = Stopwatch.StartNew();
                    HeaderCollection.Parse(block);
                    best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
                }
                return best;
            }

            var small = BestMilliseconds(20_000);
            var large = BestMilliseconds(80_000);
            runner.IsTrue(large < 9 * Math.Max(small, 5.0),
                $"four times the folded lines takes about four times as long, not sixteen ({small:0.0} ms, then {large:0.0} ms)");

            var watch = Stopwatch.StartNew();

            // A response head had no size bound at all: folds are not counted as headers, so an origin
            // could keep one header growing for ever. It is bounded like a request head now.
            watch.Restart();
            using (var reader = ReaderFor("HTTP/1.1 200 OK\r\nX-Long: a\r\n" + string.Concat(Enumerable.Repeat(" a\r\n", 60_000)) + "\r\n"))
            {
                var response = await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None);
                runner.AreEqual(1 + 2 * 60_000, response.Headers["X-Long"]!.Length, "a long folded response header within the cap is read");
            }
            watch.Stop();
            runner.IsTrue(watch.ElapsedMilliseconds < 15_000, $"and in bounded time ({watch.ElapsedMilliseconds} ms)");

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

        await runner.RunAsync("an HTTP/1 response status is any three digits from 100 to 999, and nothing else", async () =>
        {
            // 600-999 are not defined, but real origins send them (LinkedIn answers 999), and a debugging
            // proxy that turned such a reply into a 502 would hide the one thing worth looking at. They
            // are read, shown and relayed as the origin sent them; HTTP/2 and HTTP/3 stay at 100-599.
            foreach (var bad in new[] { "99", "20", "0", "000", "099", "1000", "2000", "+200", "-1", "2x0", "²²²", "0200" })
            {
                runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
                {
                    using var reader = ReaderFor($"HTTP/1.1 {bad} Reason\r\nContent-Length: 0\r\n\r\n");
                    await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None);
                }), $"the stream parser refuses status '{bad}'");

                runner.IsTrue(!HttpWireFormat.TryParseResponse(
                    Encoding.Latin1.GetBytes($"HTTP/1.1 {bad} Reason\r\n\r\nbody"), out _, out var error) && error.Length > 0,
                    $"a response file with status '{bad}' is refused with a reason");
                runner.IsTrue(!HttpWireFormat.TryParseResponse(
                    Encoding.Latin1.GetBytes($"HTTP/1.1 {bad} Reason\r\n\r\nbody"), out _, out var specific)
                    && specific.Contains("status code", StringComparison.Ordinal),
                    $"and the reason for '{bad}' names the status code, not a generic status-line failure (was: {specific})");
            }

            foreach (var good in new[] { "200", "204", "299", "404", "500", "599", "600", "700", "999" })
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
                foreach (var status in new[] { "99", "99999", "0", "1000", "600", "999" })
                {
                    number++;
                    SazImportHardeningTests.Add(archive, $"raw/{number}_c.txt", "GET https://a.test/ HTTP/1.1\r\nHost: a.test\r\n\r\n");
                    SazImportHardeningTests.Add(archive, $"raw/{number}_s.txt", $"HTTP/1.1 {status} X\r\n\r\nok");
                }
            }));
            try
            {
                var result = SazImporter.Import(path);
                runner.AreEqual(6, result.Sessions.Count, "every request still imports");
                runner.AreEqual(2, result.Sessions.Count(s => s.Response is not null), "only 600 and 999 keep their response");
                runner.AreEqual("600,999", string.Join(',', result.Sessions.Where(s => s.Response is not null).Select(s => s.Response!.StatusCode).Order()),
                    "as the origin sent them");
                runner.AreEqual(4, result.Warnings.Count, "and each response with a status outside 100-999 is reported");
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

    // ------------------------------------------------- what the proxy does with a refused message

    public static async Task RunProxyReplyAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-ParserBounds-Certs"));

        await runner.RunAsync("a request line with trailing whitespace is read; a malformed head is answered 400 and recorded", async () =>
        {
            // The old parser took "GET / HTTP/1.1 " (the version simply carried the space). The strict
            // version check must not turn that into a refusal; it also must trim the Composer's lenient version.
            foreach (var line in new[] { "GET / HTTP/1.1 ", "GET / HTTP/1.1\t", "GET / HTTP/1.1  \t ", "GET /  HTTP/1.1 " })
            {
                using var reader = ReaderFor(line + "\r\nHost: h\r\n\r\n");
                var request = await HttpParser.ReadRequestAsync(reader, CancellationToken.None);
                runner.AreEqual("HTTP/1.1", request?.HttpVersion, $"version of '{line.Replace("\t", "\\t")}'");
                runner.AreEqual("/", request?.RequestTarget, $"target of '{line.Replace("\t", "\\t")}'");
            }

            runner.IsTrue(RequestExecutor.TryParseRaw("GET http://h.test/ HTTP/1.1 extra words\nHost: h.test", out var composed, out _)
                          && composed.HttpVersion == "HTTP/1.1", "the Composer keeps only the first word as the version");
            runner.IsTrue(!RequestExecutor.TryParseRaw("GET http://h.test/ HT\u0001TP\nHost: h.test", out _, out _),
                "and refuses a version with a control character");

            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok", ct);
                return true;
            });
            using var harness = new Harness(ca);

            using (var good = await ConnectAsync(harness.Port))
            {
                await WriteAsync(good.GetStream(),
                    $"GET http://127.0.0.1:{origin.Port}/ok HTTP/1.1 \r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n");
                var reply = await ReadAsync(good.GetStream(), "ok", Patience);
                runner.IsTrue(reply.Text.Contains("200 OK", StringComparison.Ordinal),
                    $"the proxy serves a request line with a trailing space (got: {FirstLine(reply.Text)})");
            }

            var bad = new[]
            {
                "GET /a b HTTP/1.1\r\nHost: h\r\n\r\n",
                "G@T / HTTP/1.1\r\nHost: h\r\n\r\n",
                "GET / HTTP/x\r\nHost: h\r\n\r\n",
                "GET / HTTP/1.1\r\n" + HeaderBlock(Cap + 1) + "\r\n",
            };
            foreach (var wire in bad)
            {
                using var client = await ConnectAsync(harness.Port);
                await WriteAsync(client.GetStream(), wire);
                var reply = await ReadAsync(client.GetStream(), null, Patience);
                runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 400", StringComparison.Ordinal),
                    $"'{Show(wire)}' is answered 400 (got: {FirstLine(reply.Text)})");
                runner.IsTrue(reply.Eof, "and the connection is closed");
            }

            runner.IsTrue(await Poll.UntilAsync(() => harness.Store.Snapshot().Count(s => s.State == SessionState.Failed) == bad.Length),
                "each malformed head is recorded as a failed session");
            runner.IsTrue(harness.Store.Snapshot().Where(s => s.State == SessionState.Failed).All(s => s.Request is not null && s.Response?.StatusCode == 400),
                "with the 400 that was sent");
        });

        await runner.RunAsync("an HTTP/1 origin's 999 reaches the client and the session list as sent", async () =>
        {
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 999 Request denied\r\nContent-Length: 2\r\n\r\nno", ct);
                return true;
            });
            using var harness = new Harness(ca);
            using var client = await ConnectAsync(harness.Port);
            await WriteAsync(client.GetStream(), Get(origin.Port, "/linkedin"));
            var reply = await ReadAsync(client.GetStream(), "no", Patience);
            runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 999", StringComparison.Ordinal), $"the client gets the 999 (got: {FirstLine(reply.Text)})");
            runner.IsTrue(await Poll.UntilAsync(() => harness.Store.Snapshot().Any(s => s.State == SessionState.Complete && s.Response?.StatusCode == 999)),
                "and the session shows it, complete, not a failed 502");
        });

        await runner.RunAsync("a client that disconnects mid-request gets no 400 and leaves no failed session", async () =>
        {
            // The peer is gone, so a 400 recorded for it would be a reply that was never delivered.
            // Only a refusal of the framing itself (a bad chunk size, a conflicting length) earns one.
            var cut = new[]
            {
                ("a length-framed body cut short", "Content-Length: 100\r\n\r\nshort"),
                ("a chunked body cut inside a chunk", "Transfer-Encoding: chunked\r\n\r\n5\r\nhel"),
                ("a chunked body cut between chunks", "Transfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n"),
                ("a head cut in the headers", "X-Half: yes\r\n"),
            };

            foreach (var (what, rest) in cut)
            {
                var wire = $"POST /up HTTP/1.1\r\nHost: h\r\n{rest}";
                runner.AreEqual("HttpPeerClosedException", await ExceptionNameAsync(async () =>
                {
                    using var reader = ReaderFor(wire);
                    await HttpParser.ReadRequestAsync(reader, CancellationToken.None);
                }), $"the parser names {what} as the peer closing");
            }
            runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
            {
                using var reader = ReaderFor("POST /up HTTP/1.1\r\nHost: h\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\n");
                await HttpParser.ReadRequestAsync(reader, CancellationToken.None);
            }), "while a bad chunk size is a refusal, not a closed peer");

            var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var harness = new Harness(ca, lines);
            foreach (var (what, rest) in cut)
            {
                using var client = await ConnectAsync(harness.Port);
                var stream = client.GetStream();
                await WriteAsync(stream, $"POST http://127.0.0.1:1/up HTTP/1.1\r\nHost: 127.0.0.1:1\r\n{rest}");
                // The peer is gone as far as the proxy can tell, yet could still read a reply.
                try { client.Client.Shutdown(SocketShutdown.Send); }
                catch (Exception ex) when (ex is SocketException or InvalidOperationException) { /* already reset */ }
                var reply = await ReadAsync(stream, null, Patience);
                runner.AreEqual("", reply.Text, $"nothing is sent back for {what}");
            }

            runner.IsTrue(await Poll.UntilAsync(() => lines.Count(l => l.Contains("Protocol error", StringComparison.Ordinal)) >= cut.Length),
                "each is logged, as before");
            runner.AreEqual(0, harness.Store.Snapshot().Length, "and none is recorded as a session");
        });

        await runner.RunAsync("ambiguous request framing is refused, and never read as one request followed by another", async () =>
        {
            var ambiguous = new[]
            {
                ("differing duplicate Content-Length", "Content-Length: 5\r\nContent-Length: 40"),
                ("a Content-Length repeating itself in a list", "Content-Length: 5, 5"),
                ("a signed Content-Length", "Content-Length: +5"),
                ("a negative Content-Length", "Content-Length: -5"),
                ("a non-numeric Content-Length", "Content-Length: abc"),
                ("an empty Content-Length", "Content-Length:"),
                ("a Transfer-Encoding that is not chunked", "Transfer-Encoding: gzip"),
                ("Content-Length and Transfer-Encoding","Content-Length: 5\r\nTransfer-Encoding: chunked"),
                ("Transfer-Encoding and Content-Length", "Transfer-Encoding: chunked\r\nContent-Length: 5"),
                ("a Transfer-Encoding list ending in chunked, with a Content-Length", "Transfer-Encoding: gzip, chunked\r\nContent-Length: 5"),
            };

            foreach (var (what, headers) in ambiguous)
            {
                runner.AreEqual("HttpParseException", ExceptionName(() => HttpParser.DescribeRequestBody(HeaderCollection.Parse(headers))),
                    $"{what} is refused");
                runner.AreEqual("HttpParseException", await ExceptionNameAsync(async () =>
                {
                    using var reader = ReaderFor($"POST /x HTTP/1.1\r\nHost: h\r\n{headers}\r\n\r\nhello");
                    await HttpParser.ReadRequestAsync(reader, CancellationToken.None);
                }), $"and a request carrying {what} is refused whole");
            }

            // Identical copies say one thing and are allowed (RFC 9112 6.3); the other framings are unchanged.
            runner.AreEqual(5L, HttpParser.DescribeRequestBody(HeaderCollection.Parse("Content-Length: 5\r\nContent-Length: 5")).Length,
                "two identical Content-Length headers are one length");
            runner.AreEqual(HttpBodyFraming.Length, HttpParser.DescribeRequestBody(HeaderCollection.Parse("Content-Length: 0")).Framing, "Content-Length 0");
            runner.AreEqual(HttpBodyFraming.Chunked, HttpParser.DescribeRequestBody(HeaderCollection.Parse("Transfer-Encoding: chunked")).Framing, "chunked alone");
            runner.AreEqual(HttpBodyFraming.None, HttpParser.DescribeRequestBody(HeaderCollection.Parse("Host: h")).Framing, "no framing header");

            // A "Transfer-Encoding" that is only the folded continuation of another header is part of that
            // header's value, not a header: the Content-Length frames the body.
            using (var reader = ReaderFor("POST /x HTTP/1.1\r\nHost: h\r\nX-Foo: a\r\n Transfer-Encoding: chunked\r\nContent-Length: 5\r\n\r\nhello"))
            {
                var request = await HttpParser.ReadRequestAsync(reader, CancellationToken.None);
                runner.AreEqual("hello", Encoding.Latin1.GetString(request!.Body), "a folded Transfer-Encoding stays inert");
                runner.IsTrue(!request.Headers.Contains("Transfer-Encoding"), "and is not a header of its own");
            }

            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok", ct);
                return true;
            });
            using var harness = new Harness(ca);
            var attempts = new[]
            {
                "Content-Length: 5\r\nContent-Length: 40",
                "Content-Length: +5",
                "Content-Length: 5\r\nTransfer-Encoding: chunked",
                "Content-Length: 999999999999",
            };
            foreach (var headers in attempts)
            {
                using var client = await ConnectAsync(harness.Port);
                await WriteAsync(client.GetStream(),
                    $"POST http://127.0.0.1:{origin.Port}/s HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n{headers}\r\n\r\nhello"
                    + Get(origin.Port, "/smuggled"));
                var reply = await ReadAsync(client.GetStream(), null, Patience);
                runner.IsTrue(reply.Text.StartsWith("HTTP/1.1 400", StringComparison.Ordinal),
                    $"'{headers.Replace("\r\n", " | ")}' is answered 400 (got: {FirstLine(reply.Text)})");
                runner.IsTrue(reply.Eof && !reply.Text.Contains("200 OK", StringComparison.Ordinal),
                    "the bytes after it are not served as a second request");
            }

            runner.IsTrue(await Poll.UntilAsync(() => harness.Store.Snapshot().Count(s => s.State == SessionState.Failed) == attempts.Length),
                "each is recorded as a failed session");
            runner.AreEqual(0, origin.ConnectionCount, "and nothing reached the origin");
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
