using System.Drawing;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Piper.App.Theme;

/// <summary>Application palette applied by hand. WinForms' built-in dark mode is still
/// experimental, so recursive theming gives us control over the grid and editor colours we care about.</summary>
public static class Palette
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly ThemeColors Dark = new(
        Color.FromArgb(30, 30, 32), Color.FromArgb(37, 37, 40), Color.FromArgb(58, 32, 34),
        Color.FromArgb(45, 45, 48), Color.FromArgb(62, 62, 66), Color.FromArgb(224, 224, 226),
        Color.FromArgb(150, 150, 156), Color.FromArgb(0, 122, 204), Color.FromArgb(38, 79, 120),
        Color.FromArgb(106, 190, 120), Color.FromArgb(120, 170, 220), Color.FromArgb(230, 180, 100),
        Color.FromArgb(232, 110, 110), Color.FromArgb(140, 140, 148), Color.FromArgb(190, 150, 230));
    private static readonly ThemeColors Light = new(
        Color.FromArgb(250, 250, 250), Color.White, Color.FromArgb(255, 240, 240),
        Color.FromArgb(242, 242, 242), Color.FromArgb(205, 205, 205), Color.FromArgb(35, 35, 35),
        Color.FromArgb(100, 100, 100), Color.FromArgb(0, 102, 204), Color.FromArgb(214, 232, 251),
        Color.FromArgb(35, 130, 65), Color.FromArgb(55, 115, 180), Color.FromArgb(170, 105, 15),
        Color.FromArgb(190, 55, 55), Color.FromArgb(105, 105, 112), Color.FromArgb(125, 80, 180));

    private static ThemeMode _mode = DetectWindowsTheme();

    public static ThemeMode Mode => _mode;
    public static bool IsLightMode => _mode == ThemeMode.Light;
    private static ThemeColors Current => IsLightMode ? Light : Dark;

    public static Color Background => Current.Background;
    public static Color Surface => Current.Surface;
    /// <summary>Tinted editor background for a likely mistake (e.g. an empty POST/PUT body).</summary>
    public static Color SurfaceWarning => Current.SurfaceWarning;
    public static Color SurfaceAlt => Current.SurfaceAlt;
    public static Color Border => Current.Border;
    public static Color Text => Current.Text;
    public static Color TextDim => Current.TextDim;
    public static Color Accent => Current.Accent;
    public static Color Selection => Current.Selection;
    public static Color StatusOk => Current.StatusOk;
    public static Color StatusRedirect => Current.StatusRedirect;
    public static Color StatusClientError => Current.StatusClientError;
    public static Color StatusServerError => Current.StatusServerError;
    public static Color StatusTunnel => Current.StatusTunnel;
    public static Color Composed => Current.Composed;

    public static readonly Font Mono = new("Consolas", 9.5f);
    public static readonly Font UiFont = new("Segoe UI", 9f);

    public static void ToggleMode() => SetMode(IsLightMode ? ThemeMode.Dark : ThemeMode.Light);

    public static void SetMode(ThemeMode mode)
    {
        if (_mode == mode) return;
        _mode = mode;
    }

    private static ThemeMode DetectWindowsTheme()
    {
        try
        {
            // AppsUseLightTheme is the choice under Windows Settings > Personalization > Colors.
            return Registry.GetValue(PersonalizeKey, "AppsUseLightTheme", 0) is int { } value && value != 0
                ? ThemeMode.Light
                : ThemeMode.Dark;
        }
        catch
        {
            return ThemeMode.Dark;
        }
    }

    /// <summary>Colour used for a session row, by outcome.</summary>
    public static Color ForStatus(int statusCode, bool isTunnel, bool failed, bool composed)
    {
        if (failed) return StatusServerError;
        if (isTunnel) return StatusTunnel;
        if (composed) return Composed;
        return statusCode switch
        {
            >= 500 => StatusServerError,
            >= 400 => StatusClientError,
            >= 300 => StatusRedirect,
            >= 200 => StatusOk,
            _ => TextDim,
        };
    }

    /// <summary>Walks a control tree applying the palette. Safe to call again after adding children.</summary>
    public static void Apply(Control control)
    {
        // Only the ListViews and DarkTabControl opted into double buffering individually.
        // Everything else in the tree -- SplitContainer's panels, TabPages, every UserControl --
        // repaints the flicker-prone way, so a burst of captures invalidating a ListView visibly
        // flickers the whole container chain around it, not just that grid. DarkListView's helper
        // works on any Control despite the name (it reaches the same protected property every
        // Control exposes), so apply it everywhere this walk goes rather than one control at a time.
        DarkListView.EnableDoubleBuffering(control);

        switch (control)
        {
            case TextBox textBox:
                textBox.BackColor = Surface;
                textBox.ForeColor = Text;
                textBox.BorderStyle = BorderStyle.FixedSingle;
                break;
            case ListView listView:
                listView.BackColor = Surface;
                listView.ForeColor = Text;
                break;
            case ComboBox combo:
                combo.BackColor = Surface;
                combo.ForeColor = Text;
                combo.FlatStyle = FlatStyle.Flat;
                break;
            case Button button:
                button.BackColor = SurfaceAlt;
                button.ForeColor = Text;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = Border;
                break;
            case CheckBox or RadioButton or Label:
                control.BackColor = Color.Transparent;
                control.ForeColor = Text;
                break;
            case NumericUpDown numeric:
                numeric.BackColor = Surface;
                numeric.ForeColor = Text;
                break;
            case TabControl tabControl:
                // Tab strips are painted by DarkTabControl; only the pages need colouring.
                foreach (TabPage page in tabControl.TabPages)
                {
                    page.BackColor = Background;
                    page.ForeColor = Text;
                }
                break;
            case SplitContainer splitContainer:
                splitContainer.BackColor = Border;
                splitContainer.Panel1.BackColor = Background;
                splitContainer.Panel2.BackColor = Background;
                break;
            case ToolStrip toolStrip:
                toolStrip.BackColor = SurfaceAlt;
                toolStrip.ForeColor = Text;
                toolStrip.Renderer = new PaletteToolStripRenderer();
                foreach (ToolStripItem item in toolStrip.Items)
                {
                    item.BackColor = SurfaceAlt;
                    item.ForeColor = Text;
                }
                break;
            default:
                control.BackColor = Background;
                control.ForeColor = Text;
                break;
        }

        foreach (Control child in control.Controls) Apply(child);
    }

    private sealed class PaletteToolStripRenderer() : ToolStripProfessionalRenderer(new PaletteColors())
    {
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }
    }

    private sealed class PaletteColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Selection;
        public override Color MenuItemSelectedGradientBegin => Selection;
        public override Color MenuItemSelectedGradientEnd => Selection;
        public override Color MenuItemBorder => Accent;
        public override Color MenuBorder => Border;
        public override Color MenuItemPressedGradientBegin => SurfaceAlt;
        public override Color MenuItemPressedGradientEnd => SurfaceAlt;
        public override Color ToolStripDropDownBackground => SurfaceAlt;
        public override Color ImageMarginGradientBegin => SurfaceAlt;
        public override Color ImageMarginGradientMiddle => SurfaceAlt;
        public override Color ImageMarginGradientEnd => SurfaceAlt;
        public override Color ToolStripGradientBegin => SurfaceAlt;
        public override Color ToolStripGradientMiddle => SurfaceAlt;
        public override Color ToolStripGradientEnd => SurfaceAlt;
        public override Color ToolStripBorder => Border;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
        public override Color ButtonSelectedHighlight => Selection;
        public override Color ButtonPressedHighlight => Selection;
        public override Color CheckBackground => Selection;
    }

    private sealed record ThemeColors(
        Color Background, Color Surface, Color SurfaceWarning, Color SurfaceAlt, Color Border,
        Color Text, Color TextDim, Color Accent, Color Selection, Color StatusOk,
        Color StatusRedirect, Color StatusClientError, Color StatusServerError, Color StatusTunnel,
        Color Composed);
}

public enum ThemeMode
{
    Dark,
    Light,
}
