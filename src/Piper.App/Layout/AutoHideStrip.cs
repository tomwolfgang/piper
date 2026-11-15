using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Piper.App.Theme;

namespace Piper.App.Layout;

/// <summary>
/// The thin strip an auto-hidden area leaves along its window edge, naming each of its panels, as in
/// Visual Studio. Left and Right strips are vertical, with the names turned to read top to bottom, and
/// the Bottom strip is horizontal. Clicking a name, or dragging something over it, asks for that
/// panel's flyout. The strip never takes the focus, so a click on it leaves the focus where it was.
/// </summary>
internal sealed class AutoHideStrip : Control
{
    /// <summary>Space around the text along the strip, at the default font size; it grows with the font.</summary>
    private const int ItemPadding = 10;

    /// <summary>The thickness of the bar marking the open item, as on <see cref="DarkTabControl"/>'s selected tab.</summary>
    private const int MarkThickness = 2;

    /// <summary>The least space around the text, used when the names would not otherwise fit.</summary>
    private const int MinItemPadding = 6;

    private readonly DockArea _edge;
    private IReadOnlyList<(PanelId Id, string Title)> _panels = [];
    private Rectangle[] _items = [];
    private int _hovered = -1;
    private PanelId? _open;

    public AutoHideStrip(DockArea edge)
    {
        if (!Enum.IsDefined(edge)) throw new ArgumentOutOfRangeException(nameof(edge));
        _edge = edge;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        // A click on a name opens a flyout; the focus must stay in the grid or move into the flyout, never here.
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        AllowDrop = true;
        Dock = edge switch
        {
            DockArea.Left => DockStyle.Left,
            DockArea.Right => DockStyle.Right,
            _ => DockStyle.Bottom,
        };
        // AutoSize makes the dock layout take the thickness from GetPreferredSize, and keeps the theme
        // walk's row scaling off this strip: its thickness follows its own font instead.
        AutoSize = true;
        ResizeStrip();
    }

    /// <summary>Raised with the panel whose name was clicked.</summary>
    public event EventHandler<PanelId>? PanelClicked;

    /// <summary>Raised with the panel whose name something is dragged over, so a dropped session can reach it.</summary>
    public event EventHandler<PanelId>? PanelDragHover;

    public DockArea Edge => _edge;

    public bool IsVertical => _edge != DockArea.Bottom;

    /// <summary>The panels named on the strip, in order.</summary>
    public IReadOnlyList<(PanelId Id, string Title)> Panels => _panels;

    /// <summary>The panel whose flyout is open, marked with the accent; null when none is.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public PanelId? OpenPanel
    {
        get => _open;
        set
        {
            if (_open == value) return;
            _open = value;
            Invalidate();
        }
    }

    /// <summary>The thickness across the strip: the font's line height plus padding, so text never clips at any zoom.</summary>
    public int Thickness => Font.Height + 12;

    public void SetPanels(IReadOnlyList<(PanelId Id, string Title)> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        if (_panels.SequenceEqual(panels)) return;
        _panels = panels.ToArray();
        _hovered = -1;
        if (_open is { } open && !_panels.Any(panel => panel.Id == open)) _open = null;
        MeasureItems();
        AccessibilityNotifyClients(AccessibleEvents.Reorder, -1);
        Invalidate();
    }

    /// <summary>The item naming the panel, in client coordinates; empty when the strip does not name it.</summary>
    public Rectangle ItemBounds(PanelId id)
    {
        for (var i = 0; i < _panels.Count; i++)
            if (_panels[i].Id == id) return _items[i];
        return Rectangle.Empty;
    }

    /// <summary>Handles something dragged over the point, in client coordinates: opens that name's flyout.</summary>
    internal void DragHover(Point client)
    {
        if (IndexAt(client) is var index and >= 0 && _panels[index].Id != _open)
            PanelDragHover?.Invoke(this, _panels[index].Id);
    }

