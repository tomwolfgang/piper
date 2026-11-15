using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;

namespace Piper.App.Layout;

/// <summary>
/// Lets a session dragged from the grid land on the Composer or AutoResponder tab, in whichever strip
/// shows it. Every other tab refuses the drop. Attached to each strip the panel host creates.
/// </summary>
internal sealed class SessionDrop
{
    private readonly PanelRegistry _registry;
    private readonly Action<PanelId, Session> _dropped;

    /// <param name="dropped">Called with the panel that took the session: Composer or AutoResponder.</param>
    public SessionDrop(PanelRegistry registry, Action<PanelId, Session> dropped)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(dropped);
        _registry = registry;
        _dropped = dropped;
    }

    public void Attach(DarkTabControl strip)
    {
        strip.AllowDrop = true;
        strip.DragEnter += OnDragOver;
        strip.DragOver += OnDragOver;
        strip.DragDrop += OnDragDrop;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (!HasCapturedRequest(e.Data) || sender is not DarkTabControl strip || Target(strip, e) is not { } target)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        // Selecting on hover makes the target clear even when it was not the active tab.
        strip.SelectedTab = target;
        e.Effect = DragDropEffects.Copy;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (sender is not DarkTabControl strip
            || Target(strip, e) is not { } target
            || e.Data?.GetData(typeof(Session)) is not Session session
            || session.Request is null
            || _registry.IdOf(target) is not { } id)
            return;

        strip.SelectedTab = target;
        _dropped(id, session);
    }

    private static bool HasCapturedRequest(IDataObject? data) =>
        data?.GetData(typeof(Session)) is Session { Request: not null };

    /// <summary>The tab a dragged session would land on, or null where a drop means nothing.</summary>
    private TabPage? Target(DarkTabControl strip, DragEventArgs e)
    {
        var point = strip.PointToClient(new Point(e.X, e.Y));
        var index = strip.TabIndexAt(point);
        if (index >= 0)
        {
            var tab = strip.TabPages[index];
            return AcceptsSessions(tab) ? tab : null;
        }

        // Once hover has activated a tab that takes sessions, keep accepting the drop on its page too.
        var stripBottom = strip.TabCount > 0 ? strip.GetTabRect(0).Bottom : 0;
        return point.Y >= stripBottom && AcceptsSessions(strip.SelectedTab) ? strip.SelectedTab : null;
    }

    private bool AcceptsSessions(TabPage? tab) =>
        tab is not null && _registry.IdOf(tab) is PanelId.Composer or PanelId.AutoResponder;
}
