using Piper.Core.Http2;

// Frame codec + SETTINGS correctness, round-tripped through MemoryStream. No sockets, no HPACK --
// isolates bugs in the binary framing layer from bugs in header compression or connection logic.
internal static class Http2FrameTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("frame round-trips through a MemoryStream", async () =>
        {
            using var stream = new MemoryStream();
            await Http2FrameWriter.WriteAsync(stream, Http2FrameType.Ping, Http2FrameFlags.Ack, 0,
                "12345678"u8.ToArray(), CancellationToken.None);
            stream.Position = 0;

            var frame = await Http2FrameReader.ReadRequiredAsync(stream, maxFrameSize: 16_384, CancellationToken.None);
            runner.AreEqual(Http2FrameType.Ping, frame.Type, "type");
            runner.AreEqual(Http2FrameFlags.Ack, frame.Flags, "flags");
            runner.AreEqual(0, frame.StreamId, "stream id");
            runner.IsTrue("12345678"u8.SequenceEqual(frame.Payload.Span), "payload");
        });

        await runner.RunAsync("frame reader rejects a frame larger than the advertised max", async () =>
        {
            using var stream = new MemoryStream();
            await Http2FrameWriter.WriteAsync(stream, Http2FrameType.Data, Http2FrameFlags.None, 1,
                new byte[100], CancellationToken.None);
            stream.Position = 0;

            var threw = false;
            try { await Http2FrameReader.ReadRequiredAsync(stream, maxFrameSize: 50, CancellationToken.None); }
            catch (Http2ProtocolException ex) when (ex.ErrorCode == Http2ErrorCode.FrameSizeError) { threw = true; }
            runner.IsTrue(threw, "oversized frame rejected");
        });

        await runner.RunAsync("frame reader returns null at a clean end of stream", async () =>
        {
            using var stream = new MemoryStream();
            var frame = await Http2FrameReader.ReadAsync(stream, 16_384, CancellationToken.None);
            runner.IsTrue(frame is null, "no frame available");
        });

        await runner.RunAsync("large stream id preserves 31 bits and drops the reserved bit", async () =>
        {
            using var stream = new MemoryStream();
            // Top bit must be ignored on the wire (RFC 9113 4.1's 'R' reserved bit); a well-formed
            // stream id is always <= 2^31-1 in practice, but the reader must mask it defensively.
            await Http2FrameWriter.WriteAsync(stream, Http2FrameType.Headers, Http2FrameFlags.EndHeaders,
                int.MaxValue, ReadOnlyMemory<byte>.Empty, CancellationToken.None);
            stream.Position = 0;

            var frame = await Http2FrameReader.ReadRequiredAsync(stream, 16_384, CancellationToken.None);
            runner.AreEqual(int.MaxValue, frame.StreamId, "stream id round trips");
        });

        await runner.RunAsync("HEADERS larger than peer max frame size splits into CONTINUATION", async () =>
        {
            using var stream = new MemoryStream();
            var headerBlock = new byte[100];
            new Random(42).NextBytes(headerBlock);

            await Http2FrameWriter.WriteHeadersAsync(stream, streamId: 1, headerBlock, endStream: true,
                peerMaxFrameSize: 40, CancellationToken.None);
            stream.Position = 0;

            var first = await Http2FrameReader.ReadRequiredAsync(stream, 16_384, CancellationToken.None);
            runner.AreEqual(Http2FrameType.Headers, first.Type, "first frame is HEADERS");
            runner.AreEqual(40, first.Payload.Length, "first chunk size");
            runner.IsTrue(first.HasFlag(Http2FrameFlags.EndStream), "END_STREAM set on first frame");
            runner.IsTrue(!first.HasFlag(Http2FrameFlags.EndHeaders), "END_HEADERS not yet set");

            var second = await Http2FrameReader.ReadRequiredAsync(stream, 16_384, CancellationToken.None);
            runner.AreEqual(Http2FrameType.Continuation, second.Type, "second frame is CONTINUATION");
            runner.AreEqual(40, second.Payload.Length, "second chunk size");
            runner.IsTrue(!second.HasFlag(Http2FrameFlags.EndHeaders), "still not the last chunk");

            var third = await Http2FrameReader.ReadRequiredAsync(stream, 16_384, CancellationToken.None);
            runner.AreEqual(Http2FrameType.Continuation, third.Type, "third frame is CONTINUATION");
            runner.AreEqual(20, third.Payload.Length, "final chunk size");
            runner.IsTrue(third.HasFlag(Http2FrameFlags.EndHeaders), "END_HEADERS set on the last chunk");

            var reassembled = new byte[100];
            first.Payload.Span.CopyTo(reassembled);
            second.Payload.Span.CopyTo(reassembled.AsSpan(40));
            third.Payload.Span.CopyTo(reassembled.AsSpan(80));
            runner.IsTrue(headerBlock.AsSpan().SequenceEqual(reassembled), "reassembled block matches original");
        });

        await runner.RunAsync("DATA larger than peer max frame size splits across frames", async () =>
        {
            using var stream = new MemoryStream();
            var body = new byte[25];
            new Random(7).NextBytes(body);

            await Http2FrameWriter.WriteDataAsync(stream, streamId: 3, body, endStream: true,
                peerMaxFrameSize: 10, CancellationToken.None);
            stream.Position = 0;

            var chunks = new List<Http2Frame>();
            while (await Http2FrameReader.ReadAsync(stream, 16_384, CancellationToken.None) is { } f)
                chunks.Add(f);

            runner.AreEqual(3, chunks.Count, "split into 3 DATA frames (10+10+5)");
            runner.IsTrue(chunks.Take(2).All(c => !c.HasFlag(Http2FrameFlags.EndStream)), "END_STREAM not on earlier chunks");
            runner.IsTrue(chunks[^1].HasFlag(Http2FrameFlags.EndStream), "END_STREAM on the last chunk");

            var reassembled = chunks.SelectMany(c => c.Payload.ToArray()).ToArray();
            runner.IsTrue(body.AsSpan().SequenceEqual(reassembled), "reassembled body matches original");
        });

        await runner.RunAsync("empty DATA with END_STREAM writes exactly one zero-length frame", async () =>
        {
            using var stream = new MemoryStream();
            await Http2FrameWriter.WriteDataAsync(stream, 5, ReadOnlyMemory<byte>.Empty, endStream: true, 16_384, CancellationToken.None);
            stream.Position = 0;

            var frame = await Http2FrameReader.ReadRequiredAsync(stream, 16_384, CancellationToken.None);
            runner.AreEqual(0, frame.Payload.Length, "zero-length payload");
            runner.IsTrue(frame.HasFlag(Http2FrameFlags.EndStream), "END_STREAM set");
            runner.IsTrue(await Http2FrameReader.ReadAsync(stream, 16_384, CancellationToken.None) is null, "exactly one frame");
        });

        await runner.RunAsync("SETTINGS payload round-trips and layers onto existing values", () =>
        {
            var advertised = Http2Settings.Advertised();
            var payload = advertised.ToPayload();

            var peer = new Http2Settings(); // starts from RFC defaults, as a fresh connection would
            peer.ApplyPeerPayload(payload);

            runner.AreEqual(advertised.EnablePush, peer.EnablePush, "ENABLE_PUSH");
            runner.AreEqual(advertised.MaxConcurrentStreams, peer.MaxConcurrentStreams, "MAX_CONCURRENT_STREAMS");
            runner.AreEqual(advertised.InitialWindowSize, peer.InitialWindowSize, "INITIAL_WINDOW_SIZE");
            runner.AreEqual(advertised.MaxFrameSize, peer.MaxFrameSize, "MAX_FRAME_SIZE (unchanged default)");
            runner.AreEqual(advertised.HeaderTableSize, peer.HeaderTableSize, "HEADER_TABLE_SIZE (unchanged default)");
            runner.AreEqual(advertised.MaxHeaderListSize, peer.MaxHeaderListSize, "MAX_HEADER_LIST_SIZE");

            // A second, partial SETTINGS frame only touches the identifiers it mentions.
            var partial = new byte[6];
            partial[1] = 4; // id = SETTINGS_INITIAL_WINDOW_SIZE
            partial[2] = 0; partial[3] = 0; partial[4] = 0x10; partial[5] = 0; // 0x100000 = 1,048,576... use a distinct value
            peer.ApplyPeerPayload(partial);
            runner.AreEqual(advertised.MaxConcurrentStreams, peer.MaxConcurrentStreams, "untouched setting survives a later partial frame");

            return Task.CompletedTask;
        });

        await runner.RunAsync("padded DATA strips the pad-length byte and the trailing padding", () =>
        {
            // RFC 9113 6.1: payload = [1-byte Pad Length][Data][Padding]. Treating the raw payload
            // as body prepends the length byte and appends the padding, silently corrupting
            // content -- exactly how a padding-using origin (Google) served unreadable gzip.
            byte[] payload = [4, 0x1f, 0x8b, 0x08, 0x00, 0, 0, 0, 0];
            var frame = new Http2Frame(Http2FrameType.Data, Http2FrameFlags.Padded, 1, payload);

            runner.IsTrue(new byte[] { 0x1f, 0x8b, 0x08, 0x00 }.AsSpan().SequenceEqual(frame.DataPayload.Span),
                "body is just the data, without pad length or padding");
            runner.AreEqual(9, frame.Payload.Length,
                "raw payload length is unchanged -- flow control counts padding too (6.9.1)");
            return Task.CompletedTask;
        });

        await runner.RunAsync("unpadded DATA passes through untouched", () =>
        {
            byte[] payload = [1, 2, 3, 4];
            var frame = new Http2Frame(Http2FrameType.Data, Http2FrameFlags.None, 1, payload);
            runner.IsTrue(payload.AsSpan().SequenceEqual(frame.DataPayload.Span), "payload unchanged");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HEADERS strips padding and the 5-byte priority block", () =>
        {
            // [padLen=2][5-byte priority][block][2 bytes padding]
            byte[] payload = [2, 0x80, 0, 0, 1, 16, 0xAA, 0xBB, 0xCC, 0, 0];
            var frame = new Http2Frame(Http2FrameType.Headers,
                Http2FrameFlags.Padded | Http2FrameFlags.Priority | Http2FrameFlags.EndHeaders, 1, payload);

            runner.IsTrue(new byte[] { 0xAA, 0xBB, 0xCC }.AsSpan().SequenceEqual(frame.HeaderBlockPayload.Span),
                "only the HPACK block is handed to the decoder");
            return Task.CompletedTask;
        });

        await runner.RunAsync("HEADERS with priority but no padding strips only the priority block", () =>
        {
            byte[] payload = [0x80, 0, 0, 1, 16, 0xAA, 0xBB];
            var frame = new Http2Frame(Http2FrameType.Headers, Http2FrameFlags.Priority, 1, payload);
            runner.IsTrue(new byte[] { 0xAA, 0xBB }.AsSpan().SequenceEqual(frame.HeaderBlockPayload.Span), "priority stripped");
            return Task.CompletedTask;
        });

        await runner.RunAsync("CONTINUATION never carries padding or priority", () =>
        {
            byte[] payload = [0xAA, 0xBB, 0xCC];
            var frame = new Http2Frame(Http2FrameType.Continuation, Http2FrameFlags.EndHeaders, 1, payload);
            runner.IsTrue(payload.AsSpan().SequenceEqual(frame.HeaderBlockPayload.Span), "payload passed through whole");
            return Task.CompletedTask;
        });

        await runner.RunAsync("padding longer than the payload is a protocol error, not a crash", () =>
        {
            byte[] payload = [200, 1, 2]; // claims 200 bytes of padding inside a 3-byte payload
            var frame = new Http2Frame(Http2FrameType.Data, Http2FrameFlags.Padded, 1, payload);

            var threw = false;
            try { _ = frame.DataPayload; }
            catch (Http2ProtocolException ex) when (ex.ErrorCode == Http2ErrorCode.ProtocolError) { threw = true; }
            runner.IsTrue(threw, "rejected as a protocol error");
            return Task.CompletedTask;
        });

        await runner.RunAsync("SETTINGS rejects ENABLE_PUSH values other than 0 or 1", () =>
        {
            var settings = new Http2Settings();
            var bad = new byte[6];
            bad[1] = 2; // id = SETTINGS_ENABLE_PUSH
            bad[5] = 2; // value = 2, invalid

            var threw = false;
            try { settings.ApplyPeerPayload(bad); }
            catch (Http2ProtocolException ex) when (ex.ErrorCode == Http2ErrorCode.ProtocolError) { threw = true; }
            runner.IsTrue(threw, "invalid ENABLE_PUSH value rejected");
            return Task.CompletedTask;
        });
    }
}
