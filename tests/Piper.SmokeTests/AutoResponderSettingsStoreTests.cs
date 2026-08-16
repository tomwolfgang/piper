using Piper.Core.Proxy;

internal static class AutoResponderSettingsStoreTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("AutoResponder rules persist in order", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-autoresponder-rules-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new AutoResponderSettings
            {
                Enabled = true,
                PassthroughUnmatched = false,
                Rules =
                [
                    new AutoResponderRule { Match = "EXACT:https://a.example.com/", Action = "*404" },
                    new AutoResponderRule { Enabled = false, Match = "/slow", Action = "*delay:2000" },
                    new AutoResponderRule
                    {
                        Match = "REGEX:/v(?<n>\\d+)/",
                        Action = "*inline",
                        Body = """{"stub":true}""",
                        ContentType = "application/json",
                        Comment = "stub the versioned API",
                    },
                ],
            };

            AutoResponderSettingsStore.Save(saved, path);
            var restored = AutoResponderSettingsStore.Load(path);

            runner.IsTrue(restored is not null, "saved rules can be loaded");
            runner.AreEqual(true, restored!.Enabled, "master toggle");
            runner.AreEqual(false, restored.PassthroughUnmatched, "passthrough toggle");
            runner.AreEqual(3, restored.Rules.Count, "rule count");

            // Order is the semantics -- first match wins, so a reordered rule set is a different one.
            runner.AreEqual("EXACT:https://a.example.com/", restored.Rules[0].Match, "first rule kept its place");
            runner.AreEqual("/slow", restored.Rules[1].Match, "second rule kept its place");
            runner.AreEqual(false, restored.Rules[1].Enabled, "a disabled rule stays disabled");

            runner.AreEqual("*inline", restored.Rules[2].Action, "action");
            runner.AreEqual("""{"stub":true}""", restored.Rules[2].Body, "inline body travels with the rule");
            runner.AreEqual("application/json", restored.Rules[2].ContentType, "content type");
            runner.AreEqual("stub the versioned API", restored.Rules[2].Comment, "comment");
            runner.AreEqual(saved.Rules[2].Id, restored.Rules[2].Id, "ids survive, so hit counts survive an edit");

            // A rule hand-written into the file without an id must still get one.
            File.WriteAllText(path, """{"Enabled":true,"Rules":[{"Match":"/x","Action":"*404"}]}""");
            var handEdited = AutoResponderSettingsStore.Load(path);
            runner.IsTrue(!string.IsNullOrWhiteSpace(handEdited!.Rules[0].Id), "a hand-written rule is given an id");

            File.WriteAllText(path, "not json");
            runner.AreEqual<AutoResponderSettings?>(null, AutoResponderSettingsStore.Load(path),
                "corrupt rules are ignored");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });
}
