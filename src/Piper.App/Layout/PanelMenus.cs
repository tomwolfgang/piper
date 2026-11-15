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
    /// The tab menu: move to another area (the one the panel is in is greyed out) or hide it. Reset
    /// layout is deliberately absent, so it cannot be clicked by mistake next to Hide.
    /// </summary>
    public static ToolStripItem[] TabItems(PanelHost host, PanelId id, DockArea current)
    {
        ArgumentNullException.ThrowIfNull(host);
        return
        [
            MoveItem(host, id, DockArea.Left, Strings.Panels.MoveLeft, current),
            MoveItem(host, id, DockArea.Right, Strings.Panels.MoveRight, current),
            MoveItem(host, id, DockArea.Bottom, Strings.Panels.MoveBottom, current),
            new ToolStripMenuItem(Strings.Panels.Hide, null, (_, _) => host.HidePanel(id)) { Enabled = id != PanelId.Sessions },
        ];
    }

    private static ToolStripMenuItem MoveItem(PanelHost host, PanelId id, DockArea area, string text, DockArea current) =>
        new(text, null, (_, _) => host.MoveTo(id, area)) { Enabled = area != current };
}
