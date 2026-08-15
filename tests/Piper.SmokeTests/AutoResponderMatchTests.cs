using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

internal static class AutoResponderMatchTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("AutoResponder match: Fiddler prefixes", () =>
        {
            var session = SessionFor("http://api.example.com/v1/Orders?id=42");

            runner.IsTrue(Hits("orders", session), "a bare expression is a substring of the URL");
            runner.IsTrue(Hits("ORDERS", session), "and is case-insensitive, as in Fiddler");
            runner.IsTrue(!Hits("invoices", session), "a substring that is absent does not match");

            runner.IsTrue(Hits("EXACT:http://api.example.com/v1/Orders?id=42", session), "EXACT: matches the whole URL");
            runner.IsTrue(!Hits("EXACT:http://api.example.com/v1/orders?id=42", session), "EXACT: is case-sensitive");
            runner.IsTrue(!Hits("EXACT:http://api.example.com/v1/Orders", session), "EXACT: is not a prefix match");

            runner.IsTrue(Hits("NOT:invoices", session), "NOT: inverts");
            runner.IsTrue(!Hits("NOT:orders", session), "NOT: inverts a hit into a miss");
            runner.IsTrue(!Hits("NOT:EXACT:http://api.example.com/v1/Orders?id=42", session), "NOT: wraps another prefix");

            runner.IsTrue(Hits("METHOD:get", session), "METHOD: ignores case");
            runner.IsTrue(!Hits("METHOD:POST", session), "METHOD: rejects a different verb");

            runner.IsTrue(Hits("REGEX:/v1/[a-z]+\\?id=\\d+", session), "REGEX: matches, ignoring case by default");
            runner.IsTrue(!Hits("REGEX:^https://", session), "REGEX: anchors behave");

            // An empty rule must never claim traffic -- it is what a freshly added row looks like.
            runner.IsTrue(!Hits("", session), "a blank expression never matches");
            runner.IsTrue(!Hits("   ", session), "whitespace is blank too");
            runner.IsTrue(AutoResponderMatch.Parse("").IsEmpty, "a blank expression reports itself empty");

            return Task.CompletedTask;
        });

        await runner.RunAsync("AutoResponder match: headers, bodies and captures", () =>
        {
            var posted = SessionFor("https://example.com/checkout", "POST",
                body: """{"coupon":"HALFOFF"}""",
                headers: [("Content-Type", "application/json"), ("X-Env", "staging")]);

            runner.IsTrue(Hits("HEADER:X-Env", posted), "HEADER: with no value matches on presence");
            runner.IsTrue(Hits("HEADER:X-Env=staging", posted), "HEADER: matches a value");
            runner.IsTrue(Hits("HEADER:x-env=STAGING", posted), "HEADER: is case-insensitive on both halves");
            runner.IsTrue(!Hits("HEADER:X-Env=production", posted), "HEADER: rejects a different value");
            runner.IsTrue(!Hits("HEADER:X-Missing", posted), "HEADER: rejects an absent header");

            runner.IsTrue(Hits("URLWithBody:HALFOFF", posted), "URLWithBody: reaches the request body");
            runner.IsTrue(Hits("URLWithBody:checkout", posted), "URLWithBody: still matches the URL half");
            runner.IsTrue(!Hits("HALFOFF", posted), "a bare expression does not read the body");

            // Captures are the whole point of REGEX: -- they feed ${...} in the action.
            var captured = AutoResponderMatch.Parse(@"REGEX:https://(?<host>[^/]+)/(\w+)")
                .Match(SessionFor("https://cdn.example.com/assets"));
            runner.IsTrue(captured.Success, "a capturing regex matches");
            runner.AreEqual("cdn.example.com", captured.Expand("${host}"), "named group substitutes");
            runner.AreEqual("assets", captured.Expand("${1}"), "numbered group substitutes");
            runner.AreEqual("http://localhost/assets", captured.Expand("http://localhost/${1}"), "substitution inside a URL");
            runner.AreEqual("${nope}", captured.Expand("${nope}"), "an unknown reference is left alone, not blanked");

            var negated = AutoResponderMatch.Parse("NOT:REGEX:(?<host>nothing)").Match(posted);
            runner.IsTrue(negated.Success, "a negated regex still matches when the pattern is absent");
            runner.AreEqual("${host}", negated.Expand("${host}"), "but a negated match captures nothing");

            return Task.CompletedTask;
        });

        await runner.RunAsync("AutoResponder match: Q: escape into the filter grammar", () =>
        {
            var posted = SessionFor("https://api.example.com/v1/orders", "POST",
                body: "{}", headers: [("X-Env", "staging")]);

            runner.IsTrue(Hits("Q:method:POST host:api.example.com", posted), "Q: ANDs terms like the Filters tab");
            runner.IsTrue(!Hits("Q:method:GET", posted), "Q: rejects a non-matching term");
            runner.IsTrue(Hits("Q:-method:GET", posted), "Q: honours negation");
            runner.IsTrue(Hits("Q:path:/v1/ reqheader:X-Env=staging", posted), "Q: reads request headers");
            runner.IsTrue(Hits("NOT:Q:method:GET", posted), "NOT: wraps a Q: expression");

            // The response does not exist when a rule runs, so terms about it are refused up front
            // rather than quietly comparing against nothing.
            foreach (var rejected in new[] { "Q:status:200", "Q:resp:hello", "Q:size:>10", "Q:dur:>5", "Q:is:json" })
            {
                var match = AutoResponderMatch.Parse(rejected);
                runner.IsTrue(match.Warning is not null, $"{rejected} is rejected at parse time");
                runner.IsTrue(!match.Match(posted).Success, $"{rejected} never matches");
            }

            runner.IsTrue(AutoResponderMatch.Parse("Q:is:https").Warning is null, "is:https describes the request, so it is allowed");
            runner.IsTrue(Hits("Q:is:https", posted), "and it evaluates");

            var brokenQuery = AutoResponderMatch.Parse("Q:bogusfield:x");
            runner.IsTrue(brokenQuery.Warning is not null, "an unknown field is reported");

            var brokenRegex = AutoResponderMatch.Parse("REGEX:(unclosed");
            runner.IsTrue(brokenRegex.Warning is not null, "an invalid regex is reported, not thrown");
            runner.IsTrue(!brokenRegex.Match(posted).Success, "and a broken rule never matches");

            return Task.CompletedTask;
        });
    }

    private static bool Hits(string expression, Session session) =>
        AutoResponderMatch.Parse(expression).Match(session).Success;

    private static Session SessionFor(string url, string method = "GET", string? body = null,
        (string Name, string Value)[]? headers = null)
    {
        var uri = new Uri(url);
        var request = new HttpRequestData
        {
            Method = method,
            Url = uri,
            RequestTarget = uri.PathAndQuery,
            Body = body is null ? [] : Encoding.UTF8.GetBytes(body),
        };

        foreach (var (name, value) in headers ?? []) request.Headers.Add(name, value);

        return new Session
        {
            Request = request,
            IsHttps = uri.Scheme == "https",
            State = SessionState.SendingRequest,
        };
    }
}
