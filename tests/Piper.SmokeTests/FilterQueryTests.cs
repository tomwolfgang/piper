using Piper.Core.Http;
using Piper.Core.Sessions;

// Regression coverage for the Filters tab's global "Use Filters" switch. FilterQuery is the pure,
// UI-free extraction of the query composition FilterPanel applies, specifically so this path can be
// exercised without a running WinForms control.
//
// The bug this pins: unchecking "Use Filters" used to leave the previously applied query live on
// SessionStore.CompletedSessionFilter, which drops non-matching completed sessions outright instead
// of hiding them. Turning filters off must therefore compose to an empty query -- and an empty query
// must leave admission wide open -- or captured traffic is lost unrecoverably.
internal static class FilterQueryTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("FilterQuery composes the applied filter query", () =>
        {
            var staged = new FilterSettings
            {
                UseFilters = false,
                Hosts = [new HostFilterEntry { Pattern = "a.com", Enabled = true }],
                HideSuccess = true,
                HideNonSuccess = true,
                HideRedirects = true,
                HideAuthDemands = true,
                HideNotModified = true,
            };

            runner.AreEqual(string.Empty, FilterQuery.Compose(staged),
                "filters off wins over every host and status selection");
            runner.IsTrue(SearchQuery.Parse(FilterQuery.Compose(staged)).IsEmpty,
                "filters off leaves no query, so the Filters tab shows no glyph");

            staged.UseFilters = true;
            runner.IsTrue(!SearchQuery.Parse(FilterQuery.Compose(staged)).IsEmpty,
                "filters on with selections leaves a query, so the Filters tab shows its glyph");

            var single = new FilterSettings
            {
                UseFilters = true,
                Hosts = [new HostFilterEntry { Pattern = "a.com", Enabled = true }],
                HideSuccess = true,
            };
            runner.AreEqual("host:a.com -status:200..299", FilterQuery.Compose(single),
                "a host term and a status term are joined in panel order");

            single.HostsMode = 1;
            runner.AreEqual("-host:a.com -status:200..299", FilterQuery.Compose(single),
                "hide mode negates the host term");

            var partlyEnabled = new FilterSettings
            {
                UseFilters = true,
                Hosts =
                [
                    new HostFilterEntry { Pattern = "a.com", Enabled = true },
                    new HostFilterEntry { Pattern = "b.com", Enabled = false },
                ],
            };
            runner.AreEqual("host:a.com", FilterQuery.Compose(partlyEnabled),
                "unchecked host patterns are staged but excluded from the query");

            var legacy = new FilterSettings
            {
                UseFilters = true,
                HostsText = "a.com; *.b.com",
                Hosts = [],
            };
            runner.AreEqual("host:a.com|b.com", FilterQuery.Compose(legacy),
                "a filterset predating the per-host checkboxes falls back to HostsText");

            var nullHosts = new FilterSettings { UseFilters = true, HostsText = "a.com", Hosts = null! };
            runner.AreEqual("host:a.com", FilterQuery.Compose(nullHosts),
                "a malformed filterset with a null Hosts list falls back rather than throwing");

            var everything = new FilterSettings
            {
                UseFilters = true,
                HideSuccess = true,
                HideNonSuccess = true,
                HideRedirects = true,
                HideAuthDemands = true,
                HideNotModified = true,
            };
            runner.AreEqual(
                "-status:200..299 status:200..299 -status:300..303 -status:307 -status:401 -status:407 -status:304",
                FilterQuery.Compose(everything),
                "every status toggle contributes its term, with no host term to lead");

            return Task.CompletedTask;
        });

        await runner.RunAsync("filters off admits traffic the filterset would drop", () =>
        {
            // Mirrors how MainForm turns the composed query into the store's admission filter.
            static SessionStore StoreFor(FilterSettings settings)
            {
                var query = SearchQuery.Parse(FilterQuery.Compose(settings));
                return new SessionStore { CompletedSessionFilter = query.IsEmpty ? null : query.Matches };
            }

            static void Capture(SessionStore store, int statusCode)
            {
                var session = new Session();
                store.Add(session);
                session.Response = new HttpResponseData { StatusCode = statusCode };
                session.Completed = DateTimeOffset.Now;
                store.NotifyUpdated(session);
            }

            var settings = new FilterSettings { UseFilters = true, HideNonSuccess = true };
            runner.AreEqual("status:200..299", FilterQuery.Compose(settings), "hide non-2xx admits only 2xx");

            var filtered = StoreFor(settings);
            Capture(filtered, 404);
            runner.AreEqual(0, filtered.Count, "with filters on, a non-matching response is dropped");
            Capture(filtered, 200);
            runner.AreEqual(1, filtered.Count, "with filters on, a matching response is still collected");

            // The regression: the same staged criteria with the switch off must not filter at all.
            settings.UseFilters = false;
            var unfiltered = StoreFor(settings);
            runner.IsTrue(unfiltered.CompletedSessionFilter is null,
                "filters off installs no admission filter, so nothing is discarded");
            Capture(unfiltered, 404);
            runner.AreEqual(1, unfiltered.Count,
                "a response the filterset would reject is captured once filters are off");

            return Task.CompletedTask;
        });
    }
}
