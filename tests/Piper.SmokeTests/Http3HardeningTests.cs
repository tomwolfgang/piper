using System.Diagnostics;
using System.Net.Quic;
using Piper.Core.Http;
using Piper.Core.Http3;
using Piper.Core.Http3.Qpack;
using Piper.Core.Proxy;
using Piper.Core.Security;

// The HTTP/3 client against an origin that behaves badly in one chosen way at a time, plus the
// pieces underneath it (Alt-Svc cache, field-section limit, frame reader, SETTINGS). Each test is
// one thing the origin can do that used to hang, exhaust memory, restart a download, or be accepted
// although the protocol forbids it, and each names what must happen instead.
#pragma warning disable CA1416 // QUIC types are used only after the IsSupported guard in RunAsync

internal static class Http3HardeningTests
{
    private static readonly bool Quic = Http3ClientConnection.IsSupported;

    public static async Task RunAsync(TestRunner runner)
    {
        await RunCodecTestsAsync(runner);

        if (!TestHttp3Origin.IsSupported || !Quic)
        {
            await runner.RunAsync("HTTP/3 hardening end-to-end", () =>
            {
                runner.IsTrue(false, "QUIC unavailable on this machine - h3 hardening tests could not run");
                return Task.CompletedTask;
            });
            return;
        }

        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Http3Hardening-Certs"));

        await RunResponseTestsAsync(runner, ca);
        await RunControlStreamTestsAsync(runner, ca);
        await RunIdleTimeoutTestsAsync(runner, ca);
        await RunAttemptTestsAsync(runner, ca);
    }

    // ------------------------------------------------------------------ codec pieces

