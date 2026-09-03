using System.Text;
using Piper.Core.Http;
using Piper.Core.Sessions;

internal static class SearchQueryTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("bare-word search reaches the URL, the request and the response", () =>
        {
            var session = Build(
                url: "http://api.example.test/v1/Orders/checkout",
                requestHeaders: [("X-Trace", "abc123"), ("Content-Type", "application/json")],
                requestBody: "{\"coupon\":\"SPRINGSALE\"}",
                responseHeaders: [("X-Backend", "orders-eu-3")],
                responseBody: "{\"receipt\":\"R-90210\"}");

            runner.IsTrue(Hits("checkout", session), "a word in the URL matches");
            runner.IsTrue(Hits("springsale", session), "a word only in the request body matches");
            runner.IsTrue(Hits("abc123", session), "a value only in a request header matches");
            runner.IsTrue(Hits("x-trace", session), "a request header name matches");
            runner.IsTrue(Hits("r-90210", session), "a word only in the response body matches");
            runner.IsTrue(Hits("orders-eu-3", session), "a value only in a response header matches");

            runner.IsTrue(!Hits("invoices", session), "a word that appears nowhere does not match");
            return Task.CompletedTask;
        });

        await runner.RunAsync("bare-word search ignores case in both directions", () =>
        {
            var session = Build(
                url: "http://api.example.test/v1/Orders",
                responseHeaders: [("X-Request-Id", "DEADBEEF")],
                responseBody: "lowercase marker");

            runner.IsTrue(Hits("orders", session), "a lowercase query finds mixed-case session text");
            runner.IsTrue(Hits("deadbeef", session), "a lowercase query finds an uppercase header value");
            runner.IsTrue(Hits("MARKER", session), "an uppercase query finds lowercase session text");
            runner.IsTrue(Hits("OrDeRs", session), "mixed case on both sides still matches");
            return Task.CompletedTask;
        });

        await runner.RunAsync("negated bare terms exclude", () =>
        {
            var hit = Build(url: "http://api.example.test/v1/orders");
            var miss = Build(url: "http://cdn.example.test/logo.svg");

            runner.IsTrue(!Hits("-orders", hit), "a negated term rejects the session that contains it");
            runner.IsTrue(Hits("-orders", miss), "a negated term keeps a session that does not");
            runner.AreEqual(0, SearchQuery.Parse("-orders").PlainTerms.Count,
                "a negated term is not offered for match highlighting");
            return Task.CompletedTask;
        });

        await runner.RunAsync("bare-word search skips bodies it cannot read as text", () =>
        {
            var declaredBinary = Build(
                url: "http://cdn.example.test/logo.png",
                responseHeaders: [("Content-Type", "image/png")],
                responseBodyBytes: Encoding.ASCII.GetBytes("secretword"));
            runner.IsTrue(!Hits("secretword", declaredBinary), "an image/png body does not participate in text search");

            var sniffedBinary = Build(
                url: "http://cdn.example.test/blob",
                responseBodyBytes: [0x00, 0x01, (byte)'s', (byte)'n', (byte)'i', (byte)'f', (byte)'f']);
            runner.IsTrue(!Hits("sniff", sniffedBinary), "a NUL byte marks an undeclared body as binary");

            var empty = Build(url: "http://api.example.test/ping", responseBody: "");
            runner.IsTrue(!Hits("anything", empty), "an empty body matches nothing and does not throw");
            runner.IsTrue(Hits("ping", empty), "the URL of a body-less session is still searchable");
            return Task.CompletedTask;
        });

        await runner.RunAsync("the search index is bounded, and body: reaches past the bound", () =>
        {
            // The index caps each message at 64,000 characters so one huge payload cannot dominate
            // memory. A word past the cap is unreachable by bare word but still found by resp:.
            const string nearMarker = "startmarker";
            const string farMarker = "endmarker";
            var body = new StringBuilder(nearMarker).Append('x', 70_000).Append(farMarker).ToString();

            var session = Build(
                url: "http://api.example.test/v1/report",
                responseHeaders: [("Content-Type", "text/plain")],
                responseBody: body);

            runner.IsTrue(Hits(nearMarker, session), "a word inside the bound matches");
            runner.IsTrue(!Hits(farMarker, session), "a word past the 64,000-character bound does not");
            runner.IsTrue(Hits($"resp:{farMarker}", session), "resp: reads the full body and finds it");
            return Task.CompletedTask;
        });

        await runner.RunAsync("the search index is rebuilt after a session mutates", () =>
        {
            var session = Build(url: "http://api.example.test/v1/orders");
            runner.IsTrue(!Hits("latecomer", session), "the word is absent, which also caches the index");

            session.Response = TextResponse([("X-Late", "latecomer")], string.Empty);
            session.InvalidateSearchIndex();
            runner.IsTrue(Hits("latecomer", session), "invalidating rebuilds the index and the word matches");
            return Task.CompletedTask;
        });

        await runner.RunAsync("an unrecognised field is searched literally, never ignored", () =>
        {
            // A dropped term used to leave the query with no predicates at all, which made a
            // mistyped filter match every captured session.
            var typo = SearchQuery.Parse("stat:200");
            runner.IsTrue(!typo.IsEmpty, "a mistyped field still produces a predicate");
            runner.AreEqual(0, typo.Warnings.Count, "and is not reported as a broken query");

            var unrelated = Build(url: "http://api.example.test/v1/orders", responseStatus: 200);
            runner.IsTrue(!typo.Matches(unrelated), "a mistyped field does not match unrelated traffic");

            var literal = Build(
                url: "http://api.example.test/v1/metrics",
                responseHeaders: [("Content-Type", "text/plain")],
                responseBody: "stat:200 ok");
            runner.IsTrue(typo.Matches(literal), "it matches a session that really contains the text");
            runner.IsTrue(!Hits("-stat:200", literal), "and negates correctly");
            runner.IsTrue(Hits("-stat:200", unrelated), "including on a session without the text");

            var pasted = SearchQuery.Parse("http://api.example.test/v1/orders");
            runner.AreEqual(0, pasted.Warnings.Count, "pasting a URL is not a broken query");
            runner.IsTrue(pasted.Matches(unrelated), "a pasted URL matches the session it came from");
            runner.IsTrue(!pasted.Matches(Build(url: "http://cdn.example.test/v1/orders")),
                "and does not match a different host");
            return Task.CompletedTask;
        });

        await runner.RunAsync("malformed values on known fields are still reported", () =>
        {
            runner.AreEqual(1, SearchQuery.Parse("status:abc").Warnings.Count, "a non-numeric status warns");
            runner.IsTrue(SearchQuery.Parse("status:abc").IsEmpty, "and contributes no predicate");
            runner.AreEqual(1, SearchQuery.Parse("is:bogus").Warnings.Count, "an unknown is: value warns");
            runner.AreEqual(1, SearchQuery.Parse("url:/[unclosed/").Warnings.Count, "a bad regex warns");
            return Task.CompletedTask;
        });
    }

    private static bool Hits(string query, Session session) => SearchQuery.Parse(query).Matches(session);

    private static Session Build(
        string url,
        (string Name, string Value)[]? requestHeaders = null,
        string? requestBody = null,
        (string Name, string Value)[]? responseHeaders = null,
        string? responseBody = null,
        byte[]? responseBodyBytes = null,
        int responseStatus = 200)
    {
        var uri = new Uri(url);
        var request = new HttpRequestData
        {
            Method = "POST",
            RequestTarget = uri.PathAndQuery,
            Url = uri,
            Body = requestBody is null ? [] : Encoding.UTF8.GetBytes(requestBody),
        };
        request.Headers.Add("Host", uri.Host);
        foreach (var (name, value) in requestHeaders ?? [])
            request.Headers.Add(name, value);

        var session = new Session { Request = request, State = SessionState.Complete };

        if (responseHeaders is not null || responseBody is not null || responseBodyBytes is not null)
        {
            var response = TextResponse(responseHeaders, responseBody, responseBodyBytes);
            response.StatusCode = responseStatus;
            session.Response = response;
        }

        session.Completed = DateTimeOffset.Now;
        return session;
    }

    private static HttpResponseData TextResponse(
        (string Name, string Value)[]? headers, string? body, byte[]? bodyBytes = null)
    {
        var response = new HttpResponseData
        {
            Body = bodyBytes ?? (body is null ? [] : Encoding.UTF8.GetBytes(body)),
        };
        foreach (var (name, value) in headers ?? [])
            response.Headers.Add(name, value);
        return response;
    }
}
