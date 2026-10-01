using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

// S4: a regex that runs past its timeout must never be read as "this did not match". Negation (NOT:,
// a filterset's hide list) turns "did not match" into "matched", so a timeout used to fabricate an
// AutoResponder response or, in the other direction, discard every later session from capture.
internal static class RegexTimeoutSafetyTests
{
    // 40 'a' then '!' makes (a+)+ followed by an anchor fail only after trying every split: 2^40 steps.
    private static readonly string TrapPath = "/" + new string('a', 40) + "!";

    private const string UrlPattern = @"^http://api\.example\.test/(a+)+$";
    private const string PathPattern = @"^\/(a+)+$";

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("regex timeout: the trap really is catastrophic for both patterns", () =>
        {
            var url = Trap().Request!.Url!.ToString();
            runner.IsTrue(TimesOut(UrlPattern, url), "precondition: the URL pattern times out on the trap URL");
            runner.IsTrue(TimesOut(PathPattern, Trap().Path), "precondition: the path pattern times out on the trap path");
            runner.IsTrue(!TimesOut(UrlPattern, Ordinary().Request!.Url!.ToString()), "and fails fast on an ordinary URL");
            return Task.CompletedTask;
        });

        await runner.RunAsync("regex timeout: a rule match is match / no match / timed out, and a timeout is never a hit", () =>
        {
            var trap = Trap();

            var plain = AutoResponderMatch.Parse("REGEX:" + UrlPattern).Match(trap);
            runner.IsTrue(plain.TimedOut, "REGEX: reports that it timed out");
            runner.IsTrue(!plain.Success, "and does not match");

            foreach (var expression in new[]
            {
                "NOT:REGEX:" + UrlPattern,
                "NOT:NOT:REGEX:" + UrlPattern,
                "NOT:Q:path:/" + PathPattern + "/",
                "Q:path:/" + PathPattern + "/",
                "NOT:Q:-path:/" + PathPattern + "/",
            })
            {
                var result = AutoResponderMatch.Parse(expression).Match(trap);
                runner.IsTrue(!result.Success, $"{expression}: a timeout is not a hit");
                runner.IsTrue(result.TimedOut, $"{expression}: a timeout is reported as one");
            }

            var ordinary = Ordinary();
            var miss = AutoResponderMatch.Parse("REGEX:" + UrlPattern).Match(ordinary);
            runner.IsTrue(!miss.Success && !miss.TimedOut, "a pattern that fast-fails is a plain miss");
            var negatedMiss = AutoResponderMatch.Parse("NOT:REGEX:" + UrlPattern).Match(ordinary);
            runner.IsTrue(negatedMiss.Success && !negatedMiss.TimedOut, "so its negation still hits");
            var negatedQuery = AutoResponderMatch.Parse("NOT:Q:path:/" + PathPattern + "/").Match(ordinary);
            runner.IsTrue(negatedQuery.Success && !negatedQuery.TimedOut, "NOT:Q: on a healthy query still hits");
            return Task.CompletedTask;
        });

        await runner.RunAsync("regex timeout: SearchQuery.Evaluate has three states and stays timed out", () =>
        {
            var query = SearchQuery.Parse("path:/" + PathPattern + "/");
            runner.AreEqual(SearchOutcome.NoMatch, query.Evaluate(Ordinary()), "an ordinary session does not match");
            runner.AreEqual(SearchOutcome.Match, SearchQuery.Parse("path:orders").Evaluate(Ordinary()), "a matching session matches");

            runner.AreEqual(SearchOutcome.TimedOut, query.Evaluate(Trap()), "the trap times out");
            runner.IsTrue(query.RegexTimedOut, "and the query says so");
            runner.AreEqual(SearchOutcome.TimedOut, query.Evaluate(Ordinary()), "a timed-out query stays timed out");
            runner.IsTrue(!query.Matches(Ordinary()), "Matches keeps failing closed for the user's own search box");

            runner.IsTrue(query.MatchesFailOpen(Ordinary()), "MatchesFailOpen admits what a timed-out query cannot judge");
            var healthy = SearchQuery.Parse("path:/orders/");
            runner.IsTrue(healthy.MatchesFailOpen(Ordinary()) && !healthy.MatchesFailOpen(Trap()),
                "and is an ordinary match for a query that never timed out");
            return Task.CompletedTask;
        });

