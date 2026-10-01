using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Http2.Hpack;

// S7: the server-role HTTP/2 connection and the HPACK decoder against a hostile peer. Every frame
// here is one an HttpClient or a browser would never send. The HPACK cases run against the
// decoder alone; the rest drive a real Http2Connection over loopback TCP with a hand-written
// peer, so what is asserted is what a peer would see on the wire (RFC 9113 §5.4.1, §7).
internal static class Http2ServerHardeningTests
{
    private const uint MaxWindow = int.MaxValue;

    public static async Task RunAsync(TestRunner runner)
    {
        await HpackDecoderAsync(runner);
        await ProtocolErrorsAsync(runner);
        await FlowControlAsync(runner);
        await FloodsAsync(runner);
        await TimeoutsAsync(runner);
        await GoAwayAsync(runner);
        await BookkeepingAsync(runner);
    }

    // ------------------------------------------------------------------ HPACK decoder

    private static async Task HpackDecoderAsync(TestRunner runner)
    {
        // RFC 7541 §5.1 lets an integer be arbitrarily long; the decoder turns it into an int, so
        // anything past int.MaxValue used to wrap, or turn negative, instead of being refused. A
        // wrapped value even decoded as some other, valid, index.
        var hostileBlocks = new (string Name, byte[] Block)[]
        {
            ("indexed field, index above int.MaxValue (negative when cast)", Int(7, 0x80, (1UL << 31) + 5)),
            ("indexed field, index that wraps to 2 (:method GET) when cast", Int(7, 0x80, (1UL << 32) + 2)),
            ("indexed field, index of ulong.MaxValue", Int(7, 0x80, ulong.MaxValue)),
            ("indexed field, index of int.MaxValue", Int(7, 0x80, int.MaxValue)),
            ("literal with indexing, name index above int.MaxValue", Int(6, 0x40, (1UL << 31) + 1)),
            ("literal with indexing, name index that wraps to 1", Int(6, 0x40, (1UL << 32) + 1)),
            ("literal without indexing, name index above int.MaxValue", Int(4, 0x00, 1UL << 31)),
            ("literal never indexed, name index above int.MaxValue", Int(4, 0x10, (1UL << 40) + 3)),
            ("table size update above int.MaxValue (negative when cast)", Int(5, 0x20, 1UL << 31)),
            ("table size update that wraps to 100 when cast", Int(5, 0x20, (1UL << 32) + 100)),
            ("table size update of int.MaxValue", Int(5, 0x20, int.MaxValue)),
            ("string length of int.MaxValue (position plus length overflows)",
                [0x40, .. Int(7, 0x00, int.MaxValue)]),
            ("string length above int.MaxValue", [0x40, .. Int(7, 0x00, (1UL << 31) + 10)]),
            ("string length that wraps to 1 when cast",
                [0x40, .. Int(7, 0x00, (1UL << 32) + 1), (byte)'a', 0x01, (byte)'b']),
            ("integer with endless continuation bytes", [0xFF, .. Enumerable.Repeat((byte)0x80, 12), 0x00]),
            ("integer with a sixth continuation byte of zeroes", [0xFF, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00]),
            ("truncated integer", [0xFF]),
            ("truncated integer continuation", [0xFF, 0x81]),
            ("string longer than the block", [0x40, 0x05, (byte)'a']),
            ("string with no length", [0x40]),
            ("index 0", [0x80]),
            ("index past the tables", Int(7, 0x80, 1000)),
        };

        foreach (var (name, block) in hostileBlocks)
        {
            await runner.RunAsync($"HpackDecoder rejects {name} with a decoding error only", () =>
            {
                var outcome = DecodeOutcome(block);
                runner.AreEqual("HttpParseException", outcome, "the only failure a caller has to handle");
                return Task.CompletedTask;
            });
        }

        await runner.RunAsync("HpackDecoder still decodes the values just below every limit", () =>
        {
            // Index 61 is the last static entry. Index 62+ needs the dynamic table: 100 entries make
            // the oldest one index 161, which takes a two-byte integer (127 + 34).
            var decoder = new HpackDecoder();
            var inserts = new List<byte>();
            for (var i = 0; i < 100; i++) inserts.AddRange(LiteralIndexed($"n{i:D3}", "v"));
            decoder.Decode(inserts.ToArray());

            var oldest = decoder.Decode(Int(7, 0x80, 161));
            runner.AreEqual(("n000", "v"), oldest[0], "index 161 is the oldest of 100 entries");
            var newest = decoder.Decode(Int(7, 0x80, 62));
            runner.AreEqual(("n099", "v"), newest[0], "index 62 is the newest");
            var lastStatic = decoder.Decode(Int(7, 0x80, 61));
            runner.AreEqual(("www-authenticate", ""), lastStatic[0], "index 61 is the last static entry");

            var update = decoder.Decode(Int(5, 0x20, 4096));
            runner.AreEqual(0, update.Count, "a size update at the advertised limit is accepted");
            var shrunk = decoder.Decode([.. Int(5, 0x20, 0), .. Int(7, 0x80, 2)]);
            runner.AreEqual((":method", "GET"), shrunk[0], "a size update to zero empties the table, static entries remain");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HpackDecoder never fails with anything but a decoding error on random blocks", () =>
        {
            var random = new Random(20260930);
            var seed = new HpackDecoder().Decode(Convert.FromHexString("828684418cf1e3c2e5f23a6ba0ab90f4ff"));
            runner.AreEqual(4, seed.Count, "the fuzzing seed itself decodes");

            var stray = new Dictionary<string, int>();
            var valid = Convert.FromHexString("828684418cf1e3c2e5f23a6ba0ab90f4ff");
            for (var i = 0; i < 30_000; i++)
            {
                byte[] block;
                if (i % 2 == 0)
                {
                    block = new byte[random.Next(1, 48)];
                    random.NextBytes(block);
                }
                else
                {
                    // A valid block with a few bytes flipped: reaches deeper states than pure noise.
                    block = (byte[])valid.Clone();
                    for (var flips = random.Next(1, 4); flips > 0; flips--)
                        block[random.Next(block.Length)] = (byte)random.Next(256);
                }

                var outcome = DecodeOutcome(block);
                if (outcome is not ("ok" or "HttpParseException"))
                    stray[outcome] = stray.GetValueOrDefault(outcome) + 1;
            }

            runner.AreEqual("", string.Join(", ", stray.Select(kv => $"{kv.Key} x{kv.Value}")), "no other exception type escaped the decoder");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Http2Settings clamps a peer's unsigned 32-bit values instead of wrapping them negative", () =>
        {
            var settings = new Http2Settings();
            settings.ApplyPeerPayload(
            [
                .. SettingsEntry(1, uint.MaxValue), .. SettingsEntry(3, uint.MaxValue), .. SettingsEntry(6, 0x8000_0000),
            ]);
            runner.AreEqual(int.MaxValue, settings.HeaderTableSize, "SETTINGS_HEADER_TABLE_SIZE");
            runner.AreEqual(int.MaxValue, settings.MaxConcurrentStreams ?? 0, "SETTINGS_MAX_CONCURRENT_STREAMS");
            runner.AreEqual(int.MaxValue, settings.MaxHeaderListSize ?? 0, "SETTINGS_MAX_HEADER_LIST_SIZE");

            settings.ApplyPeerPayload(SettingsEntry(3, 0));
            runner.AreEqual(0, settings.MaxConcurrentStreams ?? -1, "a genuine zero is kept");

            var threw = false;
            try { settings.ApplyPeerPayload(SettingsEntry(4, 0x8000_0000)); }
            catch (Http2ProtocolException ex) { threw = ex.ErrorCode == Http2ErrorCode.FlowControlError; }
            runner.IsTrue(threw, "an initial window above 2^31-1 is FLOW_CONTROL_ERROR");
            return Task.CompletedTask;
        });
    }

    private static string DecodeOutcome(byte[] block)
    {
        try
        {
            new HpackDecoder().Decode(block);
            return "ok";
        }
        catch (HttpParseException) { return "HttpParseException"; }
        catch (Exception ex) { return ex.GetType().Name; }
    }

    // ----------------------------------------------------- HPACK errors on a connection

    private static async Task ProtocolErrorsAsync(TestRunner runner)
    {
        await runner.RunAsync("Http2Connection answers an HPACK integer overflow with GOAWAY COMPRESSION_ERROR", async () =>
        {
            // The unchecked cast used to raise IndexOutOfRangeException, which the connection did not
            // catch: the peer got a closed socket and no reason (RFC 9113 §4.3, §7).
            await using var peer = await Peer.StartAsync();
            await peer.SendHeadersAsync(1, endStream: true, Int(7, 0x80, (1UL << 31) + 5));
            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.CompressionError, GoAwayCode(goAway), "COMPRESSION_ERROR");
            runner.IsTrue(peer.Requests.IsEmpty, "nothing reached the handler");
        });

        await runner.RunAsync("Http2Connection answers an oversize HPACK table size update with GOAWAY COMPRESSION_ERROR", async () =>
        {
            await using var peer = await Peer.StartAsync();
            await peer.SendHeadersAsync(1, endStream: true, [.. Int(5, 0x20, (1UL << 32) + 100), .. Get("/x")]);
            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.CompressionError, GoAwayCode(goAway), "COMPRESSION_ERROR");
        });

        await runner.RunAsync("Http2Connection answers an HPACK string length overflow with GOAWAY COMPRESSION_ERROR", async () =>
        {
            await using var peer = await Peer.StartAsync();
            await peer.SendHeadersAsync(1, endStream: true, [0x40, .. Int(7, 0x00, int.MaxValue)]);
            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.CompressionError, GoAwayCode(goAway), "COMPRESSION_ERROR");
        });

