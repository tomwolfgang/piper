using Piper.Core.Http;
using Piper.Core.Sessions;

// The capture list's right-click hides: what each kind matches, and how the list treats the hostile
// values captured sessions hand it -- hosts, process names and content types are all attacker-controlled.
internal static class SessionHideListTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("a hide matches its own kind: host, process or media type", () =>
        {
            var hides = new SessionHideList();
            runner.IsTrue(hides.Add(SessionHideKind.Host, "example.com"), "a host is hidden");
            runner.IsTrue(hides.Add(SessionHideKind.Process, " Game "), "a process is hidden, trimmed");
            runner.IsTrue(hides.Add(SessionHideKind.ContentType, "image/png"), "a media type is hidden");
            var (host, process, type) = (hides.Items[0], hides.Items[1], hides.Items[2]);

            runner.IsTrue(host.Matches(Session("api.example.com")), "a host hide covers its subdomains");
            runner.IsTrue(!host.Matches(Session("evil-example.com")), "but not a lookalike");
            runner.AreEqual("Game", process.Value, "the stored process is trimmed");
            runner.IsTrue(process.Matches(Session("a.test", process: "game")), "a process matches without regard to case");
            runner.IsTrue(process.Matches(Session("a.test", process: "  GAME\t")), "and with whitespace around the captured name");
            runner.IsTrue(!process.Matches(Session("a.test", process: "gamer")), "but only the whole name");
            runner.IsTrue(type.Matches(Session("a.test", contentType: "IMAGE/PNG; q=1")), "a media type ignores parameters and case");
            runner.IsTrue(!type.Matches(Session("a.test", contentType: "image/pngx")), "but only the whole type");
            runner.IsTrue(!type.Matches(Session("a.test")), "and a session with no response is not matched");
            return Task.CompletedTask;
        });

        await runner.RunAsync("each hide counts every session it matches", () =>
        {
            var hides = new SessionHideList();
            hides.Add(SessionHideKind.Process, "game");
            hides.Add(SessionHideKind.Host, "example.com");
            var both = Session("example.com", process: "game");
            var neither = Session("other.test", process: "browser");

            runner.IsTrue(hides.HidesAndCounts(both), "a session matching both is hidden");
            runner.IsTrue(!hides.HidesAndCounts(neither), "one matching neither is not");
            runner.AreEqual(1, hides.Items[0].Hidden, "the first hide counts it");
            runner.AreEqual(1, hides.Items[1].Hidden, "and so does the second, not only the first match");

            hides.ResetCounts();
            runner.AreEqual(0, hides.Items[0].Hidden + hides.Items[1].Hidden, "a reset zeroes every count");
            return Task.CompletedTask;
        });

        await runner.RunAsync("hides refuse malformed, duplicate and unbounded input", () =>
        {
            var hides = new SessionHideList();
            runner.IsTrue(!hides.Add(SessionHideKind.Process, null), "a null value is refused");
            runner.IsTrue(!hides.Add(SessionHideKind.Process, "   "), "a blank value is refused");
            runner.IsTrue(!hides.Add(SessionHideKind.Process, new string('p', SessionHideList.MaxValueLength + 1)), "an overlong value is refused");
            runner.IsTrue(hides.Add(SessionHideKind.Process, new string('p', SessionHideList.MaxValueLength)), "the longest allowed value is kept");

            // Session.Host is the raw Host header when the request line had no parseable URL.
            foreach (var host in new[] { "a.com\" -host:x", "a.com host:b", "a/b", "*", "a.com;b.com" })
                runner.IsTrue(!hides.Add(SessionHideKind.Host, host), $"a host that is not plain hostname material is refused ({host})");

            runner.IsTrue(hides.Add(SessionHideKind.Host, "example.com"), "a plain host is kept");
            runner.IsTrue(!hides.Add(SessionHideKind.Host, " EXAMPLE.com "), "a case-differing duplicate is refused");
            runner.IsTrue(hides.Add(SessionHideKind.Process, "example.com"), "the same text as another kind is a different hide");

            while (hides.Add(SessionHideKind.ContentType, $"type/{hides.Count}")) { }
            runner.AreEqual(SessionHideList.MaxHides, hides.Count, "the list stops at its cap");
            runner.IsTrue(hides.IsFull, "and says it is full");

            var first = hides.Items[0];
            runner.IsTrue(hides.Remove(first), "a hide can be removed");
            runner.IsTrue(!hides.Remove(first), "but only once");
            runner.IsTrue(hides.Clear(), "Clear empties the list");
            runner.IsTrue(!hides.Clear(), "and reports nothing to do when it is empty");
            return Task.CompletedTask;
        });
    }

    private static Session Session(string host, string? process = null, string? contentType = null)
    {
        var session = new Session
        {
            Request = new HttpRequestData { Method = "GET", Url = new Uri($"https://{host}/"), RequestTarget = "/" },
            ProcessName = process ?? string.Empty,
        };
        if (contentType is not null)
        {
            session.Response = new HttpResponseData { StatusCode = 200 };
            session.Response.Headers.Add("Content-Type", contentType);
        }
        return session;
    }
}
