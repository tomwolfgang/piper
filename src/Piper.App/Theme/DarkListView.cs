using System.Windows.Forms;

namespace Piper.App.Theme;

/// <summary>
/// Owner-draws a <see cref="ListView"/> in the dark palette.
/// </summary>
/// <remarks>
/// Column headers are drawn by the OS common control and ignore BackColor entirely, so
/// they stay light on a dark form unless the whole control is owner-drawn. This wires up
/// the plain case; grids that colour rows by meaning draw themselves.
/// </remarks>
internal static class DarkListView
{
    /// <summary>
    /// Turns on the buffered-paint behind <see cref="Control.DoubleBuffered"/>, which the base
    /// <see cref="Control"/> class exposes only as <c>protected</c> -- there is no public way to
    /// flip it on a plain <see cref="ListView"/> instance without subclassing, so this reaches it
    /// via reflection instead. Without it, a virtual-mode owner-drawn grid that gets invalidated
    /// often (e.g. once per captured session) visibly tears/flickers on repaint.
    /// </summary>
    public static void EnableDoubleBuffering(Control control)
    {
        typeof(Control).InvokeMember("DoubleBuffered",
            System.Reflection.BindingFlags.SetProperty | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, control, [true]);
    }

    public static void Attach(ListView list)
    {
        EnableDoubleBuffering(list);
        list.OwnerDraw = true;
        list.BackColor = Palette.Surface;
        list.ForeColor = Palette.Text;
        list.BorderStyle = BorderStyle.None;

        list.DrawColumnHeader += DrawHeader;
        list.DrawSubItem += DrawSubItem;
        list.DrawItem += (_, e) => e.DrawDefault = false;
    }

    /// <summary>
    /// Appends a zero-text column that soaks up the space right of the last real column.
    /// </summary>
    /// <remarks>
    /// Without it the header strip past the final column is painted by the OS common
    /// control in the system (light) colour, leaving a white notch on a dark form. Owner
    /// draw never gets a chance at that region because no column covers it.
    /// </remarks>
    public static void AddFillerColumn(ListView list)
    {
        var filler = list.Columns.Add(string.Empty, 0);
        var adjusting = false;

        void Resize()
        {
            if (adjusting) return;
            adjusting = true;
            try
            {
                var used = 0;
                for (var i = 0; i < list.Columns.Count - 1; i++) used += list.Columns[i].Width;
                filler.Width = Math.Max(0, list.ClientSize.Width - used);
            }
            finally
            {
                adjusting = false;
            }
        }

        list.Resize += (_, _) => Resize();
        list.ColumnWidthChanged += (_, _) => Resize();
        Resize();
    }

    public static void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var background = new SolidBrush(Palette.SurfaceAlt);
        e.Graphics.FillRectangle(background, e.Bounds);

        using var separator = new Pen(Palette.Border);
        e.Graphics.DrawLine(separator, e.Bounds.Right - 1, e.Bounds.Top + 2, e.Bounds.Right - 1, e.Bounds.Bottom - 2);
        e.Graphics.DrawLine(separator, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, Palette.UiFont,
            Rectangle.Inflate(e.Bounds, -6, 0), Palette.TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static void DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null) return;

        using var background = new SolidBrush(e.Item.Selected ? Palette.Selection : Palette.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);

        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, Palette.Mono,
            Rectangle.Inflate(e.Bounds, -5, 0), Palette.Text,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
