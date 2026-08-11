using System.Drawing;
using System.Windows.Forms;

namespace Piper.App.Theme;

/// <summary>Dark palette applied by hand. WinForms' built-in dark mode is still experimental,
/// and recursive theming gives us control over the grid and editor colours we care about.</summary>
public static class Palette
{
    public static readonly Color Background = Color.FromArgb(30, 30, 32);
    public static readonly Color Surface = Color.FromArgb(37, 37, 40);
    /// <summary>Same darkness as <see cref="Surface"/>, tinted red -- for editors flagging a
    /// likely mistake (e.g. an empty POST/PUT body) rather than an active error state.</summary>
    public static readonly Color SurfaceWarning = Color.FromArgb(58, 32, 34);
    public static readonly Color SurfaceAlt = Color.FromArgb(45, 45, 48);
    public static readonly Color Border = Color.FromArgb(62, 62, 66);
    public static readonly Color Text = Color.FromArgb(224, 224, 226);
    public static readonly Color TextDim = Color.FromArgb(150, 150, 156);
    public static readonly Color Accent = Color.FromArgb(0, 122, 204);
    public static readonly Color Selection = Color.FromArgb(38, 79, 120);

    public static readonly Color StatusOk = Color.FromArgb(106, 190, 120);
    public static readonly Color StatusRedirect = Color.FromArgb(120, 170, 220);
    public static readonly Color StatusClientError = Color.FromArgb(230, 180, 100);
    public static readonly Color StatusServerError = Color.FromArgb(232, 110, 110);
    public static readonly Color StatusTunnel = Color.FromArgb(140, 140, 148);
    public static readonly Color Composed = Color.FromArgb(190, 150, 230);

    public static readonly Font Mono = new("Consolas", 9.5f);
    public static readonly Font UiFont = new("Segoe UI", 9f);

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
                toolStrip.Renderer = new DarkToolStripRenderer();
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

    private sealed class DarkToolStripRenderer() : ToolStripProfessionalRenderer(new DarkColors())
    {
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }
    }

    private sealed class DarkColors : ProfessionalColorTable
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
}
