using System.ComponentModel;
using System.Windows.Forms;
using Piper.App.Controls;
using Piper.App.Theme;

namespace Piper.App.Layout;

/// <summary>
/// Renders a <see cref="PanelLayout"/> onto the main window: an outer horizontal split with the Left and
/// Right areas side by side above and the Bottom area below, each area a <see cref="DarkTabControl"/>. It
/// is the only way the window reaches its panels, so a panel can move without its callers knowing where
/// it is. An empty area collapses its side of the split.
/// </summary>
internal sealed class PanelHost : UserControl
{
    private const int SplitterWidth = 4;
    private static readonly PanelId[] AllPanels = Enum.GetValues<PanelId>();
    private static readonly DockArea[] AllAreas = Enum.GetValues<DockArea>();

    private readonly PanelRegistry _registry;
    private readonly SplitContainer _outer;
    private readonly SplitContainer _inner;
    private readonly Dictionary<DockArea, DarkTabControl> _strips = [];
    private readonly HashSet<PanelId> _checked = [];
    private readonly ContextMenuStrip _tabMenu = new();
    private readonly Dictionary<PanelId, FloatingPanelForm> _floating = [];
    private PanelLayout _layout;
    private EventHandler<DarkTabControl>? _stripCreated;
    private Func<Keys, bool> _processShortcut = _ => false;
    private KeyEventHandler _shortcutKeyDown = (_, _) => { };
    private bool _shuttingDown;

    /// <summary>True until the layout's fractions have been applied to a host with a real size.</summary>
    private bool _fractionsPending = true;

    /// <summary>True while pages are being placed, so the strips' selection events are not taken as the user's.</summary>
    private bool _arranging;

    public PanelHost(PanelRegistry registry, PanelLayout layout)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(layout);
        _registry = registry;
        _layout = layout.Clone();

