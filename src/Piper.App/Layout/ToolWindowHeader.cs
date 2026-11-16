using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Piper.App.Theme;

namespace Piper.App.Layout;

/// <summary>
/// The title bar of an auto-hidden panel's flyout, as on a Visual Studio tool window: the panel's name,
/// then a button listing the area's panels, a pin that docks the area again and a close button that
/// hides the panel. It is accent-coloured while the focus is in the flyout. It never takes the focus
/// itself, so a click on it leaves the focus where it was.
/// </summary>
internal sealed class ToolWindowHeader : Control
{
    /// <summary>The header's buttons, left to right.</summary>
    internal enum Button { Panels, Pin, Close }

    private static readonly Button[] Buttons = [Button.Panels, Button.Pin, Button.Close];

    private readonly ToolTip _toolTip = new();
    private Button? _hovered;
    private Button? _pressed;
    private bool _active;

    public ToolWindowHeader()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AccessibleRole = AccessibleRole.TitleBar;
        Font = Palette.UiFont;
        Height = PreferredHeight;
    }

    /// <summary>Raised with the screen point under the panels button, where its menu should open.</summary>
    public event EventHandler<Point>? PanelsRequested;

    public event EventHandler? PinClicked;

    public event EventHandler? CloseClicked;

    /// <summary>Raised with a screen point when the title is right-clicked, for the panel's tab menu.</summary>
    public event EventHandler<Point>? MenuRequested;

    /// <summary>The height the header wants for its font: the text with 5 px above and below.</summary>
    public int PreferredHeight => Font.Height + 10;

    /// <summary>True while the focus is in the flyout; the header then shows the accent.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            Invalidate();
        }
    }

    /// <summary>A button's bounds in client coordinates; for tests and the accessible children.</summary>
    internal Rectangle ButtonBounds(Button button)
    {
        var side = Math.Min(Math.Max(Scaled(22), 1), Math.Max(Height, 1));
        var gap = Scaled(2);
        var right = Width - Scaled(4);
        var index = Buttons.Length - 1 - Array.IndexOf(Buttons, button);
        var x = right - side - index * (side + gap);
        return new Rectangle(x, (Height - side) / 2, side, side);
    }

    /// <summary>The tooltip and accessible name of a button.</summary>
    internal static string ButtonName(Button button) => button switch
    {
        Button.Panels => Strings.Panels.HeaderPanels,
        Button.Pin => Strings.Panels.HeaderPin,
        _ => Strings.Panels.HeaderClose,
    };

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        Height = PreferredHeight;
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        AccessibleName = Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var back = _active ? Palette.Accent : Palette.SurfaceAlt;
        // On the accent the light theme's text is too dark; its background is near white, as the dark theme's text is.
        var onAccent = Palette.IsLightMode ? Palette.Background : Palette.Text;
        var fore = _active ? onAccent : Palette.Text;
        g.Clear(back);

        var firstButton = ButtonBounds(Button.Panels).Left;
        var titleLeft = Scaled(8);
        var title = new Rectangle(titleLeft, 0, Math.Max(firstButton - titleLeft - Scaled(4), 0), Height);
        TextRenderer.DrawText(g, Text, Font, title, fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var button in Buttons)
        {
            var bounds = ButtonBounds(button);
            var lit = _hovered == button || _pressed == button;
            if (lit)
            {
                using var hover = new SolidBrush(Palette.Selection);
                g.FillRectangle(hover, bounds);
            }

            DrawGlyph(g, button, bounds, lit ? Palette.Text : fore);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hovered = ButtonAt(e.Location);
        if (hovered == _hovered) return;
        _hovered = hovered;
        _toolTip.SetToolTip(this, hovered is { } button ? ButtonName(button) : null);
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hovered is null) return;
        _hovered = null;
        _toolTip.SetToolTip(this, null);
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _pressed = e.Button == MouseButtons.Left ? ButtonAt(e.Location) : null;
        if (_pressed is not null) Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressed;
        _pressed = null;
        if (pressed is not null) Invalidate();

        if (e.Button == MouseButtons.Right && ButtonAt(e.Location) is null)
        {
            MenuRequested?.Invoke(this, PointToScreen(e.Location));
            return;
        }

        if (e.Button != MouseButtons.Left || pressed is not { } button || ButtonAt(e.Location) != button) return;
        Press(button);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (Capture || _pressed is null) return;
        _pressed = null;
        Invalidate();
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new HeaderAccessibleObject(this);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }

    private void Press(Button button)
    {
        switch (button)
        {
            case Button.Panels:
                var bounds = ButtonBounds(Button.Panels);
                PanelsRequested?.Invoke(this, PointToScreen(new Point(bounds.Left, bounds.Bottom)));
                break;
            case Button.Pin:
                PinClicked?.Invoke(this, EventArgs.Empty);
                break;
            case Button.Close:
                CloseClicked?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private Button? ButtonAt(Point client)
    {
        foreach (var button in Buttons)
            if (ButtonBounds(button).Contains(client)) return button;
        return null;
    }

    /// <summary>Scales a size at 96 DPI and 100% zoom to this control's DPI and the current zoom.</summary>
    private int Scaled(int logical) => (int)Math.Round(LogicalToDeviceUnits(logical) * Palette.ZoomMultiplier);

    /// <summary>Draws a button's glyph, sized from the button so it follows the DPI and the zoom.</summary>
    private static void DrawGlyph(Graphics g, Button button, Rectangle bounds, Color color)
    {
        var s = bounds.Width;
        var cx = bounds.Left + s / 2f;
        var cy = bounds.Top + s / 2f;
        using var pen = new Pen(color, Math.Max(1f, s / 16f));
        using var brush = new SolidBrush(color);
        switch (button)
        {
            case Button.Panels:
                // A small downward triangle: this opens a list.
                var w = s * 0.36f;
                var h = s * 0.18f;
                g.FillPolygon(brush, [new PointF(cx - w / 2, cy - h / 2), new PointF(cx + w / 2, cy - h / 2), new PointF(cx, cy + h / 2)]);
                break;
            case Button.Pin:
                // A pin lying on its side, as Visual Studio draws an auto-hidden window's pin: the head on
                // the right, a bar across, the needle to the left.
                var bar = cx - s * 0.04f;
                g.DrawRectangle(pen, bar, cy - s * 0.13f, s * 0.26f, s * 0.26f);
                g.DrawLine(pen, bar + s * 0.26f, cy - s * 0.04f, bar + s * 0.26f, cy + s * 0.04f);
                g.DrawLine(pen, bar, cy - s * 0.22f, bar, cy + s * 0.22f);
                g.DrawLine(pen, bar, cy, cx - s * 0.30f, cy);
                break;
            default:
                var r = s * 0.17f;
                g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                g.DrawLine(pen, cx - r, cy + r, cx + r, cy - r);
                break;
        }
    }

    /// <summary>The header as a title bar whose three buttons are push buttons a screen reader can press.</summary>
    private sealed class HeaderAccessibleObject(ToolWindowHeader owner) : ControlAccessibleObject(owner)
    {
        public override int GetChildCount() => Buttons.Length;

        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < Buttons.Length ? new ButtonAccessibleObject(owner, this, Buttons[index]) : null;
    }

    private sealed class ButtonAccessibleObject(ToolWindowHeader owner, AccessibleObject parent, Button button) : AccessibleObject
    {
        public override string Name => ButtonName(button);

        public override AccessibleRole Role => AccessibleRole.PushButton;

        public override AccessibleObject Parent => parent;

        public override Rectangle Bounds => owner.RectangleToScreen(owner.ButtonBounds(button));

        public override void DoDefaultAction() => owner.Press(button);
    }
}
