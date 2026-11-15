using System.Windows.Forms;

namespace Piper.App.Layout;

/// <summary>The menus that place panels: View > Panels and the menu on a panel's tab.</summary>
internal static class PanelMenus
{
    private static readonly PanelId[] AllPanels = Enum.GetValues<PanelId>();

    /// <summary>The panel's name in View > Panels.</summary>
    public static string Title(PanelId id) => id switch
    {
        PanelId.Sessions => Strings.Panels.Sessions,
        PanelId.Inspectors => Strings.Tabs.Inspectors,
        PanelId.Composer => Strings.Tabs.Composer,
        PanelId.Filters => Strings.Tabs.Filters,
        PanelId.AutoResponder => Strings.Tabs.AutoResponder,
        PanelId.Log => Strings.Tabs.Log,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    /// <summary>
    /// View > Panels: a check item per panel showing its Ctrl+Alt+n focus shortcut. Ticking one shows
    /// and focuses the panel; unticking hides it. Sessions cannot be hidden, so its item stays ticked
    /// and greyed out.
    /// </summary>
    public static ToolStripMenuItem Panels(PanelHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        var menu = new ToolStripMenuItem(Strings.Panels.Title);
        var items = new Dictionary<PanelId, ToolStripMenuItem>();
        foreach (var id in AllPanels)
        {
            var item = Menus.Item(Title(id), Strings.Shortcuts.FocusPanel((int)id + 1), (_, _) =>
            {
                if (host.IsHidden(id)) host.FocusPanel(id);
                else host.HidePanel(id);
            });
            item.Enabled = id != PanelId.Sessions;
            items[id] = item;
            menu.DropDownItems.Add(item);
        }

        menu.DropDownOpening += (_, _) =>
        {
            foreach (var (id, item) in items) item.Checked = !host.IsHidden(id);
        };
        return menu;
    }

    /// <summary>
    /// The panel a Ctrl+Alt+1..6 focus shortcut names, or null. AltGr arrives as Ctrl+Alt, so in a
    /// text box the chord types a character on many layouts (AZERTY's <c>~ # { [ |</c>) and is left
    /// to the box; everywhere else it focuses the panel.
    /// </summary>
    public static PanelId? FocusChordTarget(KeyEventArgs e, bool textInputFocused)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (textInputFocused || !e.Control || !e.Alt || e.KeyCode is < Keys.D1 or > Keys.D6) return null;
        return (PanelId)(e.KeyCode - Keys.D1);
    }

    /// <summary>
    /// The tab menu: move to another area (the one the panel is docked in is greyed out; <paramref name="current"/>
    /// is null for a floating panel), Float or Dock, Auto-hide area (docked only, and never for the area
    /// holding the sessions list), Always on top (floating only), or hide it. Reset
    /// layout is deliberately absent, so it cannot be clicked by mistake next to Hide.
    /// </summary>
    public static ToolStripItem[] TabItems(PanelHost host, PanelId id, DockArea? current)
    {
        ArgumentNullException.ThrowIfNull(host);
        var floating = host.IsFloating(id);
        return
        [
            MoveItem(host, id, DockArea.Left, Strings.Panels.MoveLeft, current),
            MoveItem(host, id, DockArea.Right, Strings.Panels.MoveRight, current),
            MoveItem(host, id, DockArea.Bottom, Strings.Panels.MoveBottom, current),
            new ToolStripSeparator(),
            floating
                ? new ToolStripMenuItem(Strings.Panels.Dock, null, (_, _) => host.DockPanel(id))
                : new ToolStripMenuItem(Strings.Panels.Float, null, (_, _) => host.FloatPanel(id)) { Enabled = host.CanFloat(id) },
            AutoHideItem(host, current),
            new ToolStripMenuItem(Strings.Panels.TopMost, null, (_, _) => host.SetTopMost(id, !host.IsTopMost(id)))
            {
                Enabled = floating,
                Checked = floating && host.IsTopMost(id),
            },
            new ToolStripSeparator(),
            new ToolStripMenuItem(Strings.Panels.Hide, null, (_, _) => host.HidePanel(id)) { Enabled = id != PanelId.Sessions },
        ];
    }

    /// <summary>
    /// The menu under a flyout header's panels button: one item per panel in the area, the one showing
    /// checked. Choosing one opens it in the flyout with the focus.
    /// </summary>
    public static ToolStripItem[] AreaItems(PanelHost host, IEnumerable<PanelId> panels, PanelId? open)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(panels);
        return panels
            .Select(id => (ToolStripItem)new ToolStripMenuItem(Title(id), null, (_, _) => host.FocusPanel(id)) { Checked = id == open })
            .ToArray();
    }

    /// <summary>Checked while the panel's area is auto-hidden; greyed out for a floating panel and for the sessions area.</summary>
    private static ToolStripMenuItem AutoHideItem(PanelHost host, DockArea? current) =>
        current is { } area
            ? new(Strings.Panels.AutoHide, null, (_, _) => host.SetAutoHidden(area, !host.IsAutoHidden(area)))
            {
                Enabled = host.CanAutoHide(area),
                Checked = host.IsAutoHidden(area),
            }
            : new(Strings.Panels.AutoHide) { Enabled = false };

    private static ToolStripMenuItem MoveItem(PanelHost host, PanelId id, DockArea area, string text, DockArea? current) =>
        new(text, null, (_, _) => host.MoveTo(id, area)) { Enabled = area != current };
}
