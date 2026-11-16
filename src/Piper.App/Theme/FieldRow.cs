using System.Windows.Forms;

namespace Piper.App.Theme;

/// <summary>
/// A docked row holding one borderless single-line <see cref="TextBox"/>, drawn with a single 1 px
/// <see cref="Palette.Border"/> line instead of the text box's own frame.
/// </summary>
/// <remarks>
/// A framed text box docked into a fixed-height row had its bottom border clipped once the font
/// grew, and inside a page it doubled the page's own edge. This row is the font's height plus 8 px,
/// scaled, insets the text 8 px, and fills itself with the text box's colour so the inset reads as
/// part of the field. It sizes itself, so the theme walk's row scaling leaves it alone.
/// </remarks>
internal sealed class FieldRow : Panel
{
    private readonly TextBox _box;
    private readonly bool _bottomEdgeOnly;

    /// <param name="box">The text box; its border is removed.</param>
    /// <param name="bottomEdgeOnly">True draws only the line under the row, for a field that sits flush on a grid; false frames it.</param>
    public FieldRow(TextBox box, bool bottomEdgeOnly)
    {
        ArgumentNullException.ThrowIfNull(box);
        _box = box;
        _bottomEdgeOnly = bottomEdgeOnly;
        box.BorderStyle = BorderStyle.None;
        box.Dock = DockStyle.None;
        Controls.Add(box);

        Dock = DockStyle.Top;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);

        // A zoom gives the text box a new font, and with it a new height.
        box.SizeChanged += (_, _) =>
        {
            Parent?.PerformLayout(this, nameof(Height));
            PerformLayout();
        };
        box.BackColorChanged += (_, _) => Invalidate();
    }

    private int Inset => LogicalToDeviceUnits(8);

    public override Size GetPreferredSize(Size proposedSize) =>
        new(Math.Max(proposedSize.Width, 0), _box.Font.Height + LogicalToDeviceUnits(8));

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var inset = Inset;
        var height = _box.Height;
        _box.SetBounds(inset, Math.Max(0, (Height - height) / 2), Math.Max(0, Width - inset * 2), height);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(_box.BackColor);
        using var edge = new Pen(Palette.Border);
        if (_bottomEdgeOnly) e.Graphics.DrawLine(edge, 0, Height - 1, Width, Height - 1);
        else e.Graphics.DrawRectangle(edge, 0, 0, Width - 1, Height - 1);
    }
}
