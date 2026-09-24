using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Piper.Core.Sessions;

namespace Piper.App.Theme;

/// <summary>Application palette applied by hand. WinForms' built-in dark mode is still
/// experimental, so recursive theming gives us control over the grid and editor colours we care about.</summary>
public static class Palette
{
    private const string PersonalizeKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;
    private static readonly ThemeColors Dark = new(
        Color.FromArgb(30, 30, 32), Color.FromArgb(37, 37, 40), Color.FromArgb(58, 32, 34),
        Color.FromArgb(45, 45, 48), Color.FromArgb(62, 62, 66), Color.FromArgb(224, 224, 226),
        Color.FromArgb(150, 150, 156), Color.FromArgb(0, 122, 204), Color.FromArgb(38, 79, 120),
        Color.FromArgb(106, 190, 120), Color.FromArgb(120, 170, 220), Color.FromArgb(230, 180, 100),
        Color.FromArgb(232, 110, 110), Color.FromArgb(140, 140, 148), Color.FromArgb(190, 150, 230),
        Color.FromArgb(105, 205, 200));
    private static readonly ThemeColors Light = new(
        Color.FromArgb(250, 250, 250), Color.White, Color.FromArgb(255, 240, 240),
        Color.FromArgb(242, 242, 242), Color.FromArgb(205, 205, 205), Color.FromArgb(35, 35, 35),
        Color.FromArgb(100, 100, 100), Color.FromArgb(0, 102, 204), Color.FromArgb(214, 232, 251),
        Color.FromArgb(35, 130, 65), Color.FromArgb(55, 115, 180), Color.FromArgb(170, 105, 15),
        Color.FromArgb(190, 55, 55), Color.FromArgb(105, 105, 112), Color.FromArgb(125, 80, 180),
        Color.FromArgb(20, 125, 125));

    /// <summary>
    /// Unscaled heights of the fixed-height rows the walk resizes. Keyed weakly so a closed dialog's
    /// controls are not kept alive by the cache.
    /// </summary>
    private static readonly ConditionalWeakTable<Control, BaseHeight> BaseHeights = [];

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

    /// <summary>Rows an AutoResponder rule answered, so a faked response is obvious at a glance.</summary>
    public static Color AutoResponded => Current.AutoResponded;

    /// <summary>Text drawn on a row the Find Sessions dialog marked. The mark colours are chosen
    /// by the user rather than by the theme, so one near-black keeps every column readable on
    /// them, the status colours included.</summary>
    public static readonly Color MarkedRowText = Color.FromArgb(20, 20, 20);

    // Cached instances rather than a lookup per access: the owner-draw paths read these once per
    // cell per repaint, so this has to be a field read with no allocation behind it.
    private static Font _mono = FontScale.Scaled(FontScale.Mono);
    private static Font _uiFont = FontScale.Scaled(FontScale.Ui);
    private static Font _uiFontBold = FontScale.Scaled(FontScale.UiBold);

    public static Font Mono => _mono;
    public static Font UiFont => _uiFont;
    public static Font UiFontBold => _uiFontBold;

    /// <summary>
    /// Picks up a new <see cref="FontScale.Step"/>. Call before <see cref="Apply"/>, which is what
    /// pushes the new fonts onto the live control tree.
    /// </summary>
    public static void RescaleFonts()
    {
        _mono = FontScale.Scaled(FontScale.Mono);
        _uiFont = FontScale.Scaled(FontScale.Ui);
        _uiFontBold = FontScale.Scaled(FontScale.UiBold);
    }

    public static void ToggleMode() => SetMode(IsLightMode ? ThemeMode.Dark : ThemeMode.Light);

    public static void ApplyWindowChrome(Form form)
    {
        if (!form.IsHandleCreated)
        {
            form.HandleCreated -= ApplyWindowChromeOnHandleCreated;
            form.HandleCreated += ApplyWindowChromeOnHandleCreated;
            return;
        }

        var darkMode = IsLightMode ? 0 : 1;
        if (!SetDwmAttribute(form.Handle, DwmwaUseImmersiveDarkMode, darkMode))
            SetDwmAttribute(form.Handle, DwmwaUseImmersiveDarkModeBefore20H1, darkMode);
        SetDwmAttribute(form.Handle, DwmwaCaptionColor, ColorTranslator.ToWin32(SurfaceAlt));
        SetDwmAttribute(form.Handle, DwmwaTextColor, ColorTranslator.ToWin32(Text));
    }

