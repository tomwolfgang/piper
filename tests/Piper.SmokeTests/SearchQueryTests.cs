using Piper.Core.Sessions;

internal static class SearchQueryTests
{
    // The query examples the UI advertises, restated here. The smoke runner cannot reference
    // Piper.App, so these are copies of the strings in ComposerPanel's and SessionListView's
    // placeholders and MainForm's "Search syntax" dialog. If a field is renamed in SearchQuery
    // without updating those, this test fails and points at the ones the UI still promises.
    private static readonly string[] Advertised =
    [
        // ComposerPanel search box placeholder.
        "method:POST host:api  status:4xx  -is:image",
        // SessionListView filter box placeholder.
        "status:4xx host:api  -is:image  body:\"order id\"",
        // MainForm's worked example under Help > Search syntax.
        "method:POST host:api status:>=400 -is:image body:\"order\"",

        // Every token the Composer used to advertise, individually.
        "method:POST",
        "host:api",
        "status:4xx",
        "body:\"user_id\"",
        "header:Authorization",
        "size:>100kb",
        "dur:>500",
        "is:json",
        "-is:image",
        "/v[0-9]+\\/orders/",
    ];

    public static Task RunAsync(TestRunner runner) => runner.RunAsync("advertised search examples parse", () =>
    {
        foreach (var example in Advertised)
        {
            var query = SearchQuery.Parse(example);

            // Parse does not throw on a bad term: Compile's exception becomes a Warnings entry and
            // the term is dropped. So an empty Warnings list is the only real proof of support.
            runner.AreEqual(string.Empty, string.Join("; ", query.Warnings), $"no warnings for '{example}'");
            runner.IsTrue(!query.IsEmpty, $"'{example}' compiles to at least one predicate");
        }

        // Guard the assertion above: an unsupported field must warn rather than pass silently.
        var bogus = SearchQuery.Parse("bogus:1");
        runner.AreEqual(1, bogus.Warnings.Count, "an unknown field is reported as a warning");
        runner.IsTrue(bogus.IsEmpty, "an unknown field contributes no predicate");

        var bogusIs = SearchQuery.Parse("is:notathing");
        runner.AreEqual(1, bogusIs.Warnings.Count, "an unknown 'is:' value is reported as a warning");

        return Task.CompletedTask;
    });
}