        var frameCases = new (string Name, Http2FrameType Type, Http2FrameFlags Flags, int StreamId, byte[] Payload, Http2ErrorCode Expected)[]
        {
            ("DATA on stream 0", Http2FrameType.Data, Http2FrameFlags.None, 0, "x"u8.ToArray(), Http2ErrorCode.ProtocolError),
            ("DATA on stream 0 with END_STREAM and no payload", Http2FrameType.Data, Http2FrameFlags.EndStream, 0, [], Http2ErrorCode.ProtocolError),
            ("DATA on an idle odd stream", Http2FrameType.Data, Http2FrameFlags.None, 5, "x"u8.ToArray(), Http2ErrorCode.ProtocolError),
            ("DATA on an even stream the server never opened", Http2FrameType.Data, Http2FrameFlags.None, 2, "x"u8.ToArray(), Http2ErrorCode.ProtocolError),
            ("WINDOW_UPDATE on an idle stream", Http2FrameType.WindowUpdate, Http2FrameFlags.None, 7, WindowUpdate(10), Http2ErrorCode.ProtocolError),
            ("RST_STREAM on an idle stream", Http2FrameType.RstStream, Http2FrameFlags.None, 9, RstStream(Http2ErrorCode.Cancel), Http2ErrorCode.ProtocolError),
            ("RST_STREAM on stream 0", Http2FrameType.RstStream, Http2FrameFlags.None, 0, RstStream(Http2ErrorCode.Cancel), Http2ErrorCode.ProtocolError),
            ("RST_STREAM with a short payload", Http2FrameType.RstStream, Http2FrameFlags.None, 1, [0, 0, 8], Http2ErrorCode.FrameSizeError),
            ("PUSH_PROMISE from a client", Http2FrameType.PushPromise, Http2FrameFlags.EndHeaders, 1, [0, 0, 0, 2], Http2ErrorCode.ProtocolError),
            ("SETTINGS acknowledgement with a payload", Http2FrameType.Settings, Http2FrameFlags.Ack, 0, SettingsEntry(3, 5), Http2ErrorCode.FrameSizeError),
        };