    private static void ApplyWindowChromeOnHandleCreated(object? sender, EventArgs e)
    {
        if (sender is Form form) ApplyWindowChrome(form);
    }

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
    /// <summary>
    /// The row colour for a captured session.
    /// </summary>
    /// <remarks>
    /// A session an AutoResponder rule answered is coloured for that before anything else: when
    /// rules are running, "did this come from a rule or from the origin" is the thing you are
    /// looking for, and the status itself is still spelled out in the Result column.
    /// </remarks>
    public static Color ForStatus(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return session.IsAutoResponded
            ? AutoResponded
            : ForStatus(session.StatusCode, session.IsTunnel, session.State == SessionState.Failed, session.IsComposed);
    }

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

    /// <summary>
    /// Colour for an HTTP method badge, so a list of requests is scannable by verb the way the
    /// Result column makes it scannable by status. Reuses the status colours rather than adding a
    /// second set, so both themes and any future palette edit stay in step -- the two never appear
    /// in the same column, so sharing them does not make either ambiguous.
    /// </summary>
    public static Color ForMethod(string? method) => method?.Trim().ToUpperInvariant() switch
    {
        "GET" => StatusOk,
        "POST" => StatusRedirect,
        "PUT" or "PATCH" => StatusClientError,
        "DELETE" => StatusServerError,
        "HEAD" or "OPTIONS" or "TRACE" => TextDim,
        _ => Text,
    };

    /// <summary>Walks a control tree applying the palette. Safe to call again after adding children.</summary>
    public static void Apply(Control control)
    {
        if (control is Form form) ApplyWindowChrome(form);

        // Only the ListViews and DarkTabControl opted into double buffering individually.
        // Everything else in the tree -- SplitContainer's panels, TabPages, every UserControl --
        // repaints the flicker-prone way, so a burst of captures invalidating a ListView visibly
        // flickers the whole container chain around it, not just that grid. DarkListView's helper
        // works on any Control despite the name (it reaches the same protected property every
        // Control exposes), so apply it everywhere this walk goes rather than one control at a time.
        DarkListView.EnableDoubleBuffering(control);

        ApplyFont(control);
        ApplyRowHeight(control);

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
            case ListBox listBox:
                listBox.BackColor = Surface;
                listBox.ForeColor = Text;
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
                ApplyToolStrip(toolStrip);
                break;
            default:
                control.BackColor = Background;
                control.ForeColor = Text;
                break;
        }

        // A context menu is attached to a control, not parented by it, so the walk below never
        // reaches it: every right-click menu stayed system-light in dark mode and kept the font size
        // it was built with after a zoom change.
        if (control.ContextMenuStrip is { } contextMenu) ApplyToolStrip(contextMenu);

