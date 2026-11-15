using System.Windows.Forms;
using Piper.App.Theme;

namespace Piper.App.Layout;

/// <summary>
/// Moves a panel by dragging its tab: onto another docked strip, along its own, onto an auto-hidden
/// area's strip, or out of the main window to float it. It holds the mouse capture on the dragged tab's
/// strip rather than using OLE <c>DoDragDrop</c>, because sessions already use OLE drag onto these strips
/// and WebView2 is an OLE drop target of its own. Every way a drag can end (mouse-up, Esc, losing the
/// capture, the strip going away, the host shutting down or rearranging) releases the capture and clears
/// the marker; only a mouse-up changes the layout. It also marks, and docks on release, a floating
/// window the user drags by its title bar, whose tab row is hidden.
/// </summary>
internal sealed class TabDrag : IMessageFilter
{
    private const int WmKeyDown = 0x0100;
    private const int VkEscape = 0x1B;

    private readonly PanelHost _host;
    private readonly IEnumerable<DarkTabControl> _strips;

    /// <summary>The strip holding the capture, or null when no drag is under way.</summary>
    private DarkTabControl? _source;
    private PanelId _panel;
    private int _sourceIndex;
    private DarkTabControl? _marked;
    private AutoHideStrip? _markedAutoHide;
    /// <summary>The kind of drop the cursor shows, or null before the first move.</summary>
    private DropKind? _shownKind;
    /// <summary>The floating window being dragged by its title bar, or null.</summary>
    private Form? _window;

