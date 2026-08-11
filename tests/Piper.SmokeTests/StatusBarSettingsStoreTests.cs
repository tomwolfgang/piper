using Piper.Core.Sessions;

internal static class StatusBarSettingsStoreTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("status bar settings persist and restore", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-status-bar-settings-{Guid.NewGuid():N}.json");
        try
        {
            var saved = new StatusBarSettings
            {
                CaptureEnabled = false,
                CaptureScope = "NonBrowsers",
            };

            StatusBarSettingsStore.Save(saved, path);
            var restored = StatusBarSettingsStore.Load(path);

            runner.IsTrue(restored is not null, "saved settings can be loaded");
            runner.AreEqual(saved.CaptureEnabled, restored!.CaptureEnabled, "capture state");
            runner.AreEqual(saved.CaptureScope, restored.CaptureScope, "capture scope");

            File.WriteAllText(path, "not json");
            runner.AreEqual<StatusBarSettings?>(null, StatusBarSettingsStore.Load(path), "corrupt settings are ignored");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });
}
