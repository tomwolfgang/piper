using System.Windows.Forms;

namespace Piper.App.Layout;

/// <summary>
/// Owns the six panels and the <see cref="TabPage"/> each one lives in, wherever that page is shown: in
/// an area's strip or, once a panel is hidden, in none. Because a hidden panel is outside every control
/// tree, the window's own disposal cannot reach it, so the registry disposes each panel itself, once.
/// </summary>
internal sealed class PanelRegistry : IDisposable
{
    private static readonly PanelId[] AllPanels = Enum.GetValues<PanelId>();

    private readonly Dictionary<PanelId, Control> _contents = [];
    private readonly Dictionary<PanelId, TabPage> _pages = [];
    private readonly Dictionary<TabPage, PanelId> _ids = [];
    private bool _disposed;

    /// <summary>Takes ownership of one content control per panel; every <see cref="PanelId"/> must be present.</summary>
    public PanelRegistry(IReadOnlyDictionary<PanelId, (Control Content, string Title)> panels)
    {
        ArgumentNullException.ThrowIfNull(panels);
        foreach (var id in AllPanels)
        {
            var (content, title) = panels[id];
            ArgumentNullException.ThrowIfNull(content);
            var page = NewPage(title, content);
            _contents[id] = content;
            _pages[id] = page;
            _ids[page] = id;
        }
    }

    public TabPage PageOf(PanelId id) => _pages[id];

    public PanelId? IdOf(TabPage page) => _ids.TryGetValue(page, out var id) ? id : null;

    public Control ContentOf(PanelId id) => _contents[id];

    /// <summary>All six panels' content, wherever they are.</summary>
    public IEnumerable<Control> Contents => AllPanels.Select(id => _contents[id]);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The window usually got there first; IsDisposed keeps every control to a single Dispose.
        foreach (var id in AllPanels)
        {
            if (!_contents[id].IsDisposed) _contents[id].Dispose();
            if (!_pages[id].IsDisposed) _pages[id].Dispose();
        }
    }

    private static TabPage NewPage(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }
}