        foreach (var area in AllAreas) _strips[area] = NewStrip();
        _inner = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = SplitterWidth };
        _inner.Panel1.Controls.Add(_strips[DockArea.Left]);
        _inner.Panel2.Controls.Add(_strips[DockArea.Right]);
        _outer = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = SplitterWidth };
        _outer.Panel1.Controls.Add(_inner);
        _outer.Panel2.Controls.Add(_strips[DockArea.Bottom]);
        Controls.Add(_outer);

        Arrange();
        _inner.SplitterMoved += OnSplitterMoved;
        _outer.SplitterMoved += OnSplitterMoved;
    }

    /// <summary>
    /// Raised for every strip that can show a panel, so the window can attach its session drop handling.
    /// The docked strips exist before anyone can subscribe, so a new subscriber is handed each of them at once.
    /// </summary>
    public event EventHandler<DarkTabControl>? StripCreated
    {
        add
        {
            _stripCreated += value;
            if (value is null) return;
            foreach (var area in AllAreas) value(this, _strips[area]);
            foreach (var form in _floating.Values) value(this, form.Strip);
        }
        remove => _stripCreated -= value;
    }

    /// <summary>Raised when the user changes something a <see cref="Snapshot"/> would record.</summary>
    public event EventHandler? LayoutChanged;

    /// <summary>
    /// Brings a panel into view without taking the keyboard focus. A hidden panel is shown again only for
    /// an <see cref="PanelReveal.Explicit"/> reveal; a <see cref="PanelReveal.Passive"/> one leaves it hidden.
    /// </summary>
    public void ShowPanel(PanelId id, PanelReveal reveal)
    {
        if (_layout.IsHidden(id))
        {
            if (reveal == PanelReveal.Passive) return;
            Rearrange(() => _layout.Show(id));
            // A theme toggle or zoom while it was hidden may have missed it.
            Retheme(id);
        }

        var page = _registry.PageOf(id);
        // SelectWithoutFocus, not SelectedTab: the grid must keep the focus while its selection is shown.
        if (page.Parent is DarkTabControl strip) strip.SelectWithoutFocus(strip.TabPages.IndexOf(page));

        // Brought forward, never activated: the grid keeps the focus here too.
        if (_floating.TryGetValue(id, out var window)) window.Reveal();
    }

    /// <summary>Shows the panel and moves the keyboard focus into it, activating its window if it floats.</summary>
    public void FocusPanel(PanelId id)
    {
        ShowPanel(id, PanelReveal.Explicit);
        if (_floating.TryGetValue(id, out var window))
        {
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            window.Activate();
        }

        _registry.ContentOf(id).Focus();
    }

    public PanelId? SelectedPanel(DockArea area) => _layout.SelectedIn(area);

    public bool IsHidden(PanelId id) => _layout.IsHidden(id);

    public bool IsFloating(PanelId id) => _layout.IsFloating(id);

    public bool IsTopMost(PanelId id) => _layout.TopMost(id);

    /// <summary>
    /// Every panel can float. The movable-panels spike found the inspector's WebView2 survives being
    /// moved into another window and back with the same core and handle, and the WebP and video views
    /// harden any core they have not seen, so floating weakens none of the lock-down.
    /// </summary>
    public bool CanFloat(PanelId id) => Enum.IsDefined(id);

    /// <summary>
    /// Set from the moment the main window starts closing: floating windows closed after that hand their
    /// pages back without docking, so the layout saved just before keeps them floating.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShuttingDown
    {
        get => _shuttingDown;
        set
        {
            _shuttingDown = value;
            foreach (var window in _floating.Values)
            {
                window.ShuttingDown = value;
                // Only the main window is disabled while the proxy is restored; its shortcuts must not
                // run from a floating window meanwhile.
                window.Enabled = !value;
            }
            // A shutdown that failed leaves Piper running: bring back any window closed meanwhile.
            if (!value) OpenFloatingWindows();
        }
    }

    /// <summary>Hands floating windows the main window's keyboard shortcuts: its dispatcher and its <c>KeyDown</c> handler.</summary>
    public void RouteKeys(Func<Keys, bool> processShortcut, KeyEventHandler keyDown)
    {
        ArgumentNullException.ThrowIfNull(processShortcut);
        ArgumentNullException.ThrowIfNull(keyDown);
        _processShortcut = processShortcut;
        _shortcutKeyDown = keyDown;
    }

    /// <summary>
    /// Moves the panel into a window of its own, at <paramref name="screenLocation"/> or where its page
    /// is now, the size of that page. A panel already floating is brought forward instead.
    /// </summary>
    public void FloatPanel(PanelId id, Point? screenLocation = null)
    {
        if (!CanFloat(id)) return;
        if (_layout.IsFloating(id))
        {
            ShowPanel(id, PanelReveal.Explicit);
            return;
        }

        var wasHidden = _layout.IsHidden(id);
        var page = _registry.PageOf(id);
        var docked = page.Parent is not null && page.IsHandleCreated;
        var location = screenLocation ?? (docked ? page.PointToScreen(Point.Empty) : Cursor.Position);
        var size = docked ? page.Size : Size.Empty;
        var bounds = new Rectangle(location, new Size(
            Math.Max(size.Width, PanelLayout.MinFloatingWidth), Math.Max(size.Height, PanelLayout.MinFloatingHeight)));
        Rearrange(() =>
        {
            _layout.Float(id, bounds, DeviceDpi);
            // Keeps the new window's caption on some screen, so it can always be dragged back.
            _layout.Normalise(Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray());
        });
        if (wasHidden) Retheme(id);
    }

    /// <summary>Puts a floating panel back at the end of the area it came from, selected, and closes its window.</summary>
    public void DockPanel(PanelId id)
    {
        if (!_layout.IsFloating(id)) return;
        Rearrange(() => _layout.Dock(id));
        Retheme(id);
    }

    /// <summary>Keeps a floating panel's window above every other window, or stops doing so.</summary>
    public void SetTopMost(PanelId id, bool value)
    {
        _layout.SetTopMost(id, value);
        if (_floating.TryGetValue(id, out var window)) window.TopMost = value;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens a window for every panel the layout has floating. Called once the main window is on
    /// screen, so the owned windows never appear before their owner.
    /// </summary>
    public void ShowFloatingWindows() => OpenFloatingWindows();

    /// <summary>The window a floating panel is shown in, or null; for tests.</summary>
    internal FloatingPanelForm? FloatingWindow(PanelId id) => _floating.GetValueOrDefault(id);

    /// <summary>Docks the panel at the end of the area and selects it there; a hidden panel is shown.</summary>
    public void MoveTo(PanelId id, DockArea area)
    {
        var wasHidden = _layout.IsHidden(id);
        if (!wasHidden && !_layout.IsFloating(id) && _layout.AreaOf(id) == area) return;
        Rearrange(() => _layout.Move(id, area));
        if (wasHidden) Retheme(id);
    }

    /// <summary>Hides the panel; the sessions list cannot be hidden. Focus inside it goes back to the sessions list.</summary>
    public void HidePanel(PanelId id)
    {
        if (id == PanelId.Sessions || _layout.IsHidden(id)) return;
        var hadFocus = _registry.ContentOf(id).ContainsFocus;
        Rearrange(() => _layout.Hide(id));
        if (hadFocus) _registry.ContentOf(PanelId.Sessions).Focus();
    }

    /// <summary>Puts every panel back where <see cref="PanelLayout.Default"/> has it, shown, with the default splits.</summary>
    public void ResetLayout()
    {
        var hidden = AllPanels.Where(_layout.IsHidden).ToArray();
        Rearrange(() => _layout = PanelLayout.Default());
        foreach (var id in hidden) Retheme(id);
    }

    /// <summary>
    /// Re-applies the palette to every panel, wherever it is. The window's own walk only reaches the
    /// panels docked in it, so a hidden one would otherwise keep the old theme and font size.
    /// </summary>
    public void ApplyTheme()
    {
        // The window first: its font is what the panel inside it inherits.
        foreach (var window in _floating.Values)
        {
            Palette.Apply(window);
            window.Invalidate(true);
        }

        foreach (var id in AllPanels) Retheme(id);
        Palette.Apply(_tabMenu);
    }

    /// <summary>The tab menu's items for a panel, built fresh each time it opens.</summary>
    internal ToolStripItem[] TabMenuItems(PanelId id) =>
        PanelMenus.TabItems(this, id, _layout.IsFloating(id) ? null : _layout.AreaOf(id));

    /// <summary>The menu shown for a tab, exposed so tests can see and close it.</summary>
    internal ContextMenuStrip TabMenu => _tabMenu;

    /// <summary>Marks the panel's tab with a check glyph, wherever the panel is shown.</summary>
    public void SetTabChecked(PanelId id, bool value)
    {
        if (value) _checked.Add(id);
        else _checked.Remove(id);
        var page = _registry.PageOf(id);
        if (page.Parent is DarkTabControl strip) strip.SetTabChecked(page, value);
    }

    /// <summary>A copy of the layout as it is on screen now, including where the user left the splitters.</summary>
    public PanelLayout Snapshot()
    {
        CaptureFractions();
        CaptureFloatingBounds();
        return _layout.Clone();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // Fractions only mean something once the host has its real size; before that the splits are
        // the default 150 px and every distance would be clamped. Applied once, so the user's
        // splitter is never pulled back; SplitContainer keeps the proportions on later resizes.
        if (!_fractionsPending || !IsHandleCreated || Width <= 0 || Height <= 0) return;
        _fractionsPending = false;
        if (BothShown(_outer)) SetDistance(_outer, _outer.Height, BottomDistance());
        if (BothShown(_inner)) SetDistance(_inner, _inner.Width, LeftDistance());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Each window hands its page back as it goes; the registry disposes the panels.
            foreach (var window in _floating.Values.ToArray()) window.Dispose();
            _floating.Clear();
            _tabMenu.Dispose();
        }

        base.Dispose(disposing);
    }

    // Rounded, not truncated: truncating moved the split a pixel on every restart.
    private int LeftDistance() => (int)Math.Round(_layout.LeftFraction * _inner.Width);

    private int BottomDistance() => _outer.Height - (int)Math.Round(_layout.BottomFraction * _outer.Height) - _outer.SplitterWidth;

    private DarkTabControl NewStrip()
    {
        var strip = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        strip.SelectedIndexChanged += OnStripSelectionChanged;
        strip.TabMenuRequested += OnTabMenuRequested;
        return strip;
    }

    /// <summary>Applies a change to the layout and shows the result, keeping the user's splitters.</summary>
    private void Rearrange(Action change)
    {
        CaptureFractions();
        CaptureFloatingBounds();
        // Moving the grid can recreate its handles, which loses the selection and the scroll position.
        var grid = _registry.ContentOf(PanelId.Sessions) as SessionListView;
        var gridPage = _registry.PageOf(PanelId.Sessions);
        var gridParent = gridPage.Parent;
        var gridState = grid?.CaptureViewState();

        change();
        CloseStaleWindows();
        Arrange();
        OpenFloatingWindows();
        _fractionsPending = true;
        PerformLayout();
        if (grid is not null && gridState is { } state && gridPage.Parent != gridParent) grid.RestoreViewState(state);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the window of every panel the layout no longer has floating; the page goes back first.</summary>
    private void CloseStaleWindows()
    {
        foreach (var (id, window) in _floating.Where(pair => !_layout.IsFloating(pair.Key)).ToArray())
        {
            _floating.Remove(id);
            window.CloseWithoutDocking();
        }
    }

    /// <summary>Opens a window for every floating panel that has none, once the main window is on screen.</summary>
    private void OpenFloatingWindows()
    {
        if (FindForm() is not { Visible: true } owner) return;
        foreach (var id in AllPanels.Where(id => _layout.IsFloating(id) && !_floating.ContainsKey(id)))
        {
            var page = _registry.PageOf(id);
            var window = new FloatingPanelForm(owner, page, _processShortcut, _shortcutKeyDown)
            {
                ShuttingDown = _shuttingDown,
                Enabled = !_shuttingDown,
                TopMost = _layout.TopMost(id),
            };
            if (_layout.FloatingBounds(id) is { } bounds) window.Bounds = bounds;
            window.Strip.SetTabChecked(page, _checked.Contains(id));
            window.Strip.TabMenuRequested += OnTabMenuRequested;
            window.DockRequested += (_, _) => DockPanel(id);
            window.FormClosed += (_, _) =>
            {
                // The owner closing, or a shutdown, ends the window without a dock; forget it.
                if (_floating.TryGetValue(id, out var current) && current == window) _floating.Remove(id);
            };
            _floating[id] = window;
            _stripCreated?.Invoke(this, window.Strip);
            window.Show();
        }
    }

    /// <summary>Reads where the user left each floating window back into the layout.</summary>
    private void CaptureFloatingBounds()
    {
        foreach (var (id, window) in _floating)
        {
            if (window.IsDisposed) continue;
            var bounds = window.WindowState == FormWindowState.Normal ? window.Bounds : window.RestoreBounds;
            _layout.Float(id, bounds, window.DeviceDpi);
        }
    }

    private void Retheme(PanelId id)
    {
        var page = _registry.PageOf(id);
        Palette.Apply(page);
        page.Invalidate(true);
    }

    private void OnTabMenuRequested(object? sender, int index)
    {
        if (sender is not DarkTabControl strip || index < 0 || index >= strip.TabCount
            || _registry.IdOf(strip.TabPages[index]) is not { } id)
            return;

        // The previous items are disposed only now, never from their own click handler.
        foreach (var old in _tabMenu.Items.Cast<ToolStripItem>().ToArray()) old.Dispose();
        _tabMenu.Items.AddRange(TabMenuItems(id));
        _tabMenu.Font = Palette.UiFont;
        Palette.Apply(_tabMenu);

        // At the pointer for a right-click; under the tab for the keyboard.
        var tab = strip.GetTabRect(index);
        var pointer = strip.PointToClient(Cursor.Position);
        _tabMenu.Show(strip, tab.Contains(pointer) ? pointer : new Point(tab.Left, tab.Bottom));
    }

    /// <summary>Puts every docked panel's page into its area's strip, in order, selected and checked as the layout says.</summary>
    private void Arrange()
    {
        _arranging = true;
        try
        {
            foreach (var area in AllAreas)
            {
                var strip = _strips[area];
                var pages = _layout.DockedIn(area).Select(_registry.PageOf).ToArray();
                if (!strip.TabPages.Cast<TabPage>().SequenceEqual(pages))
                {
                    strip.TabPages.Clear();
                    strip.TabPages.AddRange(pages);
                }

                foreach (var id in AllPanels)
                {
                    var page = _registry.PageOf(id);
                    strip.SetTabChecked(page, _checked.Contains(id) && page.Parent == strip);
                }

                if (_layout.SelectedIn(area) is { } selected)
                    strip.SelectWithoutFocus(strip.TabPages.IndexOf(_registry.PageOf(selected)));
            }

            var left = _strips[DockArea.Left].TabCount > 0;
            var right = _strips[DockArea.Right].TabCount > 0;
            ShowSides(_inner, left, right);
            ShowSides(_outer, left || right, _strips[DockArea.Bottom].TabCount > 0);
        }
        finally
        {
            _arranging = false;
        }
    }

    /// <summary>Collapses the empty side of a split. Only one side can collapse, so with both empty the second stays.</summary>
    private static void ShowSides(SplitContainer split, bool first, bool second)
    {
        split.Panel1Collapsed = !first;
        if (first) split.Panel2Collapsed = !second;
    }

    private static bool BothShown(SplitContainer split) => !split.Panel1Collapsed && !split.Panel2Collapsed;

    /// <summary>Sets a splitter within both panels' minimum sizes, or leaves it where it is when they cannot both fit.</summary>
    private static void SetDistance(SplitContainer split, int extent, int distance)
    {
        var max = extent - split.Panel2MinSize - split.SplitterWidth;
        if (max < split.Panel1MinSize) return;
        split.SplitterDistance = Math.Clamp(distance, split.Panel1MinSize, max);
    }

    /// <summary>
    /// Reads the splitters back into the layout, once they have been set from it. A split that is not
    /// shown at a real size says nothing about where the user wants it: a minimised window, or a split
    /// too small for its two panels, has had its splitter clamped, so the known fractions are kept. A
    /// splitter still within a pixel of where its fraction puts it keeps the exact fraction, so pixel
    /// rounding never creeps into the saved layout.
    /// </summary>
    private void CaptureFractions()
    {
        if (_fractionsPending || FindForm() is { WindowState: FormWindowState.Minimized }) return;
        if (BothShown(_inner) && Fits(_inner, _inner.Width) && Math.Abs(_inner.SplitterDistance - LeftDistance()) > 1)
            _layout.LeftFraction = (double)_inner.SplitterDistance / _inner.Width;
        if (BothShown(_outer) && Fits(_outer, _outer.Height) && Math.Abs(_outer.SplitterDistance - BottomDistance()) > 1)
            _layout.BottomFraction = (double)(_outer.Height - _outer.SplitterDistance - _outer.SplitterWidth) / _outer.Height;
    }

    private static bool Fits(SplitContainer split, int extent) =>
        extent >= split.Panel1MinSize + split.Panel2MinSize + split.SplitterWidth;

    private void OnStripSelectionChanged(object? sender, EventArgs e)
    {
        if (_arranging || sender is not DarkTabControl { SelectedTab: { } page } || _registry.IdOf(page) is not { } id) return;
        _layout.Select(id);
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnSplitterMoved(object? sender, SplitterEventArgs e)
    {
        if (_fractionsPending) return;
        LayoutChanged?.Invoke(this, EventArgs.Empty);
    }
}
