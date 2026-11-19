using System.Drawing;
using Piper.App.Theme;
using Piper.Core.Sessions;

// Theme templates: a picked theme survives a restart the way MainForm restores it, a saved name
// that is not exactly a template is ignored, and Purple keeps its text and status colours readable.
// Uses a temp file, never the user's own theme setting.
internal static class ThemeTemplateUiTests
{
    public static void Run(Action<bool, string> check)
    {
        var startMode = Palette.Mode;
        var path = Path.Combine(Path.GetTempPath(), $"piper-theme-ui-{Guid.NewGuid():N}.json");
        try
        {
            foreach (var bad in new[] { null, "", "purple", "PURPLE", "2", "-1", " Purple", "Purple ", "Blue", "Dark,Light" })
                check(!Palette.TryParseMode(bad, out _), $"'{bad}' is not a theme template");

            foreach (var mode in Enum.GetValues<ThemeMode>())
            {
                Palette.SetMode(mode == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark);
                ThemeSettingsStore.Save(new ThemeSettings { Theme = mode.ToString() }, path);
                // As MainForm's constructor does on start-up.
                check(Palette.TryParseMode(ThemeSettingsStore.Load(path)?.Theme, out var restored) && restored == mode,
                    $"{mode} is restored from the saved setting");
                Palette.SetMode(restored);
                check(Palette.Mode == mode, $"{mode} is the active theme after restore");
            }

            Palette.SetMode(ThemeMode.Purple);
            check(Palette.Background == Color.FromArgb(30, 26, 43), "Purple has its own background");
            check(!Palette.IsLightMode, "Purple is dark-based, so the title bar and scrollbars stay dark");

            foreach (var (name, fore) in new[]
            {
                ("Text", Palette.Text), ("TextDim", Palette.TextDim), ("StatusOk", Palette.StatusOk),
                ("StatusRedirect", Palette.StatusRedirect), ("StatusClientError", Palette.StatusClientError),
                ("StatusServerError", Palette.StatusServerError), ("StatusTunnel", Palette.StatusTunnel),
                ("Composed", Palette.Composed), ("AutoResponded", Palette.AutoResponded),
            })
            {
                foreach (var (backName, back) in new[] { ("Background", Palette.Background), ("Surface", Palette.Surface), ("SurfaceAlt", Palette.SurfaceAlt) })
                {
                    var ratio = Contrast(fore, back);
                    check(ratio >= 4.5, $"Purple {name} on {backName} meets WCAG AA ({ratio:0.00}:1)");
                }
            }
            check(Contrast(Palette.Text, Palette.Selection) >= 4.5, "Purple text on a selected row meets WCAG AA");
        }
        finally
        {
            Palette.SetMode(startMode);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static double Contrast(Color a, Color b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c) => 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);

    private static double Linear(byte channel)
    {
        var s = channel / 255.0;
        return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
    }
}
