using System.ComponentModel;
using System.Windows.Forms;
using Piper.App.Theme;
using Microsoft.Web.WebView2.WinForms;

namespace Piper.App.Layout;

/// <summary>
/// An auto-hidden area's panel, slid out over the neighbouring area as in Visual Studio. It is a child
/// control at the top of the z-order, so nothing under it moves or reflows, and it has no animation or
/// translucency. A <see cref="ToolWindowHeader"/> names the panel and carries its buttons; the page sits
/// under it in a one-page strip with its tab row hidden, so the strip still takes dropped sessions. The
/// page is only lent to it: closing or disposing hands it back.
/// </summary>
/// <remarks>
/// It closes on a mouse-down outside it and its strip (watched by a message filter that exists only
/// while it is open), on Esc while it has the focus, when the focus moves into a WebView2 outside it
/// (whose clicks never reach the filter), and, when it was opened with the focus, when the focus leaves
/// it. The window's <c>Deactivate</c> closes it through <see cref="PanelHost.CloseFlyouts"/>.
/// </remarks>
internal sealed class Flyout : Control
{
    /// <summary>The draggable inner edge.</summary>
    public const int GripSize = 5;

    private const int WmLButtonDown = 0x0201;
    private const int WmRButtonDown = 0x0204;
    private const int WmMButtonDown = 0x0207;

    private readonly DockArea _edge;
    private readonly Func<double> _fraction;
    private readonly Action<double> _setFraction;
    private readonly OutsideClickFilter _filter;
    private WebView2[] _watchedWebViews = [];
    private Rectangle _frame;
    private bool _filterInstalled;
    private bool _closeOnLeave;
    private bool _resizing;

    /// <summary>Bumped on every open and close, so a deferred close meant for an earlier opening does nothing.</summary>
    private int _generation;

    /// <param name="fraction">The flyout's size as a fraction of <see cref="Frame"/>, across its edge.</param>
    /// <param name="setFraction">Stores a size the user dragged; the store clamps it.</param>
    public Flyout(DockArea edge, Func<double> fraction, Action<double> setFraction)
    {
        if (!Enum.IsDefined(edge)) throw new ArgumentOutOfRangeException(nameof(edge));
        ArgumentNullException.ThrowIfNull(fraction);
        ArgumentNullException.ThrowIfNull(setFraction);
        _edge = edge;
        _fraction = fraction;
        _setFraction = setFraction;
        _filter = new OutsideClickFilter(this);

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Visible = false;
        Strip = new DarkTabControl { Font = Palette.UiFont, ShowTabs = false };
        Header = new ToolWindowHeader();
        Controls.Add(Strip);
        Controls.Add(Header);
    }

    /// <summary>Raised after the flyout closes, with whether the focus was inside it.</summary>
    public event EventHandler<bool>? Closed;

    /// <summary>Raised once the user has finished dragging the inner edge.</summary>
    public event EventHandler? Resized;

    /// <summary>The flyout's one-page strip, its tab row hidden under the header.</summary>
    public DarkTabControl Strip { get; }

    /// <summary>The title bar above the page.</summary>
    public ToolWindowHeader Header { get; }

    /// <summary>
    /// The least size across its edge, in pixels, the page needs to stay usable: its width for a side
    /// flyout, its height for the Bottom one. The flyout keeps to it unless the frame itself is smaller.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<TabPage, int> MinimumExtentOf { get; set; } = _ => 0;

    /// <summary>The auto-hide strip that opens this flyout; a click on it is not a click outside.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control? AutoHideStrip { get; set; }

    /// <summary>Every panel's content, searched on open for WebView2s outside the flyout.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<IEnumerable<Control>> Panels { get; set; } = () => [];

    /// <summary>
    /// The rectangle, in the parent's coordinates, the flyout opens into: the area the split layout
    /// fills. The flyout keeps to its <see cref="DockArea"/> edge of it and never leaves it.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Rectangle Frame
    {
        get => _frame;
        set
        {
            _frame = value;
            Reposition();
        }
    }

    public TabPage? Page { get; private set; }

    public bool IsOpen => Page is not null;

    /// <summary>True while the outside-click filter is registered; for tests.</summary>
    internal bool FilterInstalled => _filterInstalled;

    /// <summary>
    /// Shows the page over the neighbouring area and brings the flyout to the front. With
    /// <paramref name="takeFocus"/> the focus moves into the page and the flyout closes when it leaves.
    /// </summary>
    public void Open(TabPage page, bool takeFocus)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (IsDisposed) return;
        if (Page != page)
        {
            Close();
            _generation++;
            Strip.TabPages.Add(page);
            Page = page;
            Header.Text = page.Text;
            _closeOnLeave = false;
            WatchWebViews();
            InstallFilter();
        }

