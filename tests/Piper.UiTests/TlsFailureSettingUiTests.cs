using Piper.App;
using Piper.Core.Proxy;

// The Configurations switch for the rows a client leaves by closing right after the TLS handshake (X2 slice 3).
// The dialog is built with stand-ins for the certificate actions, so nothing reaches the certificate store or settings.
internal static class TlsFailureSettingUiTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("== Configurations has a switch for the after-handshake rows, on by default, applied to the options");
        var options = new ProxyOptions();
        using var dialog = new ConfigurationsDialog(options, true, "AllProcesses", true, false,
            () => { }, () => { }, () => { }, () => { }, () => { }, () => false);
        var box = TlsFailureUiTests.All(dialog).OfType<CheckBox>().Single(b => b.Text.StartsWith("Show a row when a client closes right after", StringComparison.Ordinal));
        check(box.Checked, "the switch shows the rows are on");
        box.Checked = false;
        dialog.ApplyTo(options);
        check(!options.RecordClosedAfterHandshake, "turning it off reaches the proxy options");
        box.Checked = true;
        dialog.ApplyTo(options);
        check(options.RecordClosedAfterHandshake, "and turning it on again");
    }
}