    public override Size GetPreferredSize(Size proposedSize) =>
        IsVertical ? new Size(Thickness, Height) : new Size(Width, Thickness);

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        ResizeStrip();
        MeasureItems();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        MeasureItems();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetHovered(IndexAt(e.Location));
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        SetHovered(-1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && IndexAt(e.Location) is var index and >= 0) PanelClicked?.Invoke(this, _panels[index].Id);
    }

    protected override void OnDragEnter(DragEventArgs drgevent)
    {
        base.OnDragEnter(drgevent);
        DragHover(PointToClient(new Point(drgevent.X, drgevent.Y)));
    }

    protected override void OnDragOver(DragEventArgs drgevent)
    {
        base.OnDragOver(drgevent);
        DragHover(PointToClient(new Point(drgevent.X, drgevent.Y)));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Palette.SurfaceAlt);

        for (var i = 0; i < _panels.Count; i++)
        {
            var bounds = _items[i];
            var open = _panels[i].Id == _open;
            var hovered = i == _hovered;

            if (open || hovered)
            {
                using var face = new SolidBrush(open ? Palette.Background : Palette.Surface);
                g.FillRectangle(face, bounds);
            }

            // The bar sits on the side facing the content, where the flyout opens.
            var mark = InnerEdge(bounds, MarkThickness);
            if (open)
            {
                using var accent = new SolidBrush(Palette.Accent);
                g.FillRectangle(accent, mark);
            }
            else if (hovered)
            {
                using var hover = new SolidBrush(Palette.Border);
                g.FillRectangle(hover, mark);
            }

            if (i < _panels.Count - 1)
            {
                using var separator = new Pen(Palette.Border);
                if (IsVertical) g.DrawLine(separator, bounds.Left + 5, bounds.Bottom, bounds.Right - 6, bounds.Bottom);
                else g.DrawLine(separator, bounds.Right, bounds.Top + 5, bounds.Right, bounds.Bottom - 6);
            }

            DrawTitle(g, _panels[i].Title, bounds, open || hovered ? Palette.Text : Palette.TextDim);
        }

        using var edge = new Pen(Palette.Border);
        switch (_edge)
        {
            case DockArea.Left: g.DrawLine(edge, Width - 1, 0, Width - 1, Height); break;
            case DockArea.Right: g.DrawLine(edge, 0, 0, 0, Height); break;
            default: g.DrawLine(edge, 0, 0, Width, 0); break;
        }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new StripAccessibleObject(this);

    /// <summary>The panel at a point, or -1.</summary>
    private int IndexAt(Point client)
    {
        for (var i = 0; i < _items.Length; i++)
            if (_items[i].Contains(client)) return i;
        return -1;
    }

    private void SetHovered(int index)
    {
        if (_hovered == index) return;
        _hovered = index;
        Invalidate();
    }

    private void ResizeStrip()
    {
        if (IsVertical) Width = Thickness;
        else Height = Thickness;
        Parent?.PerformLayout(this, nameof(Font));
    }

    /// <summary>
    /// Lays the names end to end from the strip's start. Each length is measured with the same GDI+
    /// call that draws a turned name, and with GDI for a level one, so an item fits its own text. When
    /// the names do not all fit, the padding shrinks first; then the longest names share what is left
    /// equally and are drawn with an ellipsis, so text is shortened but never cut through.
    /// </summary>
    private void MeasureItems()
    {
        var count = _panels.Count;
        var texts = new int[count];
        using (var measure = Graphics.FromHwnd(0))
        {
            for (var i = 0; i < count; i++)
            {
                var text = _panels[i].Title;
                texts[i] = TextRenderer.MeasureText(text, Font).Width;
                if (!IsVertical) continue;
                using var format = VerticalFormat(trim: false);
                texts[i] = Math.Max(texts[i], (int)Math.Ceiling(measure.MeasureString(text, Font, int.MaxValue, format).Height));
            }
        }

        var available = (IsVertical ? Height : Width) - Math.Max(count - 1, 0);
        var padding = Font.Height * ItemPadding / 15 + 4;
        if (available > 0 && texts.Sum() + count * padding * 2 > available) padding = MinItemPadding;
        var lengths = texts.Select(text => text + padding * 2).ToArray();
        if (available > 0 && lengths.Sum() > available) ShareOut(lengths, available);

        var items = new Rectangle[count];
        var offset = 0;
        for (var i = 0; i < count; i++)
        {
            items[i] = IsVertical ? new Rectangle(0, offset, Thickness, lengths[i]) : new Rectangle(offset, 0, lengths[i], Thickness);
            offset += lengths[i] + 1;
        }

        _items = items;
    }

    /// <summary>Shortens the longest lengths to an equal share so they all fit <paramref name="available"/>; shorter ones keep theirs.</summary>
    private static void ShareOut(int[] lengths, int available)
    {
        var order = Enumerable.Range(0, lengths.Length).OrderBy(i => lengths[i]).ToArray();
        var left = available;
        for (var k = 0; k < order.Length; k++)
        {
            var share = Math.Max(left / (order.Length - k), 0);
            var i = order[k];
            if (lengths[i] > share) lengths[i] = share;
            left -= lengths[i];
        }
    }

    /// <summary>The <paramref name="thickness"/>-wide band of an item along the side facing the content.</summary>
    private Rectangle InnerEdge(Rectangle item, int thickness) => _edge switch
    {
        DockArea.Left => new Rectangle(item.Right - thickness, item.Top, thickness, item.Height),
        DockArea.Right => new Rectangle(item.Left, item.Top, thickness, item.Height),
        _ => new Rectangle(item.Left, item.Top, item.Width, thickness),
    };

    private void DrawTitle(Graphics g, string text, Rectangle bounds, Color color)
    {
        if (!IsVertical)
        {
            var inset = Rectangle.Inflate(bounds, -MinItemPadding / 2, 0);
            TextRenderer.DrawText(g, text, Font, inset, color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            return;
        }

        // GDI ignores a rotated transform, so a turned name is drawn by GDI+ in its vertical direction.
        var hint = g.TextRenderingHint;
        var smoothing = g.SmoothingMode;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.SmoothingMode = SmoothingMode.None;
        using var brush = new SolidBrush(color);
        using var format = VerticalFormat(trim: true);
        g.DrawString(text, Font, brush, Rectangle.Inflate(bounds, 0, -MinItemPadding / 2), format);
        g.TextRenderingHint = hint;
        g.SmoothingMode = smoothing;
    }

    private static StringFormat VerticalFormat(bool trim) => new(StringFormatFlags.DirectionVertical | StringFormatFlags.NoWrap)
    {
        Alignment = StringAlignment.Center,
        LineAlignment = StringAlignment.Center,
        HotkeyPrefix = HotkeyPrefix.None,
        Trimming = trim ? StringTrimming.EllipsisCharacter : StringTrimming.None,
    };

    /// <summary>The strip as a page tab list, so a screen reader reads each name as a tab it can open.</summary>
    private sealed class StripAccessibleObject(AutoHideStrip owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.PageTabList;

        public override int GetChildCount() => owner._panels.Count;

        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < owner._panels.Count ? new ItemAccessibleObject(owner, this, index) : null;
    }

    private sealed class ItemAccessibleObject(AutoHideStrip owner, AccessibleObject parent, int index) : AccessibleObject
    {
        private bool Exists => index < owner._panels.Count;

        public override AccessibleRole Role => AccessibleRole.PageTab;

        public override string? Name
        {
            get => Exists ? owner._panels[index].Title : null;
            set { }
        }

        public override AccessibleObject Parent => parent;

        public override Rectangle Bounds =>
            Exists && owner.IsHandleCreated ? owner.RectangleToScreen(owner._items[index]) : Rectangle.Empty;

        public override AccessibleStates State
        {
            get
            {
                var state = AccessibleStates.Selectable;
                if (Exists && owner._panels[index].Id == owner._open) state |= AccessibleStates.Selected;
                if (!owner.Visible) state |= AccessibleStates.Invisible;
                return state;
            }
        }

        public override void DoDefaultAction()
        {
            if (Exists) owner.PanelClicked?.Invoke(owner, owner._panels[index].Id);
        }
    }
}
