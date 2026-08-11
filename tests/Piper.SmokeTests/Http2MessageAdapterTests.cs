using Piper.Core.Http;
using Piper.Core.Http2;

// Pure translation-layer correctness: HttpRequestData/HttpResponseData <-> h2 pseudo-header field
// lists. No sockets, no HPACK -- isolates bugs here from bugs in framing or header compression.
internal static class Http2MessageAdapterTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("request -> h2 fields: pseudo-headers first, Host stripped, names lowercased", () =>
        {
            var request = new HttpRequestData
            {
                Method = "POST",
                Url = new Uri("https://example.com/api/orders?id=42"),
            };
            request.Headers.Add("Host", "example.com");
            request.Headers.Add("Content-Type", "application/json");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Add("X-Custom-Header", "kept-verbatim");

            var fields = Http2MessageAdapter.ToHeaderFields(request);

            runner.AreEqual((":method", "POST"), fields[0], ":method first");
            runner.AreEqual((":scheme", "https"), fields[1], ":scheme second");
            runner.AreEqual((":authority", "example.com"), fields[2], ":authority third");
            runner.AreEqual((":path", "/api/orders?id=42"), fields[3], ":path fourth");
            runner.IsTrue(!fields.Any(f => f.Name.Equals("host", StringComparison.OrdinalIgnoreCase)), "no Host header on the wire");
            runner.IsTrue(!fields.Any(f => f.Name.Equals("connection", StringComparison.OrdinalIgnoreCase)), "Connection stripped");
            runner.IsTrue(fields.Any(f => f.Name == "content-type" && f.Value == "application/json"), "content-type lowercased");
            runner.IsTrue(fields.Any(f => f.Name == "x-custom-header" && f.Value == "kept-verbatim"), "custom header preserved, lowercased");
            return Task.CompletedTask;
        });

        await runner.RunAsync("request round-trips through h2 fields", () =>
        {
            var original = new HttpRequestData
            {
                Method = "GET",
                Url = new Uri("https://api.example.com/v1/items"),
            };
            original.Headers.Add("Accept", "application/json");

            var fields = Http2MessageAdapter.ToHeaderFields(original);
            var rebuilt = Http2MessageAdapter.ToRequest(fields);

            runner.AreEqual("GET", rebuilt.Method, "method");
            runner.AreEqual("https://api.example.com/v1/items", rebuilt.Url!.ToString(), "url");
            runner.AreEqual("/v1/items", rebuilt.RequestTarget, "request target");
            runner.AreEqual("application/json", rebuilt.Headers["accept"], "regular header survives");
            runner.AreEqual("HTTP/2", rebuilt.HttpVersion, "http version tagged");
            return Task.CompletedTask;
        });

        await runner.RunAsync("response round-trips through h2 fields, including synthesized reason phrase", () =>
        {
            var original = new HttpResponseData { StatusCode = 404 };
            original.Headers.Add("Content-Type", "text/plain");
            original.Headers.Add("Set-Cookie", "a=1");
            original.Headers.Add("Set-Cookie", "b=2");

            var fields = Http2MessageAdapter.ToHeaderFields(original);
            runner.AreEqual((":status", "404"), fields[0], ":status first");

            var rebuilt = Http2MessageAdapter.ToResponse(fields);
            runner.AreEqual(404, rebuilt.StatusCode, "status code");
            runner.AreEqual("Not Found", rebuilt.ReasonPhrase, "synthesized reason phrase");
            runner.AreEqual("HTTP/2 404 Not Found", rebuilt.StartLine, "StartLine renders sensibly for the UI");
            runner.AreEqual(2, rebuilt.Headers.GetValues("Set-Cookie").Count(), "duplicate headers preserved");
            return Task.CompletedTask;
        });

        await runner.RunAsync("ToRequest falls back to the tunnel's scheme when :scheme is missing", () =>
        {
            var fields = new List<(string Name, string Value)>
            {
                (":method", "GET"), (":authority", "example.com"), (":path", "/"),
            };
            var rebuilt = Http2MessageAdapter.ToRequest(fields, isHttps: true);
            runner.AreEqual("https", rebuilt.Url!.Scheme, "falls back to https");
            return Task.CompletedTask;
        });

        await runner.RunAsync("ResolveUrl returns null when a piece is missing", () =>
        {
            runner.IsTrue(Http2MessageAdapter.ResolveUrl(null, "example.com", "/") is null, "missing scheme");
            runner.IsTrue(Http2MessageAdapter.ResolveUrl("https", null, "/") is null, "missing authority");
            runner.IsTrue(Http2MessageAdapter.ResolveUrl("https", "example.com", null) is null, "missing path");
            runner.IsTrue(Http2MessageAdapter.ResolveUrl("https", "example.com", "/x") is not null, "all present resolves");
            return Task.CompletedTask;
        });
    }
}
