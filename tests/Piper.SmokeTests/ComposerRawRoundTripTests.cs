using System.Text;
using Piper.Core.Proxy;

// The composer's Raw tab is the default view for a loaded request, and switching away from it
// parses the raw text back into the structured fields. That makes BuildRawText -> TryParseRaw a
// load-bearing round trip: anything it loses is silently dropped from the request the user sends.
internal static class ComposerRawRoundTripTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("raw round trip keeps the body when there are no headers", () =>
        {
            // LoadSession strips Host and Content-Length, so a request whose only headers were
            // those arrives here with an empty header block. The blank line still has to land
            // exactly one CRLF after the request line or the body picks up a stray newline.
            const string body = """{"a":1}""";
            var raw = RequestExecutor.BuildRawText("GET", "http://example.com/items", string.Empty, body);

            runner.AreEqual("GET http://example.com/items HTTP/1.1\r\n\r\n" + body, raw, "no stray blank line");
            runner.IsTrue(RequestExecutor.TryParseRaw(raw, out var parsed, out var error), $"parses ({error})");
            runner.AreEqual(0, parsed.Headers.Count, "no headers");
            runner.AreEqual(body, Encoding.UTF8.GetString(parsed.Body), "body survives verbatim");
            return Task.CompletedTask;
        });

        await runner.RunAsync("raw round trip keeps duplicate headers and their order", () =>
        {
            const string headers = "Accept: text/plain\r\n"
                                 + "Cookie: a=1\r\n"
                                 + "X-Trace: first\r\n"
                                 + "Cookie: b=2\r\n"
                                 + "X-Trace: second";
            var raw = RequestExecutor.BuildRawText("post", " http://example.com/v1/items ", headers, "hello");

            runner.IsTrue(RequestExecutor.TryParseRaw(raw, out var parsed, out var error), $"parses ({error})");
            runner.AreEqual("POST", parsed.Method, "method upper-cased");
            runner.AreEqual("http://example.com/v1/items", parsed.Url!.ToString(), "url trimmed");
            runner.AreEqual("Accept,Cookie,X-Trace,Cookie,X-Trace",
                string.Join(',', parsed.Headers.Select(h => h.Name)), "names in order, duplicates kept");
            runner.AreEqual("text/plain,a=1,first,b=2,second",
                string.Join(',', parsed.Headers.Select(h => h.Value)), "values in order, duplicates kept");
            runner.AreEqual("hello", Encoding.UTF8.GetString(parsed.Body), "body");
            return Task.CompletedTask;
        });

        await runner.RunAsync("raw round trip keeps a multi-line body byte for byte", () =>
        {
            // Only the head is normalised. The body goes out exactly as typed: flattening its CRLFs
            // to LF broke multipart bodies, whose boundaries are CRLF-delimited.
            var raw = RequestExecutor.BuildRawText("PUT", "http://example.com/doc",
                "Content-Type: text/plain", "line one\r\nline two\r\n\r\nline four");

            runner.IsTrue(RequestExecutor.TryParseRaw(raw, out var parsed, out var error), $"parses ({error})");
            runner.AreEqual("line one\r\nline two\r\n\r\nline four", Encoding.UTF8.GetString(parsed.Body),
                "every body line survives with its CRLF");

            const string multipart = "--b\r\nContent-Disposition: form-data; name=\"a\"\r\n\r\n1\r\n--b--\r\n";
            var form = RequestExecutor.BuildRawText("POST", "http://example.com/upload",
                "Content-Type: multipart/form-data; boundary=b", multipart);
            runner.IsTrue(RequestExecutor.TryParseRaw(form, out var formParsed, out _), "a multipart request parses");
            runner.AreEqual(multipart, Encoding.UTF8.GetString(formParsed.Body), "its framing is untouched");

            // A head typed with bare LFs (pasted from a log) still splits from its body correctly.
            var lfOnly = "POST http://example.com/v1 HTTP/1.1\nContent-Type: text/plain\n\nbody\r\nline";
            runner.IsTrue(RequestExecutor.TryParseRaw(lfOnly, out var lfParsed, out _), "an LF-only head parses");
            runner.AreEqual("text/plain", lfParsed.Headers["Content-Type"], "with its header intact");
            runner.AreEqual("body\r\nline", Encoding.UTF8.GetString(lfParsed.Body), "and the body as typed");
            return Task.CompletedTask;
        });
    }
}
