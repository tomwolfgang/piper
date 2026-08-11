using Piper.Core.Http;
using Piper.Core.Http3;
using Piper.Core.Http3.Qpack;

// QUIC varint, HTTP/3 framing and QPACK, checked against the RFCs' own worked examples. Same
// discipline as the HPACK tests: get the primitives provably right before any of it touches a
// socket, because a one-bit error here corrupts headers rather than failing loudly.
internal static class Http3CodecTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("QUIC varint decodes RFC 9000 A.1's sample encodings", () =>
        {
            void Check(string hex, long expected)
            {
                var pos = 0;
                runner.AreEqual(expected, VarInt.Read(Convert.FromHexString(hex), ref pos), $"0x{hex}");
            }

            Check("c2197c5eff14e88c", 151_288_809_941_952_652);
            Check("9d7f3e7d", 494_878_333);
            Check("7bbd", 15_293);
            Check("25", 37);
            Check("4025", 37); // the RFC's own example of a non-minimal encoding
            return Task.CompletedTask;
        });

        await runner.RunAsync("QUIC varint round-trips across every length class", () =>
        {
            long[] values = [0, 1, 63, 64, 16_383, 16_384, 1_073_741_823, 1_073_741_824, VarInt.MaxValue];
            int[] expectedLengths = [1, 1, 1, 2, 2, 4, 4, 8, 8];

            for (var i = 0; i < values.Length; i++)
            {
                var encoded = VarInt.Encode(values[i]);
                runner.AreEqual(expectedLengths[i], encoded.Length, $"{values[i]} encodes in {expectedLengths[i]} bytes");
                var pos = 0;
                runner.AreEqual(values[i], VarInt.Read(encoded, ref pos), $"{values[i]} round trips");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("QPACK decodes RFC 9204 B.1 (static table, no dynamic state)", () =>
        {
            // 0000 | Required Insert Count = 0, Base = 0
            // 510b 2f69 6e64 6578 2e68 746d 6c | Literal with Name Reference, static index 1
            var fields = QpackDecoder.Decode(Convert.FromHexString("0000510b2f696e6465782e68746d6c"));

            runner.AreEqual(1, fields.Count, "one field");
            runner.AreEqual((":path", "/index.html"), fields[0], ":path from the RFC's worked example");
            return Task.CompletedTask;
        });

        await runner.RunAsync("QPACK static table matches the RFC at its boundaries", () =>
        {
            runner.AreEqual(99, QpackStaticTable.Count, "99 entries (0-98)");
            runner.AreEqual((":authority", ""), QpackStaticTable.Get(0), "index 0");
            runner.AreEqual((":path", "/"), QpackStaticTable.Get(1), "index 1");
            runner.AreEqual((":method", "GET"), QpackStaticTable.Get(17), "index 17");
            runner.AreEqual((":status", "200"), QpackStaticTable.Get(25), "index 25");
            runner.AreEqual(("x-frame-options", "sameorigin"), QpackStaticTable.Get(98), "last index");
            return Task.CompletedTask;
        });

        await runner.RunAsync("QPACK encoder output round-trips through the decoder", () =>
        {
            var fields = new List<(string Name, string Value)>
            {
                (":method", "GET"),                          // exact static hit -> single indexed byte
                (":scheme", "https"),                        // exact static hit
                (":path", "/api/orders?id=42"),              // static name, literal value
                (":authority", "example.com"),               // static name, literal value
                ("user-agent", "Piper/0.1.0"),               // static name, literal value
                ("x-custom-header", "kept-verbatim"),        // fully literal
                ("set-cookie", "a=1"),
                ("set-cookie", "b=2"),                       // duplicates must survive
            };

            var encoded = QpackEncoder.Encode(fields);
            var decoded = QpackDecoder.Decode(encoded);

            runner.AreEqual(fields.Count, decoded.Count, "field count preserved");
            for (var i = 0; i < fields.Count; i++)
                runner.AreEqual(fields[i], decoded[i], $"field[{i}] round trip");
            return Task.CompletedTask;
        });

        await runner.RunAsync("QPACK encodes an exact static match as one byte", () =>
        {
            // :method GET is static index 17 -> 0xc0 | 17 = 0xd1, after the 2-byte prefix.
            var encoded = QpackEncoder.Encode([(":method", "GET")]);
            runner.AreEqual(3, encoded.Length, "2-byte prefix plus a single indexed byte");
            runner.AreEqual((byte)0xd1, encoded[2], "indexed field line, static index 17");
            return Task.CompletedTask;
        });

        await runner.RunAsync("QPACK rejects field sections that need a dynamic table", () =>
        {
            // Required Insert Count != 0 means the peer ignored our advertised zero capacity.
            var threw = false;
            try { QpackDecoder.Decode(Convert.FromHexString("0200d1")); }
            catch (HttpParseException) { threw = true; }
            runner.IsTrue(threw, "non-zero Required Insert Count is rejected, not misread");

            // Indexed field line with T=0 (dynamic) -- 0x80 with the static bit clear.
            threw = false;
            try { QpackDecoder.Decode(Convert.FromHexString("000081")); }
            catch (HttpParseException) { threw = true; }
            runner.IsTrue(threw, "dynamic table reference is rejected");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HTTP/3 frames round-trip, including large payloads", async () =>
        {
            var payload = new byte[20_000];
            new Random(3).NextBytes(payload);

            var bytes = Http3FrameWriter.Encode(Http3FrameType.Data, payload);
            using var stream = new MemoryStream(bytes);
            var reader = new Http3StreamReader(stream);

            var frame = await reader.ReadFrameAsync(1 << 20, CancellationToken.None);
            runner.IsTrue(frame is not null, "frame read");
            runner.AreEqual(Http3FrameType.Data, frame!.Value.Type, "type");
            runner.IsTrue(payload.AsSpan().SequenceEqual(frame.Value.Payload.Span), "payload intact across a 2-byte length varint");

            runner.IsTrue(await reader.ReadFrameAsync(1 << 20, CancellationToken.None) is null, "clean end of stream");
        });

        await runner.RunAsync("HTTP/3 SETTINGS payload round-trips", () =>
        {
            var payload = Http3FrameWriter.EncodeSettings(
                (Http3SettingId.QpackMaxTableCapacity, 0),
                (Http3SettingId.QpackBlockedStreams, 0),
                (Http3SettingId.MaxFieldSectionSize, 65_536));

            var decoded = Http3FrameWriter.DecodeSettings(payload);
            runner.AreEqual(0L, decoded[Http3SettingId.QpackMaxTableCapacity], "QPACK capacity advertised as 0");
            runner.AreEqual(0L, decoded[Http3SettingId.QpackBlockedStreams], "no blocked streams");
            runner.AreEqual(65_536L, decoded[Http3SettingId.MaxFieldSectionSize], "max field section size");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HTTP/3 frame reader rejects an oversized payload", async () =>
        {
            var bytes = Http3FrameWriter.Encode(Http3FrameType.Data, new byte[5000]);
            using var stream = new MemoryStream(bytes);
            var reader = new Http3StreamReader(stream);

            var threw = false;
            try { await reader.ReadFrameAsync(1000, CancellationToken.None); }
            catch (HttpParseException) { threw = true; }
            runner.IsTrue(threw, "payload beyond the cap is rejected");
        });
    }
}
