using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;

namespace Piper.App.Controls;

/// <summary>
/// The Composer's sent-request history: a searchable tree of requests grouped under collapsible
/// hosts, with repeat sends of one endpoint folded into a single counted row.
/// </summary>
/// <remarks>
/// Still a virtual, owner-drawn <see cref="ListView"/> rather than a <see cref="TreeView"/>: WinForms
/// groups do not collapse in virtual mode, so the tree is flattened to rows by
/// <see cref="ComposerHistoryView"/> and expanding is just a rebuild. That keeps the list virtual,
/// which a 2000-entry history needs, and keeps every layout decision in one paint routine.
/// </remarks>
public sealed class ComposerHistoryTree : UserControl
{
    /// <summary>Width of the expand/collapse triangle, and the per-level indent step.</summary>
    private const int GlyphWidth = 14;

    private readonly TextBox _searchBox;
    private readonly ListView _list;
    private readonly Label _count;

    private readonly SolidBrush _surfaceBrush = new(Palette.Surface);
    private readonly SolidBrush _groupBrush = new(Palette.SurfaceAlt);
    private readonly SolidBrush _selectionBrush = new(Palette.Selection);
    private readonly SolidBrush _glyphBrush = new(Palette.TextDim);

    // Shared by the search box's grammar examples (which need longer than the 5s default to read)
    // and the truncated-row tooltip, which simply stays up as long as the hover.
    private readonly ToolTip _toolTip = new() { AutoPopDelay = 30000 };
    private string? _toolTipText;

    private IReadOnlyList<Session> _history = [];
    private IReadOnlyList<ComposerRow> _rows = [];

    // Collapsed hosts persist; expanded repeat-rows deliberately do not. Folding a noisy host away
    // is a standing preference, but drilling into one endpoint's individual sends is a momentary
    // act that should not still be open days later.
    private readonly HashSet<string> _collapsedHosts;
    private readonly HashSet<string> _expandedRequests = new(StringComparer.Ordinal);

    // Build force-expands every host while a search is active, so the collapsed set and what the
    // pane shows disagree for as long as the query stands.
    private bool _searchActive;

    private Font? _measuredWith;
    private int _methodWidth;

    /// <summary>Raised when a row is double-clicked or activated with Enter.</summary>
    public event EventHandler<Session>? SessionActivated;

    /// <summary>Raised with every send behind the selected rows, not just the newest of each.</summary>
    public event EventHandler<IReadOnlyList<Session>>? RemoveRequested;

