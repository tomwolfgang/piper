using Piper.Core.Http;
using Piper.Core.Sessions;

/// <summary>
/// Flattening rules for the Composer history tree. Lives here rather than beside the control
/// because the smoke tests only reference Piper.Core.
/// </summary>
internal static class ComposerHistoryViewTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("composer history groups by host", () =>
    {
        var history = new List<Session>
        {
            Sent("https://api.example.test/user", "GET", 1),
            Sent("https://api.example.test/user", "GET", 3),
            Sent("https://api.example.test/orders", "POST", 2),
            Sent("https://other.example.test/ping", "GET", 9),
            Sent("http://api.example.test:8080/user", "GET", 5),
        };

        var rows = ComposerHistoryView.Build(history, SearchQuery.Empty, null, null);

        // other.example.test was used most recently, so its group sorts first.
        runner.AreEqual(ComposerRowKind.Host, rows[0].Kind, "first row is a host");
        runner.AreEqual("other.example.test", rows[0].Host, "most recently used host leads");
        runner.AreEqual(1, rows[0].Count, "host count is its total sends");

        // A non-default port is a different server to a debugging proxy, so it groups separately.
        runner.AreEqual("api.example.test:8080", rows[2].Host, "a non-default port is its own group");
        runner.AreEqual("api.example.test", rows[4].Host, "the default-port host keeps its bare name");
        runner.AreEqual(3, rows[4].Count, "the bare host counts all three of its sends");

        // Repeated GET /user folds into one row carrying the newest send.
        var user = rows.First(row => row.Kind == ComposerRowKind.Request
            && row.Host == "api.example.test" && row.Session.Path == "/user");
        runner.AreEqual(2, user.Count, "repeat sends fold into one counted row");
        runner.AreEqual<DateTimeOffset?>(At(3), user.Session.Completed, "the newest send survives the fold");
        runner.IsTrue(!user.Expanded, "a repeat row starts folded");

        // Every send is still reachable, which is what removal acts on.
        runner.AreEqual(2, user.Sends.Count, "the folded row carries both sends");
        runner.AreEqual<DateTimeOffset?>(At(1), user.Sends[1].Completed, "older sends follow the newest");

        // A collapsed host emits its header and nothing else.
        var collapsed = ComposerHistoryView.Build(
            history, SearchQuery.Empty, new HashSet<string> { "api.example.test" }, null);
        runner.AreEqual(5, collapsed.Count, "collapsing a host hides its requests");
        runner.IsTrue(!collapsed.Single(row => row.Host == "api.example.test").Expanded,
                "the collapsed host reports itself folded");

        // History starts folded; explicitly opened hosts are the only exception kept in view
        // state. This keeps a large restored history scannable when the Composer first opens.
        var initiallyCollapsed = ComposerHistoryView.InitiallyCollapsedHosts(history);
        var initiallyFolded = ComposerHistoryView.Build(history, SearchQuery.Empty, initiallyCollapsed, null);
        runner.AreEqual(3, initiallyFolded.Count(row => row.Kind == ComposerRowKind.Host),
            "the initial folded state keeps every host header");
        runner.IsTrue(initiallyFolded.Where(row => row.Kind == ComposerRowKind.Host).All(row => !row.Expanded),
            "every host starts folded");

        var reopenedHost = new HashSet<string> { "other.example.test" };
        var foldedExceptOpened = ComposerHistoryView.Build(history, SearchQuery.Empty,
            ComposerHistoryView.InitiallyCollapsedHosts(history, reopenedHost), null);
        runner.IsTrue(foldedExceptOpened.Single(row => row.Kind == ComposerRowKind.Host && row.Host == "other.example.test").Expanded,
            "an explicitly opened host is restored expanded");

        // Expanding a repeat row lists the individual sends beneath it.
        var expanded = ComposerHistoryView.Build(history, SearchQuery.Empty, null,
            new HashSet<string> { ComposerHistoryView.ExpandKey("api.example.test", "GET /user") });
        var sends = expanded.Where(row => row.Kind == ComposerRowKind.Send).ToList();
        runner.AreEqual(2, sends.Count, "an expanded repeat row lists each send");
        runner.AreEqual<DateTimeOffset?>(At(3), sends[0].Session.Completed, "sends are listed newest first");

        // A live search has to reach inside a folded group or it looks like it found nothing.
        var searched = ComposerHistoryView.Build(history, SearchQuery.Parse("orders"),
            new HashSet<string> { "api.example.test" }, null);
        runner.AreEqual(1, searched.Count(row => row.Kind == ComposerRowKind.Request),
            "search expands collapsed hosts");

        return Task.CompletedTask;
    });

    public static Task RunHostileHostAsync(TestRunner runner) =>
        runner.RunAsync("composer history bounds hostile hosts", () =>
        {
            // No URL and no Host header: everything of the kind shares one group rather than
            // producing an unnamed one per request.
            var noHost = new Session { Request = new HttpRequestData { Method = "GET", RequestTarget = "/a" } };
            var alsoNoHost = new Session { Request = new HttpRequestData { Method = "GET", RequestTarget = "/b" } };

            // A Host header arrives off the wire or out of an imported archive, so it can be any
            // length and carry anything at all.
            var huge = new Session { Request = Relative("/c", new string('h', 10_000)) };
            var control = new Session { Request = Relative("/d", "evil\r\nX-Injected: 1") };

            var rows = ComposerHistoryView.Build([noHost, alsoNoHost, huge, control], SearchQuery.Empty, null, null);
            var hosts = rows.Where(row => row.Kind == ComposerRowKind.Host).ToList();

            runner.AreEqual(3, hosts.Count, "one bucket for hostless requests plus one per hostile host");
            runner.AreEqual(2, hosts.Single(row => row.Host == ComposerHistoryView.NoHost).Count,
                "hostless requests share a single group");
            runner.IsTrue(hosts.All(row => ComposerHistoryView.IsDisplayableHost(row.Host)),
                "every emitted host is bounded and free of control characters");

            // Relative targets keep their own rows: Session.Path is empty for all of them, so
            // grouping on it alone would fold unrelated requests together.
            runner.AreEqual(2, rows.Count(row => row.Kind == ComposerRowKind.Request && row.Host == ComposerHistoryView.NoHost),
                "relative targets are not folded into each other");

            // Every row the pane paints dereferences ComposerRow.Session on every repaint, so a
            // session Build should have dropped would fault the paint routine rather than simply
            // look wrong -- and in a virtual owner-drawn list that repeats on every repaint. A
            // request-only archive import is an attacker-influenced source of these entries.
            var requestless = new Session();
            var tunnel = new Session { IsTunnel = true, Request = Relative("/t", "tunnel.test") };
            var kept = Sent("https://kept.example.test/ok", "GET", 1);

            var filtered = ComposerHistoryView.Build([requestless, tunnel, kept], SearchQuery.Empty, null, null);
            runner.AreEqual(1, filtered.Count(row => row.Kind == ComposerRowKind.Host),
                "a request-less session and a tunnel are both dropped");
            runner.AreEqual("kept.example.test", filtered[0].Host, "only the real send survives");
            runner.IsTrue(filtered.All(row => row.Sends.Count > 0 && row.Session.Request is not null),
                "every emitted row carries a session the painter can read");

            // What Build refuses to emit, the view-state file must refuse to carry.
            runner.IsTrue(!ComposerHistoryView.IsDisplayableHost(new string('h', 10_000)), "an oversized host is rejected");
            runner.IsTrue(!ComposerHistoryView.IsDisplayableHost("evil\r\nX-Injected: 1"), "a control character is rejected");
            runner.IsTrue(!ComposerHistoryView.IsDisplayableHost(null), "a missing host is rejected");

            // The actual boundary, not just a value far past it.
            runner.IsTrue(ComposerHistoryView.IsDisplayableHost(new string('h', ComposerHistoryView.MaxHostLength)),
                "a host exactly at the limit is accepted");
            runner.IsTrue(!ComposerHistoryView.IsDisplayableHost(new string('h', ComposerHistoryView.MaxHostLength + 1)),
                "one character past the limit is rejected");

            // A bidi override makes one hostname render as another, in a pane whose whole job is
            // saying which origin was contacted.
            runner.IsTrue(!ComposerHistoryView.IsDisplayableHost("spoof‮tset.dab"),
                "a bidi override is rejected");

            // An astral character straddling the cut would leave a lone surrogate. That survives
            // neither a JSON round-trip nor a later comparison against what Build produces, so the
            // group would silently re-expand on every restart. Surrogates never reach the name.
            var straddling = new string('h', ComposerHistoryView.MaxHostLength - 1) + "\U0001F4A9";
            var emitted = ComposerHistoryView.Build(
                [new Session { Request = Relative("/e", straddling) }], SearchQuery.Empty, null, null)[0].Host;
            runner.IsTrue(ComposerHistoryView.IsDisplayableHost(emitted), "a straddling astral host is still displayable");
            runner.IsTrue(!emitted.Any(char.IsSurrogate), "no lone surrogate reaches the group name");
            runner.AreEqual(emitted, RoundTrip(emitted), "the emitted host survives the state file unchanged");

            // Targets are bounded for the same reasons hosts are: they are measured on hover and
            // re-concatenated into an expand key on every keystroke.
            var longTarget = new Session
            {
                Request = new HttpRequestData { Method = "GET", RequestTarget = "/" + new string('p', 50_000) },
            };
            runner.AreEqual(ComposerHistoryView.MaxTargetLength, ComposerHistoryView.TargetOf(longTarget).Length,
                "an oversized request target is truncated");

            // The method is the third field of the same request line, so leaving it alone would let
            // a payload simply move there: it is hashed into a dictionary key on every keystroke,
            // and the pane glues it to the sanitised target to make one displayed string.
            var hostileMethod = new Session
            {
                Request = new HttpRequestData { Method = "GET‮" + new string('m', 50_000), RequestTarget = "/x" },
            };
            runner.AreEqual(ComposerHistoryView.MaxMethodLength, ComposerHistoryView.MethodOf(hostileMethod).Length,
                "an oversized method is truncated");
            runner.IsTrue(!ComposerHistoryView.MethodOf(hostileMethod).Contains('‮'),
                "a bidi override in the method cannot reverse the target shown beside it");
            runner.IsTrue(ComposerHistoryView.RequestKey(hostileMethod).Length
                    <= ComposerHistoryView.MaxMethodLength + ComposerHistoryView.MaxTargetLength + 1,
                "the expand key stays bounded whichever field carries the payload");

            return Task.CompletedTask;
        });

    /// <summary>Writes a host through the real state file and reads it back.</summary>
    private static string RoundTrip(string host)
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-composer-view-{Guid.NewGuid():N}.json");
        try
        {
            ComposerViewStateStore.Save(new ComposerViewState { ExpandedHosts = [host] }, path);
            var restored = ComposerViewStateStore.Load(path).ExpandedHosts;
            return restored.Count == 1 ? restored[0] : string.Empty;
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static DateTimeOffset At(int minute) => DateTimeOffset.UnixEpoch.AddMinutes(minute);

    private static Session Sent(string url, string method, int minute) => new()
    {
        IsComposed = true,
        Completed = At(minute),
        Request = new HttpRequestData
        {
            Method = method,
            Url = new Uri(url),
            RequestTarget = new Uri(url).PathAndQuery,
        },
    };

    private static HttpRequestData Relative(string target, string host)
    {
        var request = new HttpRequestData { Method = "GET", RequestTarget = target };
        request.Headers.Add("Host", host);
        return request;
    }
}
