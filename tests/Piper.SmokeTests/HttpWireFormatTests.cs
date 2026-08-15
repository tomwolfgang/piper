using System.IO.Compression;
using System.Text;
using Piper.Core.Http;

internal static class HttpWireFormatTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("raw HTTP responses round-trip", () =>
        {
            var original = HttpResponseData.Canned(201, Encoding.UTF8.GetBytes("""{"id":1}"""), "application/json");
            original.Headers.Add("Set-Cookie", "a=1");
            original.Headers.Add("Set-Cookie", "b=2");
            original.ReasonPhrase = "Created";

            runner.IsTrue(HttpWireFormat.TryParseResponse(HttpWireFormat.Serialize(original), out var parsed, out var error),
                $"a serialised response parses back ({error})");
            runner.AreEqual(201, parsed.StatusCode, "status");
            runner.AreEqual("Created", parsed.ReasonPhrase, "reason phrase");
            runner.AreEqual("application/json", parsed.ContentType, "content type");
            runner.AreEqual(2, parsed.Headers.GetValues("Set-Cookie").Count(), "duplicate headers keep their order and count");
            runner.AreEqual("""{"id":1}""", Encoding.UTF8.GetString(parsed.Body), "body");

            // A body holding NUL and high bytes has to survive the text round trip untouched.
            var binary = HttpResponseData.Canned(200, [0x00, 0xFF, 0x10, 0x80, 0x00], "application/octet-stream");
            runner.IsTrue(HttpWireFormat.TryParseResponse(HttpWireFormat.Serialize(binary), out var binaryBack, out _),
                "a binary response parses back");
            runner.AreEqual(Convert.ToHexString(binary.Body), Convert.ToHexString(binaryBack.Body), "binary body is byte-exact");

            runner.IsTrue(!HttpWireFormat.TryParseResponse([], out _, out _), "an empty file is rejected");
            runner.IsTrue(!HttpWireFormat.TryParseResponse(Encoding.UTF8.GetBytes("hello there\r\n\r\nbody"), out _, out var why),
                "text that is not a response is rejected");
            runner.IsTrue(why.Length > 0, "and the reason says so");

            return Task.CompletedTask;
        });

        await runner.RunAsync("editing a canned response keeps it well-formed", () =>
        {
            // What the editor shows for a gzipped response: the decoded body, with the encoding
            // header gone so the text and the headers cannot disagree.
            var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(Encoding.UTF8.GetBytes("""{"stub":true}"""));

            var stored = HttpResponseData.Canned(200, compressed.ToArray(), "application/json");
            stored.Headers.Set("Content-Encoding", "gzip");
            stored.Headers.Set("Transfer-Encoding", "chunked");

            var text = HttpWireFormat.ToEditableText(stored);
            runner.IsTrue(text.Contains("""{"stub":true}"""), "the body is shown decoded");
            runner.IsTrue(!text.Contains("Content-Encoding", StringComparison.OrdinalIgnoreCase),
                "Content-Encoding is dropped with the compression");
            runner.IsTrue(!text.Contains("Transfer-Encoding", StringComparison.OrdinalIgnoreCase),
                "so is stale framing");
            runner.IsTrue(text.Contains("Content-Length: 13"), "Content-Length describes the decoded body");

            // Editing the body must re-frame it, whatever the typed Content-Length said.
            var edited = text.Replace("""{"stub":true}""", """{"stub":true,"extra":"much longer body"}""");
            runner.IsTrue(HttpWireFormat.TryParseEditedResponse(edited, out var raw, out var error), $"edited text parses ({error})");
            runner.IsTrue(HttpWireFormat.TryParseResponse(raw, out var reparsed, out _), "and the bytes it produced parse");
            runner.AreEqual("""{"stub":true,"extra":"much longer body"}""", Encoding.UTF8.GetString(reparsed.Body), "edited body");
            runner.AreEqual(reparsed.Body.Length.ToString(), reparsed.Headers["Content-Length"],
                "Content-Length is recalculated, not left stale");

            // An editor hands back lone newlines; a bare LF on the blank line would otherwise fold
            // the body into the header block.
            var lfOnly = "HTTP/1.1 200 OK\nContent-Type: text/plain\n\nplain body";
            runner.IsTrue(HttpWireFormat.TryParseEditedResponse(lfOnly, out var lfRaw, out _), "LF-only text is accepted");
            runner.IsTrue(HttpWireFormat.TryParseResponse(lfRaw, out var lfParsed, out _), "and produces a real response");
            runner.AreEqual("plain body", Encoding.UTF8.GetString(lfParsed.Body), "the body did not end up in the headers");
            runner.AreEqual("text/plain", lfParsed.ContentType, "and the header survived");

            runner.IsTrue(!HttpWireFormat.TryParseEditedResponse("just some notes", out _, out var badReason),
                "text with no status line is refused");
            runner.IsTrue(badReason.Length > 0, "with a reason to show the user");

            // A rule with nothing stored yet still opens on something editable.
            runner.IsTrue(HttpWireFormat.ToEditableText(null).StartsWith("HTTP/1.1 200 OK", StringComparison.Ordinal),
                "a rule with no response yet gets a starting point");

            return Task.CompletedTask;
        });
    }
}
