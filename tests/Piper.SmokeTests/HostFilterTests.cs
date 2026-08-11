using Piper.Core.Http;
using Piper.Core.Sessions;

// Regression coverage for the Filters tab's Hosts box. It was reported broken twice: first
// because typing a pattern before checking "Use Filters" silently applied nothing (fixed in
// FilterPanel.OnCriteriaChanged), and then again -- this is that second bug. HostFilterTerm is
// the pure, UI-free extraction of the query-composition logic FilterPanel.ComposeHostsTerm used
// to do inline, specifically so this path can be exercised without a running WinForms control.
internal static class HostFilterTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("HostFilterTerm composes a host: term", () =>
        {
            runner.AreEqual(string.Empty, HostFilterTerm.Compose(null, hide: false), "null input");
            runner.AreEqual(string.Empty, HostFilterTerm.Compose("   ", hide: false), "whitespace-only input");

            runner.AreEqual("host:api.curseforge.com",
                HostFilterTerm.Compose("api.curseforge.com", hide: false), "bare domain, show mode");

            runner.AreEqual("host:curseforge.com",
                HostFilterTerm.Compose("*.curseforge.com", hide: false), "*.domain wildcard is stripped");

            runner.AreEqual("host:curseforge.com",
                HostFilterTerm.Compose("*curseforge.com", hide: false),
                "a wildcard without the dot is still stripped, not left as a literal '*'");

            runner.AreEqual(string.Empty, HostFilterTerm.Compose("*", hide: false),
                "a lone '*' strips down to nothing usable, rather than matching literally");

            runner.AreEqual("-host:curseforge.com",
                HostFilterTerm.Compose("*.curseforge.com", hide: true), "hide mode negates the term");

            runner.AreEqual("host:localhost|curseforge.com|example.net",
                HostFilterTerm.Compose("localhost; *.curseforge.com; *.example.net", hide: false),
                "semicolon-separated patterns are OR'd together");

            runner.AreEqual("host:a.com|b.com",
                HostFilterTerm.Compose("a.com,\r\nb.com", hide: false),
                "commas and newlines are both accepted delimiters");

            runner.AreEqual("host:a.com|b.com",
                HostFilterTerm.Compose("  a.com  ;  b.com  ", hide: false),
                "surrounding whitespace on each pattern is trimmed");

            return Task.CompletedTask;
        });

        await runner.RunAsync("Filters tab Hosts box actually narrows the session grid", () =>
        {
            var api = SessionFor("api.curseforge.com");
            var bare = SessionFor("curseforge.com");
            var secondaryHost = SessionFor("example.net");
            var other = SessionFor("example.com");
            var all = new[] { api, bare, secondaryHost, other };

            bool Matches(string term, Session session) => SearchQuery.Parse(term).Matches(session);

            // "*.curseforge.com" -- the exact pattern from the report -- matches both the
            // subdomain and the bare domain, but nothing else.
            var wildcardTerm = HostFilterTerm.Compose("*.curseforge.com", hide: false);
            runner.IsTrue(Matches(wildcardTerm, api), "*.curseforge.com matches api.curseforge.com");
            runner.IsTrue(Matches(wildcardTerm, bare), "*.curseforge.com matches curseforge.com itself");
            runner.IsTrue(!Matches(wildcardTerm, secondaryHost), "*.curseforge.com does not match example.net");
            runner.IsTrue(!Matches(wildcardTerm, other), "*.curseforge.com does not match example.com");

            // "api.curseforge.com" -- the bare-domain half of the report -- is a substring match,
            // so it matches only hosts that actually contain that exact string.
            var bareTerm = HostFilterTerm.Compose("api.curseforge.com", hide: false);
            runner.IsTrue(Matches(bareTerm, api), "api.curseforge.com matches api.curseforge.com");
            runner.IsTrue(!Matches(bareTerm, bare), "api.curseforge.com does not match the bare domain");
            runner.IsTrue(!Matches(bareTerm, other), "api.curseforge.com does not match example.com");

            // Hide mode is the complement of show-only for the same pattern set.
            var hideTerm = HostFilterTerm.Compose("*.curseforge.com", hide: true);
            var shown = all.Where(s => Matches(wildcardTerm, s)).ToHashSet();
            var hidden = all.Where(s => Matches(hideTerm, s)).ToHashSet();
            runner.AreEqual(0, shown.Intersect(hidden).Count(), "show-only and hide never agree on the same session");
            runner.AreEqual(all.Length, shown.Count + hidden.Count(), "together they cover every session");

            // Multiple patterns are OR'd, matching either one.
            var multiTerm = HostFilterTerm.Compose("curseforge.com; example.net", hide: false);
            runner.IsTrue(Matches(multiTerm, api), "multi-pattern list matches curseforge.com hosts");
            runner.IsTrue(Matches(multiTerm, secondaryHost), "multi-pattern list matches example.net");
            runner.IsTrue(!Matches(multiTerm, other), "multi-pattern list does not match example.com");

            return Task.CompletedTask;
        });
    }

    private static Session SessionFor(string host) => new()
    {
        Request = new HttpRequestData
        {
            Method = "GET",
            Url = new Uri($"https://{host}/"),
            RequestTarget = "/",
        },
        IsHttps = true,
    };
}
