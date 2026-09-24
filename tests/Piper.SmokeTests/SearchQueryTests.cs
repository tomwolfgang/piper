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

        await runner.RunAsync("every offered find scope names a field the grammar knows", () =>
        {
            var session = Build(
                url: "http://api.example.test/v1/orders?tag=urlmarker",
                requestHeaders: [("X-Trace", "reqheadermarker")],
                requestBody: "reqbodymarker",
                responseHeaders: [("X-Backend", "respheadermarker")],
                responseBody: "respbodymarker");

            foreach (var (label, field) in SearchQuery.Scopes)
            {
                var scoped = SearchQuery.Parse("orders", field);
                runner.AreEqual(0, scoped.Warnings.Count, $"scope '{label}' parses without warnings");
                runner.IsTrue(!scoped.IsEmpty, $"scope '{label}' compiles a predicate");
            }

            runner.IsTrue(SearchQuery.Parse("urlmarker", "url").Matches(session),
                "the URL scope reaches the query string");
            runner.IsTrue(!SearchQuery.Parse("reqbodymarker", "url").Matches(session),
                "and not the request body");
            runner.IsTrue(SearchQuery.Parse("reqheadermarker", "reqheader").Matches(session),
                "the request-header scope reaches a request header");
            runner.IsTrue(!SearchQuery.Parse("respheadermarker", "reqheader").Matches(session),
                "and not a response header");
            runner.IsTrue(SearchQuery.Parse("respheadermarker", "respheader").Matches(session),
                "the response-header scope reaches a response header");
            runner.IsTrue(SearchQuery.Parse("reqheadermarker", "header").Matches(session)
                && SearchQuery.Parse("respheadermarker", "header").Matches(session),
                "the headers scope reaches both sides");
            runner.IsTrue(SearchQuery.Parse("reqbodymarker", "body").Matches(session)
                && SearchQuery.Parse("respbodymarker", "body").Matches(session),
                "the bodies scope reaches both sides");
            runner.IsTrue(!SearchQuery.Parse("respheadermarker", "body").Matches(session),
                "and not a header");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a find scope restricts bare terms but not fielded ones", () =>
        {
            var requestOnly = Build(
                url: "http://api.example.test/v1/orders",
                requestBody: "{\"marker\":1}",
                responseBody: "nothing here");
            var responseOnly = Build(
                url: "http://cdn.example.test/logo.svg",
                requestBody: "{}",
                responseBody: "marker in the response",
                responseStatus: 404);

            runner.IsTrue(SearchQuery.Parse("marker", "req").Matches(requestOnly),
                "a request-body scope matches a term in the request body");
            runner.IsTrue(!SearchQuery.Parse("marker", "req").Matches(responseOnly),
                "and not one that only appears in the response body");
            runner.IsTrue(SearchQuery.Parse("marker", "resp").Matches(responseOnly),
                "a response-body scope matches the other way round");
            runner.IsTrue(!SearchQuery.Parse("orders", "resp").Matches(requestOnly),
                "a scoped term no longer reaches the URL");
            runner.IsTrue(SearchQuery.Parse("orders", "url").Matches(requestOnly),
                "a URL scope still matches the URL");

            runner.IsTrue(SearchQuery.Parse("status:404 marker", "resp").Matches(responseOnly),
                "a term that names its own field keeps it under a scope");
            runner.IsTrue(!SearchQuery.Parse("status:200 marker", "resp").Matches(responseOnly),
                "and is still ANDed with the scoped term");
            runner.IsTrue(!SearchQuery.Parse("-marker", "resp").Matches(responseOnly),
                "negation survives the scope");

            runner.AreEqual("req", SearchQuery.Parse("marker", "req").Fields[0],
                "the scope is reported as the field the query used");
            runner.IsTrue(SearchQuery.Parse("", "req").IsEmpty, "an empty scoped query matches nothing");
            return Task.CompletedTask;
        });

        await runner.RunAsync("next-match navigation walks the highlighted rows and wraps", () =>
        {
            var rows = new List<Session>
            {
                Build(url: "http://api.example.test/v1/orders"),
                Build(url: "http://cdn.example.test/logo.svg"),
                Build(url: "http://api.example.test/v1/orders/42"),
            };
            var query = SearchQuery.Parse("orders");

            runner.AreEqual(0, query.NextMatchIndex(rows, -1), "nothing selected starts at the top");
            runner.AreEqual(2, query.NextMatchIndex(rows, 0), "the next match skips the non-matching row");
            runner.AreEqual(0, query.NextMatchIndex(rows, 2), "the last match wraps to the first");
            runner.AreEqual(0, query.NextMatchIndex(rows, 99), "an out-of-range start searches from the top");

            runner.AreEqual(-1, SearchQuery.Parse("invoices").NextMatchIndex(rows, -1),
                "a query with no match reports no row");
            runner.AreEqual(-1, query.NextMatchIndex([], -1), "an empty list reports no row");
            runner.AreEqual(-1, SearchQuery.Empty.NextMatchIndex(rows, -1), "an empty query reports no row");
            return Task.CompletedTask;
        });

        await runner.RunAsync("status alternatives accept the full status syntax", () =>
        {
            var ok = Build(url: "http://api.example.test/ok", responseBody: string.Empty, responseStatus: 200);
            var found = Build(url: "http://api.example.test/moved", responseBody: string.Empty, responseStatus: 302);
            var notFound = Build(url: "http://api.example.test/missing", responseBody: string.Empty, responseStatus: 404);
            var unavailable = Build(url: "http://api.example.test/down", responseBody: string.Empty, responseStatus: 503);

            // Each alternative used to go through int.TryParse alone, so a class shorthand became an
            // unmatched -1 and "status:4xx|5xx" quietly matched nothing at all.
            runner.IsTrue(Hits("status:4xx|5xx", notFound), "status:4xx|5xx matches a 404");
            runner.IsTrue(Hits("status:4xx|5xx", unavailable), "and a 503");
            runner.IsTrue(!Hits("status:4xx|5xx", ok), "but not a 200");
            runner.IsTrue(Hits("status:200|3xx", found), "a code and a class mix");
            runner.IsTrue(Hits("status:200|3xx", ok), "either side matches");
            runner.IsTrue(!Hits("status:200|3xx", notFound), "and nothing else does");
            runner.IsTrue(Hits("status:>=500|404", notFound), "comparisons work as alternatives");
            runner.IsTrue(Hits("status:>=500|404", unavailable), "on both sides");
            runner.IsTrue(Hits("-status:4xx|5xx", ok) && !Hits("-status:4xx|5xx", notFound),
                "a negated alternative list is the complement");
            runner.IsTrue(Hits("status:404|", notFound), "an empty alternative is ignored");

            // An alternative that cannot be read is reported, the same as status:abc on its own,
            // instead of dropping out of the list without a word.
            runner.AreEqual(1, SearchQuery.Parse("status:abc|def").Warnings.Count, "unreadable alternatives warn");
            runner.AreEqual(1, SearchQuery.Parse("status:200|abc").Warnings.Count, "even next to a good one");
            return Task.CompletedTask;
        });

        await runner.RunAsync("domain: matches a host and its subdomains", () =>
        {
            var apex = Build(url: "http://example.com/");
            var sub = Build(url: "http://api.Example.com/");
            var lookalike = Build(url: "http://evil-example.com/");
            var suffixed = Build(url: "http://example.com.attacker.net/");

            runner.IsTrue(Hits("domain:example.com", apex), "the domain itself");
            runner.IsTrue(Hits("domain:example.com", sub), "a subdomain, in any case");
            runner.IsTrue(!Hits("domain:example.com", lookalike), "not a host that merely ends in the text");
            runner.IsTrue(!Hits("domain:example.com", suffixed), "not a host that merely contains it");
            runner.IsTrue(Hits("d:*.example.com", sub), "d: is an alias, and a *. wildcard is accepted");
            runner.IsTrue(Hits("domain:other.test|example.com", apex), "alternatives with |");
            runner.IsTrue(Hits("-domain:example.com", lookalike) && !Hits("-domain:example.com", sub),
                "negation hides the domain and nothing else");
            runner.AreEqual(1, SearchQuery.Parse("domain:*").Warnings.Count, "a pattern with no domain in it warns");

            // Session.Host is the raw Host header when the request line had no parseable URL, so it
            // can carry a port. A domain must still match it, or a show-only list saved as
            // "example.com" would discard those sessions at admission.
            static Session RawHost(string hostHeader)
            {
                var request = new HttpRequestData { Method = "GET", RequestTarget = "/" };
                request.Headers.Add("Host", hostHeader);
                return new Session { Request = request, State = SessionState.Complete };
            }

            runner.AreEqual("example.com:8443", RawHost("example.com:8443").Host, "precondition: the host keeps its port");
            runner.IsTrue(Hits("domain:example.com", RawHost("example.com:8443")), "a host with a port matches its domain");
            runner.IsTrue(Hits("domain:example.com", RawHost("api.example.com:443")), "and so does a subdomain with one");
            runner.IsTrue(!Hits("domain:example.com", RawHost("evil-example.com:8443")), "a lookalike with a port still does not");
            runner.IsTrue(Hits("domain:10.0.0.1", RawHost("10.0.0.1:8080")), "an IPv4 address with a port matches the address");
            runner.IsTrue(Hits("domain:[::1]", RawHost("[::1]:8080")), "a bracketed IPv6 literal with a port matches it");
            runner.IsTrue(!Hits("domain:example.com", RawHost("example.com:notaport")), "a colon that is not a port is not stripped");
            runner.IsTrue(!Hits("-domain:example.com", RawHost("example.com:8443")), "hiding the domain hides its hosts with a port too");
            return Task.CompletedTask;
        });

        await runner.RunAsync("domain: keeps matching fragments the way saved host lists expect", () =>
        {
            // Saved filtersets hold patterns written when every host pattern was a substring. A
            // fragment must keep that meaning, or a show-only list of them would match nothing after
            // an upgrade and discard all traffic at admission.
            var api = Build(url: "http://api.curseforge.com/");
            var lan = Build(url: "http://192.168.1.20/");
            var local = Build(url: "http://localhost:8080/");

            runner.IsTrue(Hits("domain:curseforge", api), "a word with no dot matches anywhere in the host");
            runner.IsTrue(Hits("domain:api.", api), "so does a single word ending in a dot");

            // A trailing root dot on a full domain names the same domain; treating it as a substring
            // fragment let a pasted FQDN admit a lookalike that merely contains it.
            var apex = Build(url: "http://example.com/");
            runner.IsTrue(Hits("domain:example.com.", apex), "example.com. matches example.com");
            runner.IsTrue(Hits("domain:example.com.", Build(url: "http://api.example.com/")), "and its subdomains");
            runner.IsTrue(!Hits("domain:example.com.", Build(url: "http://example.com.attacker.net/")),
                "but not a host that only contains it");
            runner.IsTrue(Hits("domain:192.168.1", lan), "and a partial IPv4 address");
            runner.IsTrue(Hits("domain:192.168.", lan), "with or without its trailing dot");
            runner.IsTrue(Hits("domain:localhost", local), "a single-label host still matches");
            runner.IsTrue(Hits("domain:192.168.1.20", lan), "a full IPv4 address matches itself");
            runner.IsTrue(!Hits("domain:92.168.1.20", lan), "and not a longer address ending in it");
            runner.IsTrue(SearchQuery.MatchesHostPattern("API.CurseForge.com", "*.curseforge.com"),
                "the shared rule ignores case and a wildcard");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a number too large for its field is a warning, not a crash", () =>
        {
            // long.Parse throws OverflowException, which the parser used not to catch, so typing one
            // digit too many into the filter box took the app down.
            var session = Build(url: "http://api.example.test/", responseBody: string.Empty, responseStatus: 200);
            foreach (var text in new[] { "status:99999999999999999999", "status:200|99999999999999999999", "id:>99999999999999999999" })
            {
                Exception? thrown = null;
                SearchQuery? query = null;
                try { query = SearchQuery.Parse(text); }
                catch (Exception ex) { thrown = ex; }
                runner.IsTrue(thrown is null, $"{text} parses ({thrown?.GetType().Name})");
                runner.AreEqual(1, query?.Warnings.Count ?? -1, $"{text} is reported as a warning");
            }

            runner.IsTrue(Hits("status:200|99999999999999999999", session), "and the query still runs without the bad term");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a regex that backtracks past its timeout fails the query closed", () =>
        {
            // A catastrophic pattern is ordinary input: the user types the pattern and the traffic
            // supplies the text. The timeout used to escape Matches and crash every grid refresh.
            const string pattern = @"(\w+\s?)*$";
            var trap = new string('a', 40) + "!";
            Session Trap() => Build(
                url: "http://api.example.test/" + trap,
                requestHeaders: [("Content-Type", "text/plain")],
                requestBody: trap);
            var session = Trap();

            var probe = new System.Text.RegularExpressions.Regex(pattern,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(250));
            var probeTimedOut = false;
            try { probe.IsMatch(session.Path); }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { probeTimedOut = true; }
            runner.IsTrue(probeTimedOut, "precondition: the pattern really does time out on this input");

            // The pattern can match an empty tail, so only fields whose first catastrophic run comes
            // before any easy match prove the point: the path, the whole-session index and the body.
            foreach (var text in new[] { $"path:/{pattern}/", $"/{pattern}/", $"body:/{pattern}/" })
            {
                var query = SearchQuery.Parse(text);
                Exception? thrown = null;
                var matched = true;
                try { matched = query.Matches(session); }
                catch (Exception ex) { thrown = ex; }
                runner.IsTrue(thrown is null, $"{text} does not throw ({thrown?.GetType().Name})");
                runner.IsTrue(!matched, $"{text} does not match");
                runner.IsTrue(query.RegexTimedOut, $"{text} reports that it timed out");
            }

            // Negation must not turn the timeout into "matches everything": an AutoResponder rule
            // built on it would then answer every request.
            var negated = SearchQuery.Parse($"-path:/{pattern}/");
            runner.IsTrue(!negated.Matches(session), "a negated term that timed out fails closed too");

            // One timeout costs one timeout, not one per session per refresh: a grid of trap rows used
            // to take 250 ms each on every 150 ms rebuild.
            var rows = Enumerable.Range(0, 40).Select(_ => Trap()).ToList();
            var grid = SearchQuery.Parse($"path:/{pattern}/");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var shown = rows.Count(grid.Matches);
            clock.Stop();
            runner.AreEqual(0, shown, "no trap row is shown");
            runner.IsTrue(clock.ElapsedMilliseconds < 2_500,
                $"40 trap rows are filtered in about one timeout, not forty ({clock.ElapsedMilliseconds} ms)");

            var healthy = SearchQuery.Parse("path:/orders/");
            runner.IsTrue(healthy.Matches(Build(url: "http://api.example.test/orders")) && !healthy.RegexTimedOut,
                "an ordinary pattern is unaffected");
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
