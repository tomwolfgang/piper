using System.Windows.Forms;

namespace Piper.App.Theme;

/// <summary>
/// A <see cref="TabControl"/> that paints its own tab strip in the dark palette.
/// </summary>
/// <remarks>
/// Owner-drawing via <see cref="TabDrawMode.OwnerDrawFixed"/> only covers the tab faces:
/// the strip either side of them is still painted by the OS common control in the system
/// (light) colour, and a managed Paint handler gets overwritten. Taking over painting
/// entirely with <see cref="ControlStyles.UserPaint"/> is the only way to own the whole
/// strip. Tab pages are real child windows and keep rendering themselves.
/// </remarks>
internal sealed class DarkTabControl : TabControl
{
    private readonly HashSet<TabPage> _checkedTabs = [];

    public DarkTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // Signals to Palette.Apply that this control already owns its drawing.
        DrawMode = TabDrawMode.OwnerDrawFixed;
        Padding = new Point(14, 4);
        ResizeTabStrip();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ResizeTabStrip();
    }

    /// <summary>
    /// Sizes the tab strip from the current font. <see cref="TabDrawMode.OwnerDrawFixed"/> means the
    /// strip height is ours to set, and a fixed one clips the labels as soon as the UI font grows.
    /// </summary>
    private void ResizeTabStrip() => ItemSize = new Size(0, Font.Height + 11);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Palette.Background);

        var stripHeight = TabCount > 0 ? GetTabRect(0).Bottom : ItemSize.Height;

        using (var strip = new SolidBrush(Palette.SurfaceAlt))
            g.FillRectangle(strip, new Rectangle(0, 0, Width, stripHeight));

        for (var i = 0; i < TabCount; i++)
        {
            var bounds = GetTabRect(i);
            var selected = i == SelectedIndex;

            using (var face = new SolidBrush(selected ? Palette.Background : Palette.SurfaceAlt))
                g.FillRectangle(face, bounds);

            if (selected)
            {
                using var accent = new SolidBrush(Palette.Accent);
                g.FillRectangle(accent, new Rectangle(bounds.X, bounds.Y, bounds.Width, 2));
            }
            else
            {
                using var separator = new Pen(Palette.Border);
                g.DrawLine(separator, bounds.Right - 1, bounds.Top + 5, bounds.Right - 1, bounds.Bottom - 5);
            }

            var textBounds = bounds;
            if (_checkedTabs.Contains(TabPages[i]))
            {
                var checkBounds = new Rectangle(bounds.X + 9, bounds.Y + (bounds.Height - 13) / 2, 13, 13);
                DarkListView.DrawCheckGlyph(g, checkBounds, isChecked: true);
                textBounds = new Rectangle(bounds.X + 17, bounds.Y, bounds.Width - 17, bounds.Height);
            }

            TextRenderer.DrawText(g, TabPages[i].Text, Font, textBounds,
                selected ? Palette.Text : Palette.TextDim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        using (var edge = new Pen(Palette.Border))
            g.DrawLine(edge, 0, stripHeight, Width, stripHeight);
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate(); // repaint the strip so the accent follows the selection
    }

    /// <summary>
    /// Selects a tab for the program rather than the user. Assigning <see cref="TabControl.SelectedIndex"/>
    /// runs the same leave-page validation as a click, which moves keyboard focus onto this control,
    /// so a session grid that re-renders the inspector on every arrow key lost focus to its tabs.
    /// Setting the native selection skips that step; the page swap and the change event still run.
    /// </summary>
    public void SelectWithoutFocus(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, TabCount);
        if (index == SelectedIndex) return;
        if (!IsHandleCreated)
        {
            SelectedIndex = index; // no window yet, so nothing to focus
            return;
        }

        SendMessage(Handle, TcmSetCurSel, index, 0);
        OnSelectedIndexChanged(EventArgs.Empty);
    }

    private const int TcmSetCurSel = 0x1300 + 12;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    /// <summary>Displays a checkbox glyph beside tabs representing an active toggleable feature.</summary>
    public void SetTabChecked(TabPage tab, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (isChecked) _checkedTabs.Add(tab);
        else _checkedTabs.Remove(tab);
        Invalidate();
    }
}
