using Piper.App;
using Piper.App.Controls;
using Piper.App.Theme;

// The warning bar shown while HTTPS is switched on but not decrypted: real WinForms, no certificate store.
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
