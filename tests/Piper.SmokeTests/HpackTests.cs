using Piper.Core.Http;
using Piper.Core.Http2.Hpack;

// HPACK/Huffman correctness against RFC 7541's own worked examples. No sockets, no concurrency --
// this is where a subtle off-by-one (Huffman code length, prefix-integer continuation) would
// otherwise silently corrupt unrelated headers rather than crash loudly, so it runs before any
// socket-level HTTP/2 code is trusted.
internal static class HpackTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("Huffman round-trips arbitrary ASCII", () =>
        {
            var original = "www.example.com"u8.ToArray();
            var encoded = Huffman.Encode(original);
            var decoded = Huffman.Decode(encoded);
            runner.IsTrue(original.AsSpan().SequenceEqual(decoded), "round trip preserves bytes");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Huffman decodes RFC 7541 C.4.1 :authority literal", () =>
        {
            // From the RFC's own worked example: Huffman-coded "www.example.com".
            var huffman = Convert.FromHexString("f1e3c2e5f23a6ba0ab90f4ff");
            var decoded = Huffman.Decode(huffman);
            runner.AreEqual("www.example.com", System.Text.Encoding.Latin1.GetString(decoded), "decoded value");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Huffman decodes RFC 7541 C.4.2 cache-control literal", () =>
        {
            var huffman = Convert.FromHexString("a8eb10649cbf");
            var decoded = Huffman.Decode(huffman);
            runner.AreEqual("no-cache", System.Text.Encoding.Latin1.GetString(decoded), "decoded value");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Huffman rejects an embedded EOS symbol", () =>
        {
            // 30 one-bits = the EOS code itself, padded to whole bytes.
            var eos = Convert.FromHexString("3fffffff");
            var threw = false;
            try { Huffman.Decode(eos); }
            catch (HttpParseException) { threw = true; }
            runner.IsTrue(threw, "EOS in the data is a decoding error");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HPACK decodes RFC 7541 C.4.1 first request", () =>
        {
            var block = Convert.FromHexString("828684418cf1e3c2e5f23a6ba0ab90f4ff");
            var decoder = new HpackDecoder();
            var fields = decoder.Decode(block);

            runner.AreEqual(4, fields.Count, "field count");
            runner.AreEqual((":method", "GET"), fields[0], ":method");
            runner.AreEqual((":scheme", "http"), fields[1], ":scheme");
            runner.AreEqual((":path", "/"), fields[2], ":path");
            runner.AreEqual((":authority", "www.example.com"), fields[3], ":authority");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HPACK decodes RFC 7541 C.4.2 second request (indexed :authority + dynamic table)", () =>
        {
            var decoder = new HpackDecoder();
            decoder.Decode(Convert.FromHexString("828684418cf1e3c2e5f23a6ba0ab90f4ff")); // primes the dynamic table
            var fields = decoder.Decode(Convert.FromHexString("828684be5886a8eb10649cbf"));

            runner.AreEqual(5, fields.Count, "field count");
            runner.AreEqual((":authority", "www.example.com"), fields[3], ":authority via dynamic table index 62");
            runner.AreEqual(("cache-control", "no-cache"), fields[4], "cache-control");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HPACK decodes a literal-without-indexing field (RFC 7541 C.2.2)", () =>
        {
            var decoder = new HpackDecoder();
            var fields = decoder.Decode(Convert.FromHexString("040c2f73616d706c652f70617468"));
            runner.AreEqual(1, fields.Count, "field count");
            runner.AreEqual((":path", "/sample/path"), fields[0], ":path");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HPACK decodes a fully-literal field (RFC 7541 C.2.1)", () =>
        {
            var decoder = new HpackDecoder();
            var fields = decoder.Decode(Convert.FromHexString("400a637573746f6d2d6b6579 0d637573746f6d2d686561646572".Replace(" ", "")));
            runner.AreEqual(1, fields.Count, "field count");
            runner.AreEqual(("custom-key", "custom-header"), fields[0], "literal name and value");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HPACK encoder output round-trips through the decoder", () =>
        {
            var fields = new List<(string Name, string Value)>
            {
                (":method", "POST"),
                (":scheme", "https"),
                (":path", "/api/orders?id=42"),
                (":authority", "example.com"),
                ("content-type", "application/json"),
                ("x-custom-header", "kept-verbatim"),
                ("set-cookie", "a=1"),
                ("set-cookie", "b=2"), // duplicates must survive
            };

            var encoded = HpackEncoder.Encode(fields);
            var decoded = new HpackDecoder().Decode(encoded);

            runner.AreEqual(fields.Count, decoded.Count, "field count preserved");
            for (var i = 0; i < fields.Count; i++)
                runner.AreEqual(fields[i], decoded[i], $"field[{i}] round trip");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HPACK decoder rejects a dynamic table size update above the advertised limit", () =>
        {
            var decoder = new HpackDecoder(advertisedMaxSize: 4096);
            // 001xxxxx with a 5-bit prefix maxed out (31) plus continuation encoding 8192-31=8161
            // as 0xe1 0x3f (RFC 7541 5.1's own continuation algorithm), decoding to 8192 overall.
            var block = Convert.FromHexString("3fe13f");
            var threw = false;
            try { decoder.Decode(block); }
            catch (HttpParseException) { threw = true; }
            runner.IsTrue(threw, "oversized table update is rejected");
            return Task.CompletedTask;
        });
    }
}
