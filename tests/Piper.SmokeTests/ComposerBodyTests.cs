using System.Text;
using System.Text.RegularExpressions;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

// A body typed into a fresh Composer request goes out under the Composer's default headers, which
// name no Content-Type. Every method that carries a body must reach the origin with it, framed
// and labelled well enough that the origin reads it as a body rather than ignoring it.
internal static class ComposerBodyTests
{
    private const string Json = """{"a":1}""";

    public static async Task RunAsync(TestRunner runner)
    {
        foreach (var method in new[] { "POST", "PUT", "PATCH" })
        {
            await runner.RunAsync($"composer {method} sends its JSON body with the default headers", async () =>
            {
                var (head, body) = await SendAsync(method, "Host: example.com\r\nAccept: application/json");

                runner.IsTrue(head.StartsWith($"{method} /items HTTP/1.1\r\n", StringComparison.Ordinal), "method on the wire");
                runner.IsTrue(head.Contains($"Content-Length: {Json.Length}\r\n"), "body framed with its length");
                runner.AreEqual(Json, body, "body bytes reached the origin");
                runner.IsTrue(head.Contains("Content-Type: application/json\r\n"),
                    "a JSON body with no Content-Type is labelled as JSON, or origins ignore it");
            });
        }

        await runner.RunAsync("composer keeps a Content-Type the user typed", async () =>
        {
            var (head, _) = await SendAsync("PUT", "Content-Type: text/plain");
            runner.IsTrue(head.Contains("Content-Type: text/plain\r\n"), "user's Content-Type kept");
            runner.IsTrue(!head.Contains("application/json"), "no second Content-Type added");
        });

        await runner.RunAsync("a replayed request keeps its missing Content-Type", async () =>
        {
            // Ctrl+R reproduces a capture. Labelling it would make a request the origin rejected
            // for its missing Content-Type succeed on replay, hiding the very bug being debugged.
            var (head, body) = await SendAsync("PUT", "Accept: application/json", fromEditor: false);
            runner.AreEqual(Json, body, "body reached the origin");
            runner.IsTrue(!head.Contains("Content-Type:"), "replay sent without a Content-Type");
        });

        await runner.RunAsync("composer does not label a body that is not one JSON document", async () =>
        {
            // Malformed, trailing bytes after a valid root, a bare scalar, and nesting past the
            // reader's depth limit: none may be labelled, and none may fail the send.
            var deep = new string('[', 100) + new string(']', 100);
            foreach (var sent in new[] { "not json {", """{"a":1} tail""", "42", deep })
            {
                var (head, body) = await SendAsync("PUT", "Accept: */*", sent);
                runner.AreEqual(sent, body, $"body reached the origin ({sent[..Math.Min(12, sent.Length)]})");
                runner.IsTrue(!head.Contains("Content-Type:"), $"no guessed Content-Type ({sent[..Math.Min(12, sent.Length)]})");
            }
        });
    }

    private static async Task<(string Head, string Body)> SendAsync(
        string method, string headers, string body = Json, bool fromEditor = true)
    {
        var received = new TaskCompletionSource<(string, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var origin = new TestRawOrigin(async (head, stream, ct) =>
        {
            var match = Regex.Match(head, @"\r\nContent-Length: (\d+)\r\n");
            var length = match.Success ? int.Parse(match.Groups[1].Value) : 0;
            var bytes = new byte[length];
            await stream.ReadExactlyAsync(bytes, ct);
            received.TrySetResult((head, Encoding.UTF8.GetString(bytes)));
            await TestRawOrigin.WriteAsync(stream, "HTTP/1.1 204 No Content\r\n\r\n", ct);
            return false;
        });

        var url = new Uri($"http://127.0.0.1:{origin.Port}/items");
        var request = new HttpRequestData
        {
            Method = method,
            HttpVersion = "HTTP/1.1",
            Url = url,
            RequestTarget = url.PathAndQuery,
            Headers = HeaderCollection.Parse(headers),
            Body = Encoding.UTF8.GetBytes(body),
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var session = await new RequestExecutor(new ProxyOptions(), new SessionStore()).ExecuteAsync(request, timeout.Token, labelJsonBody: fromEditor);
        if (session.State != SessionState.Complete) throw new InvalidOperationException($"send failed: {session.Error}");
        return await received.Task.WaitAsync(timeout.Token);
    }
}
