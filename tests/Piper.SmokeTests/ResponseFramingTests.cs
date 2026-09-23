using System.Text;
using Piper.Core.Http;

// Parser-level conformance for HTTP/1.1 message framing (RFC 9112 6.3), over a MemoryStream
// rather than a socket: framing is decided entirely by the header block, and malformed input is
// far easier to express as literal wire bytes than to coax out of a real origin server.
//
// These tests exist to pin the framing rules down *before* the response path stops buffering.
// The desync assertions are the important ones: almost every framing mistake shows up not as a
// wrong body but as the *next* message on a keep-alive connection being misparsed.
internal static class ResponseFramingTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("body framing is classified from the header block", () =>
        {
            // Bodiless statuses win over every framing header. An origin answering HEAD, 204 or 304
            // routinely states the Content-Length a GET would have returned; reading that many bytes
            // would swallow whatever comes next on the connection.
            runner.AreEqual(HttpBodyFraming.None,
                Describe("Content-Length: 1234", "HEAD", 200).Framing, "HEAD has no body");
            runner.AreEqual(HttpBodyFraming.None,
                Describe("Content-Length: 1234", "GET", 204).Framing, "204 has no body");
            runner.AreEqual(HttpBodyFraming.None,
                Describe("Content-Length: 1234", "GET", 304).Framing, "304 has no body");
            runner.AreEqual(HttpBodyFraming.None,
                Describe("Transfer-Encoding: chunked", "head", 200).Framing, "the method test ignores case");

            runner.AreEqual(HttpBodyFraming.Length,
                Describe("Content-Length: 5", "GET", 200).Framing, "Content-Length frames by length");
            runner.AreEqual(5L,
                Describe("Content-Length: 5", "GET", 200).Length, "and carries the length");

            runner.AreEqual(HttpBodyFraming.Chunked,
                Describe("Transfer-Encoding: chunked", "GET", 200).Framing, "chunked frames by chunks");

            // RFC 9112 6.3: Transfer-Encoding wins, and the Content-Length must not be honoured.
            runner.AreEqual(HttpBodyFraming.Chunked,
                Describe("Content-Length: 5\r\nTransfer-Encoding: chunked", "GET", 200).Framing,
                "Transfer-Encoding beats Content-Length");

            runner.AreEqual(HttpBodyFraming.UntilClose,
                Describe("Content-Type: text/plain", "GET", 200).Framing,
                "a response with no framing header runs until the connection closes");

            // A request cannot be delimited by the connection closing: the client still has to read
            // the answer on that same connection.
            runner.AreEqual(HttpBodyFraming.None,
                HttpParser.DescribeRequestBody(HeaderCollection.Parse("Content-Type: text/plain")).Framing,
                "a request with no framing header has no body");
            runner.AreEqual(HttpBodyFraming.Length,
                HttpParser.DescribeRequestBody(HeaderCollection.Parse("Content-Length: 9")).Framing,
                "a request honours Content-Length");

            return Task.CompletedTask;
        });

        await runner.RunAsync("a Content-Length only counts when both hops would read it identically", () =>
        {
            // NumberStyles.None: no sign, no whitespace, no hex. Anything a next hop might read
            // differently must not be treated as a length at all, or the two ends disagree about
            // where the body stops -- which is request smuggling, not a cosmetic difference.
            // Optional whitespace around a field value is stripped when the header block is parsed
            // (RFC 9110 5.5), so " 5" is genuinely the length 5 and is not listed here.
            // A response is refused outright (RFC 9112 6.3 rule 5) rather than read until close: a
            // relayed response carries its headers on, so the client would be handed the very
            // length Piper had decided not to believe. A request is refused too (rule 6): read as
            // having no body, its body bytes would be parsed as a second, smuggled request.
            foreach (var bad in new[] { "+5", "0x5", "abc", "5.0", "-5", "", "5, 5" })
            {
                runner.IsTrue(Rejects($"Content-Length: {bad}"),
                    $"a response with Content-Length '{bad}' is rejected");
                runner.IsTrue(RejectsRequest($"Content-Length: {bad}"),
                    $"a request with Content-Length '{bad}' is rejected");
            }

            // A value too large for Int64 is likewise not a length.
            runner.IsTrue(Rejects("Content-Length: 99999999999999999999999"),
                "an overflowing Content-Length is rejected");
            runner.IsTrue(RejectsRequest("Content-Length: 99999999999999999999999"),
                "an overflowing request Content-Length is rejected");

            // Two copies are one length only when they agree; otherwise each hop may pick a
            // different one.
            runner.IsTrue(Rejects("Content-Length: 5\r\nContent-Length: 7"), "conflicting copies are rejected");
            runner.IsTrue(RejectsRequest("Content-Length: 5\r\nContent-Length: 7"),
                "conflicting copies on a request are rejected");
            runner.AreEqual(HttpBodyDescriptor.OfLength(5),
                Describe("Content-Length: 5\r\nContent-Length: 5", "GET", 200), "identical copies are one length");
            runner.AreEqual(HttpBodyDescriptor.OfLength(5),
                HttpParser.DescribeRequestBody(HeaderCollection.Parse("Content-Length: 5\r\nContent-Length: 5")),
                "and identical copies on a request are one length too");
            runner.AreEqual(HttpBodyFraming.None,
                Describe("Content-Length: abc", "HEAD", 200).Framing, "and a bodiless response is never judged on it");

            return Task.CompletedTask;
        });

        await runner.RunAsync("length-framed and chunked bodies read back byte-exact", async () =>
        {
            var lengthFramed = await ReadResponseAsync(
                "HTTP/1.1 200 OK\r\nContent-Length: 5\r\n\r\nhello", "GET");
            runner.AreEqual("hello", Encoding.Latin1.GetString(lengthFramed.Body), "a length-framed body");

            var chunked = await ReadResponseAsync(
                "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n6\r\n world\r\n0\r\n\r\n", "GET");
            runner.AreEqual("hello world", Encoding.Latin1.GetString(chunked.Body), "chunks are joined in order");

            // Chunk extensions, uppercase and leading-zero hex sizes are all legal spellings.
            var awkward = await ReadResponseAsync(
                "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n"
                + "0005;name=value\r\nhello\r\nC\r\n and goodbye\r\n0\r\n\r\n", "GET");
            runner.AreEqual("hello and goodbye", Encoding.Latin1.GetString(awkward.Body),
                "chunk extensions, leading zeros and uppercase hex are all accepted");

            // Trailers after the terminating chunk are consumed, not left on the reader.
            var trailered = await ReadResponseAsync(
                "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n4\r\nbody\r\n0\r\nExpires: soon\r\n\r\n", "GET");
            runner.AreEqual("body", Encoding.Latin1.GetString(trailered.Body), "a trailered body");

            // Bare LF line endings, which RFC 9112 2.2 tells a recipient to tolerate.
            var bareLf = await ReadResponseAsync(
                "HTTP/1.1 200 OK\nTransfer-Encoding: chunked\n\n3\nabc\n0\n\n", "GET");
            runner.AreEqual("abc", Encoding.Latin1.GetString(bareLf.Body), "bare LF terminators are tolerated");

            var untilClose = await ReadResponseAsync(
                "HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\n\r\neverything to the end", "GET");
            runner.AreEqual("everything to the end", Encoding.Latin1.GetString(untilClose.Body),
                "a close-delimited body");
        });

        await runner.RunAsync("a bodiless response does not consume the next one", async () =>
        {
            // The whole point of getting bodiless framing right. Each pair puts a second, obviously
            // distinct response immediately after the first on one reader: if the first response
            // wrongly reads a body, the second parses as garbage or not at all.
            var cases = new (string Method, string First, string What)[]
            {
                ("HEAD", "HTTP/1.1 200 OK\r\nContent-Length: 1234\r\n\r\n", "HEAD with a Content-Length"),
                ("GET", "HTTP/1.1 204 No Content\r\nContent-Length: 99\r\n\r\n", "204 with a Content-Length"),
                ("GET", "HTTP/1.1 304 Not Modified\r\nContent-Length: 77\r\n\r\n", "304 with a Content-Length"),
                ("HEAD", "HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n", "HEAD on a chunked resource"),
            };

            foreach (var (method, first, what) in cases)
            {
                using var reader = ReaderFor(first + "HTTP/1.1 418 I am a teapot\r\nContent-Length: 3\r\n\r\ntea");

                var head = await HttpParser.ReadResponseAsync(reader, method, CancellationToken.None);
                runner.AreEqual(0, head.Body.Length, $"{what} yields no body");

                var next = await HttpParser.ReadResponseAsync(reader, "GET", CancellationToken.None);
                runner.AreEqual(418, next.StatusCode, $"the response after {what} still parses");
                runner.AreEqual("tea", Encoding.Latin1.GetString(next.Body), $"and keeps its own body after {what}");
            }
        });

        await runner.RunAsync("206 Partial Content keeps its range headers and length", async () =>
        {
            var partial = await ReadResponseAsync(
                "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes 10-14/2048\r\nContent-Length: 5\r\n\r\nspam!", "GET");

            runner.AreEqual(206, partial.StatusCode, "status");
            runner.AreEqual("bytes 10-14/2048", partial.Headers["Content-Range"], "Content-Range is preserved verbatim");
            runner.AreEqual("spam!", Encoding.Latin1.GetString(partial.Body), "only the ranged bytes are read");
        });

        await runner.RunAsync("interim responses are consumed, but not without limit", async () =>
        {
            var continued = await ReadResponseAsync(
                "HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 103 Early Hints\r\nLink: </s.css>\r\n\r\n"
                + "HTTP/1.1 201 Created\r\nContent-Length: 2\r\n\r\nok", "GET");
            runner.AreEqual(201, continued.StatusCode, "the final response is the one returned");
            runner.AreEqual("ok", Encoding.Latin1.GetString(continued.Body), "with its own body");
            runner.AreEqual(null, continued.Headers["Link"], "an interim response's headers are not merged into it");


            // An origin that only ever sends 1xx must not be able to keep us reading for ever.
            var flood = string.Concat(Enumerable.Repeat("HTTP/1.1 100 Continue\r\n\r\n", 50));
            runner.IsTrue(await ThrowsParseAsync(() => ReadResponseAsync(flood, "GET")),
                "an unending run of interim responses is rejected");
        });

        await runner.RunAsync("malformed framing is rejected rather than guessed at", async () =>
        {
            var rejected = new (string Wire, string What)[]
            {
                ("hello there\r\n\r\n", "a status line that is not a status line"),
                ("HTTP/1.1\r\n\r\n", "a status line with no status code"),
                ("HTTP/1.1 twohundred OK\r\n\r\n", "a non-numeric status code"),
                ("HTTP/1.1 -1 Bad\r\n\r\n", "a negative status code"),
                ("HTTP/1.1 200 OK\r\n bad: folded first\r\n\r\n", "a header block starting with a folded line"),
                ("HTTP/1.1 200 OK\r\nnocolon\r\n\r\n", "a header line with no colon"),
                ("HTTP/1.1 200 OK\r\n: empty\r\n\r\n", "a header line with an empty name"),
                ("HTTP/1.1 200 OK\r\nContent-Length: 10\r\n\r\nshort", "a body shorter than its declared length"),
                ("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n-1\r\nxx\r\n", "a negative chunk size"),
                ("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nzz\r\nxx\r\n", "a non-hex chunk size"),
                ("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\nFFFFFFFFFFFFFFFF\r\n", "a chunk size that overflows"),
                ("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhel", "a chunk shorter than its declared size"),
                ("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n", "a chunked body with no terminating chunk"),
                ("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhelloXY\r\n0\r\n\r\n", "a chunk running on past its size"),
                ("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n\r\n\r\n5\r\nhello\r\n0\r\n\r\n", "blank lines where a chunk size belongs"),
                ("HTTP/1.1 200 OK\r\nContent-Length: 5\r\nContent-Length: 6\r\n\r\nhello!", "conflicting Content-Length headers"),
                ("HTTP/1.1 200 OK\r\nContent-Length: 5\r\n", "a header block that never ends"),
                ("", "a connection that closes before the status line"),
            };

            foreach (var (wire, what) in rejected)
                runner.IsTrue(await ThrowsParseAsync(() => ReadResponseAsync(wire, "GET")), $"{what} is rejected");

            // The relay parses chunks itself, so it has to refuse the same malformed framing rather
            // than pass it on.
            foreach (var (wire, what) in rejected.Where(r => r.Item1.Contains("chunked", StringComparison.Ordinal)))
            {
                var body = wire[(wire.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..];
                runner.IsTrue(await ThrowsParseAsync(async () =>
                {
                    using var reader = ReaderFor(body);
                    await HttpBodyRelay.RelayAsync(reader, HttpBodyDescriptor.Chunked, Stream.Null,
                        rechunkDownstream: true, captureLimit: long.MaxValue, CancellationToken.None);
                }), $"{what} is rejected by the relay too");
            }

            // Attacker-controlled counts and lengths stay bounded (CLAUDE.md), so a peer cannot make
            // the parser allocate without limit before it decides the message is malformed.
            var manyHeaders = "HTTP/1.1 200 OK\r\n"
                + string.Concat(Enumerable.Range(0, 250).Select(i => $"X-Pad-{i}: v\r\n")) + "\r\n";
            runner.IsTrue(await ThrowsParseAsync(() => ReadResponseAsync(manyHeaders, "GET")),
                "a header block with too many headers is rejected");

            var hugeHeaderLine = "HTTP/1.1 200 OK\r\nX-Pad: " + new string('a', 200 * 1024) + "\r\n\r\n";
            runner.IsTrue(await ThrowsParseAsync(() => ReadResponseAsync(hugeHeaderLine, "GET")),
                "an over-long header line is rejected");
        });

        await runner.RunAsync("reading a head leaves the body on the reader", async () =>
        {
            // The split the streaming path depends on: after the head is read, the framing is known
            // and not one body byte has been consumed.
            using var reader = ReaderFor("HTTP/1.1 200 OK\r\nContent-Length: 11\r\n\r\nhello world");

            var (head, body) = await HttpParser.ReadResponseHeadAsync(reader, "GET", CancellationToken.None);

            runner.AreEqual(200, head.StatusCode, "the head is parsed");
            runner.AreEqual(0, head.Body.Length, "and carries no body yet");
            runner.AreEqual(HttpBodyFraming.Length, body.Framing, "the framing is known from the head alone");
            runner.AreEqual(11L, body.Length, "including the exact body length");

            var remaining = await reader.ReadExactlyAsync(11, CancellationToken.None);
            runner.AreEqual("hello world", Encoding.Latin1.GetString(remaining), "the body is still there to be read");
        });
    }

    private static HttpBodyDescriptor Describe(string headerBlock, string method, int status) =>
        HttpParser.DescribeResponseBody(HeaderCollection.Parse(headerBlock), method, status);

    private static bool Rejects(string headerBlock) => Throws(() => Describe(headerBlock, "GET", 200));

    private static bool RejectsRequest(string headerBlock) =>
        Throws(() => HttpParser.DescribeRequestBody(HeaderCollection.Parse(headerBlock)));

    private static bool Throws(Func<HttpBodyDescriptor> describe)
    {
        try
        {
            describe();
            return false;
        }
        catch (HttpParseException)
        {
            return true;
        }
    }

    private static HttpStreamReader ReaderFor(string wire) =>
        new(new MemoryStream(Encoding.Latin1.GetBytes(wire)));

    private static async Task<HttpResponseData> ReadResponseAsync(string wire, string method)
    {
        using var reader = ReaderFor(wire);
        return await HttpParser.ReadResponseAsync(reader, method, CancellationToken.None);
    }

    private static async Task<bool> ThrowsParseAsync(Func<Task> action)
    {
        try
        {
            await action();
            return false;
        }
        catch (HttpParseException)
        {
            return true;
        }
    }
}
