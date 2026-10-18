using Piper.Core.Proxy;
using Piper.Core.Security;

// C1: the pure trust rules - when HTTPS is decrypted, what the state is called, and what the startup
// prompt may do. Nothing here touches the Windows certificate store.
internal static class TrustPolicyTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await RunStateMatrixAsync(runner);
        await RunStartupDecisionAsync(runner);
        await RunSettingsPersistAsync(runner);
    }

    private static Task RunStateMatrixAsync(TestRunner runner) => runner.RunAsync("HTTPS state matrix: decrypt only when on and (trusted or opted in without trust)", () =>
    {
        foreach (var decrypt in new[] { true, false })
        foreach (var trusted in new[] { true, false })
        foreach (var optIn in new[] { true, false })
        {
            var expected = !decrypt ? HttpsState.Off
                : trusted ? HttpsState.Decrypting
                : optIn ? HttpsState.DecryptingUntrusted : HttpsState.NotTrusted;
            var what = $"decrypt={decrypt} trusted={trusted} optIn={optIn}";
            runner.AreEqual(expected, TrustPolicy.StateOf(decrypt, trusted, optIn), $"state for {what}");
            runner.AreEqual(expected is HttpsState.Decrypting or HttpsState.DecryptingUntrusted, TrustPolicy.ShouldDecrypt(decrypt, trusted, optIn), $"decrypting for {what}");
            runner.AreEqual(!trusted && !optIn, TrustPolicy.IsSuspended(trusted, optIn), $"suspended for {what}");
        }

        return Task.CompletedTask;
    });

    private static Task RunStartupDecisionAsync(TestRunner runner) => runner.RunAsync("startup trust decision never stops capture and never installs by itself", () =>
    {
        foreach (var choice in new[] { TrustPromptChoice.NotNow, TrustPromptChoice.DontAskAgain, TrustPromptChoice.InstallCertificate })
        {
            var decision = TrustPolicy.DecideStartup(true, false, true, false, true, () => choice);
            runner.IsTrue(decision.Prompted && decision.StartCapture, $"{choice}: the prompt was shown and capture still starts");
            runner.AreEqual(choice == TrustPromptChoice.InstallCertificate, decision.InstallRequested, $"{choice}: install is requested only by Install");
            runner.AreEqual(choice == TrustPromptChoice.DontAskAgain, decision.SuppressFuturePrompts, $"{choice}: the prompt is silenced only by Don't ask again");
        }

        runner.IsTrue(!TrustPolicy.DecideStartup(true, false, true, false, false, () => TrustPromptChoice.NotNow).StartCapture,
            "capture stays off when the user turned capture-on-startup off");

        var asked = 0;
        TrustPromptChoice Ask() { asked++; return TrustPromptChoice.NotNow; }
        var quiet = new[]
        {
            TrustPolicy.DecideStartup(true, true, true, false, true, Ask),   // already trusted
            TrustPolicy.DecideStartup(false, false, true, false, true, Ask), // decryption off
            TrustPolicy.DecideStartup(true, false, false, false, true, Ask), // Don't ask again
            TrustPolicy.DecideStartup(true, false, true, true, true, Ask),   // opted in to decrypt without trust
        };
        runner.IsTrue(quiet.All(d => !d.Prompted && d.StartCapture), "no prompt in those cases, and capture starts");
        runner.AreEqual(0, asked, "the prompt callback ran only when a prompt was due");
        return Task.CompletedTask;
    });

    private static Task RunSettingsPersistAsync(TestRunner runner) => runner.RunAsync("Don't ask again and the untrusted opt-in persist, default safely, and change only when saved", () =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-trust-settings-{Guid.NewGuid():N}.json");
        try
        {
            var options = new ProxyOptions();
            runner.IsTrue(options.AskToTrustCertificate && !options.DecryptWhenUntrusted, "the prompt is on and the opt-in is off by default");

            var first = TrustPolicy.DecideStartup(true, false, options.AskToTrustCertificate, false, true, () => TrustPromptChoice.DontAskAgain);
            if (first.SuppressFuturePrompts) options.AskToTrustCertificate = false;
            options.DecryptWhenUntrusted = true;
            runner.IsTrue(ProxyConfigurationSettingsStore.Load(path) is null, "nothing is written until Save");
            ProxyConfigurationSettingsStore.Save(ProxyConfigurationSettings.From(options), path);

            var restarted = new ProxyOptions();
            ProxyConfigurationSettingsStore.Load(path)!.ApplyTo(restarted);
            runner.IsTrue(!restarted.AskToTrustCertificate && restarted.DecryptWhenUntrusted && restarted.DecryptHttps,
                "both choices survive a restart and Decrypt HTTPS is untouched");

            restarted.AskToTrustCertificate = true; // what the Configurations checkbox does
            restarted.DecryptWhenUntrusted = false;
            ProxyConfigurationSettingsStore.Save(ProxyConfigurationSettings.From(restarted), path);
            var again = new ProxyOptions();
            ProxyConfigurationSettingsStore.Load(path)!.ApplyTo(again);
            runner.IsTrue(again.AskToTrustCertificate && !again.DecryptWhenUntrusted, "both are reversible");

            File.WriteAllText(path, "{\"DecryptHttps\":true}");
            var legacy = new ProxyOptions { AskToTrustCertificate = false, DecryptWhenUntrusted = true };
            ProxyConfigurationSettingsStore.Load(path)!.ApplyTo(legacy);
            runner.IsTrue(legacy.AskToTrustCertificate && !legacy.DecryptWhenUntrusted, "a settings file from an older Piper gets the safe defaults");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }

        return Task.CompletedTask;
    });
}