        foreach (Control child in control.Controls) Apply(child);
    }

    /// <summary>
    /// Pushes the current font size onto a control.
    /// </summary>
    /// <remarks>
    /// A form's own font is set outright, because WinForms cascades it to every descendant that has
    /// not been given a font of its own -- which is most of the tree, including the session grid,
    /// whose row height follows its font. A control that was handed a palette font explicitly gets
    /// the current instance of that same font instead. Anything else is left alone: a control that
    /// is merely inheriting must keep inheriting, or the cascade stops there.
    /// </remarks>
    private static void ApplyFont(Control control)
    {
        if (control is Form)
        {
            control.Font = UiFont;
            return;
        }

        // Control.Font returns the parent's own instance when no font was set locally.
        if (ReferenceEquals(control.Font, control.Parent?.Font)) return;
        if (FontScale.Rebase(control.Font) is { } rebased) control.Font = rebased;
    }

    /// <summary>
    /// Grows or shrinks the fixed-height docked rows the layout is built from, so scaled text is not
    /// clipped by a row sized for the default font.
    /// </summary>
    /// <remarks>
    /// A docked single-line row has no auto-height in WinForms and there are about thirty-five of
    /// these constants, so the walk scales them rather than each being edited by hand. It
    /// deliberately leaves <see cref="SplitContainer.SplitterDistance"/>, the splitter minimum
    /// sizes, and ListView column widths alone -- splitters are user-draggable, and the columns
    /// expand to fit the view (the session list also re-measures its compact columns, which the form
    /// asks it to after a zoom), so those absorb the change on their own. A dialog whose whole
    /// client size needs to grow does that at its own call site via <see cref="ScaleDialogSize"/>.
    /// </remarks>
    private static void ApplyRowHeight(Control control)
    {
        if (control.AutoSize || control.Dock is not (DockStyle.Top or DockStyle.Bottom)) return;

        if (FontScale.IsDefault)
        {
            // Back at the default size: restore the row and then forget it, so this shared walk
            // stops touching geometry at all until a zoom change asks it to again. A theme toggle
            // has no business resizing anything.
            if (!BaseHeights.TryGetValue(control, out var known)) return;
            if (control.Height != known.Value) control.Height = known.Value;
            BaseHeights.Remove(control);
            return;
        }

        // Captured on the first visit, which is before any scaling has touched this control, so the
        // stored value stays the unscaled one no matter how often the size changes afterwards.
        var unscaled = BaseHeights.GetValue(control, static c => new BaseHeight(c.Height)).Value;
        var target = (int)Math.Round(unscaled * FontScale.Multiplier);
        if (control.Height != target) control.Height = target;
    }

    /// <summary>
    /// Scales a fixed-size dialog's client area by the current font size. Call before
    /// <see cref="Apply"/>, from a dialog whose rows would otherwise push its buttons out of view.
    /// </summary>
    public static void ScaleDialogSize(Form dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        if (FontScale.IsDefault) return;

        var multiplier = FontScale.Multiplier;
        dialog.ClientSize = new Size(
            (int)Math.Round(dialog.ClientSize.Width * multiplier),
            (int)Math.Round(dialog.ClientSize.Height * multiplier));
    }

    private static void ApplyToolStrip(ToolStrip toolStrip)
    {
        toolStrip.BackColor = SurfaceAlt;
        toolStrip.ForeColor = Text;
        toolStrip.Font = UiFont;
        toolStrip.Renderer = new PaletteToolStripRenderer();
        foreach (ToolStripItem item in toolStrip.Items) ApplyToolStripItem(item);
    }

    private static void ApplyToolStripItem(ToolStripItem item)
    {
        item.BackColor = SurfaceAlt;
        item.ForeColor = item.Enabled ? Text : TextDim;

        // Items inherit the strip's font unless one was set on them directly, which only the status
        // bar's monospaced timing label does.
        if (FontScale.Rebase(item.Font) is { } rebased) item.Font = rebased;

        if (item is not ToolStripDropDownItem dropDownItem) return;

        dropDownItem.DropDown.BackColor = SurfaceAlt;
        dropDownItem.DropDown.ForeColor = Text;
        dropDownItem.DropDown.Renderer = new PaletteToolStripRenderer();
        foreach (ToolStripItem child in dropDownItem.DropDownItems) ApplyToolStripItem(child);
    }

    private sealed class PaletteToolStripRenderer() : ToolStripProfessionalRenderer(new PaletteColors())
    {
        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // A status label's colour is its meaning (Capturing is green, Not Capturing red, the
            // timing summary dimmed), set by its owner on the item. Forcing the palette text colour
            // here painted every one of them the same. Menus and toolbars keep the palette colour.
            e.TextColor = !e.Item.Enabled ? TextDim
                : e.Item is ToolStripStatusLabel ? e.Item.ForeColor
                : Text;
            base.OnRenderItemText(e);
        }
    }

    private static bool SetDwmAttribute(IntPtr handle, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, Marshal.SizeOf<int>()) == 0;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

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

    private sealed class BaseHeight(int value)
    {
        public int Value { get; } = value;
    }

    private sealed record ThemeColors(
        Color Background, Color Surface, Color SurfaceWarning, Color SurfaceAlt, Color Border,
        Color Text, Color TextDim, Color Accent, Color Selection, Color StatusOk,
        Color StatusRedirect, Color StatusClientError, Color StatusServerError, Color StatusTunnel,
        Color Composed, Color AutoResponded);
}

public enum ThemeMode
{
    Dark,
    Light,
}
