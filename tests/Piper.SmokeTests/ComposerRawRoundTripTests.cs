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

        await runner.RunAsync("raw round trip keeps a multi-line body", () =>
        {
            // TryParseRaw normalises CRLF to LF throughout, so the body comes back LF-terminated.
            // That is the one intentional asymmetry here; no line may be lost or gain a blank.
            var raw = RequestExecutor.BuildRawText("PUT", "http://example.com/doc",
                "Content-Type: text/plain", "line one\r\nline two\r\n\r\nline four");

            runner.IsTrue(RequestExecutor.TryParseRaw(raw, out var parsed, out var error), $"parses ({error})");
            runner.AreEqual("line one\nline two\n\nline four", Encoding.UTF8.GetString(parsed.Body),
                "every body line survives");
            return Task.CompletedTask;
        });
    }
}