    private static async Task RunCodecTestsAsync(TestRunner runner)
    {
        await runner.RunAsync("QPACK: a field section is limited by its decoded size, not its encoded size", () =>
        {
            // The static entry with the most bytes in it among the one-byte indexes.
            var best = Enumerable.Range(0, 63).MaxBy(i => QpackStaticTable.Get(i).Name.Length + QpackStaticTable.Get(i).Value.Length);
            var entry = QpackStaticTable.Get(best);
            var perField = entry.Name.Length + entry.Value.Length + 32;

            byte[] Section(int fields)
            {
                var block = new List<byte> { 0, 0 };
                for (var i = 0; i < fields; i++) block.Add((byte)(0xC0 | best));
                return block.ToArray();
            }

            var limit = perField * 10L;
            runner.AreEqual(10, QpackDecoder.Decode(Section(10), limit).Count, "exactly at the limit decodes");

            var threw = false;
            try { QpackDecoder.Decode(Section(11), limit); }
            catch (Http3ProtocolException ex) { threw = ex.ErrorCode == Http3ErrorCode.ExcessiveLoad; }
            runner.IsTrue(threw, "one field past the limit is refused as H3_EXCESSIVE_LOAD");

            var big = Section(4_000);
            runner.IsTrue(big.Length < 4_100, $"the hostile block is tiny ({big.Length} bytes encoded)");
            threw = false;
            try { QpackDecoder.Decode(big, Http3ClientConnection.MaxFieldSectionSize); }
            catch (Http3ProtocolException ex) { threw = ex.ErrorCode == Http3ErrorCode.ExcessiveLoad; }
            runner.IsTrue(threw, "yet it decodes to more than the advertised 128 KiB and is refused");
            return Task.CompletedTask;
        });

        await runner.RunAsync("QPACK: an integer that does not fit an int is rejected, not wrapped", () =>
        {
            // 2^32 + 2 cast to int is 2: a different static entry than the one the bytes name.
            var index = new List<byte> { 0, 0 };
            Piper.Core.Http.PrefixInteger.Write(index, 0xC0, 6, (1L << 32) + 2);
            var threw = false;
            try { QpackDecoder.Decode(index.ToArray()); }
            catch (HttpParseException) { threw = true; }
            runner.IsTrue(threw, "indexed field line index 2^32 + 2");

            // Literal name length 2^32 + 3 would wrap to 3 and swallow the next three bytes as a name.
            var literal = new List<byte> { 0, 0 };
            Piper.Core.Http.PrefixInteger.Write(literal, 0x20, 3, (1L << 32) + 3);
            literal.AddRange("abc"u8.ToArray());
            literal.Add(0);
            threw = false;
            try { QpackDecoder.Decode(literal.ToArray()); }
            catch (HttpParseException) { threw = true; }
            runner.IsTrue(threw, "literal name length 2^32 + 3");

            var nameRef = new List<byte> { 0, 0 };
            Piper.Core.Http.PrefixInteger.Write(nameRef, 0x50, 4, (1L << 32) + 1);
            nameRef.Add(0);
            threw = false;
            try { QpackDecoder.Decode(nameRef.ToArray()); }
            catch (HttpParseException) { threw = true; }
            runner.IsTrue(threw, "literal with name reference 2^32 + 1");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HTTP/3 SETTINGS: duplicates, HTTP/2 identifiers and truncation are connection errors", () =>
        {
            var ok = Http3FrameWriter.ParseSettings(Http3FrameWriter.EncodeSettings(
                (Http3SettingId.MaxFieldSectionSize, 4096), (Http3SettingId.QpackMaxTableCapacity, 7), (0x21, 99), (0x1f * 5 + 0x21, 1)));
            runner.AreEqual(4096L, ok.MaxFieldSectionSize, "MAX_FIELD_SECTION_SIZE");
            runner.AreEqual(7L, ok.QpackMaxTableCapacity, "QPACK_MAX_TABLE_CAPACITY");
            runner.AreEqual(0L, ok.QpackBlockedStreams, "absent means zero");
            runner.AreEqual((long?)null, Http3FrameWriter.ParseSettings([]).MaxFieldSectionSize, "empty SETTINGS is valid; the field section size is unlimited");

            Http3ErrorCode? Code(byte[] payload)
            {
                try { Http3FrameWriter.ParseSettings(payload); return null; }
                catch (Http3ProtocolException ex) { return ex.ErrorCode; }
            }

            runner.AreEqual((Http3ErrorCode?)Http3ErrorCode.SettingsError,
                Code(Http3FrameWriter.EncodeSettings((0x06, 1), (0x06, 2))), "a repeated identifier");
            foreach (var id in new long[] { 0x00, 0x02, 0x03, 0x04, 0x05 })
                runner.AreEqual((Http3ErrorCode?)Http3ErrorCode.SettingsError,
                    Code(Http3FrameWriter.EncodeSettings((id, 1))), $"HTTP/2 identifier 0x{id:x}");
            runner.AreEqual((Http3ErrorCode?)Http3ErrorCode.FrameError, Code([0x06]), "an identifier with no value");
            runner.AreEqual((Http3ErrorCode?)Http3ErrorCode.FrameError, Code([0x40]), "a truncated variable-length integer");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HTTP/3 frame reader: header, bounded payload, copy, skip and drain", async () =>
        {
            static Http3StreamReader ReaderOver(byte[] bytes, int chunk = int.MaxValue) =>
                new(new TricklingStream(bytes, chunk));

            // Header only, then a payload whose claimed length is not trusted.
            var frame = Http3FrameWriter.Encode(Http3FrameType.Data, new byte[300]);
            var reader = ReaderOver(frame);
            var header = await reader.ReadFrameHeaderAsync(CancellationToken.None);
            runner.AreEqual((0L, 300L), header!.Value, "type and length without touching the payload");
            runner.AreEqual(300, (await reader.ReadPayloadAsync(300, 300, CancellationToken.None)).Length, "payload at the cap");
            runner.IsTrue(await reader.ReadFrameHeaderAsync(CancellationToken.None) is null, "clean end on a frame boundary");

            reader = ReaderOver(frame);
            await reader.ReadFrameHeaderAsync(CancellationToken.None);
            await ExpectParseFailureAsync(runner, "payload one past the cap", () => reader.ReadPayloadAsync(300, 299, CancellationToken.None).AsTask());

            reader = ReaderOver(Http3FrameWriter.EncodeHeader(Http3FrameType.Headers, (1L << 40)));
            header = await reader.ReadFrameHeaderAsync(CancellationToken.None);
            await ExpectParseFailureAsync(runner, "a 2^40-byte claim is refused, never allocated", () => reader.ReadPayloadAsync(header!.Value.Length, long.MaxValue, CancellationToken.None).AsTask());

            // Truncations.
            reader = ReaderOver([0x00]);
            await ExpectParseFailureAsync(runner, "a type with no length", () => reader.ReadFrameHeaderAsync(CancellationToken.None).AsTask());
            reader = ReaderOver(frame[..100]);
            await reader.ReadFrameHeaderAsync(CancellationToken.None);
            await ExpectParseFailureAsync(runner, "a payload cut short", () => reader.ReadPayloadAsync(300, 300, CancellationToken.None).AsTask());

            // Copy moves what a read brought, at least one byte, never more than asked for.
            reader = ReaderOver(frame, chunk: 7);
            await reader.ReadFrameHeaderAsync(CancellationToken.None);
            using var sink = new MemoryStream();
            long copied = 0;
            var calls = 0;
            while (copied < 300)
            {
                var n = await reader.CopyPayloadAsync(sink, 300 - copied, CancellationToken.None);
                runner.IsTrue(n is >= 1 and <= 7, $"copy returns between 1 and 7 bytes ({n})");
                copied += n;
                calls++;
            }
            runner.AreEqual(300L, sink.Length, "all of it arrived");
            runner.IsTrue(calls >= 300 / 7, $"in many small steps, each a chance to count progress ({calls})");

            reader = ReaderOver(frame[..50]);
            await reader.ReadFrameHeaderAsync(CancellationToken.None);
            await ExpectParseFailureAsync(runner, "copy past the end of the stream", async () =>
            {
                while (true) await reader.CopyPayloadAsync(Stream.Null, 300, CancellationToken.None);
            });

            // Skip.
            reader = ReaderOver(Http3FrameWriter.Encode(Http3FrameType.Data, new byte[40]).Concat(Http3FrameWriter.Encode(Http3FrameType.Data, [1, 2])).ToArray(), chunk: 5);
            var skipped = await reader.ReadFrameHeaderAsync(CancellationToken.None);
            await reader.SkipPayloadAsync(skipped!.Value.Length, CancellationToken.None);
            var next = await reader.ReadFrameAsync(100, CancellationToken.None);
            runner.AreEqual(2, next!.Value.Payload.Length, "the frame after a skipped one is intact");
            reader = ReaderOver(new byte[10]);
            await ExpectParseFailureAsync(runner, "skip past the end", () => reader.SkipPayloadAsync(11, CancellationToken.None).AsTask());

            // Drain.
            await ReaderOver(new byte[5000], chunk: 100).DrainAsync(5000, CancellationToken.None);
            await ExpectParseFailureAsync(runner, "drain past its budget", () => ReaderOver(new byte[5001], chunk: 100).DrainAsync(5000, CancellationToken.None).AsTask());
        });
    }

    private static async Task ExpectParseFailureAsync(TestRunner runner, string what, Func<Task> action)
    {
        try { await action(); runner.IsTrue(false, $"{what}: expected HttpParseException, none thrown"); }
        catch (HttpParseException) { runner.IsTrue(true, what); }
    }

    // A stream that hands out its bytes a few at a time, as a network does.
    private sealed class TricklingStream(byte[] bytes, int chunk) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = Math.Min(Math.Min(buffer.Length, chunk), bytes.Length - _position);
            bytes.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ------------------------------------------------------------------ raw frames and origins

    private static byte[] Frame(long type, ReadOnlySpan<byte> payload)
    {
        var head = new List<byte>();
        VarInt.Write(head, type);
        VarInt.Write(head, payload.Length);
        return [.. head, .. payload];
    }

    private static byte[] ResponseHeaders(int status, params (string Name, string Value)[] extra) =>
        Frame((long)Http3FrameType.Headers, QpackEncoder.Encode([(":status", status.ToString()), .. extra]));

    private static byte[] DataFrame(int size) => Frame((long)Http3FrameType.Data, new byte[size]);

    private static async Task SendAsync(QuicStream stream, CancellationToken ct, bool last, params byte[][] pieces)
    {
        for (var i = 0; i < pieces.Length; i++)
            await stream.WriteAsync(pieces[i], completeWrites: last && i == pieces.Length - 1, ct);
        if (last && pieces.Length == 0) await stream.WriteAsync(ReadOnlyMemory<byte>.Empty, completeWrites: true, ct);
    }

    private static Task Silence(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    private static TestHttp3Origin.Behavior Respond(Func<QuicStream, CancellationToken, Task> respond) =>
        new() { RespondRaw = respond };

    private static byte[] ControlStream(params byte[][] frames)
    {
        var bytes = new List<byte>();
        VarInt.Write(bytes, Http3StreamType.Control);
        foreach (var frame in frames) bytes.AddRange(frame);
        return [.. bytes];
    }

    private static byte[] Settings(params (long, long)[] settings) =>
        Frame((long)Http3FrameType.Settings, Http3FrameWriter.EncodeSettings(settings));

    private static byte[] GoAway(long id) => Frame((long)Http3FrameType.GoAway, VarInt.Encode(id));

    private static TestHttp3Origin.Behavior Control(byte[] bytes, bool keepOpen = true, Func<QuicStream, CancellationToken, Task>? respond = null) => new()
    {
        WriteControlStream = async (stream, ct) =>
        {
            await stream.WriteAsync(bytes, completeWrites: !keepOpen, ct);
            await stream.FlushAsync(ct);
        },
        RespondRaw = respond ?? ((_, ct) => Silence(ct)),
    };

    private static HttpRequestData Get(int port, string method = "GET") =>
        new() { Method = method, Url = new Uri($"https://127.0.0.1:{port}/resource") };

    private static Task<TestHttp3Origin> StartOrigin(CertificateAuthority ca, TestHttp3Origin.Behavior behavior) =>
        TestHttp3Origin.StartAsync(ca.GetCertificateFor("127.0.0.1"),
            (_, _) => Task.FromResult(HttpResponseData.Simple(200, "OK", "ok")), behavior);

    private sealed record Outcome(
        HttpResponseData? Response, Exception? Failure, TimeSpan Elapsed,
        long? ClientCloseCode, Http3PeerSettings? PeerSettings, long? GoAwayStreamId);

    // One request against an origin with the given behaviour. The close code the client sent is
    // read before the connection is disposed, because disposing closes with NO_ERROR.
    private static async Task<Outcome> ExchangeAsync(
        CertificateAuthority ca, TestHttp3Origin.Behavior behavior,
        Action<Http3ClientConnection>? configure = null, string method = "GET", TimeSpan? cancelRequestAfter = null)
    {
        await using var origin = await StartOrigin(ca, behavior);
        var options = new ProxyOptions { ValidateUpstreamCertificates = false };
        await using var connection = await Http3ClientConnection.ConnectAsync("127.0.0.1", origin.Port, options, CancellationToken.None);
        connection.IdleTimeout = TimeSpan.FromSeconds(8);
        configure?.Invoke(connection);

        using var cancellation = new CancellationTokenSource();
        if (cancelRequestAfter is { } delay) cancellation.CancelAfter(delay);

        HttpResponseData? response = null;
        Exception? failure = null;
        var clock = Stopwatch.StartNew();
        try { response = await connection.SendRequestAsync(Get(origin.Port, method), cancellation.Token); }
        catch (Exception ex) { failure = ex; }
        clock.Stop();

        // Only a connection error is answered with a close code; the others close quietly.
        var closeCode = failure is Http3ProtocolException ? await origin.ClientCloseCodeAsync(TimeSpan.FromSeconds(3)) : null;
        return new Outcome(response, failure, clock.Elapsed, closeCode,
            connection.PeerSettings, connection.GoAwayStreamId);
    }

    private static string Describe(Exception? ex) => ex is null ? "no failure" : $"{ex.GetType().Name}: {ex.Message}";

    private static void ExpectProtocolError(TestRunner runner, Outcome outcome, Http3ErrorCode code, string what)
    {
        runner.IsTrue(outcome.Failure is Http3ProtocolException { } ex && ex.ErrorCode == code,
            $"{what}: expected {code} (was: {Describe(outcome.Failure)})");
        runner.AreEqual((long?)(long)code, outcome.ClientCloseCode, $"{what}: the origin is told {code} on the wire");
    }

    // ------------------------------------------------------------------ the response

    private static async Task RunResponseTestsAsync(TestRunner runner, CertificateAuthority ca)
    {
        await runner.RunAsync("HTTP/3: the cap is on the running body total, not on each frame", async () =>
        {
            // 40 frames of 16 KiB: every one far below any per-frame cap, 640 KiB together.
            var over = await ExchangeAsync(ca,
                Respond(async (s, ct) => await SendAsync(s, ct, true, [ResponseHeaders(200), .. Enumerable.Repeat(DataFrame(16 * 1024), 40)])),
                c => c.MaxResponseBodyBytes = 256 * 1024);
            runner.IsTrue(over.Failure is Http3ResponseTooLargeException, $"640 KiB against a 256 KiB limit (was: {Describe(over.Failure)})");

            var exact = await ExchangeAsync(ca,
                Respond(async (s, ct) => await SendAsync(s, ct, true, [ResponseHeaders(200), .. Enumerable.Repeat(DataFrame(16 * 1024), 16)])),
                c => c.MaxResponseBodyBytes = 256 * 1024);
            runner.AreEqual(256 * 1024, exact.Response?.Body.Length ?? -1, $"exactly the limit is carried (was: {Describe(exact.Failure)})");

            var oneOver = await ExchangeAsync(ca,
                Respond(async (s, ct) => await SendAsync(s, ct, true, [ResponseHeaders(200), .. Enumerable.Repeat(DataFrame(16 * 1024), 16), DataFrame(1)])),
                c => c.MaxResponseBodyBytes = 256 * 1024);
            runner.IsTrue(oneOver.Failure is Http3ResponseTooLargeException, $"one byte past the limit (was: {Describe(oneOver.Failure)})");

            var oneFrame = await ExchangeAsync(ca,
                Respond(async (s, ct) => await SendAsync(s, ct, true, ResponseHeaders(200), DataFrame(300 * 1024))),
                c => c.MaxResponseBodyBytes = 256 * 1024);
            runner.IsTrue(oneFrame.Failure is Http3ResponseTooLargeException, $"a single frame too big is refused on its length (was: {Describe(oneFrame.Failure)})");
        });

        await runner.RunAsync("HTTP/3: a declared Content-Length over the limit is refused on the headers", async () =>
        {
            // Headers promise 100 MB; the origin then sends nothing, so only an early refusal returns fast.
            var outcome = await ExchangeAsync(ca,
                Respond(async (s, ct) =>
                {
                    await SendAsync(s, ct, false, ResponseHeaders(200, ("content-length", "100000000")));
                    await Silence(ct);
                }));
            runner.IsTrue(outcome.Failure is Http3ResponseTooLargeException, $"too large on the declaration (was: {Describe(outcome.Failure)})");
            runner.IsTrue(outcome.Elapsed < TimeSpan.FromSeconds(4), $"without waiting for a body ({outcome.Elapsed.TotalSeconds:0.0} s)");

            // HEAD carries the length of a body that is not sent.
            var head = await ExchangeAsync(ca,
                Respond((s, ct) => SendAsync(s, ct, true, ResponseHeaders(200, ("content-length", "100000000")))),
                method: "HEAD");
            runner.AreEqual(200, head.Response?.StatusCode ?? -1, $"a HEAD response is not judged by its Content-Length (was: {Describe(head.Failure)})");
        });

        await runner.RunAsync("HTTP/3: a header section is limited to the advertised field-section size", async () =>
        {
            // A HEADERS frame of 200 KiB: well under the old limit (the body cap), over the advertised 128 KiB.
            var oversize = await ExchangeAsync(ca,
                Respond((s, ct) => SendAsync(s, ct, true, Frame((long)Http3FrameType.Headers, new byte[200 * 1024]))));
            ExpectProtocolError(runner, oversize, Http3ErrorCode.ExcessiveLoad, "200 KiB HEADERS frame");

            // A small frame that decodes to far more than the limit.
            var best = Enumerable.Range(0, 63).MaxBy(i => QpackStaticTable.Get(i).Name.Length + QpackStaticTable.Get(i).Value.Length);
            var bomb = new List<byte> { 0, 0 };
            for (var i = 0; i < 4_000; i++) bomb.Add((byte)(0xC0 | best));
            var amplified = await ExchangeAsync(ca,
                Respond((s, ct) => SendAsync(s, ct, true, Frame((long)Http3FrameType.Headers, bomb.ToArray()))));
            ExpectProtocolError(runner, amplified, Http3ErrorCode.ExcessiveLoad, "a 4 KB block that decodes past 128 KiB");

            // A huge but valid trailing section is held to the same limit.
            var trailers = await ExchangeAsync(ca,
                Respond((s, ct) => SendAsync(s, ct, true, ResponseHeaders(200), DataFrame(10), Frame((long)Http3FrameType.Headers, bomb.ToArray()))));
            ExpectProtocolError(runner, trailers, Http3ErrorCode.ExcessiveLoad, "an oversize trailer section");
        });

        await runner.RunAsync("HTTP/3: frames out of order are connection errors (H3_FRAME_UNEXPECTED)", async () =>
        {
            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, DataFrame(10), ResponseHeaders(200)))),
                Http3ErrorCode.FrameUnexpected, "DATA before HEADERS");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, DataFrame(10)))),
                Http3ErrorCode.FrameUnexpected, "DATA with no HEADERS at all");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                    ResponseHeaders(200), DataFrame(10), Frame((long)Http3FrameType.Headers, QpackEncoder.Encode([("x-trailer", "1")])), DataFrame(10)))),
                Http3ErrorCode.FrameUnexpected, "DATA after the trailer section");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                    ResponseHeaders(200), Frame((long)Http3FrameType.Headers, QpackEncoder.Encode([("a", "1")])), Frame((long)Http3FrameType.Headers, QpackEncoder.Encode([("b", "2")]))))),
                Http3ErrorCode.FrameUnexpected, "a second trailer section");

            foreach (var (type, name) in new[] { (0x04L, "SETTINGS"), (0x07L, "GOAWAY"), (0x0dL, "MAX_PUSH_ID"), (0x02L, "reserved 0x02"), (0x06L, "reserved 0x06"), (0x08L, "reserved 0x08"), (0x09L, "reserved 0x09") })
                ExpectProtocolError(runner,
                    await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, ResponseHeaders(200), Frame(type, [0])))),
                    Http3ErrorCode.FrameUnexpected, $"{name} on a request stream");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, ResponseHeaders(200), Frame((long)Http3FrameType.PushPromise, [0])))),
                Http3ErrorCode.IdError, "PUSH_PROMISE when no push was enabled");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, ResponseHeaders(200), Frame((long)Http3FrameType.CancelPush, [0])))),
                Http3ErrorCode.FrameUnexpected, "CANCEL_PUSH on a request stream (control stream only, RFC 9114 section 7.2.3)");
        });

        await runner.RunAsync("HTTP/3: trailers, interim responses and unknown frames are accepted within limits", async () =>
        {
            var ok = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(103, ("link", "</a.css>; rel=preload")),
                Frame(0x40, [1, 2, 3]),
                ResponseHeaders(200, ("x-final", "yes")),
                DataFrame(100),
                Frame(0x1f * 9 + 0x21, new byte[1000]),
                DataFrame(50),
                Frame((long)Http3FrameType.Headers, QpackEncoder.Encode([("x-trailer", "1")])))));
            runner.AreEqual(150, ok.Response?.Body.Length ?? -1, $"the body around an interim response, a grease frame and trailers (was: {Describe(ok.Failure)})");
            runner.AreEqual("yes", ok.Response?.Headers["x-final"], "the final headers, not the interim ones");
            runner.AreEqual("HTTP/3", ok.Response?.HttpVersion, "tagged as h3");

            var manyInterim = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                [.. Enumerable.Repeat(ResponseHeaders(103), 40), ResponseHeaders(200)])));
            runner.IsTrue(manyInterim.Failure is HttpParseException, $"an endless run of 1xx is cut (was: {Describe(manyInterim.Failure)})");

            var floods = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                [ResponseHeaders(200), .. Enumerable.Repeat(Frame(0x40, [0]), 100)])));
            ExpectProtocolError(runner, floods, Http3ErrorCode.ExcessiveLoad, "100 ignorable frames");

            var bigIgnored = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200), Frame(0x40, new byte[100 * 1024]))));
            ExpectProtocolError(runner, bigIgnored, Http3ErrorCode.ExcessiveLoad, "a 100 KiB ignorable frame");

            var noStatus = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                Frame((long)Http3FrameType.Headers, QpackEncoder.Encode([("x-only", "1")])))));
            runner.IsTrue(noStatus.Failure is HttpParseException, $"a response with no :status (was: {Describe(noStatus.Failure)})");

            // :status is three digits, 100-599: a 0, a 600, a padded 0000000200 or a signed one would
            // otherwise land in the session, the rules and the downstream HTTP/1 status line.
            foreach (var badStatus in new[] { "0", "99", "600", "999", "99999", "0000000200", "+200", "20x", "" })
            {
                var invalid = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                    Frame((long)Http3FrameType.Headers, QpackEncoder.Encode([(":status", badStatus)])))));
                runner.IsTrue(invalid.Failure is HttpParseException && invalid.Response is null,
                    $"a response with :status '{badStatus}' is refused (was: {Describe(invalid.Failure)})");
            }

            var noHeaders = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true)));
            runner.IsTrue(noHeaders.Failure is HttpParseException, $"a stream that ends with no response (was: {Describe(noHeaders.Failure)})");

            var truncated = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200), DataFrame(1000)[..500])));
            runner.IsTrue(truncated.Failure is HttpParseException, $"a DATA frame cut short by the end of the stream (was: {Describe(truncated.Failure)})");
        });

        await runner.RunAsync("HTTP/3: the body must be as long as the Content-Length says (RFC 9114 section 4.1.2)", async () =>
        {
            var exact = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200, ("content-length", "1000")), DataFrame(600), DataFrame(400))));
            runner.AreEqual(1000, exact.Response?.Body.Length ?? -1, $"exactly the announced length, in two frames (was: {Describe(exact.Failure)})");

            // A clean end of stream after 500 of 1000 bytes used to be a complete response.
            var shortBody = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200, ("content-length", "1000")), DataFrame(500))));
            runner.IsTrue(shortBody.Failure is HttpParseException { Message: var shortMessage } && shortMessage.Contains("Content-Length", StringComparison.Ordinal),
                $"500 of 1000 bytes (was: {Describe(shortBody.Failure)})");

            var empty = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, ResponseHeaders(200, ("content-length", "5")))));
            runner.IsTrue(empty.Failure is HttpParseException, $"a length with no body at all (was: {Describe(empty.Failure)})");

            var longBody = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200, ("content-length", "100")), DataFrame(60), DataFrame(60))));
            runner.IsTrue(longBody.Failure is HttpParseException, $"120 of 100 bytes, refused as soon as it passes (was: {Describe(longBody.Failure)})");

            var zero = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, ResponseHeaders(200, ("content-length", "0")))));
            runner.AreEqual(0, zero.Response?.Body.Length ?? -1, $"an announced empty body is fine (was: {Describe(zero.Failure)})");

            // Responses that announce a length for a body that is not sent.
            var head = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, ResponseHeaders(200, ("content-length", "1000")))), method: "HEAD");
            runner.AreEqual(200, head.Response?.StatusCode ?? -1, $"HEAD (was: {Describe(head.Failure)})");
            foreach (var status in new[] { 204, 304 })
            {
                var none = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, ResponseHeaders(status, ("content-length", "1000")))));
                runner.AreEqual(status, none.Response?.StatusCode ?? -1, $"{status} (was: {Describe(none.Failure)})");
            }

            // Not a plain number: nothing to compare with, so no judgement; two numbers that disagree: ambiguous.
            var unparsable = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200, ("content-length", "5, 5")), DataFrame(7))));
            runner.AreEqual(7, unparsable.Response?.Body.Length ?? -1, $"an unparsable Content-Length is not enforced (was: {Describe(unparsable.Failure)})");
            var conflicting = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200, ("content-length", "5"), ("content-length", "6")), DataFrame(5))));
            runner.IsTrue(conflicting.Failure is HttpParseException, $"two different Content-Length values (was: {Describe(conflicting.Failure)})");
        });

        await runner.RunAsync("HTTP/3: a trailer section carries no pseudo-headers, and a second response is not a trailer", async () =>
        {
            var statusInTrailer = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200), DataFrame(5), Frame((long)Http3FrameType.Headers, QpackEncoder.Encode([(":status", "500")])))));
            runner.IsTrue(statusInTrailer.Failure is HttpParseException, $":status in trailers (was: {Describe(statusInTrailer.Failure)})");

            var lateInterim = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200), DataFrame(5), ResponseHeaders(103))));
            runner.IsTrue(lateInterim.Failure is HttpParseException, $"a 1xx after the final response (was: {Describe(lateInterim.Failure)})");

            var pathInTrailer = await ExchangeAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200), Frame((long)Http3FrameType.Headers, QpackEncoder.Encode([(":path", "/")])))));
            runner.IsTrue(pathInTrailer.Failure is HttpParseException, $"any pseudo-header (was: {Describe(pathInTrailer.Failure)})");
        });
    }

    // ------------------------------------------------------------------ control stream

    private static async Task RunControlStreamTestsAsync(TestRunner runner, CertificateAuthority ca)
    {
        await runner.RunAsync("HTTP/3: the origin's SETTINGS are parsed and kept", async () =>
        {
            var behavior = Control(
                ControlStream(Settings((Http3SettingId.MaxFieldSectionSize, 4096), (Http3SettingId.QpackMaxTableCapacity, 0), (0x21, 5))),
                respond: async (s, ct) =>
                {
                    await Task.Delay(500, ct); // let the control stream land first
                    await SendAsync(s, ct, true, ResponseHeaders(200), DataFrame(3));
                });
            var outcome = await ExchangeAsync(ca, behavior);

            runner.AreEqual(3, outcome.Response?.Body.Length ?? -1, $"the request is unaffected (was: {Describe(outcome.Failure)})");
            runner.AreEqual((long?)4096, outcome.PeerSettings?.MaxFieldSectionSize, "MAX_FIELD_SECTION_SIZE was read from the control stream");
        });

        await runner.RunAsync("HTTP/3: a bad control stream fails the request and closes with the right code", async () =>
        {
            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(GoAway(8)))),
                Http3ErrorCode.MissingSettings, "first frame is not SETTINGS");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings(), Settings()))),
                Http3ErrorCode.FrameUnexpected, "a second SETTINGS");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings((0x02, 1))))),
                Http3ErrorCode.SettingsError, "an HTTP/2-only setting");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings((0x06, 1), (0x06, 2))))),
                Http3ErrorCode.SettingsError, "a repeated setting");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings()), keepOpen: false)),
                Http3ErrorCode.ClosedCriticalStream, "the control stream is closed");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings(), Frame((long)Http3FrameType.Data, [1])))),
                Http3ErrorCode.FrameUnexpected, "DATA on the control stream");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings(), Frame((long)Http3FrameType.MaxPushId, [1])))),
                Http3ErrorCode.FrameUnexpected, "MAX_PUSH_ID from the origin");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings(), Frame(0x08, [1])))),
                Http3ErrorCode.FrameUnexpected, "a frame type reserved from HTTP/2");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings(), Frame((long)Http3FrameType.CancelPush, [1])))),
                Http3ErrorCode.IdError, "CANCEL_PUSH for a push that never existed");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Frame((long)Http3FrameType.Settings, new byte[5000])))),
                Http3ErrorCode.FrameError, "an oversize SETTINGS frame");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings(), Frame((long)Http3FrameType.GoAway, [])))),
                Http3ErrorCode.FrameError, "an empty GOAWAY");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings(), Frame((long)Http3FrameType.GoAway, [0x04, 0x00])))),
                Http3ErrorCode.FrameError, "a GOAWAY with trailing bytes");

            var floodOfGrease = ControlStream([Settings(), .. Enumerable.Repeat(Frame(0x40, [0]), 100)]);
            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(floodOfGrease)),
                Http3ErrorCode.ExcessiveLoad, "an endless run of ignorable control frames");
        });

        await runner.RunAsync("HTTP/3: extra or forbidden unidirectional streams are connection errors", async () =>
        {
            ExpectProtocolError(runner,
                await ExchangeAsync(ca, new TestHttp3Origin.Behavior
                {
                    OnConnected = async (connection, ct) =>
                    {
                        var second = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
                        await second.WriteAsync(ControlStream(Settings()), completeWrites: false, ct);
                    },
                    RespondRaw = (_, ct) => Silence(ct),
                }),
                Http3ErrorCode.StreamCreationError, "a second control stream");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, new TestHttp3Origin.Behavior
                {
                    OnConnected = async (connection, ct) =>
                    {
                        var push = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
                        await push.WriteAsync(new byte[] { (byte)Http3StreamType.Push, 0x00 }, completeWrites: false, ct);
                    },
                    RespondRaw = (_, ct) => Silence(ct),
                }),
                Http3ErrorCode.IdError, "a push stream although push was never enabled");

            // Too many streams in total, even if each is harmless (type 0x21 is unknown, so ignored).
            ExpectProtocolError(runner,
                await ExchangeAsync(ca, new TestHttp3Origin.Behavior
                {
                    OnConnected = async (connection, ct) =>
                    {
                        for (var i = 0; i < 40; i++)
                        {
                            var grease = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
                            await grease.WriteAsync(new byte[] { 0x21 }, completeWrites: true, ct);
                        }
                    },
                    RespondRaw = (_, ct) => Silence(ct),
                }),
                Http3ErrorCode.ExcessiveLoad, "forty streams");

            // An unknown stream type alone is not an error.
            var tolerated = await ExchangeAsync(ca, new TestHttp3Origin.Behavior
            {
                OnConnected = async (connection, ct) =>
                {
                    var grease = await connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct);
                    await grease.WriteAsync(new byte[] { 0x21, 1, 2, 3 }, completeWrites: false, ct);
                },
                RespondRaw = (s, ct) => SendAsync(s, ct, true, ResponseHeaders(200), DataFrame(4)),
            });
            runner.AreEqual(4, tolerated.Response?.Body.Length ?? -1, $"a stream of an unknown type is ignored (was: {Describe(tolerated.Failure)})");
        });

        await runner.RunAsync("HTTP/3: GOAWAY decides whether the request is processed (RFC 9114 section 5.2)", async () =>
        {
            // The request is stream 0. GOAWAY(0): that stream and above will not be processed.
            var refused = await ExchangeAsync(ca, Control(ControlStream(Settings(), GoAway(0))));
            runner.IsTrue(refused.Failure is Http3GoAwayException { GoAwayStreamId: 0 },
                $"GOAWAY(0) refuses the request, and says it is safe to repeat (was: {Describe(refused.Failure)})");
            runner.AreEqual((long?)0, refused.GoAwayStreamId, "and is exposed");
            runner.IsTrue(refused.Elapsed < TimeSpan.FromSeconds(5), $"without waiting out the idle timeout ({refused.Elapsed.TotalSeconds:0.0} s)");

            // GOAWAY(4): streams below 4 -- ours -- are still processed.
            var processed = await ExchangeAsync(ca, Control(ControlStream(Settings(), GoAway(4)),
                respond: async (s, ct) =>
                {
                    await Task.Delay(500, ct);
                    await SendAsync(s, ct, true, ResponseHeaders(200), DataFrame(6));
                }));
            runner.AreEqual(6, processed.Response?.Body.Length ?? -1, $"GOAWAY(4) lets request 0 finish (was: {Describe(processed.Failure)})");
            runner.AreEqual((long?)4, processed.GoAwayStreamId, "the limit is recorded");

            // A tightening GOAWAY after the first.
            var tightened = await ExchangeAsync(ca, Control(ControlStream(Settings(), GoAway(8), GoAway(0))));
            runner.IsTrue(tightened.Failure is Http3GoAwayException, $"GOAWAY(8) then GOAWAY(0) (was: {Describe(tightened.Failure)})");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings(), GoAway(5)))),
                Http3ErrorCode.IdError, "GOAWAY of an id that is not a client-initiated bidirectional stream");

            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream(Settings(), GoAway(4), GoAway(8)))),
                Http3ErrorCode.IdError, "a GOAWAY that raises the limit");

            // Repeats that change nothing are still bounded.
            ExpectProtocolError(runner,
                await ExchangeAsync(ca, Control(ControlStream([Settings(), .. Enumerable.Repeat(GoAway(8), 20)]))),
                Http3ErrorCode.ExcessiveLoad, "twenty GOAWAY frames");
        });

        await runner.RunAsync("HTTP/3: a GOAWAY that arrives after the request was sent still refuses it", async () =>
        {
            // The GOAWAY is held back until the origin has read the whole request, so the request stream
            // is registered by the time it lands: the other order of the race, made certain.
            var requestSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var behavior = new TestHttp3Origin.Behavior
            {
                WriteControlStream = async (stream, ct) =>
                {
                    await stream.WriteAsync(ControlStream(Settings()), completeWrites: false, ct);
                    await stream.FlushAsync(ct);
                    await requestSeen.Task.WaitAsync(ct);
                    await stream.WriteAsync(GoAway(0), completeWrites: false, ct);
                    await stream.FlushAsync(ct);
                },
                RespondRaw = (_, ct) =>
                {
                    requestSeen.TrySetResult();
                    return Silence(ct);
                },
            };

            var outcome = await ExchangeAsync(ca, behavior);
            runner.IsTrue(outcome.Failure is Http3GoAwayException { GoAwayStreamId: 0 },
                $"refused while waiting for the response (was: {Describe(outcome.Failure)})");
            runner.IsTrue(outcome.Elapsed < TimeSpan.FromSeconds(5), $"promptly ({outcome.Elapsed.TotalSeconds:0.0} s)");
        });

        await runner.RunAsync("HTTP/3: a reset control stream is a closed critical stream", async () =>
        {
            var behavior = new TestHttp3Origin.Behavior
            {
                WriteControlStream = async (stream, ct) =>
                {
                    await stream.WriteAsync(ControlStream(Settings()), completeWrites: false, ct);
                    await Task.Delay(200, ct);
                    stream.Abort(QuicAbortDirection.Write, (long)Http3ErrorCode.NoError);
                },
                RespondRaw = (_, ct) => Silence(ct),
            };
            ExpectProtocolError(runner, await ExchangeAsync(ca, behavior), Http3ErrorCode.ClosedCriticalStream, "the control stream is reset");
        });
    }

    // ------------------------------------------------------------------ idle timeout

    private static async Task RunIdleTimeoutTestsAsync(TestRunner runner, CertificateAuthority ca)
    {
        await runner.RunAsync("HTTP/3: an origin that never answers is cut by the idle timeout", async () =>
        {
            var outcome = await ExchangeAsync(ca, Respond((_, ct) => Silence(ct)), c => c.IdleTimeout = TimeSpan.FromMilliseconds(400));
            runner.IsTrue(outcome.Failure is TimeoutException, $"timeout (was: {Describe(outcome.Failure)})");
            runner.IsTrue(outcome.Elapsed < TimeSpan.FromSeconds(5), $"promptly ({outcome.Elapsed.TotalSeconds:0.0} s)");
        });

        await runner.RunAsync("HTTP/3: a body that stalls part-way is cut by the idle timeout", async () =>
        {
            var outcome = await ExchangeAsync(ca,
                Respond(async (s, ct) =>
                {
                    await SendAsync(s, ct, false, ResponseHeaders(200), DataFrame(1000));
                    await Silence(ct);
                }),
                c => c.IdleTimeout = TimeSpan.FromMilliseconds(400));
            runner.IsTrue(outcome.Failure is TimeoutException, $"timeout (was: {Describe(outcome.Failure)})");
        });

        await runner.RunAsync("HTTP/3: a slow download that keeps arriving outlives the idle timeout", async () =>
        {
            // 10 pieces, 200 ms apart: 2 s in all, against a 1 s idle timeout.
            var outcome = await ExchangeAsync(ca,
                Respond(async (s, ct) =>
                {
                    await SendAsync(s, ct, false, ResponseHeaders(200));
                    for (var i = 0; i < 10; i++)
                    {
                        await SendAsync(s, ct, false, DataFrame(8 * 1024));
                        await Task.Delay(200, ct);
                    }
                    await SendAsync(s, ct, true);
                }),
                c => c.IdleTimeout = TimeSpan.FromSeconds(1));
            runner.AreEqual(10 * 8 * 1024, outcome.Response?.Body.Length ?? -1, $"all of it (was: {Describe(outcome.Failure)})");
            runner.IsTrue(outcome.Elapsed > TimeSpan.FromMilliseconds(1_500), $"and it did take longer than the timeout ({outcome.Elapsed.TotalSeconds:0.0} s)");
        });

        await runner.RunAsync("HTTP/3: a trickle inside one frame counts as progress", async () =>
        {
            // One 4 KiB DATA frame whose payload arrives in 8 slices 200 ms apart.
            var frame = DataFrame(4096);
            var outcome = await ExchangeAsync(ca,
                Respond(async (s, ct) =>
                {
                    await SendAsync(s, ct, false, ResponseHeaders(200));
                    for (var offset = 0; offset < frame.Length; offset += 520)
                    {
                        await SendAsync(s, ct, false, frame[offset..Math.Min(frame.Length, offset + 520)]);
                        await Task.Delay(200, ct);
                    }
                    await SendAsync(s, ct, true);
                }),
                c => c.IdleTimeout = TimeSpan.FromSeconds(1));
            runner.AreEqual(4096, outcome.Response?.Body.Length ?? -1, $"the frame arrived whole (was: {Describe(outcome.Failure)})");
        });

        await runner.RunAsync("HTTP/3: empty DATA frames and ignorable (unknown-type) frames are not progress", async () =>
        {
            foreach (var (name, frame) in new[] { ("empty DATA", DataFrame(0)), ("ignorable", Frame(0x40, [1, 2, 3])) })
            {
                var outcome = await ExchangeAsync(ca,
                    Respond(async (s, ct) =>
                    {
                        await SendAsync(s, ct, false, ResponseHeaders(200));
                        for (var i = 0; i < 60; i++) // within the 64-frame allowance, so only time can end it
                        {
                            await SendAsync(s, ct, false, frame);
                            await Task.Delay(60, ct);
                        }
                    }),
                    c => c.IdleTimeout = TimeSpan.FromMilliseconds(500));
                runner.IsTrue(outcome.Failure is TimeoutException, $"a stream of {name} frames keeps nothing alive (was: {Describe(outcome.Failure)})");
                runner.IsTrue(outcome.Elapsed < TimeSpan.FromSeconds(3), $"{name}: cut near the idle timeout ({outcome.Elapsed.TotalSeconds:0.0} s)");
            }
        });

        await runner.RunAsync("HTTP/3: a trickle of body bytes does not keep the request alive", async () =>
        {
            // One byte every 300 ms against a 1 s idle timeout: each byte arrives inside the window, so an
            // idle timer re-armed by any byte would run for ever. A window has to carry real bytes.
            var outcome = await ExchangeAsync(ca,
                Respond(async (s, ct) =>
                {
                    await SendAsync(s, ct, false, ResponseHeaders(200));
                    for (var i = 0; i < 100; i++)
                    {
                        await SendAsync(s, ct, false, DataFrame(1));
                        await Task.Delay(300, ct);
                    }
                }),
                c => c.IdleTimeout = TimeSpan.FromSeconds(1));
            runner.IsTrue(outcome.Failure is TimeoutException, $"cut (was: {Describe(outcome.Failure)})");
            runner.IsTrue(outcome.Elapsed < TimeSpan.FromSeconds(4), $"within a couple of idle periods, not 30 s ({outcome.Elapsed.TotalSeconds:0.0} s)");

            // The same rate with the minimum lowered to one byte per window is allowed: it is a setting.
            var allowed = await ExchangeAsync(ca,
                Respond(async (s, ct) =>
                {
                    await SendAsync(s, ct, false, ResponseHeaders(200));
                    for (var i = 0; i < 6; i++)
                    {
                        await SendAsync(s, ct, false, DataFrame(1));
                        await Task.Delay(300, ct);
                    }
                    await SendAsync(s, ct, true);
                }),
                c => { c.IdleTimeout = TimeSpan.FromSeconds(1); c.MinProgressBytes = 1; });
            runner.AreEqual(6, allowed.Response?.Body.Length ?? -1, $"with a one-byte minimum the same trickle completes (was: {Describe(allowed.Failure)})");
        });

        await runner.RunAsync("HTTP/3: a response has an overall time ceiling, however steadily it progresses", async () =>
        {
            // 8 KiB every 200 ms is healthy by any idle measure, for ever; the ceiling is 1.5 s.
            var outcome = await ExchangeAsync(ca,
                Respond(async (s, ct) =>
                {
                    await SendAsync(s, ct, false, ResponseHeaders(200));
                    while (true)
                    {
                        await SendAsync(s, ct, false, DataFrame(8 * 1024));
                        await Task.Delay(200, ct);
                    }
                }),
                c => { c.IdleTimeout = TimeSpan.FromSeconds(1); c.MaxResponseTime = TimeSpan.FromMilliseconds(1500); });
            runner.IsTrue(outcome.Failure is TimeoutException { Message: var m } && m.Contains("longer than", StringComparison.Ordinal),
                $"cut by the ceiling (was: {Describe(outcome.Failure)})");
            runner.IsTrue(outcome.Elapsed < TimeSpan.FromSeconds(5), $"({outcome.Elapsed.TotalSeconds:0.0} s)");

            // A non-positive ceiling is refused up front, where it cannot be mistaken for an origin fault.
            var threw = false;
            try
            {
                await using var origin = await StartOrigin(ca, new TestHttp3Origin.Behavior());
                await using var connection = await Http3ClientConnection.ConnectAsync("127.0.0.1", origin.Port,
                    new ProxyOptions { ValidateUpstreamCertificates = false }, CancellationToken.None);
                connection.MaxResponseTime = TimeSpan.Zero;
            }
            catch (ArgumentOutOfRangeException) { threw = true; }
            runner.IsTrue(threw, "MaxResponseTime rejects zero");
        });

        await runner.RunAsync("HTTP/3: a stream reset part-way through the body fails the request", async () =>
        {
            var outcome = await ExchangeAsync(ca,
                Respond(async (s, ct) =>
                {
                    await SendAsync(s, ct, false, ResponseHeaders(200), DataFrame(2000));
                    await Task.Delay(100, ct);
                    s.Abort(QuicAbortDirection.Write, (long)Http3ErrorCode.InternalError);
                }));
            runner.IsTrue(outcome.Failure is QuicException, $"a reset is a failure, never a complete response (was: {Describe(outcome.Failure)})");
            runner.IsTrue(outcome.Response is null, "and no partial body is returned");
        });

        await runner.RunAsync("HTTP/3: the caller's cancellation is not mistaken for a timeout", async () =>
        {
            var outcome = await ExchangeAsync(ca, Respond((_, ct) => Silence(ct)), cancelRequestAfter: TimeSpan.FromMilliseconds(300));
            runner.IsTrue(outcome.Failure is OperationCanceledException,
                $"cancelled, not timed out (was: {Describe(outcome.Failure)})");
        });
    }

    // ------------------------------------------------------------------ the attempt

    private sealed record Attempt(HttpResponseData? Response, bool Eligible, int Sent, Exception? Failure, AltSvcCache Cache, int OriginPort);

    // The whole decision as the proxy sees it: Http3Attempt against an origin, with an Alt-Svc cache
    // that already knows the origin speaks h3 on the right UDP port.
    private static async Task<Attempt> AttemptAsync(
        CertificateAuthority ca, TestHttp3Origin.Behavior behavior, Action<ProxyOptions>? configure = null,
        CancellationToken ct = default, Action<AltSvcCache>? configureCache = null, string method = "GET")
    {
        await using var origin = await StartOrigin(ca, behavior);
        var options = new ProxyOptions
        {
            ValidateUpstreamCertificates = false,
            EnableHttp3Upstream = true,
            Http3ConnectTimeout = TimeSpan.FromSeconds(5),
            Http3ResponseTimeout = TimeSpan.FromSeconds(1),
        };
        configure?.Invoke(options);

        var altSvc = new AltSvcCache();
        configureCache?.Invoke(altSvc);
        altSvc.RecordAltSvc("127.0.0.1", origin.Port, $"h3=\":{origin.Port}\"; ma=3600");

        var sent = 0;
        HttpResponseData? response = null;
        Exception? failure = null;
        try
        {
            response = await Http3Attempt.TryFetchAsync(Get(origin.Port, method), new Uri($"https://127.0.0.1:{origin.Port}/resource"),
                options, altSvc, () => sent++, ct);
        }
        catch (Exception ex) { failure = ex; }

        // Through the cache's own state, so the answer does not depend on QUIC being available here.
        return new Attempt(response, altSvc.TryGetRecorded("127.0.0.1", origin.Port, out _), sent, failure, altSvc, origin.Port);
    }

    private static async Task RunAttemptTestsAsync(TestRunner runner, CertificateAuthority ca)
    {
        await runner.RunAsync("Http3Attempt: a slow download that keeps arriving completes and the host stays eligible", async () =>
        {
            // 2.4 s in all, against a 1 s response timeout: the old whole-response budget would have
            // cut this, restarted it over TCP from byte 0 and barred the host for 30 minutes.
            var attempt = await AttemptAsync(ca, Respond(async (s, ct) =>
            {
                await SendAsync(s, ct, false, ResponseHeaders(200));
                for (var i = 0; i < 12; i++)
                {
                    await SendAsync(s, ct, false, DataFrame(8 * 1024));
                    await Task.Delay(200, ct);
                }
                await SendAsync(s, ct, true);
            }));

            runner.AreEqual(12 * 8 * 1024, attempt.Response?.Body.Length ?? -1, $"the whole body came over h3 (was: {Describe(attempt.Failure)})");
            runner.AreEqual(1, attempt.Sent, "the request was marked sent once");
            runner.AreEqual(true, attempt.Eligible, "and the host is still eligible for h3");
        });

        await runner.RunAsync("Http3Attempt: a body that stalls falls back to TCP and bars the host", async () =>
        {
            var attempt = await AttemptAsync(ca, Respond(async (s, ct) =>
            {
                await SendAsync(s, ct, false, ResponseHeaders(200), DataFrame(2000));
                await Silence(ct);
            }));

            runner.IsTrue(attempt.Response is null && attempt.Failure is null, $"null, so the caller uses TCP (was: {Describe(attempt.Failure)})");
            runner.AreEqual(false, attempt.Eligible, "a path that stops delivering part-way (an MTU black hole looks like this) would otherwise cost every large response an idle period");
        });

        await runner.RunAsync("Http3Attempt: no answer at all falls back to TCP and bars the host", async () =>
        {
            var attempt = await AttemptAsync(ca, Respond((_, ct) => Silence(ct)));
            runner.IsTrue(attempt.Response is null && attempt.Failure is null, $"null (was: {Describe(attempt.Failure)})");
            runner.AreEqual(false, attempt.Eligible, "a connection that completes the handshake and then says nothing is what a UDP-dropping network looks like");
        });

        await runner.RunAsync("Http3Attempt: an oversize response falls back and leaves the origin on TCP only for the soft cool-down", async () =>
        {
            var attempt = await AttemptAsync(ca,
                Respond((s, ct) => SendAsync(s, ct, true, ResponseHeaders(200, ("content-length", "999999999")))),
                configureCache: c => c.SoftCooldown = TimeSpan.FromMilliseconds(700));
            runner.IsTrue(attempt.Response is null && attempt.Failure is null, $"null (was: {Describe(attempt.Failure)})");
            runner.AreEqual(false, attempt.Eligible, "not retried at once: a hostile origin would cost a handshake and a response every time");

            await Task.Delay(900);
            runner.IsTrue(attempt.Cache.TryGetRecorded("127.0.0.1", attempt.OriginPort, out _),
                "but nothing is wrong with h3 itself, so the cool-down is short");
        });

        await runner.RunAsync("Http3Attempt: GOAWAY falls back for the soft cool-down; a protocol violation bars for the long one", async () =>
        {
            var goAway = await AttemptAsync(ca, Control(ControlStream(Settings(), GoAway(0))),
                configureCache: c => c.SoftCooldown = TimeSpan.FromMilliseconds(700));
            runner.IsTrue(goAway.Response is null && goAway.Failure is null, $"null (was: {Describe(goAway.Failure)})");
            runner.AreEqual(false, goAway.Eligible, "a GOAWAY origin is not tried again at once");
            await Task.Delay(900);
            runner.IsTrue(goAway.Cache.TryGetRecorded("127.0.0.1", goAway.OriginPort, out _), "an origin that is shutting down is not an origin without h3");

            var violation = await AttemptAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, DataFrame(5), ResponseHeaders(200))));
            runner.IsTrue(violation.Response is null && violation.Failure is null, $"null (was: {Describe(violation.Failure)})");
            runner.AreEqual(false, violation.Eligible, "DATA before HEADERS makes the host's h3 untrustworthy for the cool-down");

            var badSection = await AttemptAsync(ca, Respond((s, ct) => SendAsync(s, ct, true, Frame((long)Http3FrameType.Headers, new byte[200 * 1024]))));
            runner.IsTrue(badSection.Response is null, "an oversize header section also falls back");
            runner.AreEqual(false, badSection.Eligible, "and bars the host");
        });

        await runner.RunAsync("Http3Attempt: the Alt-Svc port is where the UDP connection goes", async () =>
        {
            await using var origin = await StartOrigin(ca, new TestHttp3Origin.Behavior());
            var options = new ProxyOptions { ValidateUpstreamCertificates = false, EnableHttp3Upstream = true };

            // The URL says port 443 (nothing listens there over QUIC); the header says where h3 lives.
            var altSvc = new AltSvcCache();
            altSvc.RecordAltSvc("127.0.0.1", 443, $"h3=\":{origin.Port}\"");
            var url = new Uri("https://127.0.0.1/resource");
            var request = new HttpRequestData { Method = "GET", Url = url };
            var response = await Http3Attempt.TryFetchAsync(request, url, options, altSvc, () => { }, CancellationToken.None);

            runner.AreEqual("ok", response?.BodyAsText(), "answered by the origin on the advertised port");
        });

        await runner.RunAsync("Http3Attempt: a short body falls back and bars the host instead of counting as a success", async () =>
        {
            var attempt = await AttemptAsync(ca, Respond((s, ct) => SendAsync(s, ct, true,
                ResponseHeaders(200, ("content-length", "1000")), DataFrame(500))));
            runner.IsTrue(attempt.Response is null && attempt.Failure is null, $"null, so the caller refetches over TCP (was: {Describe(attempt.Failure)})");
            runner.AreEqual(false, attempt.Eligible, "and the bad framing is held against the origin's h3");
        });

        await runner.RunAsync("Http3Attempt: a trickle falls back and bars the host", async () =>
        {
            var attempt = await AttemptAsync(ca, Respond(async (s, ct) =>
            {
                await SendAsync(s, ct, false, ResponseHeaders(200));
                for (var i = 0; i < 100; i++)
                {
                    await SendAsync(s, ct, false, DataFrame(1));
                    await Task.Delay(300, ct);
                }
            }));
            runner.IsTrue(attempt.Response is null && attempt.Failure is null, $"null (was: {Describe(attempt.Failure)})");
            runner.AreEqual(false, attempt.Eligible, "one byte per period is not a download");
        });

        await runner.RunAsync("Http3Attempt: a non-positive timeout setting neither throws nor bars the host", async () =>
        {
            var attempt = await AttemptAsync(ca, new TestHttp3Origin.Behavior(),
                options => { options.Http3ResponseTimeout = TimeSpan.Zero; options.Http3MaxResponseTime = TimeSpan.FromSeconds(-5); });
            runner.AreEqual("ok", attempt.Response?.BodyAsText(), $"the defaults apply instead (was: {Describe(attempt.Failure)})");
            runner.AreEqual(true, attempt.Eligible, "and the host stays eligible");
        });

        await runner.RunAsync("Http3Attempt: the caller's cancellation propagates and does not bar the host", async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            var attempt = await AttemptAsync(ca, Respond((_, ct) => Silence(ct)),
                options => options.Http3ResponseTimeout = TimeSpan.FromSeconds(30), cts.Token);

            runner.IsTrue(attempt.Failure is OperationCanceledException, $"cancelled (was: {Describe(attempt.Failure)})");
            runner.AreEqual(true, attempt.Eligible, "a user cancelling says nothing about the origin");
        });

        await runner.RunAsync("Http3Attempt: methods with side effects and non-https URLs never reach h3", async () =>
        {
            var options = new ProxyOptions { EnableHttp3Upstream = true };
            var altSvc = new AltSvcCache();
            altSvc.RecordAltSvc("example.com", 443, "h3=\":443\"");

            var secure = new Uri("https://example.com/");
            var post = new HttpRequestData { Method = "POST", Url = secure };
            runner.IsTrue(await Http3Attempt.TryFetchAsync(post, secure, options, altSvc, () => { }, CancellationToken.None) is null, "POST");

            var insecure = new Uri("http://example.com/");
            var plain = new HttpRequestData { Method = "GET", Url = insecure };
            runner.IsTrue(await Http3Attempt.TryFetchAsync(plain, insecure, options, altSvc, () => { }, CancellationToken.None) is null, "http");
        });
    }
}