        foreach (var c in frameCases)
        {
            await runner.RunAsync($"Http2Connection answers {c.Name} with GOAWAY {c.Expected}", async () =>
            {
                await using var peer = await Peer.StartAsync();
                await peer.SendAsync(c.Type, c.Flags, c.StreamId, c.Payload);
                var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
                runner.AreEqual(c.Expected, GoAwayCode(goAway), "connection error code");
                runner.IsTrue(peer.Requests.IsEmpty, "nothing reached the handler");
            });
        }

        await runner.RunAsync("Http2Connection still drops DATA and WINDOW_UPDATE for a stream that is already closed", async () =>
        {
            // The strictness above is for streams that were never opened. A stream that finished, or
            // that this side reset, legitimately draws a few late frames (RFC 9113 §5.1, closed).
            await using var peer = await Peer.StartAsync();
            await peer.SendHeadersAsync(1, endStream: true, Get("/first"));
            runner.IsTrue(await peer.ReadUntilAsync(f => f.StreamId == 1 && f.HasFlag(Http2FrameFlags.EndStream)) is not null, "stream 1 answered");
            await Poll.UntilAsync(() => peer.Connection.InFlightHandlers == 0);

            await peer.SendAsync(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 1, WindowUpdate(100));
            await peer.SendAsync(Http2FrameType.RstStream, Http2FrameFlags.None, 1, RstStream(Http2ErrorCode.Cancel));
            await peer.SendAsync(Http2FrameType.Data, Http2FrameFlags.None, 1, "late"u8.ToArray());
            await peer.SendHeadersAsync(3, endStream: true, Get("/second"));

            var outcome = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway || (f.StreamId == 3 && f.HasFlag(Http2FrameFlags.EndStream)));
            runner.IsTrue(outcome is { Type: not Http2FrameType.GoAway }, "the connection survives and stream 3 is answered");
        });
    }

    // ---------------------------------------------------------------- flow control

    private static async Task FlowControlAsync(TestRunner runner)
    {
        await runner.RunAsync("a connection WINDOW_UPDATE past 2^31-1 is GOAWAY FLOW_CONTROL_ERROR", async () =>
        {
            await using var peer = await Peer.StartAsync();
            await peer.SendAsync(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 0, WindowUpdate(MaxWindow));
            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.FlowControlError, GoAwayCode(goAway), "RFC 9113 §6.9.1");
        });

        await runner.RunAsync("a connection WINDOW_UPDATE that lands exactly on 2^31-1 is accepted", async () =>
        {
            await using var peer = await Peer.StartAsync();
            await peer.SendAsync(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 0, WindowUpdate(MaxWindow - 65_535));
            await peer.SendHeadersAsync(1, endStream: true, Get("/ok"));
            var outcome = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway || (f.StreamId == 1 && f.HasFlag(Http2FrameFlags.EndStream)));
            runner.IsTrue(outcome is { Type: not Http2FrameType.GoAway }, "the boundary itself is legal and the stream is answered");
        });

        await runner.RunAsync("a stream WINDOW_UPDATE past 2^31-1 resets only that stream with FLOW_CONTROL_ERROR", async () =>
        {
            await using var peer = await Peer.StartAsync();
            await peer.SendHeadersAsync(1, endStream: false, Get("/upload", "POST"));
            await peer.SendAsync(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 1, WindowUpdate(MaxWindow));

            var reset = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway || (f.Type == Http2FrameType.RstStream && f.StreamId == 1));
            runner.IsTrue(reset is { Type: Http2FrameType.RstStream }, "a stream error, not a connection error");
            runner.AreEqual(Http2ErrorCode.FlowControlError, RstCode(reset), "FLOW_CONTROL_ERROR");

            await peer.SendHeadersAsync(3, endStream: true, Get("/after"));
            var after = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway || (f.StreamId == 3 && f.HasFlag(Http2FrameFlags.EndStream)));
            runner.IsTrue(after is { Type: not Http2FrameType.GoAway }, "the connection carries on and stream 3 is answered");
        });

        await runner.RunAsync("a stream window may reach 2^31-1 but no SETTINGS change may push it past", async () =>
        {
            await using var peer = await Peer.StartAsync();
            await peer.SendHeadersAsync(1, endStream: false, Get("/upload", "POST"));
            await peer.SendAsync(Http2FrameType.WindowUpdate, Http2FrameFlags.None, 1, WindowUpdate(MaxWindow - 65_535));
            await peer.SendAsync(Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8]);
            var pong = await peer.ReadUntilAsync(f => f.Type is Http2FrameType.Ping or Http2FrameType.GoAway or Http2FrameType.RstStream);
            runner.IsTrue(pong is { Type: Http2FrameType.Ping }, "exactly 2^31-1 is accepted");

            // RFC 9113 §6.9.2: raising the initial window by 1 moves this stream's window to 2^31.
            await peer.SendAsync(Http2FrameType.Settings, Http2FrameFlags.None, 0, SettingsEntry(4, 65_536));
            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.FlowControlError, GoAwayCode(goAway), "connection error FLOW_CONTROL_ERROR");
        });
    }

    // ---------------------------------------------------------------------- floods

    private static async Task FloodsAsync(TestRunner runner)
    {
        // The peer below never reads: the connection's writer is blocked, exactly as when a peer
        // stops draining its socket. Every reply that has to be queued then stays queued. Before the
        // cap, a PING, SETTINGS or reset flood grew the queue without limit (CVE-2019-9512, -9514,
        // -9515), and an ENHANCE_YOUR_CALM GOAWAY was never sent.
        const int flood = 1_500;

        await runner.RunAsync("a PING flood from a peer that is not reading ends with GOAWAY ENHANCE_YOUR_CALM", async () =>
        {
            await using var peer = await Peer.StartAsync(new PeerOptions { BlockWrites = true });
            for (var i = 0; i < flood; i++) await peer.SendAsync(Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8]);
            await peer.OpenWritesWhenAsync(() => peer.Connection.IsClosing);

            var (acks, goAway) = await peer.CountUntilGoAwayAsync(f => f.Type == Http2FrameType.Ping && f.HasFlag(Http2FrameFlags.Ack));
            runner.AreEqual(Http2ErrorCode.EnhanceYourCalm, GoAwayCode(goAway), "ENHANCE_YOUR_CALM");
            runner.IsTrue(acks < flood, $"the queue was cut off at the cap, not the whole flood answered ({acks} of {flood})");
            runner.IsTrue(acks <= Http2Connection.DefaultMaxPendingControlFrames, $"never more than the cap queued ({acks})");
        });

        await runner.RunAsync("a SETTINGS flood from a peer that is not reading ends with GOAWAY ENHANCE_YOUR_CALM", async () =>
        {
            await using var peer = await Peer.StartAsync(new PeerOptions { BlockWrites = true });
            for (var i = 0; i < flood; i++) await peer.SendAsync(Http2FrameType.Settings, Http2FrameFlags.None, 0, ReadOnlyMemory<byte>.Empty);
            await peer.OpenWritesWhenAsync(() => peer.Connection.IsClosing);

            var (acks, goAway) = await peer.CountUntilGoAwayAsync(f => f.Type == Http2FrameType.Settings && f.HasFlag(Http2FrameFlags.Ack));
            runner.AreEqual(Http2ErrorCode.EnhanceYourCalm, GoAwayCode(goAway), "ENHANCE_YOUR_CALM");
            runner.IsTrue(acks < flood, $"the queue was cut off at the cap ({acks} of {flood})");
        });

        await runner.RunAsync("a reset flood (malformed requests) from a peer that is not reading ends with GOAWAY ENHANCE_YOUR_CALM", async () =>
        {
            await using var peer = await Peer.StartAsync(new PeerOptions { BlockWrites = true });
            var noPath = HpackEncoder.Encode([(":method", "GET"), (":scheme", "https"), (":authority", "example.com")]);
            for (var i = 0; i < flood; i++) await peer.SendHeadersAsync(2 * i + 1, endStream: true, noPath);
            await peer.OpenWritesWhenAsync(() => peer.Connection.IsClosing);

            var (resets, goAway) = await peer.CountUntilGoAwayAsync(f => f.Type == Http2FrameType.RstStream);
            runner.AreEqual(Http2ErrorCode.EnhanceYourCalm, GoAwayCode(goAway), "ENHANCE_YOUR_CALM");
            runner.IsTrue(resets < flood, $"the queue was cut off at the cap ({resets} of {flood})");
            runner.IsTrue(peer.Requests.IsEmpty, "no malformed request reached the handler");
        });

        await runner.RunAsync("the control-frame cap counts what is still unwritten, not what was ever sent", async () =>
        {
            await using var peer = await Peer.StartAsync(new PeerOptions { BlockWrites = true });
            const int burst = 900;
            for (var i = 0; i < burst; i++) await peer.SendAsync(Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8]);
            await peer.OpenWritesWhenAsync(() => peer.Connection.PendingControlFrames >= burst);
            var seen = 0;
            var first = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway || (f.Type == Http2FrameType.Ping && ++seen == burst));
            runner.IsTrue(first is { Type: Http2FrameType.Ping }, "a burst under the cap is answered in full");

            // The queue has drained, so a second burst is again under the cap. Reading in between is
            // what keeps the queue short.
            for (var i = 0; i < burst; i++) await peer.SendAsync(Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8]);
            seen = 0;
            var second = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway || (f.Type == Http2FrameType.Ping && ++seen == burst));
            runner.IsTrue(second is { Type: Http2FrameType.Ping }, "and so is the next one, with no GOAWAY");
        });

        await runner.RunAsync("a configured control-frame cap is honoured", async () =>
        {
            await using var peer = await Peer.StartAsync(new PeerOptions { BlockWrites = true, MaxPendingControlFrames = 10 });
            for (var i = 0; i < 50; i++) await peer.SendAsync(Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8]);
            await peer.OpenWritesWhenAsync(() => peer.Connection.IsClosing);
            var (acks, goAway) = await peer.CountUntilGoAwayAsync(f => f.Type == Http2FrameType.Ping && f.HasFlag(Http2FrameFlags.Ack));
            runner.AreEqual(Http2ErrorCode.EnhanceYourCalm, GoAwayCode(goAway), "ENHANCE_YOUR_CALM");
            runner.IsTrue(acks <= 10, $"at most the configured cap was queued ({acks})");
        });
    }

    // -------------------------------------------------------------------- timeouts

    private static async Task TimeoutsAsync(TestRunner runner)
    {
        await runner.RunAsync("a client that goes silent after the handshake is sent GOAWAY NO_ERROR and dropped", async () =>
        {
            await using var peer = await Peer.StartAsync(new PeerOptions { IdleTimeout = TimeSpan.FromMilliseconds(300) });
            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.NoError, GoAwayCode(goAway), "a quiet close, not an error");
            runner.IsTrue(await peer.EndedWithinAsync(TimeSpan.FromSeconds(5)), "RunAsync returned");
        });

        await runner.RunAsync("a client that never sends the connection preface is dropped", async () =>
        {
            await using var peer = await Peer.StartAsync(new PeerOptions { IdleTimeout = TimeSpan.FromMilliseconds(300), SkipHandshake = true });
            runner.IsTrue(await peer.EndedWithinAsync(TimeSpan.FromSeconds(5)), "RunAsync returned");
            runner.IsTrue(peer.RunException is IOException, $"reported as a closed connection ({peer.RunException?.GetType().Name})");
        });

        await runner.RunAsync("any frame re-arms the idle timeout", async () =>
        {
            await using var peer = await Peer.StartAsync(new PeerOptions { IdleTimeout = TimeSpan.FromMilliseconds(500) });
            for (var i = 0; i < 12; i++)
            {
                await peer.SendAsync(Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8]);
                await Task.Delay(100);
            }

            // 1.2 s of traffic against a 0.5 s timeout: alive throughout. Only PING acks and the
            // handshake frames may have arrived by now.
            var early = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway, TimeSpan.FromMilliseconds(50));
            runner.IsTrue(early is null, "no GOAWAY while the peer kept talking");

            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.NoError, GoAwayCode(goAway), "and the silence afterwards is what ends it");
        });

        await runner.RunAsync("a request being handled keeps the connection open past the idle timeout", async () =>
        {
            await using var peer = await Peer.StartAsync(new PeerOptions
            {
                IdleTimeout = TimeSpan.FromMilliseconds(300),
                HandlerDelay = TimeSpan.FromMilliseconds(1_000),
            });
            await peer.SendHeadersAsync(1, endStream: true, Get("/slow"));
            var outcome = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway || (f.StreamId == 1 && f.HasFlag(Http2FrameFlags.EndStream)));
            runner.IsTrue(outcome is { Type: not Http2FrameType.GoAway }, "the slow response arrives; waiting on an origin is not idleness");

            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.NoError, GoAwayCode(goAway), "once the handler is done the idle clock runs");
        });

        await runner.RunAsync("a peer that stops reading is cut off after the idle timeout, not held for ever", async () =>
        {
            // The first write, the SETTINGS frame, blocks and never completes.
            await using var peer = await Peer.StartAsync(new PeerOptions { BlockWrites = true, IdleTimeout = TimeSpan.FromMilliseconds(300) });
            await peer.SendAsync(Http2FrameType.Ping, Http2FrameFlags.None, 0, new byte[8]);
            runner.IsTrue(await peer.EndedWithinAsync(TimeSpan.FromSeconds(5)), "RunAsync returned although its writer was blocked");
        });
    }

    // --------------------------------------------------------------------- GOAWAY

    private static async Task GoAwayAsync(TestRunner runner)
    {
        await runner.RunAsync("a peer GOAWAY lets the streams in flight finish, then the connection ends", async () =>
        {
            // RFC 9113 §6.8: GOAWAY is a graceful shutdown notice. It used to cancel every stream, so
            // a response the handler was about to send never arrived.
            await using var peer = await Peer.StartAsync(new PeerOptions { HandlerDelay = TimeSpan.FromMilliseconds(400) });
            await peer.SendHeadersAsync(1, endStream: true, Get("/draining"));
            await peer.SendAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, GoAway(0, Http2ErrorCode.NoError));

            var response = await peer.ReadUntilAsync(f => f.StreamId == 1 && f.HasFlag(Http2FrameFlags.EndStream));
            runner.IsTrue(response is not null, "the response for the stream in flight arrived");
            var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
            runner.AreEqual(Http2ErrorCode.NoError, GoAwayCode(goAway), "then this side says goodbye");
            runner.IsTrue(await peer.EndedWithinAsync(TimeSpan.FromSeconds(5)), "and RunAsync returns without waiting for the peer to close");
        });

        await runner.RunAsync("a stream opened after the peer's GOAWAY is refused, so the drain cannot be kept open", async () =>
        {
            await using var peer = await Peer.StartAsync(new PeerOptions { HandlerDelay = TimeSpan.FromMilliseconds(400) });
            await peer.SendHeadersAsync(1, endStream: true, Get("/before"));
            await peer.SendAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, GoAway(0, Http2ErrorCode.NoError));
            await peer.SendHeadersAsync(3, endStream: true, Get("/after"));
            await peer.SendHeadersAsync(5, endStream: true, Get("/after-too"));

            var refused = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.RstStream && f.StreamId == 3);
            runner.AreEqual(Http2ErrorCode.RefusedStream, RstCode(refused), "REFUSED_STREAM: nothing was processed");
            var answered = await peer.ReadUntilAsync(f => f.StreamId == 1 && f.HasFlag(Http2FrameFlags.EndStream));
            runner.IsTrue(answered is not null, "the stream opened before the GOAWAY is still answered");
            runner.AreEqual(1, peer.Requests.Count, "only that one reached the handler");
            runner.IsTrue(await peer.EndedWithinAsync(TimeSpan.FromSeconds(5)), "and the connection then ends");
        });

        await runner.RunAsync("a peer GOAWAY lets a request still being uploaded finish", async () =>
        {
            await using var peer = await Peer.StartAsync();
            await peer.SendHeadersAsync(1, endStream: false, Get("/upload", "POST"));
            await peer.SendAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, GoAway(0, Http2ErrorCode.NoError));
            await Task.Delay(100);
            await peer.SendAsync(Http2FrameType.Data, Http2FrameFlags.EndStream, 1, "body"u8.ToArray());

            var response = await peer.ReadUntilAsync(f => f.StreamId == 1 && f.HasFlag(Http2FrameFlags.EndStream));
            runner.IsTrue(response is not null, "the upload was completed and answered");
            runner.AreEqual("body", peer.Requests.SingleOrDefault()?.BodyAsText(), "with its whole body");
            runner.IsTrue(await peer.EndedWithinAsync(TimeSpan.FromSeconds(5)), "then the connection ends");
        });

        await runner.RunAsync("a peer GOAWAY with nothing in flight ends the connection at once", async () =>
        {
            await using var peer = await Peer.StartAsync();
            await peer.SendAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, GoAway(0, Http2ErrorCode.NoError));
            runner.IsTrue(await peer.EndedWithinAsync(TimeSpan.FromSeconds(5)), "RunAsync returned");
        });

        await runner.RunAsync("a GOAWAY on a stream is PROTOCOL_ERROR and a short one FRAME_SIZE_ERROR", async () =>
        {
            await using (var peer = await Peer.StartAsync())
            {
                await peer.SendAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 1, GoAway(0, Http2ErrorCode.NoError));
                var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
                runner.AreEqual(Http2ErrorCode.ProtocolError, GoAwayCode(goAway), "RFC 9113 §6.8: GOAWAY applies to the connection");
            }

            await using (var peer = await Peer.StartAsync())
            {
                await peer.SendAsync(Http2FrameType.GoAway, Http2FrameFlags.None, 0, new byte[] { 0, 0, 0, 0 });
                var goAway = await peer.ReadUntilAsync(f => f.Type == Http2FrameType.GoAway);
                runner.AreEqual(Http2ErrorCode.FrameSizeError, GoAwayCode(goAway), "the payload is at least 8 bytes");
            }
        });
    }

    // ----------------------------------------------------------------- bookkeeping

    private static async Task BookkeepingAsync(TestRunner runner)
    {
        await runner.RunAsync("Http2Connection stops tracking a stream's handler when it finishes", async () =>
        {
            // The handler list kept one Task for every stream the connection ever carried, so a
            // long-lived connection grew by a few hundred bytes per request for its whole life.
            await using var peer = await Peer.StartAsync();
            const int streams = 150;
            var highest = 0;
            var unanswered = 0;
            for (var i = 0; i < streams; i++)
            {
                var id = 2 * i + 1;
                await peer.SendHeadersAsync(id, endStream: true, Get($"/{i}"));
                var answered = await peer.ReadUntilAsync(f => f.StreamId == id && f.HasFlag(Http2FrameFlags.EndStream));
                if (answered is null) unanswered++;
                highest = Math.Max(highest, peer.Connection.InFlightHandlers);
            }

            runner.AreEqual(0, unanswered, "every stream was answered");
            await Poll.UntilAsync(() => peer.Connection.InFlightHandlers == 0);
            runner.AreEqual(0, peer.Connection.InFlightHandlers, "nothing is tracked once every stream is done");
            runner.IsTrue(highest < 20, $"and the list never held more than a few at a time (peak {highest})");
        });

        await runner.RunAsync("Http2Stream disposal is idempotent and a late cancel is harmless", () =>
        {
            var stream = new Http2Stream(1, new HttpRequestData());
            var token = stream.Token;
            stream.Cancel();
            runner.IsTrue(token.IsCancellationRequested, "Cancel cancels the token");

            stream.Dispose();
            stream.Dispose();
            var lateCancelThrew = false;
            try { stream.Cancel(); }
            catch (ObjectDisposedException) { lateCancelThrew = true; }
            runner.IsTrue(!lateCancelThrew, "the reader may still cancel a stream whose handler has just finished");

            var tokenAfterDisposeThrew = false;
            try { _ = stream.Token; }
            catch (ObjectDisposedException) { tokenAfterDisposeThrew = true; }
            runner.IsTrue(tokenAfterDisposeThrew, "the cancellation source itself was disposed");
            return Task.CompletedTask;
        });
    }

    // --------------------------------------------------------------------- helpers

    private static byte[] Get(string path, string method = "GET") => HpackEncoder.Encode(
        [(":method", method), (":scheme", "https"), (":authority", "example.com"), (":path", path)]);

    /// <summary>An HPACK prefix integer (RFC 7541 §5.1) with <paramref name="firstBits"/> in the
    /// bits above the prefix. Written independently of the decoder under test.</summary>
    private static byte[] Int(int prefixBits, byte firstBits, ulong value)
    {
        var max = (1UL << prefixBits) - 1;
        if (value < max) return [(byte)(firstBits | (byte)value)];

        var bytes = new List<byte> { (byte)(firstBits | (byte)max) };
        value -= max;
        while (value >= 128)
        {
            bytes.Add((byte)((value & 0x7f) | 0x80));
            value >>= 7;
        }
        bytes.Add((byte)value);
        return [.. bytes];
    }

    /// <summary>Literal with incremental indexing and a new name (RFC 7541 §6.2.1), no Huffman.</summary>
    private static byte[] LiteralIndexed(string name, string value) =>
        [0x40, .. Int(7, 0x00, (ulong)name.Length), .. System.Text.Encoding.ASCII.GetBytes(name),
            .. Int(7, 0x00, (ulong)value.Length), .. System.Text.Encoding.ASCII.GetBytes(value)];

    private static byte[] WindowUpdate(uint increment) => [(byte)(increment >> 24), (byte)(increment >> 16), (byte)(increment >> 8), (byte)increment];

    private static byte[] RstStream(Http2ErrorCode code) => WindowUpdate((uint)code);

    private static byte[] GoAway(int lastStreamId, Http2ErrorCode code) => [.. WindowUpdate((uint)lastStreamId), .. WindowUpdate((uint)code)];

    private static byte[] SettingsEntry(ushort id, uint value) =>
        [(byte)(id >> 8), (byte)id, (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static Http2ErrorCode? GoAwayCode(Http2Frame? goAway) => goAway is null ? null : ErrorCodeAt(goAway.Value, 4);

    private static Http2ErrorCode? RstCode(Http2Frame? reset) => reset is null ? null : ErrorCodeAt(reset.Value, 0);

    private static Http2ErrorCode ErrorCodeAt(Http2Frame frame, int offset)
    {
        var s = frame.Payload.Span[offset..];
        return (Http2ErrorCode)(((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | s[3]);
    }

    private sealed class PeerOptions
    {
        public TimeSpan IdleTimeout { get; init; } = Http2Connection.DefaultIdleTimeout;
        public int MaxPendingControlFrames { get; init; } = Http2Connection.DefaultMaxPendingControlFrames;
        public TimeSpan HandlerDelay { get; init; }

        /// <summary>Holds every write the connection makes until <see cref="Peer.OpenWrites"/>, as a
        /// peer that has stopped reading its socket does.</summary>
        public bool BlockWrites { get; init; }

        /// <summary>Sends neither the connection preface nor SETTINGS.</summary>
        public bool SkipHandshake { get; init; }
    }

    /// <summary>A hand-driven HTTP/2 client over loopback TCP speaking to an
    /// <see cref="Http2Connection"/>, which it can also starve of reads.</summary>
    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly TcpClient _server;
        private readonly GatedStream _gate;
        private readonly Task _run;
        private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(30));

        public ConcurrentQueue<HttpRequestData> Requests { get; } = new();
        public Http2Connection Connection { get; }
        public Exception? RunException { get; private set; }
        private NetworkStream Wire => _client.GetStream();

        private Peer(TcpClient client, TcpClient server, PeerOptions options)
        {
            _client = client;
            _server = server;
            _gate = new GatedStream(server.GetStream(), open: !options.BlockWrites);
            Connection = new Http2Connection(_gate, async (request, ct) =>
            {
                Requests.Enqueue(request);
                if (options.HandlerDelay > TimeSpan.Zero) await Task.Delay(options.HandlerDelay, ct).ConfigureAwait(false);
                return (Http2StreamResponse)HttpResponseData.Simple(200, "OK", "ok");
            })
            {
                IdleTimeout = options.IdleTimeout,
                MaxPendingControlFrames = options.MaxPendingControlFrames,
            };
            _run = Task.Run(async () =>
            {
                try { await Connection.RunAsync(_cts.Token).ConfigureAwait(false); }
                catch (Exception ex) { RunException = ex; }
            });
        }

        public static async Task<Peer> StartAsync(PeerOptions? options = null)
        {
            options ??= new PeerOptions();
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var client = new TcpClient { NoDelay = true };
            var accept = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port).ConfigureAwait(false);
            var server = await accept.ConfigureAwait(false);
            listener.Stop();

            var peer = new Peer(client, server, options);
            if (!options.SkipHandshake)
            {
                await peer.Wire.WriteAsync("PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray()).ConfigureAwait(false);
                await peer.SendAsync(Http2FrameType.Settings, Http2FrameFlags.None, 0, ReadOnlyMemory<byte>.Empty).ConfigureAwait(false);
            }
            return peer;
        }

        public void OpenWrites() => _gate.Open();

        /// <summary>Opens the held writes once <paramref name="taken"/> says the connection has taken in
        /// everything sent so far. Opening on a timer instead would let a slow reader see the queue
        /// drain before the flood had reached it.</summary>
        public async Task OpenWritesWhenAsync(Func<bool> taken)
        {
            await Poll.UntilAsync(taken).ConfigureAwait(false);
            _gate.Open();
        }

        public Task SendAsync(Http2FrameType type, Http2FrameFlags flags, int streamId, ReadOnlyMemory<byte> payload) =>
            Http2FrameWriter.WriteAsync(Wire, type, flags, streamId, payload, _cts.Token);

        public Task SendHeadersAsync(int streamId, bool endStream, byte[] block) =>
            SendAsync(Http2FrameType.Headers,
                Http2FrameFlags.EndHeaders | (endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None), streamId, block);

        /// <summary>Reads frames until one matches, returning null if the connection ends first.</summary>
        public Task<Http2Frame?> ReadUntilAsync(Func<Http2Frame, bool> match) => ReadUntilAsync(match, TimeSpan.FromSeconds(5));

        public async Task<Http2Frame?> ReadUntilAsync(Func<Http2Frame, bool> match, TimeSpan timeoutAfter)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            timeout.CancelAfter(timeoutAfter);
            try
            {
                while (await Http2FrameReader.ReadAsync(Wire, 1 << 24, timeout.Token).ConfigureAwait(false) is { } frame)
                    if (match(frame)) return frame;
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            return null;
        }

        /// <summary>Counts the frames that satisfy <paramref name="count"/> up to the first GOAWAY.</summary>
        public async Task<(int Count, Http2Frame? GoAway)> CountUntilGoAwayAsync(Func<Http2Frame, bool> count)
        {
            var seen = 0;
            var goAway = await ReadUntilAsync(f =>
            {
                if (f.Type == Http2FrameType.GoAway) return true;
                if (count(f)) seen++;
                return false;
            }).ConfigureAwait(false);
            return (seen, goAway);
        }

        public async Task<bool> EndedWithinAsync(TimeSpan limit) =>
            await Task.WhenAny(_run, Task.Delay(limit)).ConfigureAwait(false) == _run;

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            _gate.Open();
            await _cts.CancelAsync().ConfigureAwait(false);
            try { await _run.ConfigureAwait(false); } catch { }
            _server.Dispose();
            _cts.Dispose();
        }
    }

    /// <summary>Delays every write to the wrapped stream until opened. Reads pass straight through.</summary>
    private sealed class GatedStream(Stream inner, bool open) : Stream
    {
        private readonly TaskCompletionSource _open = open
            ? Completed()
            : new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource Completed()
        {
            var source = new TaskCompletionSource();
            source.SetResult();
            return source;
        }

        public void Open() => _open.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _open.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override ValueTask DisposeAsync() => inner.DisposeAsync();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