        await runner.RunAsync("regex timeout: a rule that timed out is skipped, flagged, and answers nothing", () =>
        {
            var broken = new AutoResponderRule { Match = "NOT:Q:path:/" + PathPattern + "/", Action = "*418" };
            var working = new AutoResponderRule { Match = "orders", Action = "*503" };
            var responder = new AutoResponder();
            var announced = new List<string>();
            responder.RuleTimedOut += (_, description) => announced.Add(description);
            responder.Apply(new AutoResponderSettings { Enabled = true, Rules = [broken, working] });

            var first = responder.Evaluate(Trap());
            runner.AreEqual(AutoResponderOutcome.Passthrough, first.Outcome, "the trap request is not answered by the NOT:Q: rule");
            runner.IsTrue(responder.TimedOutRuleIds.Contains(broken.Id), "the rule is flagged as broken");
            runner.IsTrue(!responder.TimedOutRuleIds.Contains(working.Id), "its neighbour is not");
            runner.AreEqual(1, announced.Count, "the timeout is announced once");

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var later = responder.Evaluate(Ordinary());
            responder.Evaluate(Trap());
            clock.Stop();
            runner.AreEqual(working.Id, later.Rule?.Id, "a later request skips the broken rule and reaches the next one");
            runner.AreEqual(AutoResponderOutcome.Respond, later.Outcome, "and is answered by it");
            runner.AreEqual(1, announced.Count, "the broken rule is not announced again");
            runner.IsTrue(clock.ElapsedMilliseconds < 200,
                $"a broken rule is not evaluated again, so it costs no further timeout ({clock.ElapsedMilliseconds} ms)");
            runner.AreEqual(0L, responder.StatsFor(broken.Id).Hits, "a timeout is never counted as a hit");

            var elsewhere = responder.Evaluate(SessionFor("http://api.example.test/other"));
            runner.AreEqual(AutoResponderOutcome.Passthrough, elsewhere.Outcome, "an unrelated request still goes upstream");

            responder.Apply(responder.Export());
            runner.AreEqual(0, responder.TimedOutRuleIds.Count, "re-applying the rules recompiles them and clears the flag");
            return Task.CompletedTask;
        });

        await runner.RunAsync("regex timeout: a timed-out REGEX: rule is skipped in the engine too", () =>
        {
            var rule = new AutoResponderRule { Match = "NOT:REGEX:" + UrlPattern, Action = "*418" };
            var responder = new AutoResponder();
            responder.Apply(new AutoResponderSettings { Enabled = true, Rules = [rule] });

            runner.AreEqual(AutoResponderOutcome.Passthrough, responder.Evaluate(Trap()).Outcome,
                "a catastrophic NOT:REGEX: does not fabricate a response");
            runner.AreEqual(AutoResponderOutcome.Passthrough, responder.Evaluate(Ordinary()).Outcome,
                "nor does it answer later traffic");
            runner.IsTrue(responder.TimedOutRuleIds.Contains(rule.Id), "and the rule is flagged");

            var tested = responder.Evaluate(Trap(), recordHit: false);
            runner.AreEqual(AutoResponderOutcome.Passthrough, tested.Outcome, "the panel's tester agrees");
            return Task.CompletedTask;
        });

        await runner.RunAsync("regex timeout: a filterset timeout keeps admitting later traffic", () =>
        {
            var query = SearchQuery.Parse("path:/" + PathPattern + "/");
            var store = new SessionStore { CompletedSessionFilter = query.MatchesFailOpen };

            store.Add(Completed(Ordinary()));
            runner.AreEqual(0, store.Count, "before the timeout the filterset discards what it does not match");

            store.Add(Completed(Trap()));
            runner.IsTrue(query.RegexTimedOut, "the trap request timed the filterset out");
            runner.AreEqual(1, store.Count, "the request it could not judge is kept");

            store.Add(Completed(Ordinary()));
            store.Add(Completed(SessionFor("http://api.example.test/invoices")));
            runner.AreEqual(3, store.Count, "later traffic keeps being admitted instead of being silently discarded");

            var closed = SearchQuery.Parse("path:/" + PathPattern + "/");
            var closedStore = new SessionStore { CompletedSessionFilter = closed.Matches };
            closedStore.Add(Completed(Trap()));
            closedStore.Add(Completed(Ordinary()));
            runner.AreEqual(0, closedStore.Count, "(Matches itself still fails closed: the reason the admission filter does not use it)");
            return Task.CompletedTask;
        });

