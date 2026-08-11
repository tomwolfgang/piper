using Piper.Core.Sessions;

internal static class FilterSettingsStoreTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("filter settings persist and restore", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-filter-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new FilterSettings
            {
                UseFilters = true,
                HostsMode = 1,
                HostsText = "localhost; *.example.test",
                HideSuccess = true,
                HideNonSuccess = true,
                HideRedirects = true,
                HideAuthDemands = true,
                HideNotModified = true,
            };

            FilterSettingsStore.Save(saved, path);
            var restored = FilterSettingsStore.Load(path);

            runner.IsTrue(restored is not null, "saved settings can be loaded");
            runner.AreEqual(saved.UseFilters, restored!.UseFilters, "use filters");
            runner.AreEqual(saved.HostsMode, restored.HostsMode, "host mode");
            runner.AreEqual(saved.HostsText, restored.HostsText, "host text");
            runner.AreEqual(saved.HideSuccess, restored.HideSuccess, "hide successes");
            runner.AreEqual(saved.HideNonSuccess, restored.HideNonSuccess, "hide non-successes");
            runner.AreEqual(saved.HideRedirects, restored.HideRedirects, "hide redirects");
            runner.AreEqual(saved.HideAuthDemands, restored.HideAuthDemands, "hide auth demands");
            runner.AreEqual(saved.HideNotModified, restored.HideNotModified, "hide not modified");

            File.WriteAllText(path, "not json");
            runner.AreEqual<FilterSettings?>(null, FilterSettingsStore.Load(path), "corrupt settings are ignored");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });
}
