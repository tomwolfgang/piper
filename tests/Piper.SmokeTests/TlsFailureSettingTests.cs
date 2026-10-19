using Piper.App;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

// The switch for the rows a client leaves by closing right after the TLS handshake (X2 slice 3), and the
// README section that explains every row, which has to use the words the grid does.
internal static class TlsFailureSettingTests
{
    public static Task RunTlsFailureSettingAsync(TestRunner runner) => runner.RunAsync("settings: the after-handshake rows switch persists, applies and defaults on", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-tls-settings-{Guid.NewGuid():N}.json");
        try
        {
            runner.IsTrue(new ProxyConfigurationSettings().RecordClosedAfterHandshake && new ProxyOptions().RecordClosedAfterHandshake, "on by default");

            ProxyConfigurationSettingsStore.Save(new ProxyConfigurationSettings { RecordClosedAfterHandshake = false }, path);
            var restored = ProxyConfigurationSettingsStore.Load(path);
            runner.IsTrue(restored is { RecordClosedAfterHandshake: false }, "off is saved and loaded");
            var options = new ProxyOptions();
            restored!.ApplyTo(options);
            runner.IsTrue(!options.RecordClosedAfterHandshake, "and applied to the proxy options");
            runner.IsTrue(!ProxyConfigurationSettings.From(options).RecordClosedAfterHandshake, "and read back from them");

            File.WriteAllText(path, """{"DecryptHttps":false}""");
            runner.IsTrue(ProxyConfigurationSettingsStore.Load(path) is { RecordClosedAfterHandshake: true, DecryptHttps: false },
                "a settings file from before the switch existed keeps it on");
        }
        finally
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    });

    public static Task RunTlsFailureReadmeAsync(TestRunner runner) => runner.RunAsync("README: Why is nothing captured? explains every TLS ERR reason in the grid's own words", () =>
    {
        var readme = File.ReadAllText(Path.Combine(TlsFailureGrammarTests.RepositoryRoot(), "README.md"));
        var start = readme.IndexOf("\n## Why is nothing captured?", StringComparison.Ordinal);
        var end = readme.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        runner.IsTrue(start >= 0 && end > start, "the README has the section the grammar table links to");
        var section = readme[start..end];

        foreach (var kind in Enum.GetValues<TlsFailureKind>())
        {
            // The label up to its parenthesis: the README does not repeat what Piper offers.
            var label = Strings.TlsFailure.Reason(kind, null).Split(" (")[0];
            runner.IsTrue(section.Contains($"| {label}", StringComparison.Ordinal), $"the table explains {kind}: \"{label}\"");
        }
        runner.IsTrue(section.Contains("more TLS handshake failures are not shown individually", StringComparison.Ordinal), "it explains the overflow row");
        runner.IsTrue(section.Contains("`TLS ERR`", StringComparison.Ordinal) && section.Contains("is:tlsfail", StringComparison.Ordinal), "it names the Result text and the filter");
        runner.IsTrue(!section.Contains("is certificate pinning", StringComparison.OrdinalIgnoreCase) && !section.Contains("are pinning", StringComparison.OrdinalIgnoreCase),
            "and it never states pinning as a cause");
        return Task.CompletedTask;
    });
}