        await runner.RunAsync("URLWithBody: the request body is decoded once per evaluation, however many rules read it", () =>
        {
            var session = SessionFor("http://api.example.test/checkout", "POST", Gzip("alpha beta gamma"), "gzip");

            var scratch = default(MatchScratch);
            foreach (var expression in new[] { "URLWithBody:alpha", "URLWithBody:beta", "NOT:URLWithBody:delta", "URLWithBody:gamma" })
            {
                var result = AutoResponderMatch.Parse(expression).Match(session, ref scratch);
                runner.IsTrue(result.Success, $"{expression} still evaluates correctly");
            }

            runner.AreEqual(1, scratch.BodyDecodes, "four URLWithBody: rules decode the body once between them");

            var other = default(MatchScratch);
            AutoResponderMatch.Parse("checkout").Match(session, ref other);
            AutoResponderMatch.Parse("REGEX:checkout").Match(session, ref other);
            AutoResponderMatch.Parse("Q:method:POST").Match(session, ref other);
            runner.AreEqual(0, other.BodyDecodes, "rules that do not read the body never decode it");

            var bodiless = default(MatchScratch);
            runner.IsTrue(AutoResponderMatch.Parse("URLWithBody:checkout").Match(SessionFor("http://api.example.test/checkout"), ref bodiless).Success,
                "a request with no body matches on its URL");
            runner.AreEqual(0, bodiless.BodyDecodes, "and has nothing to decode");
            return Task.CompletedTask;
        });

        await runner.RunAsync("URLWithBody: the decoded body is bounded", () =>
        {
            var filler = new string('x', 3 * 1024 * 1024);
            var bomb = SessionFor("http://api.example.test/upload", "POST", Gzip("STARTMARK" + filler + "ENDMARK"), "gzip");
            runner.IsTrue(bomb.Request!.Body.Length < 64 * 1024, "precondition: the compressed body is small");

            var scratch = default(MatchScratch);
            runner.IsTrue(AutoResponderMatch.Parse("URLWithBody:STARTMARK").Match(bomb, ref scratch).Success,
                "text near the start of a compressed body is found");
            runner.IsTrue(!AutoResponderMatch.Parse("URLWithBody:ENDMARK").Match(bomb, ref scratch).Success,
                "text beyond the decode bound is not");
            runner.IsTrue(scratch.UrlWithBody!.Length < 2 * 1024 * 1024, "the haystack never grows past the bound");

            var plain = SessionFor("http://api.example.test/upload", "POST", Encoding.UTF8.GetBytes("STARTMARK" + filler + "ENDMARK"));
            var plainScratch = default(MatchScratch);
            runner.IsTrue(!AutoResponderMatch.Parse("URLWithBody:ENDMARK").Match(plain, ref plainScratch).Success,
                "an uncompressed body is bounded the same way");
            runner.IsTrue(plainScratch.UrlWithBody!.Length < 2 * 1024 * 1024, "and so is its haystack");

            var corrupt = SessionFor("http://api.example.test/upload", "POST", [0x1f, 0x8b, 8, 0, 1, 2, 3, 4, 5], "gzip");
            var corruptScratch = default(MatchScratch);
            runner.IsTrue(AutoResponderMatch.Parse("URLWithBody:upload").Match(corrupt, ref corruptScratch).Success,
                "a body that will not decode still matches on its URL");
            return Task.CompletedTask;
        });
    }

    private static bool TimesOut(string pattern, string input)
    {
        var probe = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        try
        {
            probe.IsMatch(input);
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }

    private static Session Trap() => SessionFor("http://api.example.test" + TrapPath);

    private static Session Ordinary() => SessionFor("http://api.example.test/orders");

    private static Session Completed(Session session)
    {
        session.Response = new HttpResponseData { StatusCode = 200 };
        session.Completed = DateTimeOffset.Now;
        return session;
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    private static Session SessionFor(string url, string method = "GET", byte[]? body = null, string? contentEncoding = null)
    {
        var uri = new Uri(url);
        var request = new HttpRequestData
        {
            Method = method,
            Url = uri,
            RequestTarget = uri.PathAndQuery,
            Body = body ?? [],
        };
        request.Headers.Add("Content-Type", "text/plain");
        if (contentEncoding is not null) request.Headers.Add("Content-Encoding", contentEncoding);

        return new Session
        {
            Request = request,
            IsHttps = uri.Scheme == "https",
            State = SessionState.SendingRequest,
        };
    }
}
