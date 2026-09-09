using Piper.Core.Proxy;
using Piper.Core.Sessions;

internal static class ComposerHistoryStoreTests
{
    /// <summary>A Fiddler request-only archive can append a whole capture's worth of requests at
    /// once, so the persisted history must stay bounded and keep the newest entries.</summary>
    public static Task RunAsync(TestRunner runner) => runner.RunAsync(
        "composer history persists the newest entries and stays bounded", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-composer-history-{Guid.NewGuid():N}.json");
        try
        {
            var count = ComposerHistoryStore.MaxEntries + 500;
            var sessions = new List<Session>(count);
            for (var index = 0; index < count; index++)
            {
                var raw = $"GET https://api.example.test/{index} HTTP/1.1\r\nHost: api.example.test\r\n\r\n";
                runner.IsTrue(RequestExecutor.TryParseRaw(raw, out var request, out _), $"raw request {index} parses");
                sessions.Add(new Session { Request = request, IsComposed = true, Completed = DateTimeOffset.Now });
            }

            ComposerHistoryStore.Save(sessions, path);
            var restored = ComposerHistoryStore.Load(path);

            runner.AreEqual(ComposerHistoryStore.MaxEntries, restored.Count, "history is capped");
            runner.AreEqual($"/{count - 1}", restored[^1].Path, "the newest entry survives");
            runner.AreEqual($"/{count - ComposerHistoryStore.MaxEntries}", restored[0].Path,
                "the oldest entries are the ones dropped");
            runner.IsTrue(restored.All(session => session.IsComposed), "restored entries are composed");

            File.WriteAllText(path, "not json");
            runner.AreEqual(0, ComposerHistoryStore.Load(path).Count, "corrupt history is ignored");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });
}
