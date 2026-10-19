using Piper.Core.Sessions;

internal static class ComposerViewStateStoreTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("composer view state persists and is bounded", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-composer-view-{Guid.NewGuid():N}.json");
        try
        {
            runner.AreEqual(0, ComposerViewStateStore.Load(path).ExpandedHosts.Count,
                "a missing file means no hosts override the folded default");

            ComposerViewStateStore.Save(new ComposerViewState
            {
                ExpandedHosts = ["api.example.test", "localhost:8080", "api.example.test"],
            }, path);

            var restored = ComposerViewStateStore.Load(path);
            runner.AreEqual(2, restored.ExpandedHosts.Count, "duplicates collapse to one entry");
            runner.IsTrue(restored.ExpandedHosts.Contains("api.example.test"), "a host round-trips");
            runner.IsTrue(restored.ExpandedHosts.Contains("localhost:8080"), "a host:port round-trips");

            // A host ultimately comes off the wire, so neither Save nor Load may carry one the
            // pane itself would never have produced.
            ComposerViewStateStore.Save(new ComposerViewState
            {
                ExpandedHosts = [new string('h', 10_000), "evil\r\nX-Injected: 1", string.Empty, "good.example.test"],
            }, path);
            var cleaned = ComposerViewStateStore.Load(path);
            runner.AreEqual(1, cleaned.ExpandedHosts.Count, "hostile hosts are dropped, the rest is kept");
            runner.AreEqual("good.example.test", cleaned.ExpandedHosts[0], "the valid host survives");

            ComposerViewStateStore.Save(new ComposerViewState
            {
                ExpandedHosts = [.. Enumerable.Range(0, ComposerViewStateStore.MaxHosts + 50).Select(i => $"host{i}.test")],
            }, path);
            runner.AreEqual(ComposerViewStateStore.MaxHosts, ComposerViewStateStore.Load(path).ExpandedHosts.Count,
                "the remembered host list is capped");

            // Load has to sanitise independently of Save: this file is plain JSON in the user's
            // profile, so it can carry whatever a hand edit -- or a build without the Save-side
            // guard -- put there. Written directly, bypassing Save, or this asserts nothing.
            File.WriteAllText(path,
                "{\"ExpandedHosts\":[\"good.example.test\",\"evil\\u000d\\u000aX-Injected: 1\",\"spoof\\u202Etset.dab\",\"\"]}");
            var loaded = ComposerViewStateStore.Load(path);
            runner.AreEqual(1, loaded.ExpandedHosts.Count, "Load drops hosts Save would never have written");
            runner.AreEqual("good.example.test", loaded.ExpandedHosts[0], "the valid host survives Load");

            File.WriteAllText(path, "not json");
            runner.AreEqual(0, ComposerViewStateStore.Load(path).ExpandedHosts.Count,
                "a corrupt file restores the folded default");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });
}