    public ComposerHistoryTree()
    {
        _collapsedHosts = new HashSet<string>(
            ComposerViewStateStore.Load().CollapsedHosts, StringComparer.OrdinalIgnoreCase);

        _searchBox = new TextBox
        {
            Dock = DockStyle.Top,
            Font = Palette.Mono,
            // Examples live in the tooltip below, not in a label under the box and not in this
            // placeholder: a dim label flush under the box reads as a query already typed in, and
            // this pane is narrow enough that a placeholder long enough to teach the grammar just
            // gets clipped. Help > Search syntax remains the full reference.
            PlaceholderText = Strings.Composer.SearchPlaceholder,
        };
        _searchBox.TextChanged += (_, _) => Rebuild();
        _searchBox.KeyDown += OnSearchKeyDown;
        _toolTip.SetToolTip(_searchBox, Strings.Composer.SearchTooltip.ReplaceLineEndings("\r\n"));

        _count = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 18,
            ForeColor = Palette.TextDim,
            Padding = new Padding(4, 2, 0, 0),
        };

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            VirtualMode = true,
            OwnerDraw = true,
            HideSelection = false,
            MultiSelect = true,
            // Grouping by host retires the Host column, and the remaining fields are laid out by
            // hand per row kind, so a header would label nothing.
            HeaderStyle = ColumnHeaderStyle.None,
            Font = Palette.Mono,
        };
        DarkListView.EnableDoubleBuffering(_list);
        _list.Columns.Add(string.Empty, 400, HorizontalAlignment.Left);
        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _list.DrawSubItem += OnDrawSubItem;
        _list.MouseDown += OnMouseDown;
        _list.MouseMove += OnListMouseMove;
        _list.MouseLeave += (_, _) => SetRowToolTip(null);
        _list.DoubleClick += OnDoubleClick;
        _list.KeyDown += OnListKeyDown;
        _list.Resize += (_, _) => FitColumn();
        _list.ContextMenuStrip = BuildMenu();

        var pane = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        pane.Controls.Add(_list);
        pane.Controls.Add(_count);
        pane.Controls.Add(_searchBox);

        var header = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = Strings.Composer.HistoryHeader,
            ForeColor = Palette.Text,
            Font = Palette.UiFontBold,
            Padding = new Padding(0, 5, 0, 0),
        };

        Dock = DockStyle.Fill;
        Controls.Add(pane);
        Controls.Add(header);
    }

    /// <summary>Points the pane at the Composer's history list and redraws.</summary>
    /// <remarks>The list is held by reference, so <see cref="Rebuild"/> alone picks up later edits.</remarks>
    public void SetHistory(IReadOnlyList<Session> history)
    {
        _history = history ?? [];
        Rebuild();
    }

    /// <summary>Puts focus in the search box; used by the Ctrl+K shortcut.</summary>
    public void FocusSearch()
    {
        _searchBox.Focus();
        _searchBox.SelectAll();
    }

    /// <summary>Re-flattens the tree from the current history, query, and expansion state.</summary>
    public void Rebuild()
    {
        var query = SearchQuery.Parse(_searchBox.Text);
        _searchBox.ForeColor = query.Warnings.Count > 0 ? Palette.StatusClientError : Palette.Text;

        _searchActive = !query.IsEmpty;
        var selected = SelectedKeys();
        _rows = ComposerHistoryView.Build(_history, query, _collapsedHosts, _expandedRequests);

        _list.BeginUpdate();
        _list.VirtualListSize = _rows.Count;
        _list.EndUpdate();
        Reselect(selected);
        // Expanding a group can bring the vertical scrollbar in, which changes the client width
        // without resizing the control, so Resize alone would leave the column too wide.
        FitColumn();
        _list.Invalidate();

        var hosts = 0;
        var sends = 0;
        foreach (var row in _rows)
        {
            if (row.Kind != ComposerRowKind.Host) continue;
            hosts++;
            sends += row.Count;
        }

        _count.Text = query.Warnings.Count > 0
            ? Strings.Composer.HistoryCountWithWarning(sends, query.Warnings[0])
            : Strings.Composer.HistoryCount(sends, hosts);
    }

    private void FitColumn()
    {
        if (_list.Columns.Count == 0) return;
        _list.Columns[0].Width = Math.Max(80, _list.ClientSize.Width - 2);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        FitColumn();
    }

    // ------------------------------------------------------------------ layout

    private static int IndentFor(ComposerRowKind kind) => kind switch
    {
        ComposerRowKind.Host => 2,
        ComposerRowKind.Request => 2 + GlyphWidth,
        _ => 2 + GlyphWidth * 2,
    };

    /// <summary>Whether this row has children to show, and so earns a triangle.</summary>
    private static bool IsExpandable(ComposerRow row) =>
        row.Kind == ComposerRowKind.Host || (row.Kind == ComposerRowKind.Request && row.Count > 1);

    /// <summary>Width reserved for the method badge, remeasured whenever the zoom level changes.</summary>
    private int MethodWidth()
    {
        if (ReferenceEquals(_measuredWith, Palette.Mono)) return _methodWidth;
        _measuredWith = Palette.Mono;
        _methodWidth = TextRenderer.MeasureText("OPTIONS ", _measuredWith, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        return _methodWidth;
    }

    private static string LabelFor(ComposerRow row) => row.Kind switch
    {
        ComposerRowKind.Host => row.Host,
        ComposerRowKind.Request => Strings.Composer.HistoryRowLabel(
            ComposerHistoryView.MethodOf(row.Session), ComposerHistoryView.TargetOf(row.Session)),
        _ => (row.Session.Completed ?? row.Session.Started).ToLocalTime().ToString("HH:mm:ss"),
    };

    // ----------------------------------------------------------------- painting

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        // The text is never painted from here -- OnDrawSubItem lays every row out by hand -- but it
        // is what accessibility tools and the truncation tooltip read.
        e.Item = e.ItemIndex >= 0 && e.ItemIndex < _rows.Count
            ? new ListViewItem(LabelFor(_rows[e.ItemIndex]))
            : new ListViewItem(string.Empty);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null || e.ItemIndex < 0 || e.ItemIndex >= _rows.Count) return;

        var row = _rows[e.ItemIndex];
        var selected = e.Item.Selected;
        var isHost = row.Kind == ComposerRowKind.Host;

        if (_surfaceBrush.Color != Palette.Surface) _surfaceBrush.Color = Palette.Surface;
        if (_groupBrush.Color != Palette.SurfaceAlt) _groupBrush.Color = Palette.SurfaceAlt;
        if (_selectionBrush.Color != Palette.Selection) _selectionBrush.Color = Palette.Selection;
        e.Graphics.FillRectangle(selected ? _selectionBrush : isHost ? _groupBrush : _surfaceBrush, e.Bounds);

        var indent = IndentFor(row.Kind);
        if (IsExpandable(row))
            DrawGlyph(e.Graphics, new Rectangle(e.Bounds.X + indent, e.Bounds.Y, GlyphWidth, e.Bounds.Height), row.Expanded);

        var left = e.Bounds.X + indent + GlyphWidth;
        var right = e.Bounds.Right - 4;

        // Right-hand fields first, so the label knows how much room is actually left for it.
        if (isHost)
        {
            right = DrawRight(e.Graphics, $"({row.Count:N0})", Palette.TextDim, e.Bounds, right);
        }
        else
        {
            var session = row.Session;
            right = DrawRight(e.Graphics, session.StatusText,
                selected ? Palette.Text : Palette.ForStatus(session), e.Bounds, right);
            if (row.Kind == ComposerRowKind.Request && row.Count > 1)
                right = DrawRight(e.Graphics, $"×{row.Count:N0}", Palette.TextDim, e.Bounds, right);
        }

        var available = right - left;
        if (available <= 0) return;

        if (row.Kind == ComposerRowKind.Request)
        {
            var method = ComposerHistoryView.MethodOf(row.Session);
            var methodWidth = Math.Min(MethodWidth(), available);
            TextRenderer.DrawText(e.Graphics, method, Palette.Mono,
                new Rectangle(left, e.Bounds.Y, methodWidth, e.Bounds.Height),
                selected ? Palette.Text : Palette.ForMethod(method),
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            left += methodWidth;
            available = right - left;
            if (available <= 0) return;
        }

        var text = row.Kind == ComposerRowKind.Request
            ? ComposerHistoryView.TargetOf(row.Session)
            : LabelFor(row);

        TextRenderer.DrawText(e.Graphics, text, isHost ? Palette.UiFontBold : Palette.Mono,
            new Rectangle(left, e.Bounds.Y, available, e.Bounds.Height),
            isHost || selected ? Palette.Text : Palette.TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// Draws the expand/collapse triangle as a polygon rather than a glyph so it cannot render as a
    /// missing-character box in whatever monospace font the user has, and scales with the row.
    /// </summary>
    private void DrawGlyph(Graphics graphics, Rectangle bounds, bool expanded)
    {
        if (_glyphBrush.Color != Palette.TextDim) _glyphBrush.Color = Palette.TextDim;

        var size = Math.Max(4, Math.Min(bounds.Width, bounds.Height) / 2);
        var centreX = bounds.X + bounds.Width / 2;
        var centreY = bounds.Y + bounds.Height / 2;
        var half = size / 2;

        Point[] points = expanded
            ? [
                new Point(centreX - half, centreY - half / 2),
                new Point(centreX + half, centreY - half / 2),
                new Point(centreX, centreY + half),
            ]
            : [
                new Point(centreX - half / 2, centreY - half),
                new Point(centreX + half, centreY),
                new Point(centreX - half / 2, centreY + half),
            ];

        var mode = graphics.SmoothingMode;
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.FillPolygon(_glyphBrush, points);
        graphics.SmoothingMode = mode;
    }

    /// <summary>Draws one right-aligned field and returns the new right edge for the next one.</summary>
    private static int DrawRight(Graphics graphics, string text, Color colour, Rectangle bounds, int right)
    {
        if (string.IsNullOrEmpty(text)) return right;

        var width = TextRenderer.MeasureText(graphics, text, Palette.Mono, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        TextRenderer.DrawText(graphics, text, Palette.Mono,
            new Rectangle(right - width, bounds.Y, width, bounds.Height), colour,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        return right - width - 8;
    }

    // ------------------------------------------------------------------- input

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        var item = _list.GetItemAt(e.X, e.Y);
        var index = item is not null && item.Index >= 0 && item.Index < _rows.Count ? item.Index : -1;
        if (index < 0) return;

        if (e.Button == MouseButtons.Right)
        {
            // Right-clicking inside an existing multi-selection keeps it, so "remove these" works.
            if (!_list.SelectedIndices.Contains(index))
            {
                _list.SelectedIndices.Clear();
                item!.Selected = true;
            }
            item!.Focused = true;
            return;
        }

        if (e.Button != MouseButtons.Left) return;

        // A click on the triangle toggles and nothing else, so folding a group away never also
        // changes the selection out from under the keyboard.
        var row = _rows[index];
        var indent = IndentFor(row.Kind);
        if (IsExpandable(row) && e.X >= indent && e.X < indent + GlyphWidth) Toggle(index);
    }

    private void OnDoubleClick(object? sender, EventArgs e)
    {
        var index = SelectedIndex();
        if (index < 0) return;

        var row = _rows[index];
        if (IsExpandable(row)) Toggle(index);
        else Activate(row);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.F)
        {
            FocusSearch();
            e.SuppressKeyPress = true;
            e.Handled = true;
            return;
        }

        var index = SelectedIndex();
        if (index < 0) return;
        var row = _rows[index];

        switch (e.KeyCode)
        {
            case Keys.Delete:
                RemoveSelected();
                e.Handled = true;
                return;

            case Keys.Enter:
                if (IsExpandable(row)) Toggle(index);
                else Activate(row);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;

            case Keys.Right when IsExpandable(row) && !row.Expanded:
                Toggle(index);
                e.Handled = true;
                return;

            case Keys.Left when IsExpandable(row) && row.Expanded:
                Toggle(index);
                e.Handled = true;
                return;

            case Keys.Left:
                // Already a leaf, or already folded: step out to the row that owns this one.
                SelectParent(index);
                e.Handled = true;
                return;
        }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        // Down/Enter from the search box moves into the rows without touching the mouse.
        if (_rows.Count == 0) return;

        if (e.KeyCode == Keys.Down)
        {
            _list.Focus();
            if (_list.SelectedIndices.Count == 0) Select(0);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            // Row 0 is always a host group, so "select the first row and activate it" would never
            // load anything. Enter here means "open the first request this query found".
            var index = FirstRequestIndex();
            if (index >= 0)
            {
                _list.Focus();
                _list.SelectedIndices.Clear();
                Select(index);
                Activate(_rows[index]);
            }

            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        var index = IndexAt(e.Location);
        if (index < 0)
        {
            SetRowToolTip(null);
            return;
        }

        var row = _rows[index];
        var label = LabelFor(row);
        // Approximate: the reserve matches what the right-hand fields typically take. It only
        // decides whether to offer the full text on hover, so being a few pixels out is harmless.
        var available = _list.Columns[0].Width - IndentFor(row.Kind) - GlyphWidth - 64;
        var width = TextRenderer.MeasureText(label, row.Kind == ComposerRowKind.Host ? Palette.UiFontBold : Palette.Mono,
            Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;

        SetRowToolTip(width > available ? label : null);
    }

    private void SetRowToolTip(string? text)
    {
        if (_toolTipText == text) return;
        _toolTipText = text;
        _toolTip.SetToolTip(_list, text);
    }

    // ------------------------------------------------------------------ actions

    private void Toggle(int index)
    {
        var row = _rows[index];

        // Keyed off what the row currently shows, not off set membership: a live search draws every
        // host expanded regardless of the collapsed set, and clicking then has to mean what it looks
        // like it means.
        if (row.Kind == ComposerRowKind.Host)
        {
            // Every host draws expanded during a search, so a click here would flip the persisted
            // state while the pane visibly did nothing -- and the user would only discover it after
            // clearing the query, or after a restart.
            if (_searchActive) return;

            if (row.Expanded) _collapsedHosts.Add(row.Key);
            else _collapsedHosts.Remove(row.Key);
            ComposerViewStateStore.Save(new ComposerViewState { CollapsedHosts = [.. _collapsedHosts] });
        }
        else if (row.Kind == ComposerRowKind.Request && row.Count > 1)
        {
            if (row.Expanded) _expandedRequests.Remove(row.Key);
            else _expandedRequests.Add(row.Key);
        }
        else
        {
            return;
        }

        Rebuild();
    }

    private void Activate(ComposerRow row) => SessionActivated?.Invoke(this, row.Session);

    private void SelectParent(int index)
    {
        // Captured before the loop: _rows is rebuilt underneath this control, and re-reading
        // _rows[index] inside the loop would compare against a row that may have moved.
        var parent = _rows[index].Kind;
        for (var i = index - 1; i >= 0; i--)
        {
            if (_rows[i].Kind >= parent) continue;
            _list.SelectedIndices.Clear();
            Select(i);
            return;
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var remove = Menus.Item(Strings.Composer.RemoveFromHistory, Strings.Shortcuts.Delete, (_, _) => RemoveSelected());
        menu.Items.Add(remove);
        menu.Opening += (_, _) => remove.Enabled = SelectedRows().Count > 0;
        return menu;
    }

    /// <summary>
    /// Removes every send behind the selected rows. A repeated request is one row standing for N
    /// sends, so dropping only the newest would leave a row that still looks the same.
    /// </summary>
    private void RemoveSelected()
    {
        var rows = SelectedRows();
        var doomed = new List<Session>();
        var seen = new HashSet<Session>();
        foreach (var row in rows)
        {
            foreach (var session in row.Sends)
                if (seen.Add(session)) doomed.Add(session);
        }

        if (doomed.Count == 0 || !ConfirmGroupRemoval(rows, doomed.Count)) return;
        RemoveRequested?.Invoke(this, doomed);
    }

    /// <summary>
    /// Asks before Del on a host row discards that whole group.
    /// </summary>
    /// <remarks>
    /// A repeat row says how many sends it stands for right next to the count, so removing it is
    /// what it looks like. A host row stands for every send under it, the removal rewrites
    /// composer-history.json immediately, and there is no undo -- and <see cref="SelectParent"/>
    /// deliberately parks focus on a host row when Left is pressed, so it is easy to be on one
    /// without having aimed at it.
    /// </remarks>
    private bool ConfirmGroupRemoval(List<ComposerRow> rows, int sends)
    {
        var hosts = rows.Where(row => row.Kind == ComposerRowKind.Host).ToList();
        if (hosts.Count == 0) return true;

        var what = hosts.Count == 1
            ? Strings.Composer.RemoveGroupOneHost(hosts[0].Host)
            : Strings.Composer.RemoveGroupManyHosts(hosts.Count);

        return MessageBox.Show(this,
            Strings.Composer.ConfirmRemoveGroup(what, sends),
            Strings.App.Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) == DialogResult.OK;
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The first row that actually stands for a request, or -1 when the tree holds none.</summary>
    private int FirstRequestIndex()
    {
        for (var i = 0; i < _rows.Count; i++)
            if (_rows[i].Kind != ComposerRowKind.Host) return i;

        return -1;
    }

    private int IndexAt(Point location)
    {
        var item = _list.GetItemAt(location.X, location.Y);
        return item is not null && item.Index >= 0 && item.Index < _rows.Count ? item.Index : -1;
    }

    /// <summary>
    /// The row a keystroke or double-click acts on.
    /// </summary>
    /// <remarks>
    /// Deliberately the focused row, not <c>SelectedIndices[0]</c>: that collection is ordered by
    /// index, so with rows 5 and 20 both selected it reports 5 however the selection was made, and
    /// acting on it would load a request the user never pointed at.
    /// </remarks>
    private int SelectedIndex()
    {
        if (_list.FocusedItem is { Index: var focused } && focused >= 0 && focused < _rows.Count
            && _list.SelectedIndices.Contains(focused))
        {
            return focused;
        }

        if (_list.SelectedIndices.Count == 0) return -1;
        var index = _list.SelectedIndices[0];
        return index >= 0 && index < _rows.Count ? index : -1;
    }

    /// <summary>
    /// Selects one row and moves the focus rectangle with it.
    /// </summary>
    /// <remarks>
    /// In virtual mode <c>SelectedIndices.Add</c> sets only the selected flag, leaving focus where
    /// it was, so the next arrow key would resume from the old row and the selection would appear
    /// to jump. Setting <see cref="ListViewItem.Focused"/> is legal here even though mutating the
    /// collection is not.
    /// </remarks>
    private void Select(int index)
    {
        if (index < 0 || index >= _rows.Count) return;

        _list.SelectedIndices.Add(index);
        _list.Items[index].Focused = true;
        _list.EnsureVisible(index);
    }

    private List<ComposerRow> SelectedRows() => _list.SelectedIndices.Cast<int>()
        .Where(index => index >= 0 && index < _rows.Count)
        .Select(index => _rows[index])
        .ToList();

    /// <summary>
    /// Row identities to restore after a rebuild. Rows are re-created from scratch every time, so
    /// a plain index would drift the selection whenever a group above it folds or unfolds.
    /// </summary>
    /// <remarks>
    /// A single send is remembered as that send, with its group as the fallback. Remembering only
    /// the group moved the selection from one send up to its "xN" row on the next rebuild, so
    /// Delete then removed every send of the request instead of the one the user picked.
    /// </remarks>
    private List<SelectedRow> SelectedKeys() => SelectedRows()
        .Select(row => row.Kind == ComposerRowKind.Send
            ? new SelectedRow(ComposerHistoryView.ExpandKey(row.Host, ComposerHistoryView.RequestKey(row.Session)), row.Session)
            : new SelectedRow(row.Key, null))
        .ToList();

    private void Reselect(List<SelectedRow> selection)
    {
        if (selection.Count == 0) return;

        var wantedSends = selection.Where(row => row.Send is not null).Select(row => row.Send!).ToHashSet();
        var visibleSends = _rows.Where(row => row.Kind == ComposerRowKind.Send && wantedSends.Contains(row.Session))
            .Select(row => row.Session)
            .ToHashSet();

        // A send that is no longer listed (its group was folded, or a search hides it) falls back
        // to the group row that now stands for it.
        var wantedKeys = selection
            .Where(row => row.Send is null || !visibleSends.Contains(row.Send))
            .Select(row => row.Key)
            .ToHashSet(StringComparer.Ordinal);

        _list.SelectedIndices.Clear();
        var focused = false;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var wanted = row.Kind == ComposerRowKind.Send
                ? visibleSends.Contains(row.Session)
                : wantedKeys.Contains(row.Key);
            if (!wanted) continue;

            _list.SelectedIndices.Add(i);
            if (focused) continue;
            _list.Items[i].Focused = true;
            focused = true;
        }
    }

    /// <summary>A selected row: its key, and for a single send, the send itself.</summary>
    private readonly record struct SelectedRow(string Key, Session? Send);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _toolTip.Dispose();
            _surfaceBrush.Dispose();
            _groupBrush.Dispose();
            _selectionBrush.Dispose();
            _glyphBrush.Dispose();
        }
        base.Dispose(disposing);
    }
}
