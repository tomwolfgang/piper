using Piper.Core.Http;
using Piper.Core.Sessions;

// Regression coverage for the capture list's "Hide this host" menu item, which used to only append
// a term to the grid's transient filter box and so was forgotten on the next launch.
// FilterSettings.HideHost is the pure decision logic behind it, specifically so the persisted
// outcome can be exercised without a running WinForms control.
internal static class HostFilterHideTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("hiding a host is recorded in the persisted Hosts list", () =>
        {
            // An untouched filterset starts in show-only mode with nothing listed, which filters
            // nothing -- so adopting hide mode there inverts no choice the user made.
            var fresh = new FilterSettings();
            runner.IsTrue(fresh.HideHost("api.example.com"), "hiding against an empty list succeeds");
            runner.AreEqual(1, fresh.HostsMode, "an empty list adopts hide mode");
            runner.AreEqual(1, fresh.Hosts.Count, "the host is listed");
            runner.AreEqual("api.example.com", fresh.Hosts[0].Pattern, "the listed pattern is the host");
            runner.IsTrue(fresh.Hosts[0].Enabled, "the new entry is ticked");
            runner.AreEqual("api.example.com", fresh.HostsText, "the legacy text form is kept in step");

            // Hide mode already: append, and never list the same host twice.
            runner.IsTrue(fresh.HideHost("cdn.example.net"), "a second host can be hidden");
            runner.AreEqual(2, fresh.Hosts.Count, "both hosts are listed");
            runner.IsTrue(fresh.HideHost("API.example.com"), "re-hiding an already hidden host succeeds");
            runner.AreEqual(2, fresh.Hosts.Count, "a case-differing duplicate is not listed twice");
            runner.AreEqual("api.example.com; cdn.example.net", fresh.HostsText, "legacy text lists both");

            // A host already covered by a broader enabled pattern is hidden as it stands, so there
            // is nothing to add -- the call is idempotent rather than piling up redundant entries.
            var wildcard = new FilterSettings
            {
                HostsMode = 1,
                Hosts = [new HostFilterEntry { Pattern = "*.example.com", Enabled = true }],
            };
            runner.IsTrue(wildcard.HideHost("api.example.com"), "a covered host reports success");
            runner.AreEqual(1, wildcard.Hosts.Count, "a covered host adds no redundant entry");

            // The trap the old menu item could not reach at all: an entry that is listed but
            // unticked does not hide anything, so hiding the host must tick it back on.
            var unticked = new FilterSettings
            {
                HostsMode = 1,
                Hosts = [new HostFilterEntry { Pattern = "api.example.com", Enabled = false }],
            };
            runner.IsTrue(unticked.HideHost("api.example.com"), "an unticked entry can be hidden again");
            runner.AreEqual(1, unticked.Hosts.Count, "no duplicate entry is added");
            runner.IsTrue(unticked.Hosts[0].Enabled, "the existing entry is ticked instead");

            return Task.CompletedTask;
        });

        await runner.RunAsync("hiding a host never inverts a show-only list", () =>
        {
            // Show-only means the shown set *is* the ticked entries. Hiding a host the user asked
            // to see unticks that entry; switching the mode would hide everything else instead.
            var showOnly = new FilterSettings
            {
                HostsMode = 0,
                Hosts =
                [
                    new HostFilterEntry { Pattern = "*.example.com", Enabled = true },
                    new HostFilterEntry { Pattern = "keep.example.net", Enabled = true },
                ],
            };
            runner.IsTrue(showOnly.HideHost("api.example.com"), "hiding a shown host succeeds");
            runner.AreEqual(0, showOnly.HostsMode, "show-only mode is left alone");
            runner.IsTrue(!showOnly.Hosts[0].Enabled, "the entry that showed the host is unticked");
            runner.IsTrue(showOnly.Hosts[1].Enabled, "the other shown host is untouched");

            // A host that no ticked entry shows cannot be expressed as "hide just this one" without
            // inverting the list, so nothing is changed and the caller is told.
            var elsewhere = new FilterSettings
            {
                HostsMode = 0,
                Hosts = [new HostFilterEntry { Pattern = "keep.example.net", Enabled = true }],
            };
            runner.IsTrue(!elsewhere.HideHost("api.example.com"), "an unshown host cannot be hidden");
            runner.AreEqual(0, elsewhere.HostsMode, "the mode is not switched");
            runner.AreEqual(1, elsewhere.Hosts.Count, "no entry is added");
            runner.IsTrue(elsewhere.Hosts[0].Enabled, "the existing entry is not unticked");

            // The trap in the other direction: unticking the only ticked entry would leave a
            // show-only list that shows *everything*, the host just hidden included. Hide mode is
            // the one reading of the click that still hides it.
            var soleEntry = new FilterSettings
            {
                HostsMode = 0,
                Hosts = [new HostFilterEntry { Pattern = "api.example.com", Enabled = true }],
            };
            runner.IsTrue(soleEntry.HideHost("api.example.com"), "hiding the only shown host succeeds");
            runner.AreEqual(1, soleEntry.HostsMode, "the list cannot stay show-only and still hide it");
            runner.AreEqual(1, soleEntry.Hosts.Count, "the entry is reused, not duplicated");
            runner.IsTrue(soleEntry.Hosts[0].Enabled, "and is ticked so the hide actually applies");
            runner.AreEqual("-host:api.example.com",
                HostFilterTerm.Compose(soleEntry.HostsText, hide: soleEntry.HostsMode == 1),
                "the result composes to a hide term, not an empty one");

            // Same trap with a wildcard: the parked pattern is not the host, so the host is listed
            // in its own right rather than the wildcard being re-ticked as a hide-everything rule.
            var soleWildcard = new FilterSettings
            {
                HostsMode = 0,
                Hosts = [new HostFilterEntry { Pattern = "*.example.com", Enabled = true }],
            };
            runner.IsTrue(soleWildcard.HideHost("api.example.com"), "hiding the only shown wildcard succeeds");
            runner.AreEqual(1, soleWildcard.HostsMode, "hide mode is adopted");
            runner.AreEqual(2, soleWildcard.Hosts.Count, "the host joins the list");
            runner.IsTrue(!soleWildcard.Hosts[0].Enabled, "the wildcard stays unticked, hiding nothing extra");
            runner.IsTrue(soleWildcard.Hosts[1].Enabled, "only the requested host is hidden");

            // Show-only with nothing ticked composes to an empty term and so filters nothing.
            // Adopting hide mode there inverts no live intent; the parked patterns stay parked.
            var parked = new FilterSettings
            {
                HostsMode = 0,
                Hosts = [new HostFilterEntry { Pattern = "parked.example.net", Enabled = false }],
            };
            runner.IsTrue(parked.HideHost("api.example.com"), "an inert show-only list can be hidden into");
            runner.AreEqual(1, parked.HostsMode, "an inert show-only list adopts hide mode");
            runner.AreEqual(2, parked.Hosts.Count, "the host joins the parked pattern");
            runner.IsTrue(!parked.Hosts[0].Enabled, "the parked pattern stays unticked");
            runner.IsTrue(parked.Hosts[1].Enabled, "the newly hidden host is ticked");

            return Task.CompletedTask;
        });

        await runner.RunAsync("hiding a host never runs the filterset by itself", () =>
        {
            // Piper follows Fiddler Classic here: the choice is recorded, but turning the filterset
            // on stays an explicit user action, so a right-click can never silently activate the
            // rest of a staged filterset.
            var staged = new FilterSettings { HideSuccess = true };
            runner.IsTrue(staged.HideHost("api.example.com"), "hiding succeeds while filters are off");
            runner.IsTrue(!staged.UseFilters, "\"Use Filters\" is not turned on");
            runner.IsTrue(staged.HideSuccess, "other staged criteria are untouched");

            var live = new FilterSettings { UseFilters = true, HostsMode = 1 };
            runner.IsTrue(live.HideHost("api.example.com"), "hiding succeeds while filters are on");
            runner.IsTrue(live.UseFilters, "\"Use Filters\" is not turned off either");

            return Task.CompletedTask;
        });

        await runner.RunAsync("hiding a host rejects unusable input", () =>
        {
            var settings = new FilterSettings();
            runner.IsTrue(!settings.HideHost(null), "a null host is refused");
            runner.IsTrue(!settings.HideHost(string.Empty), "an empty host is refused");
            runner.IsTrue(!settings.HideHost("   "), "a whitespace-only host is refused");
            runner.AreEqual(0, settings.Hosts.Count, "no entry is created for unusable input");
            runner.AreEqual(0, settings.HostsMode, "the mode is not switched for unusable input");

            runner.IsTrue(settings.HideHost("  api.example.com  "), "a padded host is accepted");
            runner.AreEqual("api.example.com", settings.Hosts[0].Pattern, "the pattern is trimmed");

            // A hand-edited settings file can carry a null list, null entries, blank patterns and a
            // mode outside the two the Filters tab offers. None of that may throw or be trusted.
            var malformed = new FilterSettings
            {
                HostsMode = 7,
                Hosts = null!,
            };
            runner.IsTrue(malformed.HideHost("api.example.com"), "a null Hosts list is tolerated");
            runner.AreEqual(1, malformed.HostsMode, "an out-of-range mode is coerced, then hides");
            runner.AreEqual(1, malformed.Hosts.Count, "the host is listed");

            var junk = new FilterSettings
            {
                HostsMode = 1,
                Hosts = [null!, new HostFilterEntry { Pattern = "   ", Enabled = true }],
            };
            runner.IsTrue(junk.HideHost("api.example.com"), "null and blank entries are tolerated");
            runner.AreEqual(1, junk.Hosts.Count, "unusable entries are dropped, not composed");
            runner.AreEqual("api.example.com", junk.Hosts[0].Pattern, "only the real host survives");

            // A Host header reaches Session.Host verbatim when the request line has no parseable
            // URL, so the host handed to HideHost is attacker-controlled. None of the SearchQuery
            // grammar may survive into a persisted pattern: '|' would add alternatives (hiding a
            // host the user never chose), whitespace would end the value and inject a whole extra
            // term, a leading '/' or '"' would switch it into regex or quoted mode, and ';', ','
            // and newlines would split one pattern into several.
            foreach (var hostile in new[]
                     {
                         "evil.test|api.example.com",
                         "evil.test status:200",
                         "/^.*$/",
                         "\"evil.test\"",
                         "evil.test;api.example.com",
                         "evil.test,api.example.com",
                         "evil.test\r\napi.example.com",
                         "evil.test\napi.example.com",
                         new string('a', 254),
                     })
            {
                var target = new FilterSettings { HostsMode = 1 };
                runner.IsTrue(!target.HideHost(hostile), $"refuses a hostile host ({Describe(hostile)})");
                runner.AreEqual(0, target.Hosts.Count, $"stores nothing for {Describe(hostile)}");
            }

            // A host made only of hostname characters is allowed through even when it looks like
            // grammar, because Compose always puts it in value position: "-host:-host:x" reads as
            // one negated host term whose value is the literal "-host:x", which matches no real
            // host. Allowing it keeps ':' available for IPv6 literals.
            var inert = new FilterSettings { HostsMode = 1 };
            runner.IsTrue(inert.HideHost("-host:api.example.com"), "a grammar-shaped but inert host is allowed");
            runner.IsTrue(SearchQuery.Parse(HostFilterTerm.Compose(inert.HostsText, hide: true))
                .Matches(SessionFor("api.example.com")),
                "and leaves a host the user did not pick visible");

            // Real hosts must still be accepted, including an IPv6 literal and a punycode IDN.
            foreach (var legitimate in new[] { "localhost", "192.168.0.1", "[::1]", "xn--bcher-kva.example", new string('a', 253) })
            {
                var target = new FilterSettings { HostsMode = 1 };
                runner.IsTrue(target.HideHost(legitimate), $"accepts {Describe(legitimate)}");
            }

            // A lone "*" strips to nothing, so it filters nothing and must not be treated as a
            // pattern that already covers every host.
            var wildcardOnly = new FilterSettings
            {
                HostsMode = 1,
                Hosts = [new HostFilterEntry { Pattern = "*", Enabled = true }],
            };
            runner.IsTrue(wildcardOnly.HideHost("api.example.com"), "a lone '*' does not cover the host");
            runner.AreEqual(2, wildcardOnly.Hosts.Count, "the host is listed in its own right");

            return Task.CompletedTask;
        });

        await runner.RunAsync("a hidden host survives a settings round trip and filters the grid", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), $"piper-hide-host-{Guid.NewGuid():N}.json");
            try
            {
                // The whole point of the fix: the choice reaches disk and comes back.
                var settings = new FilterSettings { UseFilters = true };
                runner.IsTrue(settings.HideHost("api.example.com"), "the host is hidden");
                FilterSettingsStore.Save(settings, path);

                var restored = FilterSettingsStore.Load(path);
                runner.IsTrue(restored is not null, "the settings can be loaded again");
                runner.AreEqual(1, restored!.HostsMode, "hide mode survives the round trip");
                runner.AreEqual(1, restored.Hosts.Count, "the hidden host survives the round trip");
                runner.AreEqual("api.example.com", restored.Hosts[0].Pattern, "with its pattern intact");
                runner.IsTrue(restored.Hosts[0].Enabled, "and still ticked");

                // Compose the restored list the same way the Filters tab does, and check it against
                // real sessions: the hidden host is filtered out and nothing else is.
                var term = HostFilterTerm.Compose(
                    string.Join(';', restored.Hosts.Where(host => host.Enabled).Select(host => host.Pattern)),
                    hide: restored.HostsMode == 1);
                runner.AreEqual("-host:api.example.com", term, "the restored list composes a hide term");

                var query = SearchQuery.Parse(term);
                runner.IsTrue(!query.Matches(SessionFor("api.example.com")), "the hidden host is filtered out");
                runner.IsTrue(query.Matches(SessionFor("other.example.com")), "other hosts still show");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>Keeps a hostile host readable, and on one line, in the assertion labels.</summary>
    private static string Describe(string host)
    {
        var visible = host.Replace("\r", "\\r").Replace("\n", "\\n");
        return visible.Length <= 40 ? $"\"{visible}\"" : $"\"{visible[..40]}...\" ({visible.Length} chars)";
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