        Reposition();
        Visible = true;
        BringToFront();
        if (!takeFocus) return;
        _closeOnLeave = true;
        if (!page.ContainsFocus)
        {
            if (page.Controls.Count > 0) page.Controls[0].Focus();
            else page.Focus();
        }
    }

    /// <summary>Hides the flyout and hands the page back. Does nothing when it is not open.</summary>
    public void Close()
    {
        if (Page is not { } page) return;
        _generation++;
        _closeOnLeave = false;
        _resizing = false;
        RemoveFilter();
        UnwatchWebViews();
        var hadFocus = ContainsFocus;
        var focused = hadFocus ? null : FocusedControl();
        Visible = false;
        if (page.Parent == Strip) Strip.TabPages.Remove(page);
        // Taking the selected page out of a tab control selects another, which moves the focus onto it
        // (see DarkTabControl.SelectWithoutFocus); focus that was outside the flyout goes back.
        if (focused is { IsDisposed: false, Focused: false, CanFocus: true }) focused.Focus();
        Page = null;
        Closed?.Invoke(this, hadFocus);
    }

    /// <summary>The control with the keyboard focus in this window, found through each container's active control.</summary>
    private Control? FocusedControl()
    {
        Control? control = FindForm();
        while (control is ContainerControl { ActiveControl: { } inner }) control = inner;
        return control is { Focused: true } ? control : null;
    }

    /// <summary>
    /// Esc closes the flyout, but only once the focused control has had it: a dialog key reaches here
    /// after the control's <c>IsInputKey</c>, so an open ComboBox dropdown or AutoComplete list keeps its
    /// Esc and closes itself instead of the whole flyout.
    /// </summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape && IsOpen && ContainsFocus && !FocusedListIsOpen())
        {
            Close();
            return true;
        }

        return base.ProcessDialogKey(keyData);
    }

    /// <summary>A belt-and-braces check for a dropdown a control did not claim through <c>IsInputKey</c>.</summary>
    private bool FocusedListIsOpen()
    {
        for (var control = FocusedControl(); control is not null && control != this; control = control.Parent)
            if (control is ComboBox { DroppedDown: true }) return true;
        return false;
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        Header.Active = true;
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        Header.Active = false;
        if (_closeOnLeave) CloseLater(requireNoFocus: true);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        var inner = ClientRectangle;
        // A 1 px border on the outer sides, the grip on the inner one.
        inner = _edge switch
        {
            DockArea.Left => new Rectangle(inner.X + 1, inner.Y + 1, inner.Width - 1 - GripSize, inner.Height - 2),
            DockArea.Right => new Rectangle(inner.X + GripSize, inner.Y + 1, inner.Width - 1 - GripSize, inner.Height - 2),
            _ => new Rectangle(inner.X + 1, inner.Y + GripSize, inner.Width - 2, inner.Height - 1 - GripSize),
        };
        var width = Math.Max(inner.Width, 0);
        var header = Math.Min(Header.PreferredHeight, Math.Max(inner.Height, 0));
        Header.Bounds = new Rectangle(inner.X, inner.Y, width, header);
        Strip.Bounds = new Rectangle(inner.X, inner.Y + header, width, Math.Max(inner.Height - header, 0));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Palette.Border);
        var grip = GripBounds();
        // The grip is a band of the strip colour inside the border, so the edge reads as one line.
        grip = _edge switch
        {
            DockArea.Left => new Rectangle(grip.X, grip.Y + 1, grip.Width - 1, grip.Height - 2),
            DockArea.Right => new Rectangle(grip.X + 1, grip.Y + 1, grip.Width - 1, grip.Height - 2),
            _ => new Rectangle(grip.X + 1, grip.Y + 1, grip.Width - 2, grip.Height - 1),
        };
        using var face = new SolidBrush(Palette.SurfaceAlt);
        g.FillRectangle(face, grip);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !GripBounds().Contains(e.Location)) return;
        _resizing = true;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _resizing || GripBounds().Contains(e.Location)
            ? (_edge == DockArea.Bottom ? Cursors.SizeNS : Cursors.SizeWE)
            : Cursors.Default;
        if (!_resizing || _frame.Width <= 0 || _frame.Height <= 0) return;

        // The size is measured from the frame's edge the flyout is anchored to, to the pointer.
        var pointer = new Point(Left + e.X, Top + e.Y);
        var fraction = _edge switch
        {
            DockArea.Left => (double)(pointer.X - _frame.Left) / _frame.Width,
            DockArea.Right => (double)(_frame.Right - pointer.X) / _frame.Width,
            _ => (double)(_frame.Bottom - pointer.Y) / _frame.Height,
        };
        _setFraction(fraction);
        Reposition();
    }

    /// <summary>
    /// The pointer left the flyout's own surface, often onto its strip. Children inherit
    /// <see cref="Control.Cursor"/>, so a resize cursor left set here would show over the whole panel.
    /// </summary>
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_resizing) Cursor = Cursors.Default;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        EndResize();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        EndResize();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // No Closed event: the host is going away and must not move the focus now.
            _generation++;
            RemoveFilter();
            UnwatchWebViews();
            // The page goes back before the disposal of this control could reach the panel inside it.
            if (Page is { } page && page.Parent == Strip) Strip.TabPages.Remove(page);
            Page = null;
        }

        base.Dispose(disposing);
    }

    private void EndResize()
    {
        if (!_resizing) return;
        _resizing = false;
        // The next move over the grip sets it again; anywhere else it must not linger.
        if (!GripBounds().Contains(PointToClient(Cursor.Position))) Cursor = Cursors.Default;
        Capture = false;
        Resized?.Invoke(this, EventArgs.Empty);
    }

    private Rectangle GripBounds() => _edge switch
    {
        DockArea.Left => new Rectangle(Width - GripSize, 0, GripSize, Height),
        DockArea.Right => new Rectangle(0, 0, GripSize, Height),
        _ => new Rectangle(0, 0, Width, GripSize),
    };

    /// <summary>
    /// Places the flyout along its edge of <see cref="Frame"/>, its size the stored fraction but never
    /// less than the page's minimum, and never outside the frame.
    /// </summary>
    private void Reposition()
    {
        var frame = _frame;
        var fraction = PanelLayout.ClampFraction(_fraction(), PanelLayout.DefaultFlyoutFraction);
        // The page's minimum plus the flyout's own border, grip and, for the Bottom one, its header.
        var minimum = Page is { } page ? Math.Max(MinimumExtentOf(page), 0) : 0;
        if (minimum > 0) minimum += 1 + GripSize + (_edge == DockArea.Bottom ? Header.PreferredHeight : 0);
        Rectangle target;
        if (_edge == DockArea.Bottom)
        {
            var height = Math.Clamp(Math.Max((int)Math.Round(fraction * frame.Height), minimum), 0, Math.Max(frame.Height, 0));
            target = new Rectangle(frame.Left, frame.Bottom - height, Math.Max(frame.Width, 0), height);
        }
        else
        {
            var width = Math.Clamp(Math.Max((int)Math.Round(fraction * frame.Width), minimum), 0, Math.Max(frame.Width, 0));
            var left = _edge == DockArea.Left ? frame.Left : frame.Right - width;
            target = new Rectangle(left, frame.Top, width, Math.Max(frame.Height, 0));
        }

        if (Bounds != target) Bounds = target;
    }

    /// <summary>Closes on the next message, unless the flyout was opened or closed again meanwhile.</summary>
    private void CloseLater(bool requireNoFocus)
    {
        if (!IsHandleCreated || IsDisposed) return;
        var generation = _generation;
        BeginInvoke(() =>
        {
            if (generation != _generation || !IsOpen || (requireNoFocus && ContainsFocus)) return;
            Close();
        });
    }

    private void InstallFilter()
    {
        if (_filterInstalled) return;
        Application.AddMessageFilter(_filter);
        _filterInstalled = true;
    }

    private void RemoveFilter()
    {
        if (!_filterInstalled) return;
        Application.RemoveMessageFilter(_filter);
        _filterInstalled = false;
    }

    /// <summary>
    /// Clicks inside a WebView2 go to the browser process and never reach the message filter, but the
    /// control still reports the focus moving in, so that closes the flyout instead.
    /// </summary>
    private void WatchWebViews()
    {
        UnwatchWebViews();
        _watchedWebViews = Panels().SelectMany(Descendants).OfType<WebView2>().Where(view => !Contains(view)).ToArray();
        foreach (var view in _watchedWebViews) view.GotFocus += OnWebViewGotFocus;
    }

    private void UnwatchWebViews()
    {
        foreach (var view in _watchedWebViews) view.GotFocus -= OnWebViewGotFocus;
        _watchedWebViews = [];
    }

    private void OnWebViewGotFocus(object? sender, EventArgs e) => CloseLater(requireNoFocus: false);

    private static IEnumerable<Control> Descendants(Control root)
    {
        yield return root;
        foreach (Control child in root.Controls)
            foreach (var descendant in Descendants(child))
                yield return descendant;
    }

    /// <summary>A mouse button went down at a screen point in one of this thread's windows.</summary>
    private void OnMouseDownAnywhere(nint hwnd, nint lParam)
    {
        if (!IsOpen || !IsHandleCreated) return;
        // Only clicks in this window count: a menu, a floating window or a dialog is not the area under
        // the flyout, and activating another window closes it through Deactivate anyway.
        if (Control.FromChildHandle(hwnd) is not { } target || target.FindForm() is not { } form || form != FindForm()) return;
        var point = Control.FromHandle(hwnd) is { } exact
            ? exact.PointToScreen(new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)))
            : Cursor.Position;
        if (RectangleToScreen(ClientRectangle).Contains(point)) return;
        if (AutoHideStrip is { Visible: true } strip && strip.RectangleToScreen(strip.ClientRectangle).Contains(point)) return;
        // Deferred: the filter list must not change while the message loop is walking it.
        CloseLater(requireNoFocus: false);
    }

    /// <summary>Watches this thread's mouse-downs; it never swallows one.</summary>
    private sealed class OutsideClickFilter(Flyout owner) : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg is WmLButtonDown or WmRButtonDown or WmMButtonDown) owner.OnMouseDownAnywhere(m.HWnd, m.LParam);
            return false;
        }
    }
}
