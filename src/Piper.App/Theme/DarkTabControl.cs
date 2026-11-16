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

    /// <summary>The tab the left button went down on, until the mouse leaves the drag threshold or the button comes up; -1 otherwise.</summary>
    private int _pressIndex = -1;
    private Point _pressPoint;
    private int _dropIndex = -1;
    private bool _dropTarget;
    private bool _showTabs = true;

    public DarkTabControl()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        // Signals to Palette.Apply that this control already owns its drawing.
        DrawMode = TabDrawMode.OwnerDrawFixed;
        // Set before the handle exists: changing it later recreates the window.
        Padding = new Point(LogicalToDeviceUnits(TabPaddingX), 4);
        ResizeTabStrip();
    }

    /// <summary>The space either side of a tab's label, at 96 DPI.</summary>
    private const int TabPaddingX = 10;

    private const int TcmSetMinTabWidth = 0x1300 + 49;

    protected override void OnHandleCreated(EventArgs e)
    {
        // The native minimum widens a short label such as "Log" to that of a long one. Sent before
        // the base class adds the tabs, which are measured as they are inserted.
        SendMessage(Handle, TcmSetMinTabWidth, 0, ItemSize.Height);
        base.OnHandleCreated(e);
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

    /// <summary>
    /// False hides the tab row: the selected page fills the whole control. A window or flyout that
    /// holds a single panel and names it elsewhere sets this, so the name is not shown twice. The tabs
    /// still exist for the code that finds a panel by its tab, but nothing can click them.
    /// </summary>
    [System.ComponentModel.DefaultValue(true)]
    public bool ShowTabs
    {
        get => _showTabs;
        set
        {
            if (_showTabs == value) return;
            _showTabs = value;
            // The display rectangle is cached until a resize; this one moves the page.
            OnResize(EventArgs.Empty);
            Invalidate();
        }
    }

    private const int TcmAdjustRect = 0x1300 + 40;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Palette.Background);
        if (!_showTabs) return;

        var stripHeight = TabCount > 0 ? GetTabRect(0).Bottom : ItemSize.Height;
        var stripColor = _dropTarget ? Tint(Palette.SurfaceAlt) : Palette.SurfaceAlt;

        using (var strip = new SolidBrush(stripColor))
            g.FillRectangle(strip, new Rectangle(0, 0, Width, stripHeight));

        for (var i = 0; i < TabCount; i++)
        {
            var bounds = GetTabRect(i);
            var selected = i == SelectedIndex;

            using (var face = new SolidBrush(selected ? Palette.Background : stripColor))
                g.FillRectangle(face, bounds);

            // While a tab is dragged over the strip the insertion marker is the only accent on it.
            if (selected && !_dropTarget)
            {
                using var accent = new SolidBrush(Palette.Accent);
                g.FillRectangle(accent, new Rectangle(bounds.X, bounds.Y, bounds.Width, 2));
            }
            else
            {
                using var separator = new Pen(Palette.Border);
                g.DrawLine(separator, bounds.Right - 1, bounds.Top + 5, bounds.Right - 1, bounds.Bottom - 5);
            }

            var textColor = selected ? Palette.Text : Palette.TextDim;
            if (_checkedTabs.Contains(TabPages[i]))
            {
                // The tab is only as wide as its label plus the padding, so the glyph and the label are
                // centred together, the label without TextRenderer's own side padding, to fit inside it.
                const int Glyph = 13, Gap = 4;
                const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
                var label = TextRenderer.MeasureText(g, TabPages[i].Text, Font, Size.Empty, Flags).Width;
                var left = bounds.X + Math.Max(2, (bounds.Width - Glyph - Gap - label) / 2);
                DarkListView.DrawCheckGlyph(g, new Rectangle(left, bounds.Y + (bounds.Height - Glyph) / 2, Glyph, Glyph), isChecked: true);
                var textLeft = left + Glyph + Gap;
                TextRenderer.DrawText(g, TabPages[i].Text, Font,
                    new Rectangle(textLeft, bounds.Y, Math.Max(0, bounds.Right - 2 - textLeft), bounds.Height), textColor, Flags);
            }
            else
            {
                TextRenderer.DrawText(g, TabPages[i].Text, Font, bounds, textColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        DrawOverflowButton(g, stripColor);

        using (var edge = new Pen(_dropTarget ? Palette.Accent : Palette.Border))
            g.DrawLine(edge, 0, stripHeight, Width, stripHeight);

        if (!_dropTarget) return;
        // The outline shows in the margin the pages leave; the pages themselves are child windows.
        var outline = DropOutlineWidth;
        using (var accent = new SolidBrush(Palette.Accent))
        {
            g.FillRectangle(accent, 0, 0, Width, outline);
            g.FillRectangle(accent, 0, Height - outline, Width, outline);
            g.FillRectangle(accent, 0, 0, outline, Height);
            g.FillRectangle(accent, Width - outline, 0, outline, Height);
        }
        DrawDropMarker(g);
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

    internal bool IsTabChecked(TabPage tab) => _checkedTabs.Contains(tab);

    /// <summary>The gap a dragged tab would land in, from 0 (before the first tab) to <see cref="TabControl.TabCount"/>, or -1.</summary>
    internal int DropIndex => _dropIndex;

    /// <summary>True while a dragged tab is over this strip and would land on it.</summary>
    internal bool IsDropTarget => _dropTarget;

    /// <summary>
    /// Marks this strip as where a dragged tab would land, with the insertion marker in gap
    /// <paramref name="index"/>, or clears both. Only what changed is repainted: the whole strip when
    /// the outline comes or goes (its pages are child windows and are left alone), otherwise just the
    /// old and new marker.
    /// </summary>
    internal void SetDropTarget(bool target, int index)
    {
        if (!target || TabCount == 0) index = -1;
        else index = Math.Clamp(index, 0, TabCount);
        if (target == _dropTarget && index == _dropIndex) return;

        var outlineChanged = target != _dropTarget;
        var oldMarker = MarkerBounds(_dropIndex);
        _dropTarget = target;
        _dropIndex = index;
        if (IsDisposed || !IsHandleCreated) return;
        if (outlineChanged)
        {
            Invalidate();
            return;
        }

        if (!oldMarker.IsEmpty) Invalidate(oldMarker);
        if (MarkerBounds(index) is { IsEmpty: false } marker) Invalidate(marker);
    }

    /// <summary>The insertion marker's bar, at 96 DPI; its caps are <see cref="MarkerCap"/> wide.</summary>
    private int MarkerWidth => LogicalToDeviceUnits(3);
    private int MarkerCap => LogicalToDeviceUnits(9);
    /// <summary>The ring of background drawn round the marker, so it stands apart from a neighbouring accent.</summary>
    private const int MarkerHalo = 1;
    private int DropOutlineWidth => LogicalToDeviceUnits(2);

    /// <summary>The insertion marker's area for a gap, halo included: a bar the height of the tabs with caps, or empty.</summary>
    private Rectangle MarkerBounds(int index)
    {
        if (index < 0 || TabCount == 0) return Rectangle.Empty;
        var tab = GetTabRect(Math.Min(index, TabCount - 1));
        var x = index < TabCount ? tab.Left : tab.Right;
        var half = MarkerCap / 2 + MarkerHalo;
        x = Math.Clamp(x, half, Math.Max(half, Width - half - 1));
        return new Rectangle(x - half, tab.Top, half * 2 + 1, tab.Height);
    }

    private void DrawDropMarker(Graphics g)
    {
        var bounds = MarkerBounds(_dropIndex);
        if (bounds.IsEmpty) return;
        var centre = bounds.Left + bounds.Width / 2;
        var bar = MarkerWidth;
        var cap = MarkerCap;
        var top = bounds.Top + MarkerHalo;
        var bottom = bounds.Bottom - MarkerHalo;
        // A bar with caps top and bottom, so the marker reads as an insertion point, not a separator.
        Rectangle[] parts =
        [
            new(centre - bar / 2, top, bar, bottom - top),
            new(centre - cap / 2, top, cap, bar),
            new(centre - cap / 2, bottom - bar, cap, bar),
        ];
        using (var halo = new SolidBrush(Palette.Background))
            foreach (var part in parts)
                g.FillRectangle(halo, Rectangle.Inflate(part, MarkerHalo, MarkerHalo));
        using var accent = new SolidBrush(Palette.Accent);
        foreach (var part in parts) g.FillRectangle(accent, part);
    }

    /// <summary>The strip's colour lifted towards the accent, for the strip a dragged tab would land on.</summary>
    private static Color Tint(Color color)
    {
        const int Weight = 56; // of 255
        static int Mix(int from, int to) => from + (to - from) * Weight / 255;
        var accent = Palette.Accent;
        return Color.FromArgb(Mix(color.R, accent.R), Mix(color.G, accent.G), Mix(color.B, accent.B));
    }

    // ------------------------------------------------------------------ overflow

    /// <summary>The native scroll arrows the tab control adds when its tabs overflow; hidden in favour of the ▾ button.</summary>
    private nint _upDown;
    private bool _overflowHot;
    private ContextMenuStrip? _overflowMenu;

    /// <summary>True when some tab lies outside the strip, so the ▾ button is shown.</summary>
    internal bool HasOverflow =>
        _showTabs && TabCount > 0 && IsHandleCreated
        && (GetTabRect(0).Left < 0 || GetTabRect(TabCount - 1).Right > Width);

    /// <summary>The ▾ button at the right end of the tab row, a square the row's height; empty when nothing overflows.</summary>
    internal Rectangle OverflowButtonBounds
    {
        get
        {
            if (!HasOverflow) return Rectangle.Empty;
            var size = ItemSize.Height;
            var row = GetTabRect(0);
            return new Rectangle(Width - size, row.Top, size, row.Height);
        }
    }

    /// <summary>True while the pointer is over the ▾ button.</summary>
    internal bool IsOverflowHot => _overflowHot;

    /// <summary>The menu the ▾ button opened, while it is open.</summary>
    internal ContextMenuStrip? OverflowMenu => _overflowMenu is { Visible: true } menu ? menu : null;

    private void DrawOverflowButton(Graphics g, Color stripColor)
    {
        var bounds = OverflowButtonBounds;
        if (bounds.IsEmpty) return;

        using (var face = new SolidBrush(_overflowHot ? Palette.Selection : stripColor))
            g.FillRectangle(face, bounds);

        // A small downward triangle, sized from the button so it grows with the font.
        var half = Math.Max(3, bounds.Height / 7);
        var centre = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        Point[] glyph =
        [
            new(centre.X - half, centre.Y - half / 2),
            new(centre.X + half + 1, centre.Y - half / 2),
            new(centre.X, centre.Y + half / 2 + 1),
        ];
        using var brush = new SolidBrush(Palette.TextDim);
        g.FillPolygon(brush, glyph);
    }

    private void SetOverflowHot(bool hot)
    {
        if (hot == _overflowHot) return;
        _overflowHot = hot;
        if (OverflowButtonBounds is { IsEmpty: false } bounds) Invalidate(bounds);
    }

    /// <summary>Opens a menu of every tab under the ▾ button, the selected one checked. Choosing one selects it, as a click on it would.</summary>
    internal void ShowOverflowMenu()
    {
        var bounds = OverflowButtonBounds;
        if (bounds.IsEmpty) return;

        _overflowMenu ??= new ContextMenuStrip();
        foreach (var old in _overflowMenu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
        for (var i = 0; i < TabCount; i++)
        {
            var page = TabPages[i];
            var item = new ToolStripMenuItem(page.Text.Replace("&", "&&", StringComparison.Ordinal)) { Checked = i == SelectedIndex };
            item.Click += (_, _) =>
            {
                // The tabs can change while the menu is open, so find the page again.
                var index = TabPages.IndexOf(page);
                if (index >= 0) SelectedIndex = index;
            };
            _overflowMenu.Items.Add(item);
        }

        Palette.Apply(_overflowMenu);
        _overflowMenu.Show(this, new Point(bounds.Right, bounds.Bottom), ToolStripDropDownDirection.BelowLeft);
    }

    /// <summary>
    /// Hides the native up-down control. The tab control creates and re-shows it whenever its tabs
    /// stop fitting -- on a resize, a font change, or a tab added, removed or renamed -- so once it
    /// exists this runs after every message. Until then it is looked for after those messages and
    /// after a paint, which follows any change that could have created it.
    /// </summary>
    private void HideUpDown()
    {
        if (_upDown == 0 || !IsWindow(_upDown)) _upDown = FindWindowEx(Handle, 0, "msctls_updown32", null);
        if (_upDown != 0 && IsWindowVisible(_upDown)) ShowWindow(_upDown, SwHide);
    }

    private static bool ReshowsUpDown(int msg) => msg is WmPaint or WmSize or WmWindowPosChanged or WmParentNotify or WmSetFont
        or TcmInsertItem or TcmDeleteItem or TcmDeleteAllItems or TcmSetItem or TcmSetItemSize or TcmSetCurSel;

    private const int WmSize = 0x0005;
    private const int WmPaint = 0x000F;
    private const int WmSetFont = 0x0030;
    private const int WmWindowPosChanged = 0x0047;
    private const int WmParentNotify = 0x0210;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonDblClk = 0x0203;
    private const int TcmDeleteItem = 0x1300 + 8;
    private const int TcmDeleteAllItems = 0x1300 + 9;
    private const int TcmSetItemSize = 0x1300 + 41;
    private const int TcmSetItem = 0x1300 + 61;
    private const int TcmInsertItem = 0x1300 + 62;
    private const int SwHide = 0;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string className, string? windowName);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    protected override void OnMouseLeave(EventArgs e)
    {
        SetOverflowHot(false);
        base.OnMouseLeave(e);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _upDown = 0;
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _overflowMenu?.Dispose();
            _overflowMenu = null;
        }
        base.Dispose(disposing);
    }

    /// <summary>
    /// Raised once the mouse has moved past <see cref="SystemInformation.DragSize"/> with the left button
    /// held down on a tab, with that tab's index and the pointer's screen position.
    /// </summary>
    public event EventHandler<(int Index, Point Screen)>? TabDragStarted;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        // A double-click is not the start of a drag: on a floating window's tab it docks the panel.
        _pressIndex = e.Button == MouseButtons.Left && e.Clicks == 1 ? TabIndexAt(e.Location) : -1;
        _pressPoint = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SetOverflowHot(OverflowButtonBounds.Contains(e.Location));
        // The press, not e.Button, says the left button is down: a press ends on mouse-up or when the
        // capture goes, and e.Button is read from the keyboard state rather than from the message.
        if (_pressIndex < 0 || _pressIndex >= TabCount) return;
        var size = SystemInformation.DragSize;
        var threshold = new Rectangle(_pressPoint.X - size.Width / 2, _pressPoint.Y - size.Height / 2, size.Width, size.Height);
        if (threshold.Contains(e.Location)) return;

        var index = _pressIndex;
        _pressIndex = -1;
        TabDragStarted?.Invoke(this, (index, PointToScreen(e.Location)));
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressIndex = -1;
        base.OnMouseUp(e);
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture) _pressIndex = -1;
        base.OnMouseCaptureChanged(e);
    }

    /// <summary>
    /// Raised with a tab's index when the user asks for that tab's menu: by right-clicking it, or with
    /// Shift+F10 or the Menu key while the strip has the focus (for the selected tab).
    /// </summary>
    public event EventHandler<int>? TabMenuRequested;

    /// <summary>The index of the tab under a point in client coordinates, or -1 when there is none.</summary>
    public int TabIndexAt(Point client)
    {
        // The ▾ button covers whatever tab lies under it.
        if (OverflowButtonBounds.Contains(client)) return -1;
        for (var i = 0; i < TabCount; i++)
            if (GetTabRect(i).Contains(client)) return i;
        return -1;
    }

    private const int WmContextMenu = 0x007B;

    /// <summary>
    /// A right-click, Shift+F10 and the Menu key all arrive as WM_CONTEXTMENU, with no position for
    /// the keyboard. Only a request for this strip itself counts: the same message bubbles up from a
    /// page's controls that have no menu of their own, and those are not asking about a tab.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        // With the tabs hidden the page's rectangle is the whole control, both ways round.
        if (m.Msg == TcmAdjustRect && !_showTabs)
        {
            m.Result = 0;
            return;
        }

        if (m.Msg == WmContextMenu && m.WParam == Handle && TabMenuRequested is { } handler)
        {
            var index = m.LParam == -1 ? SelectedIndex : TabIndexAt(PointToClient(ScreenPoint(m.LParam)));
            if (index >= 0)
            {
                handler(this, index);
                return;
            }
        }

        // A press on the ▾ button opens its menu and goes no further: the tab beneath it is neither
        // selected nor the start of a drag.
        if (m.Msg is WmLButtonDown or WmLButtonDblClk && OverflowButtonBounds.Contains(ClientPoint(m.LParam)))
        {
            ShowOverflowMenu();
            return;
        }

        base.WndProc(ref m);
        if (IsHandleCreated && (_upDown != 0 || ReshowsUpDown(m.Msg))) HideUpDown();
    }

    /// <summary>Unpacks the signed client coordinates of a mouse message.</summary>
    private static Point ClientPoint(nint lParam) => ScreenPoint(lParam);

    /// <summary>Unpacks the signed screen coordinates of a mouse message; a second monitor can be left of or above the first.</summary>
    private static Point ScreenPoint(nint lParam) => new((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
}
