using System.Drawing;

namespace Piper.App.Layout;

/// <summary>The panels the main window can place. The order is the order of Ctrl+Alt+1..6.</summary>
internal enum PanelId { Sessions, Inspectors, Composer, Filters, AutoResponder, Log }

/// <summary>The three docking areas around the window.</summary>
internal enum DockArea { Left, Right, Bottom }

/// <summary>Whether a panel is being shown because the user asked for it or as a side effect of other activity.</summary>
internal enum PanelReveal { Explicit, Passive }

/// <summary>
/// Where every panel lives: docked in an area, floating in its own window, or hidden. Pure data with no
/// WinForms types, so every operation, and the repair of a layout read from a hostile file, is checked
/// in the smoke tests. A panel keeps the area it was last docked in while it floats or is hidden, so
/// docking or showing it again returns it there.
/// </summary>
internal sealed class PanelLayout
{
    public const double DefaultLeftFraction = 0.55;
    public const double DefaultBottomFraction = 0.35;
    public const double DefaultFlyoutFraction = 0.45;
    public const int MinFloatingWidth = 300;
    public const int MinFloatingHeight = 200;

    /// <summary>The top strip of a floating window that must stay on a screen so it can be dragged.</summary>
    private const int CaptionHeight = 32;
    private const int DefaultDpi = 96;
    private const int MinDpi = 48;
    private const int MaxDpi = 960;
    private const double MinFraction = 0.1;
    private const double MaxFraction = 0.9;

    private static readonly PanelId[] AllPanels = Enum.GetValues<PanelId>();
    private static readonly DockArea[] AllAreas = Enum.GetValues<DockArea>();

    private sealed class Entry
    {
        public DockArea Area;
        public int Order;
        public bool Hidden;
        public bool Floating;
        public Rectangle? Bounds;
        public int Dpi = DefaultDpi;
        public bool TopMost;
    }

    private sealed class AreaState
    {
        public PanelId? Selected;
        public bool AutoHidden;
        public double Flyout = DefaultFlyoutFraction;
    }

    private readonly Dictionary<PanelId, Entry> _entries = [];
    private readonly Dictionary<DockArea, AreaState> _areas = [];
    private double _leftFraction = DefaultLeftFraction;
    private double _bottomFraction = DefaultBottomFraction;

    private PanelLayout()
    {
        foreach (var area in AllAreas) _areas[area] = new AreaState();
    }

    /// <summary>The window as it has always been: sessions on the left, everything else tabbed on the right.</summary>
    public static PanelLayout Default()
    {
        var layout = new PanelLayout();
        var order = 0;
        foreach (var id in AllPanels)
        {
            var onLeft = id == PanelId.Sessions;
            layout._entries[id] = new Entry { Area = onLeft ? DockArea.Left : DockArea.Right, Order = onLeft ? 0 : order++ };
        }

        layout._areas[DockArea.Left].Selected = PanelId.Sessions;
        layout._areas[DockArea.Right].Selected = PanelId.Inspectors;
        return layout;
    }

    public PanelLayout Clone()
    {
        var clone = new PanelLayout { _leftFraction = _leftFraction, _bottomFraction = _bottomFraction };
        foreach (var (id, entry) in _entries)
            clone._entries[id] = new Entry
            {
                Area = entry.Area, Order = entry.Order, Hidden = entry.Hidden, Floating = entry.Floating,
                Bounds = entry.Bounds, Dpi = entry.Dpi, TopMost = entry.TopMost,
            };
        foreach (var (area, state) in _areas)
            clone._areas[area] = new AreaState { Selected = state.Selected, AutoHidden = state.AutoHidden, Flyout = state.Flyout };
        return clone;
    }

    // ---- placement -------------------------------------------------------------------------

    /// <summary>The area the panel was last docked in, also while it floats or is hidden.</summary>
    public DockArea AreaOf(PanelId id) => _entries[id].Area;

    public bool IsFloating(PanelId id) => _entries[id] is { Floating: true, Hidden: false };

    public bool IsHidden(PanelId id) => _entries[id].Hidden;

    /// <summary>The visible, docked panels of the area, in tab order.</summary>
    public IReadOnlyList<PanelId> DockedIn(DockArea area) =>
        AllPanels.Where(id => IsDocked(id, area)).OrderBy(id => _entries[id].Order).ThenBy(id => (int)id).ToArray();

    public PanelId? SelectedIn(DockArea area) => _areas[area].Selected;

