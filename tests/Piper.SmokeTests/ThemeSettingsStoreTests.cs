using Piper.Core.Sessions;

internal static class ThemeSettingsStoreTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("theme choice persists and bad files fall back", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-theme-{Guid.NewGuid():N}.json");
        try
        {
            runner.AreEqual<ThemeSettings?>(null, ThemeSettingsStore.Load(path), "no file means follow Windows");

            ThemeSettingsStore.Save(new ThemeSettings { Theme = "Purple" }, path);
            runner.AreEqual("Purple", ThemeSettingsStore.Load(path)?.Theme, "the picked theme round trips");

            File.WriteAllText(path, "not json");
            runner.AreEqual<ThemeSettings?>(null, ThemeSettingsStore.Load(path), "corrupt settings are ignored");

            File.WriteAllText(path, "{}");
            runner.AreEqual<string?>(null, ThemeSettingsStore.Load(path)?.Theme, "a file with no theme follows Windows");

            // Only Piper writes this file, and it writes a few dozen bytes.
            File.WriteAllText(path, $$"""{"Theme":"{{new string('x', 8192)}}"}""");
            runner.AreEqual<ThemeSettings?>(null, ThemeSettingsStore.Load(path), "an oversized file is not read");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });
}
