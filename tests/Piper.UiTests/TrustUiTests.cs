using Piper.App;
using Piper.App.Controls;
using Piper.App.Theme;
using Piper.Core.Proxy;

// The trust controls that are real WinForms: the warning bar and the Configurations certificate area.
// The dialog is built with stand-ins for the certificate actions, so nothing here reaches the user's
// certificate store or settings.
internal static class TrustUiTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("== the warning bar offers the trust flow and has no way to dismiss itself");
        var trustClicks = 0;
        using (var bar = new TrustBar(() => trustClicks++))
        using (var host = new Form())
        {
            check(!bar.Visible, "the bar starts hidden");
            host.Controls.Add(bar);
            host.Show();
            bar.Visible = true; // a hidden button cannot be clicked
            var button = All(bar).OfType<Button>().Single();
            button.PerformClick();
            check(trustClicks == 1, "its button starts the trust flow");
            check(All(bar).OfType<Button>().Count() == 1, "it has no dismiss or close button");
            check(All(bar).OfType<Label>().Any(label => label.Text.StartsWith("HTTPS is not decrypted", StringComparison.Ordinal)),
                "it says HTTPS is not decrypted");
            Palette.Apply(bar);
            bar.ApplyColours();
            check(bar.BackColor == Palette.SurfaceWarning, "its warning colour is put back after a theme walk");
        }

        Console.WriteLine("== Configurations shows the live certificate state and what its buttons do");
        var trusted = false;
        var removeCalls = 0;
        var options = new ProxyOptions();
        using var dialog = new ConfigurationsDialog(options, true, "AllProcesses", true, false,
            () => trusted = true, () => { trusted = false; removeCalls++; }, () => { }, () => { }, () => { },
            () => trusted);
        dialog.SelectHttpsTab();
        dialog.Show(); // a button on a tab that is not showing cannot be clicked
        var status = All(dialog).OfType<Label>().Single(label => label.Text.StartsWith("Status:", StringComparison.Ordinal));
        check(status.Text.StartsWith("Status: Not trusted", StringComparison.Ordinal), "an untrusted root reads Not trusted");

        Button Named(string text) => All(dialog).OfType<Button>().Single(button => button.Text == text);
        Named("Trust root certificate...").PerformClick();
        check(status.Text.StartsWith("Status: Trusted", StringComparison.Ordinal), "the line reads Trusted right after Trust");
        Named("Remove trusted root").PerformClick();
        check(status.Text.StartsWith("Status: Not trusted", StringComparison.Ordinal) && removeCalls == 1, "and Not trusted again right after Remove");
        check(All(dialog).OfType<Label>().Any(label => label.Text.Contains("Cancel does not undo", StringComparison.Ordinal)),
            "the dialog says Cancel does not undo Trust, Remove or Export");

        Console.WriteLine("== the startup prompt switch is reversible from Configurations");
        var askBox = All(dialog).OfType<CheckBox>().Single(box => box.Text.StartsWith("Ask to trust", StringComparison.Ordinal));
        check(askBox.Checked, "the switch shows the prompt is on");
        askBox.Checked = false;
        dialog.ApplyTo(options);
        check(!options.AskToTrustCertificate, "turning it off is applied to the options");
        askBox.Checked = true;
        dialog.ApplyTo(options);
        check(options.AskToTrustCertificate, "turning it back on is applied too");

        var optIn = All(dialog).OfType<CheckBox>().Single(box => box.Text.StartsWith("Decrypt HTTPS even when Windows", StringComparison.Ordinal));
        check(!optIn.Checked, "decrypting without Windows trust is off by default");
        optIn.Checked = true;
        dialog.ApplyTo(options);
        check(options.DecryptWhenUntrusted, "turning it on is applied to the options only through the dialog");
        optIn.Checked = false;
        dialog.ApplyTo(options);
        check(!options.DecryptWhenUntrusted, "and it can be turned off again");

        using var silenced = new ConfigurationsDialog(new ProxyOptions { AskToTrustCertificate = false }, true, "AllProcesses", true, false,
            () => { }, () => { }, () => { }, () => { }, () => { }, () => false);
        check(!All(silenced).OfType<CheckBox>().Single(box => box.Text.StartsWith("Ask to trust", StringComparison.Ordinal)).Checked,
            "after Don't ask again the dialog shows the switch off, ready to be turned back on");

        Console.WriteLine("== the status line does not claim HTTPS passes through when the saved opt-in decrypts it");
        using var decrypting = new ConfigurationsDialog(new ProxyOptions { DecryptHttps = true, DecryptWhenUntrusted = true }, true, "AllProcesses", true, false,
            () => { }, () => { }, () => { }, () => { }, () => { }, () => false);
        var decryptingStatus = All(decrypting).OfType<Label>().Single(label => label.Text.StartsWith("Status:", StringComparison.Ordinal));
        check(decryptingStatus.Text.Contains("decrypts HTTPS anyway", StringComparison.Ordinal)
            && !decryptingStatus.Text.Contains("passes through", StringComparison.Ordinal),
            "an untrusted root with the saved opt-in on says Piper decrypts and clients must trust it themselves");
        using var decryptOff = new ConfigurationsDialog(new ProxyOptions { DecryptHttps = false, DecryptWhenUntrusted = true }, true, "AllProcesses", true, false,
            () => { }, () => { }, () => { }, () => { }, () => { }, () => false);
        check(All(decryptOff).OfType<Label>().Single(label => label.Text.StartsWith("Status:", StringComparison.Ordinal)).Text.Contains("passes through unread", StringComparison.Ordinal),
            "with Decrypt HTTPS off the opt-in changes nothing, so the line still says it passes through");
    }

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in All(child)) yield return nested;
        }
    }
}