    /// <summary>Docks the panel into the area at the index (the end when null or too large), shows it and selects it.</summary>
    public void Move(PanelId id, DockArea area, int? index = null)
    {
        var entry = _entries[id];
        var oldArea = entry.Area;
        entry.Floating = false;
        entry.Hidden = false;
        entry.Bounds = null;

        var order = DockedIn(area).Where(other => other != id).ToList();
        order.Insert(Math.Clamp(index ?? order.Count, 0, order.Count), id);
        entry.Area = area;
        for (var i = 0; i < order.Count; i++) _entries[order[i]].Order = i;

        _areas[area].Selected = id;
        // The sessions list must stay on screen, so an auto-hidden area stops hiding when it lands there.
        if (id == PanelId.Sessions) _areas[area].AutoHidden = false;
        Renumber(oldArea);
        EnsureSelection(oldArea);
    }

    public void Float(PanelId id, Rectangle bounds, int dpi)
    {
        var entry = _entries[id];
        entry.Floating = true;
        entry.Hidden = false;
        entry.Bounds = bounds;
        entry.Dpi = dpi;
        Renumber(entry.Area);
        EnsureSelection(entry.Area);
    }

    /// <summary>Returns a floating or hidden panel to the area it last lived in, at the end, selected.</summary>
    public void Dock(PanelId id) => Move(id, _entries[id].Area);

    /// <summary>Hides the panel; the sessions list cannot be hidden.</summary>
    public bool Hide(PanelId id)
    {
        if (id == PanelId.Sessions) return false;
        var entry = _entries[id];
        entry.Hidden = true;
        Renumber(entry.Area);
        EnsureSelection(entry.Area);
        return true;
    }

    /// <summary>
    /// Shows a hidden panel where it was: floating if it was floating, else back at its old place in
    /// its area's tab order. A hidden panel keeps the order it had when it was hidden, because
    /// renumbering an area only touches the panels docked there.
    /// </summary>
    public void Show(PanelId id)
    {
        var entry = _entries[id];
        if (!entry.Hidden) return;
        entry.Hidden = false;
        if (entry.Floating) return;
        Move(id, entry.Area, entry.Order);
    }

    /// <summary>The panel's place in its area's tab order; for a hidden panel, the place it will return to.</summary>
    public int OrderOf(PanelId id) => _entries[id].Order;

    /// <summary>Makes a docked, visible panel the selected tab of its area; anything else is ignored.</summary>
    public void Select(PanelId id)
    {
        var entry = _entries[id];
        if (IsDocked(id, entry.Area)) _areas[entry.Area].Selected = id;
    }

    // ---- floating extras -------------------------------------------------------------------

    public Rectangle? FloatingBounds(PanelId id) => _entries[id].Floating ? _entries[id].Bounds : null;

    public int FloatingDpi(PanelId id) => _entries[id].Dpi;

    public bool TopMost(PanelId id) => _entries[id].TopMost;

    public void SetTopMost(PanelId id, bool value) => _entries[id].TopMost = value;

    // ---- areas -----------------------------------------------------------------------------

    /// <summary>The area holding the sessions list must stay on screen, so it cannot auto-hide.</summary>
    public bool CanAutoHide(DockArea area) => !IsDocked(PanelId.Sessions, area);

    public bool IsAutoHidden(DockArea area) => _areas[area].AutoHidden;

    public bool SetAutoHidden(DockArea area, bool value)
    {
        if (value && !CanAutoHide(area)) return false;
        _areas[area].AutoHidden = value;
        return true;
    }

    public double LeftFraction
    {
        get => _leftFraction;
        set => _leftFraction = ClampFraction(value, DefaultLeftFraction);
    }

    public double BottomFraction
    {
        get => _bottomFraction;
        set => _bottomFraction = ClampFraction(value, DefaultBottomFraction);
    }

    /// <summary>
    /// The narrowest a side flyout may be while it shows the panel, in pixels at 96 DPI and 100% zoom,
    /// so the panel's own controls stay usable. The caller scales it and never exceeds the window.
    /// </summary>
    public static int MinFlyoutWidth(PanelId id) => id switch
    {
        PanelId.Composer => 560,
        PanelId.AutoResponder => 460,
        PanelId.Inspectors => 420,
        PanelId.Filters => 380,
        _ => 300,
    };

    /// <summary>
    /// The least height, in pixels at 96 DPI and 100% zoom, the Bottom area's page needs to stay usable
    /// with these panels in it: the Inspectors and the Composer need room for their editors.
    /// </summary>
    public static int MinBottomHeight(IEnumerable<PanelId> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        var height = 0;
        foreach (var id in panels) height = Math.Max(height, id is PanelId.Inspectors or PanelId.Composer ? 260 : 120);
        return height;
    }

    public double FlyoutFraction(DockArea area) => _areas[area].Flyout;

    public void SetFlyoutFraction(DockArea area, double value) =>
        _areas[area].Flyout = ClampFraction(value, DefaultFlyoutFraction);

    /// <summary>NaN and infinity become the fallback; everything else is held to 0.1..0.9.</summary>
    public static double ClampFraction(double value, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, MinFraction, MaxFraction) : fallback;