    /// <param name="strips">Every strip a tab can be dragged from or over, read afresh on each move: the docked strips and every floating window's.</param>
    public TabDrag(PanelHost host, IEnumerable<DarkTabControl> strips)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(strips);
        _host = host;
        _strips = strips;
    }

    private enum DropKind { None, Strip, AutoHide, Float }

    /// <summary>Where a drop at a point would put the panel. <see cref="Gap"/> counts the tabs as the strip shows them, the dragged one included.</summary>
    private readonly record struct Drop(DropKind Kind, DockArea Area = default, DarkTabControl? Strip = null, int Gap = -1, AutoHideStrip? AutoHide = null);

    public bool IsDragging => _source is not null;

    /// <summary>Starts dragging the tab at <paramref name="tabIndex"/> of <paramref name="source"/>; ignored while another drag runs or the host is going away.</summary>
    public void Begin(DarkTabControl source, int tabIndex, Point screenStart)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_source is not null || _host.IsDisposed || _host.ShuttingDown || source.IsDisposed || !source.IsHandleCreated) return;
        if (tabIndex < 0 || tabIndex >= source.TabCount || _host.PanelOf(source.TabPages[tabIndex]) is not { } id) return;
        if (!_strips.Contains(source)) return;

        _source = source;
        _panel = id;
        _sourceIndex = tabIndex;
        source.MouseMove += OnMouseMove;
        source.MouseUp += OnMouseUp;
        source.MouseCaptureChanged += OnCaptureChanged;
        source.Disposed += OnSourceGone;
        source.HandleDestroyed += OnSourceGone;
        Application.AddMessageFilter(this);

        // WinForms took the capture on mouse-down; taking it here as well covers a press it did not see.
        source.Capture = true;
        if (!source.Capture)
        {
            Cancel();
            return;
        }

        // A flyout open over a strip would hide the marker; it would have closed on the press anyway.
        _host.CloseFlyouts();
        Track(screenStart);
    }

    /// <summary>
    /// Marks where a floating window dragged by its title bar would dock its panel if released now: an
    /// area's tabs or an auto-hidden area's strip. Windows runs that drag in its own move loop, so the
    /// window reports each move and the end instead of this holding the capture.
    /// </summary>
    public void TrackWindow(PanelId id, Form window, Point screen)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_source is not null || _host.IsDisposed || _host.ShuttingDown || window.IsDisposed) return;
        _window = window;
        _panel = id;
        var drop = HitTest(screen);
        Mark(drop.Kind == DropKind.Strip ? drop.Strip : null, drop.Gap);
        MarkAutoHide(drop.Kind == DropKind.AutoHide ? drop.AutoHide : null);
    }

    /// <summary>
    /// Ends a title-bar drag of a floating window: released over an area's tabs or an auto-hidden area's
    /// strip after a real move, the panel docks there. Anywhere else the window simply stays where it went.
    /// </summary>
    public void EndWindow(Form window, Point screen, bool moved)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_window != window) return;
        var drop = HitTest(screen);
        var id = _panel;
        Cancel();
        if (!moved || drop.Kind is not (DropKind.Strip or DropKind.AutoHide) || _host.IsDisposed || !_host.IsHandleCreated) return;

        // Applied after the window's move loop: docking closes the window being moved.
        _host.BeginInvoke(() =>
        {
            if (_host.IsDisposed || _host.ShuttingDown) return;
            _host.MoveTo(id, drop.Area, drop.Kind == DropKind.Strip ? drop.Gap : null);
        });
    }

    /// <summary>Ends a drag, if one is under way, without changing the layout.</summary>
    public void Cancel()
    {
        if (_window is not null)
        {
            _window = null;
            Mark(null, -1);
            MarkAutoHide(null);
        }

        if (_source is not { } source) return;
        // Cleared first: releasing the capture below raises the capture event again.
        _source = null;
        Application.RemoveMessageFilter(this);
        source.MouseMove -= OnMouseMove;
        source.MouseUp -= OnMouseUp;
        source.MouseCaptureChanged -= OnCaptureChanged;
        source.Disposed -= OnSourceGone;
        source.HandleDestroyed -= OnSourceGone;
        Mark(null, -1);
        MarkAutoHide(null);
        _shownKind = null;
        if (!source.IsDisposed && source.IsHandleCreated && source.Capture) source.Capture = false;
        Cursor.Current = Cursors.Default;
    }

    /// <summary>Esc cancels the drag wherever the keyboard focus is, and is not passed on.</summary>
    bool IMessageFilter.PreFilterMessage(ref Message m)
    {
        if (_source is null || m.Msg != WmKeyDown || (int)m.WParam != VkEscape) return false;
        Cancel();
        return true;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (_source is { } source) Track(source.PointToScreen(e.Location));
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (_source is not { } source || e.Button != MouseButtons.Left) return;
        var screen = source.PointToScreen(e.Location);
        var drop = HitTest(screen);
        var id = _panel;
        var index = drop.Gap;
        // On its own docked strip the dragged tab is counted in the gaps but not in the order it moves within.
        if (drop.Kind == DropKind.Strip && drop.Strip == source && index > _sourceIndex) index--;
        Cancel();
        if (drop.Kind == DropKind.None || _host.IsDisposed || !_host.IsHandleCreated) return;

        // Applied after the mouse message: docking a floating panel closes the window this strip is in.
        _host.BeginInvoke(() =>
        {
            if (_host.IsDisposed || _host.ShuttingDown) return;
            switch (drop.Kind)
            {
                case DropKind.Strip: _host.MoveTo(id, drop.Area, index); break;
                case DropKind.AutoHide: _host.MoveTo(id, drop.Area); break;
                case DropKind.Float: _host.FloatPanel(id, screen); break;
            }
        });
    }

    private void OnCaptureChanged(object? sender, EventArgs e)
    {
        if (_source is { Capture: false }) Cancel();
    }

    private void OnSourceGone(object? sender, EventArgs e) => Cancel();

    private void Track(Point screen)
    {
        var drop = HitTest(screen);
        Mark(drop.Kind == DropKind.Strip ? drop.Strip : null, drop.Gap);
        MarkAutoHide(drop.Kind == DropKind.AutoHide ? drop.AutoHide : null);
        if (drop.Kind == _shownKind) return;
        _shownKind = drop.Kind;
        Cursor.Current = drop.Kind == DropKind.None ? Cursors.No : Cursors.SizeAll;
    }

    /// <summary>Moves the outline and marker to <paramref name="strip"/>, clearing the strip that had them.</summary>
    private void Mark(DarkTabControl? strip, int gap)
    {
        if (_marked is { } old && old != strip && !old.IsDisposed) old.SetDropTarget(false, -1);
        _marked = strip;
        if (strip is { IsDisposed: false }) strip.SetDropTarget(true, gap);
    }

    private void MarkAutoHide(AutoHideStrip? strip)
    {
        if (_markedAutoHide is { } old && old != strip && !old.IsDisposed) old.IsDropTarget = false;
        _markedAutoHide = strip;
        if (strip is { IsDisposed: false }) strip.IsDropTarget = true;
    }

    /// <summary>
    /// What is under a screen point. Floating windows come first, as they sit above the main window, and
    /// refuse tab drops: each holds one panel. Then the docked strips, then auto-hidden areas' strips.
    /// Elsewhere in the main window nothing happens; outside it and every floating window the panel floats.
    /// </summary>
    private Drop HitTest(Point screen)
    {
        var strips = _strips.Where(strip => !strip.IsDisposed && strip.IsHandleCreated).ToArray();
        foreach (var strip in strips)
            if (_host.DockedAreaOf(strip) is null && strip.FindForm() is { Visible: true } window && window != _window && window.Bounds.Contains(screen))
                return new Drop(DropKind.None);

        foreach (var strip in strips)
        {
            if (_host.DockedAreaOf(strip) is not { } area || !strip.Visible || strip.TabCount == 0) continue;
            if (strip.RectangleToScreen(strip.ClientRectangle).Contains(screen))
                return new Drop(DropKind.Strip, area, strip, GapAt(strip, strip.PointToClient(screen)));
        }

        foreach (var (area, autoHide) in _host.AutoHideTargets())
            if (autoHide.Visible && autoHide.RectangleToScreen(autoHide.ClientRectangle).Contains(screen))
                return new Drop(DropKind.AutoHide, area, AutoHide: autoHide);

        if (_host.FindForm() is { } main && main.Bounds.Contains(screen)) return new Drop(DropKind.None);
        return _host.CanFloat(_panel) ? new Drop(DropKind.Float) : new Drop(DropKind.None);
    }

    /// <summary>The gap nearest a point over the tabs, or the end for a point over the page below them.</summary>
    private static int GapAt(DarkTabControl strip, Point client)
    {
        if (client.Y >= strip.GetTabRect(0).Bottom) return strip.TabCount;
        for (var i = 0; i < strip.TabCount; i++)
        {
            var tab = strip.GetTabRect(i);
            if (client.X < tab.Left + tab.Width / 2) return i;
        }

        return strip.TabCount;
    }
}
