using Piper.Core.Http;
using Piper.Core.Sessions;

// Regression coverage for the Filters tab's Hosts list. HostFilterTerm is the pure, UI-free
// extraction of the query-composition logic FilterPanel uses, specifically so this path can be
// exercised without a running WinForms control.
internal static class HostFilterTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("HostFilterTerm composes a domain: term", () =>
        {
            runner.AreEqual(string.Empty, HostFilterTerm.Compose(null, hide: false), "null input");
            runner.AreEqual(string.Empty, HostFilterTerm.Compose("   ", hide: false), "whitespace-only input");

            runner.AreEqual("domain:api.curseforge.com",
                HostFilterTerm.Compose("api.curseforge.com", hide: false), "bare domain, show mode");

            runner.AreEqual("domain:curseforge.com",
                HostFilterTerm.Compose("*.curseforge.com", hide: false), "*.domain wildcard is stripped");

            runner.AreEqual("domain:curseforge.com",
                HostFilterTerm.Compose("*curseforge.com", hide: false),
                "a wildcard without the dot is still stripped, not left as a literal '*'");

            runner.AreEqual(string.Empty, HostFilterTerm.Compose("*", hide: false),
                "a lone '*' strips down to nothing usable, rather than matching literally");

            runner.AreEqual("-domain:curseforge.com",
                HostFilterTerm.Compose("*.curseforge.com", hide: true), "hide mode negates the term");

            runner.AreEqual("domain:localhost|curseforge.com|example.net",
                HostFilterTerm.Compose("localhost; *.curseforge.com; *.example.net", hide: false),
                "semicolon-separated patterns are OR'd together");

            runner.AreEqual("domain:a.com|b.com",
                HostFilterTerm.Compose("a.com,\r\nb.com", hide: false),
                "commas and newlines are both accepted delimiters");

            runner.AreEqual("domain:a.com|b.com",
                HostFilterTerm.Compose("  a.com  ;  b.com  ", hide: false),
                "surrounding whitespace on each pattern is trimmed");

            runner.AreEqual(2, HostFilterTerm.Split("  a.com;\r\nb.com ").Count,
                "host-list input splits into entries");

            // A space used to stay inside one pattern, so "a.com b.com" composed "host:a.com b.com":
            // the value ended at the space and "b.com" became a bare term of its own.
            runner.AreEqual("domain:a.com|b.com", HostFilterTerm.Compose("a.com b.com", hide: false),
                "whitespace separates patterns too");
            runner.AreEqual("-domain:a.com|b.com|c.com", HostFilterTerm.Compose("a.com\tb.com  c.com", hide: true),
                "tabs and runs of spaces as well");

            // The query tokenizer ends a value on any char.IsWhiteSpace, so the splitter has to as
            // well: a no-break or ideographic space pasted into the list injected a term just the same.
            foreach (var space in new[] { '\u00A0', '\u3000', '\u2003', '\v', '\f', '\u0085', '\u2028' })
            {
                runner.AreEqual("domain:a.com|b.com", HostFilterTerm.Compose($"a.com{space}b.com", hide: false),
                    $"U+{(int)space:X4} separates patterns");
            }

            // Anything that is not hostname material is left out rather than composed, however it got
            // into the list: a quote or slash would switch the value into quoted or regex mode and
            // swallow the terms after it, and '|' would add alternatives nobody asked for.
            runner.AreEqual("domain:ok.com", HostFilterTerm.Compose("\"evil.com; /x; a|b; ok.com", hide: false),
                "grammar characters drop the pattern that carries them");
            runner.AreEqual(string.Empty, HostFilterTerm.Compose("\"unterminated", hide: true),
                "and a list of nothing else composes to nothing");
            var composed = HostFilterTerm.Compose("a.com\u00A0status:200", hide: false);
            runner.IsTrue(SearchQuery.Parse(composed).Fields.SequenceEqual(["domain"]),
                "no pasted text can add a term of its own to the admission query");

            return Task.CompletedTask;
        });

        await runner.RunAsync("host filters match whole domains, never lookalikes", () =>
        {
            bool Matches(string term, string host) => SearchQuery.Parse(term).Matches(SessionFor(host));

            // Hiding a short domain used to hide every host that merely contained it, and while a
            // filterset runs those sessions are discarded at admission rather than hidden.
            var hideX = HostFilterTerm.Compose("x.com", hide: true);
            runner.IsTrue(!Matches(hideX, "x.com"), "hiding x.com hides x.com");
            runner.IsTrue(!Matches(hideX, "api.x.com"), "and its subdomains");
            runner.IsTrue(Matches(hideX, "netflix.com"), "but not netflix.com");
            runner.IsTrue(Matches(hideX, "dropbox.com"), "or dropbox.com");
            runner.IsTrue(Matches(hideX, "x.com.example.net"), "or a host that only starts with it");

            var showExample = HostFilterTerm.Compose("*.example.com", hide: false);
            runner.IsTrue(Matches(showExample, "example.com"), "show-only *.example.com admits example.com");
            runner.IsTrue(Matches(showExample, "API.Example.COM"), "and its subdomains, in any case");
            runner.IsTrue(!Matches(showExample, "evil-example.com"), "but not evil-example.com");
            runner.IsTrue(!Matches(showExample, "example.com.attacker.net"), "or example.com.attacker.net");

            var hideIp = HostFilterTerm.Compose("10.0.0.1", hide: true);
            runner.IsTrue(!Matches(hideIp, "10.0.0.1"), "hiding an IP hides that IP");
            runner.IsTrue(Matches(hideIp, "10.0.0.12"), "but not a longer one");
            runner.IsTrue(Matches(hideIp, "110.0.0.1"), "or one it is a suffix of without a dot");

            // The Hosts list decides "already covered" with the same rule, or it would skip
            // recording a host its own composed query then goes on to show.
            runner.IsTrue(HostFilterTerm.Covers("*.example.com", "api.example.com"), "a wildcard covers a subdomain");
            runner.IsTrue(!HostFilterTerm.Covers("x.com", "netflix.com"), "a short pattern does not cover a lookalike");
            runner.IsTrue(!HostFilterTerm.Covers("*", "api.example.com"), "a lone '*' covers nothing");

            // Saved lists hold fragments from when every pattern was a substring. They keep working,
            // so a show-only list of them does not start discarding all traffic after an upgrade.
            var legacy = HostFilterTerm.Compose("curseforge; api.; 192.168.1", hide: false);
            runner.IsTrue(Matches(legacy, "api.curseforge.com"), "a word fragment still matches");
            runner.IsTrue(Matches(legacy, "api.example.com"), "a trailing-dot fragment still matches");
            runner.IsTrue(Matches(legacy, "192.168.1.20"), "a partial address still matches");
            runner.IsTrue(!Matches(legacy, "example.org"), "and none of them matches an unrelated host");
            runner.IsTrue(HostFilterTerm.Covers("curseforge", "cdn.curseforge.com"), "Covers agrees on fragments");

            // host: stays a substring search for the filter box, which is what people type there.
            runner.IsTrue(Matches("host:curseforge", "api.curseforge.com"), "host: still matches a fragment");
            runner.IsTrue(Matches("host:x.com", "netflix.com"), "including inside a longer domain");

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

            // "api.curseforge.com" -- the bare-domain half of the report -- matches that host and
            // anything beneath it, so the parent domain stays out.
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