    // ---- repair ----------------------------------------------------------------------------

    /// <summary>
    /// Sets one panel's stored state verbatim, for rebuilding a layout read from disk. Nothing is
    /// validated here: the caller must run <see cref="Normalise"/> before the layout is shown.
    /// </summary>
    internal void Restore(PanelId id, DockArea area, int order, bool hidden, bool selected, Rectangle? floating, int dpi, bool topMost)
    {
        _entries[id] = new Entry
        {
            Area = area, Order = order, Hidden = hidden, Floating = floating is not null,
            Bounds = floating, Dpi = dpi, TopMost = topMost,
        };
        if (selected && floating is null && !hidden && Enum.IsDefined(area)) _areas[area].Selected = id;
    }

    /// <summary>
    /// Makes the layout safe to show: sessions visible and its area never auto-hidden, dense tab order,
    /// a valid selection per area, every visible panel docked or floating, and every floating window
    /// reachable on one of <paramref name="workAreas"/> (docked instead when there are none).
    /// </summary>
    public void Normalise(IReadOnlyList<Rectangle> workAreas)
    {
        var usable = workAreas.Where(area => area.Width > 0 && area.Height > 0).ToArray();

        _entries[PanelId.Sessions].Hidden = false;
        _areas[_entries[PanelId.Sessions].Area].AutoHidden = false;

        foreach (var entry in _entries.Values)
        {
            if (!Enum.IsDefined(entry.Area)) entry.Area = DockArea.Right;
            entry.Dpi = entry.Dpi <= 0 ? DefaultDpi : Math.Clamp(entry.Dpi, MinDpi, MaxDpi);
            if (!entry.Floating) continue;

            if (usable.Length == 0)
            {
                entry.Floating = false;
                entry.Bounds = null;
            }
            else
            {
                entry.Bounds = Reachable(entry.Bounds, usable);
            }
        }

        foreach (var area in AllAreas)
        {
            Renumber(area);
            EnsureSelection(area);
        }
    }

    private static Rectangle Reachable(Rectangle? requested, Rectangle[] workAreas)
    {
        var bounds = requested ?? new Rectangle(workAreas[0].Location, new Size(MinFloatingWidth + 100, MinFloatingHeight + 100));
        var largest = workAreas.MaxBy(area => (long)area.Width * area.Height);

        var width = Math.Min(Math.Max(bounds.Width, MinFloatingWidth), largest.Width);
        var height = Math.Min(Math.Max(bounds.Height, MinFloatingHeight), largest.Height);
        var x = (long)bounds.X;
        var y = (long)bounds.Y;

        if (!workAreas.Any(area => IntersectsCaption(x, y, width, area)))
        {
            // The window cannot be grabbed: bring it fully onto the screen nearest its centre.
            var centreX = x + width / 2;
            var centreY = y + height / 2;
            var target = workAreas.MinBy(area => Distance(centreX, centreY, area));
            width = Math.Min(width, target.Width);
            height = Math.Min(height, target.Height);
            x = Math.Clamp(x, target.Left, (long)target.Right - width);
            y = Math.Clamp(y, target.Top, (long)target.Bottom - height);
        }

        return new Rectangle((int)Math.Clamp(x, int.MinValue, int.MaxValue), (int)Math.Clamp(y, int.MinValue, int.MaxValue), width, height);
    }

    private static bool IntersectsCaption(long x, long y, long width, Rectangle area) =>
        x < area.Right && x + width > area.Left && y < area.Bottom && y + CaptionHeight > area.Top;

    private static long Distance(long x, long y, Rectangle area)
    {
        var dx = Math.Max(Math.Max(area.Left - x, x - (area.Right - 1)), 0);
        var dy = Math.Max(Math.Max(area.Top - y, y - (area.Bottom - 1)), 0);
        return dx * dx + dy * dy;
    }

    // ---- helpers ---------------------------------------------------------------------------

    private bool IsDocked(PanelId id, DockArea area) => _entries[id] is { Floating: false, Hidden: false } entry && entry.Area == area;

    private void Renumber(DockArea area)
    {
        var order = DockedIn(area);
        for (var i = 0; i < order.Count; i++) _entries[order[i]].Order = i;
    }

    /// <summary>
    /// Keeps the area's selection on one of its docked panels. An area left with none stops auto-hiding,
    /// so a panel moved into it later is docked and shown rather than tucked behind its strip.
    /// </summary>
    private void EnsureSelection(DockArea area)
    {
        var docked = DockedIn(area);
        var state = _areas[area];
        if (docked.Count == 0) state.AutoHidden = false;
        if (state.Selected is not { } selected || !docked.Contains(selected))
            state.Selected = docked.Count > 0 ? docked[0] : null;
    }
}
