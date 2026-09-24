using System.Text;
using Piper.Core.Http;
using Piper.Core.Sessions;

// Clicking a session-grid header sorts the rows by that column. The ordering itself lives in
// SessionSort so it can be checked here without a running ListView.
internal static class SessionSortTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("session sort orders each column both ways", () =>
        {
            var small = Completed("GET", "https://b.example/zeta", 200, "text/html", size: 10, "chrome", TimeSpan.FromMilliseconds(300));
            var large = Completed("POST", "https://a.example/alpha", 404, "application/json", size: 5_000, "firefox", TimeSpan.FromMilliseconds(20));
            var medium = Completed("DELETE", "https://c.example/mid", 302, "image/png", size: 700, string.Empty, TimeSpan.FromMilliseconds(1_500));
            var sessions = new[] { small, large, medium };

            void Check(SessionSortColumn column, Session[] ascending)
            {
                runner.AreEqual(Ids(ascending), Ids(Sorted(sessions, column, descending: false)), $"{column} ascending");
                runner.AreEqual(Ids(Enumerable.Reverse(ascending)), Ids(Sorted(sessions, column, descending: true)), $"{column} descending");
            }

            Check(SessionSortColumn.Id, [small, large, medium]);
            Check(SessionSortColumn.Result, [small, medium, large]);
            Check(SessionSortColumn.Method, [medium, small, large]);
            Check(SessionSortColumn.Host, [large, small, medium]);
            Check(SessionSortColumn.Path, [large, medium, small]);
            // By the text the grid shows: "image/png", "json", "text/html".
            Check(SessionSortColumn.Type, [medium, large, small]);
            // An unresolved process has no name, and sorts before every named one.
            Check(SessionSortColumn.Process, [medium, small, large]);
            Check(SessionSortColumn.Size, [small, medium, large]);
            Check(SessionSortColumn.Time, [large, small, medium]);
            return Task.CompletedTask;
        });

        await runner.RunAsync("session sort keeps ties in id order and ignores case", () =>
        {
            var first = Completed("GET", "https://Same.example/", 200, "text/plain", size: 1, "x", TimeSpan.Zero);
            var second = Completed("GET", "https://same.example/", 200, "text/plain", size: 1, "x", TimeSpan.Zero);
            var third = Completed("GET", "https://SAME.example/", 200, "text/plain", size: 1, "x", TimeSpan.Zero);
            Session[] sessions = [third, first, second];

            // Equal keys would otherwise trade places on every refresh, which reads as flicker.
            runner.AreEqual(Ids([first, second, third]), Ids(Sorted(sessions, SessionSortColumn.Host, false)),
                "equal hosts fall back to capture order");
            runner.AreEqual(Ids([third, second, first]), Ids(Sorted(sessions, SessionSortColumn.Host, true)),
                "and descending reverses the tie-break too");
            runner.AreEqual(Ids([first, second, third]), Ids(Sorted(sessions, SessionSortColumn.Size, false)),
                "equal sizes fall back to capture order");

            // Text compares without regard to case, where a plain ordinal sort would put every
            // capitalised name ahead of every lowercase one.
            var upperB = Completed("GET", "https://a.example/", 200, "text/plain", size: 1, "B", TimeSpan.Zero);
            var lowerA = Completed("GET", "https://a.example/", 200, "text/plain", size: 1, "a", TimeSpan.Zero);
            var upperC = Completed("GET", "https://a.example/", 200, "text/plain", size: 1, "C", TimeSpan.Zero);
            runner.AreEqual(Ids([lowerA, upperB, upperC]), Ids(Sorted([upperC, upperB, lowerA], SessionSortColumn.Process, false)),
                "a, B, C rather than B, C, a");
            return Task.CompletedTask;
        });

        await runner.RunAsync("session sort places rows without a status after real codes", () =>
        {
            var ok = Completed("GET", "https://a.example/", 200, "text/plain", size: 0, "x", TimeSpan.Zero);
            var serverError = Completed("GET", "https://a.example/", 500, "text/plain", size: 0, "x", TimeSpan.Zero);
            var pending = Pending("https://a.example/slow");
            var tunnel = new Session { State = SessionState.Tunnel, IsTunnel = true, Request = Request("CONNECT", "https://t.example/") };
            var failed = new Session { State = SessionState.Failed, Request = Request("GET", "https://f.example/"), Error = "refused" };
            Session[] sessions = [failed, tunnel, pending, serverError, ok];

            runner.AreEqual(Ids([ok, serverError, pending, tunnel, failed]),
                Ids(Sorted(sessions, SessionSortColumn.Result, false)),
                "codes first, then pending (-), CONNECT and ERR");
            return Task.CompletedTask;
        });

        await runner.RunAsync("session sort keeps in-flight sessions still when sorting by time", () =>
        {
            // An in-flight row's elapsed time grows on every read, so sorting by it moved the row a
            // little further down the list on every refresh. It sorts as unknown instead, after every
            // finished session like "-" in the Result column, and in capture order among its kind.
            var slow = Completed("GET", "https://a.example/", 200, "text/plain", size: 0, "x", TimeSpan.FromSeconds(30));
            var quick = Completed("GET", "https://a.example/", 200, "text/plain", size: 0, "x", TimeSpan.FromMilliseconds(100));
            var firstPending = Pending("https://a.example/one");
            var secondPending = Pending("https://a.example/two");
            Session[] sessions = [secondPending, slow, firstPending, quick];

            runner.AreEqual(Ids([quick, slow, firstPending, secondPending]), Ids(Sorted(sessions, SessionSortColumn.Time, false)),
                "finished sessions by duration, then in-flight ones in capture order");
            runner.AreEqual(Ids([secondPending, firstPending, slow, quick]), Ids(Sorted(sessions, SessionSortColumn.Time, true)),
                "descending reverses both");

            // Both finished sessions have empty bodies, so they tie on size and keep capture order.
            runner.AreEqual(Ids([slow, quick, firstPending, secondPending]), Ids(Sorted(sessions, SessionSortColumn.Size, false)),
                "by Size too, since a body still arriving counts up in place");

            var before = Ids(Sorted(sessions, SessionSortColumn.Time, false));
            Thread.Sleep(20);
            runner.AreEqual(before, Ids(Sorted(sessions, SessionSortColumn.Time, false)),
                "and the order does not drift as the in-flight sessions age");
            return Task.CompletedTask;
        });

        await runner.RunAsync("session sort survives rows changing between sorts", () =>
        {
            // Proxy threads fill sessions in while they are already listed. Sorting from a snapshot
            // of the keys means a row changing mid-refresh can never trip the framework sort.
            var sessions = Enumerable.Range(0, 500)
                .Select(i => i % 3 == 0 ? Pending($"https://h{i % 7}.example/{i}")
                    : Completed("GET", $"https://h{i % 7}.example/{i}", 200 + i % 5, "text/plain", size: i * 13 % 97, "p", TimeSpan.FromMilliseconds(i % 11)))
                .ToList();

            var threw = false;
            using var stop = new CancellationTokenSource();
            var mutator = Task.Run(() =>
            {
                var i = 0;
                while (!stop.IsCancellationRequested)
                {
                    var session = sessions[i++ % sessions.Count];
                    session.Response = HttpResponseData.Canned(200 + i % 300, new byte[i % 50], "text/plain");
                    session.Completed = i % 2 == 0 ? DateTimeOffset.Now : null;
                }
            });

            try
            {
                foreach (var column in Enum.GetValues<SessionSortColumn>())
                    for (var round = 0; round < 20; round++)
                        SessionSort.Sort(sessions, column, descending: round % 2 == 1);
            }
            catch (InvalidOperationException)
            {
                threw = true;
            }
            finally
            {
                stop.Cancel();
                mutator.Wait();
            }

            runner.IsTrue(!threw, "no sort threw while the rows were being written to");
            runner.AreEqual(500, sessions.Distinct().Count(), "and no row was lost or duplicated");
            return Task.CompletedTask;
        });

        await runner.RunAsync("the grid's short content type names", () =>
        {
            runner.AreEqual("json", MimeTypes.ShortName("application/json; charset=utf-8"), "application/json");
            runner.AreEqual("json", MimeTypes.ShortName("application/vnd.api+json"), "an application +suffix collapses");
            runner.AreEqual("text/html", MimeTypes.ShortName("text/html; charset=utf-8"), "other types keep their prefix");
            runner.AreEqual("image/svg+xml", MimeTypes.ShortName("image/svg+xml"), "an SVG stays an SVG, not image/xml");
            runner.AreEqual(string.Empty, MimeTypes.ShortName(null), "no content type");
            runner.AreEqual("weird", MimeTypes.ShortName(" weird ;x=1"), "a value with no slash is trimmed");
            return Task.CompletedTask;
        });
    }

    private static Session[] Sorted(IEnumerable<Session> sessions, SessionSortColumn column, bool descending)
    {
        var list = sessions.ToList();
        SessionSort.Sort(list, column, descending);
        return [.. list];
    }

    private static string Ids(IEnumerable<Session> sessions) => string.Join(',', sessions.Select(session => session.Id));

    private static HttpRequestData Request(string method, string url)
    {
        var uri = new Uri(url);
        return new HttpRequestData { Method = method, Url = uri, RequestTarget = uri.PathAndQuery };
    }

    private static Session Completed(string method, string url, int status, string contentType, int size,
        string process, TimeSpan duration)
    {
        var session = new Session
        {
            Request = Request(method, url),
            Response = HttpResponseData.Canned(status, Encoding.ASCII.GetBytes(new string('x', size)), contentType),
            State = SessionState.Complete,
            ProcessName = process,
        };
        // Started is fixed at construction, so a duration is expressed through Completed.
        session.Completed = session.Started + duration;
        return session;
    }

    private static Session Pending(string url) => new()
    {
        Request = Request("GET", url),
        State = SessionState.AwaitingResponse,
    };
}
