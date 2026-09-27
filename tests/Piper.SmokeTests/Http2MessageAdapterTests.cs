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

        await runner.RunAsync("ToRequest rejects malformed fields that would inject into an HTTP/1.1 upstream", () =>
        {
            // Each would otherwise be written verbatim by HeaderCollection.ToRawString() or the
            // request line (RFC 9113 §8.2.1, §8.2.2, §8.3.1).
            var malformed = new (string What, string Name, string Value)[]
            {
                ("CRLF in value", "x-a", "1\r\nx-injected: yes"),
                ("bare LF in value", "x-a", "1\nGET /smuggled HTTP/1.1"),
                ("bare CR in value", "x-a", "1\rx"),
                ("NUL in value", "x-a", "1\0"),
                ("leading SP in value", "x-a", " 1"),
                ("trailing HTAB in value", "x-a", "1\t"),
                ("uppercase name", "X-A", "1"),
                ("colon in name", "x:a", "1"),
                ("space in name", "x a", "1"),
                ("CRLF in name", "x\r\ny", "1"),
                ("empty name", "", "1"),
                ("connection", "connection", "close"),
                ("keep-alive", "keep-alive", "timeout=5"),
                ("proxy-connection", "proxy-connection", "keep-alive"),
                ("transfer-encoding", "transfer-encoding", "chunked"),
                ("upgrade", "upgrade", "websocket"),
                ("te other than trailers", "te", "gzip"),
                ("CRLF in :method", ":method", "GET / HTTP/1.1\r\nx: y"),
                ("space in :method", ":method", "G ET"),
                ("CRLF in :path", ":path", "/\r\nx: y"),
                ("space in :path", ":path", "/a b"),
                ("empty :path", ":path", ""),
                ("host naming another entity", "host", "other.example"),
                ("CRLF in :scheme", ":scheme", "https\r\nx: y"),
                ("space in :scheme", ":scheme", "ht tps"),
                ("CRLF in :authority", ":authority", "example.com\r\nx: y"),
                ("space in :authority", ":authority", "example.com evil"),
                ("empty :authority", ":authority", ""),
            };

            foreach (var (what, name, value) in malformed)
            {
                var fields = new List<(string Name, string Value)>
                {
                    (":method", "GET"), (":scheme", "https"), (":authority", "example.com"), (":path", "/"),
                };
                if (name.StartsWith(':')) fields.RemoveAll(f => f.Name == name);
                fields.Add((name, value));

                var threw = false;
                try { Http2MessageAdapter.ToRequest(fields); }
                catch (HttpParseException) { threw = true; }
                runner.IsTrue(threw, $"{what} is rejected");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("ToRequest rejects duplicated pseudo-headers and Host fields", () =>
        {
            List<(string Name, string Value)> Base() =>
                [(":method", "GET"), (":scheme", "https"), (":authority", "example.com"), (":path", "/")];

            foreach (var duplicate in new[] { ":method", ":scheme", ":authority", ":path" })
            {
                var fields = Base();
                fields.Add((duplicate, fields.First(f => f.Name == duplicate).Value));
                var threw = false;
                try { Http2MessageAdapter.ToRequest(fields); }
                catch (HttpParseException) { threw = true; }
                runner.IsTrue(threw, $"duplicate {duplicate} is rejected, even with the same value");
            }

            var twoHosts = Base();
            twoHosts.Add(("host", "example.com"));
            twoHosts.Add(("host", "example.com"));
            var rejected = false;
            try { Http2MessageAdapter.ToRequest(twoHosts); }
            catch (HttpParseException) { rejected = true; }
            runner.IsTrue(rejected, "two host fields are rejected");

            var matching = Base();
            matching.Add(("host", "EXAMPLE.com"));
            runner.AreEqual("EXAMPLE.com", Http2MessageAdapter.ToRequest(matching).Headers["host"], "a host matching :authority is kept");
            return Task.CompletedTask;
        });

        await runner.RunAsync("ToRequest accepts the legal edge cases next to the malformed ones", () =>
        {
            var fields = new List<(string Name, string Value)>
            {
                (":method", "GET"), (":scheme", "https"), (":authority", "example.com"), (":path", "/"),
                ("te", "trailers"), ("x-empty", ""), ("x-inner", "a \t b"), ("x-tchar!#$%&'*+-.^_`|~", "v"),
                ("x-obs-text", "café"),
            };
            var request = Http2MessageAdapter.ToRequest(fields);
            runner.AreEqual("trailers", request.Headers["te"], "te: trailers is allowed");
            var capitalised = Http2MessageAdapter.ToRequest(
                [(":method", "GET"), (":scheme", "https"), (":authority", "example.com"), (":path", "/"), ("te", "Trailers")]);
            runner.AreEqual("Trailers", capitalised.Headers["te"], "te token is case-insensitive");
            runner.AreEqual("", request.Headers["x-empty"], "empty value is allowed");
            runner.AreEqual("a \t b", request.Headers["x-inner"], "inner whitespace is allowed");
            runner.AreEqual("v", request.Headers["x-tchar!#$%&'*+-.^_`|~"], "every tchar is allowed in a name");
            return Task.CompletedTask;
        });

        await runner.RunAsync("ToResponse rejects fields that would split an HTTP/1.1 response", () =>
        {
            var malformed = new (string What, string Name, string Value)[]
            {
                ("CRLF in value", "x-a", "1\r\nset-cookie: evil=1"),
                ("NUL in value", "x-a", "\0"),
                ("uppercase name", "Set-Cookie", "a=1"),
                ("colon in name", "x:a", "1"),
                ("non-numeric :status", ":status", "2x0"),
                ("four-digit :status", ":status", "2000"),
                ("CRLF in :status", ":status", "200\r\nx: y"),
            };
            foreach (var (what, name, value) in malformed)
            {
                var fields = new List<(string Name, string Value)>();
                if (name != ":status") fields.Add((":status", "200"));
                fields.Add((name, value));

                var threw = false;
                try { Http2MessageAdapter.ToResponse(fields); }
                catch (HttpParseException) { threw = true; }
                runner.IsTrue(threw, $"{what} is rejected");
            }

            // Hop-by-hop fields are stripped later by the proxy, so a response carrying one is
            // tolerated rather than failed.
            var tolerated = Http2MessageAdapter.ToResponse([(":status", "200"), ("connection", "close")]);
            runner.AreEqual(200, tolerated.StatusCode, "connection-specific response field is tolerated");
            return Task.CompletedTask;
        });
    }
}
