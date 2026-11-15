using System.Windows.Forms;
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
    private readonly PanelLayout _layout;
    private readonly SplitContainer _outer;
    private readonly SplitContainer _inner;
    private readonly Dictionary<DockArea, DarkTabControl> _strips = [];
    private readonly HashSet<PanelId> _checked = [];
    private EventHandler<DarkTabControl>? _stripCreated;

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
            CaptureFractions();
            _layout.Show(id);
            Arrange();
            _fractionsPending = true;
            PerformLayout();
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        var page = _registry.PageOf(id);
        // SelectWithoutFocus, not SelectedTab: the grid must keep the focus while its selection is shown.
        if (page.Parent is DarkTabControl strip) strip.SelectWithoutFocus(strip.TabPages.IndexOf(page));
    }

    /// <summary>Shows the panel and moves the keyboard focus into it.</summary>
    public void FocusPanel(PanelId id)
    {
        ShowPanel(id, PanelReveal.Explicit);
        _registry.ContentOf(id).Focus();
    }

    public PanelId? SelectedPanel(DockArea area) => _layout.SelectedIn(area);

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
        if (BothShown(_outer))
            SetDistance(_outer, _outer.Height, _outer.Height - (int)(_layout.BottomFraction * _outer.Height) - _outer.SplitterWidth);
        if (BothShown(_inner))
            SetDistance(_inner, _inner.Width, (int)(_layout.LeftFraction * _inner.Width));
    }

    private DarkTabControl NewStrip()
    {
        var strip = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        strip.SelectedIndexChanged += OnStripSelectionChanged;
        return strip;
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

    /// <summary>Reads the splitters back into the layout, once they have been set from it.</summary>
    private void CaptureFractions()
    {
        if (_fractionsPending) return;
        if (BothShown(_inner) && _inner.Width > 0)
            _layout.LeftFraction = (double)_inner.SplitterDistance / _inner.Width;
        if (BothShown(_outer) && _outer.Height > 0)
            _layout.BottomFraction = (double)(_outer.Height - _outer.SplitterDistance - _outer.SplitterWidth) / _outer.Height;
    }

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
