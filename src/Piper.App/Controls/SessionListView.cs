using System.ComponentModel;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;

namespace Piper.App.Controls;

/// <summary>
/// Virtual-mode grid of captured sessions with a live query filter.
/// </summary>
/// <remarks>
/// Virtual mode matters here: a busy browser produces thousands of sessions a minute, and
/// materialising a ListViewItem per session would stall the UI thread. Rows are rendered
/// on demand from a filtered snapshot.
/// </remarks>
public sealed class SessionListView : UserControl
{
    // Keep the compact fields stable while using surplus space for values that tend to be long.
    // The order matches the real columns; a zero means the column stays at its minimum width.
    private static readonly int[] ColumnMinimumWidths = [52, 55, 62, 170, 300, 130, 110, 80, 70];
    private static readonly int[] ColumnGrowthWeights = [0, 0, 0, 3, 6, 2, 2, 0, 0];

    private readonly ListView _list;
    private readonly TextBox _filterBox;
    private readonly SessionStore _store;
    private readonly SolidBrush _surfaceBrush = new(Palette.Surface);
    private readonly SolidBrush _selectionBrush = new(Palette.Selection);
    private readonly SolidBrush _headerBrush = new(Palette.SurfaceAlt);
    private readonly Pen _headerBorderPen = new(Palette.Border);

    private readonly List<Session> _visible = new(1024);
    private SearchQuery _query = SearchQuery.Empty;
    private Func<Session, bool>? _visibilityFilter;
    private bool _autoScroll = true;

    public event EventHandler<Session?>? SelectionChanged;

    /// <summary>Raised whenever the capture-list selection set changes.</summary>
    public event EventHandler? SelectedSessionsChanged;

    /// <summary>Raised when the user asks to send a session to the Composer.</summary>
    public event EventHandler<Session>? SendToComposerRequested;

    /// <summary>Raised to turn a captured session into an AutoResponder rule that replays it.</summary>
    public event EventHandler<Session>? SendToAutoResponderRequested;

    /// <summary>Raised when the user asks to replay a captured request immediately.</summary>
    public event EventHandler<Session>? ResendRequested;

    /// <summary>Raised when the user double-clicks a row, asking to look at it in the inspector.</summary>
    public event EventHandler<Session>? SessionActivated;

    /// <summary>
    /// Raised with a host the user asked to stop seeing. The grid deliberately does not know how
    /// hiding is stored, so the form routes this into the Filters tab's persisted Hosts list.
    /// </summary>
    public event EventHandler<string>? HideHostRequested;

    public SessionListView(SessionStore store)
    {
        _store = store;

        _filterBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Filter  e.g.  status:4xx host:api  -is:image  body:\"order id\"",
            Font = Palette.Mono,
        };
        _filterBox.TextChanged += (_, _) => ApplyFilter();

        var filterRow = new Panel { Dock = DockStyle.Top, Height = 26, Padding = new Padding(2) };
        filterRow.Controls.Add(_filterBox);

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            VirtualMode = true,
            FullRowSelect = true,
            GridLines = false,
            HideSelection = false,
            MultiSelect = true,
            OwnerDraw = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        DarkListView.EnableDoubleBuffering(_list);

        _list.Columns.Add("#", 52, HorizontalAlignment.Right);
        _list.Columns.Add("Result", 55, HorizontalAlignment.Left);
        _list.Columns.Add("Method", 62, HorizontalAlignment.Left);
        _list.Columns.Add("Host", 170, HorizontalAlignment.Left);
        _list.Columns.Add("Path", 300, HorizontalAlignment.Left);
        _list.Columns.Add("Type", 130, HorizontalAlignment.Left);
        _list.Columns.Add("Process", 110, HorizontalAlignment.Left);
        _list.Columns.Add("Size", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Time", 70, HorizontalAlignment.Right);
        DarkListView.AddFillerColumn(_list);
        _list.Resize += (_, _) => ExpandColumnsToView();

        _list.RetrieveVirtualItem += OnRetrieveVirtualItem;
        _list.DrawColumnHeader += OnDrawColumnHeader;
        _list.DrawSubItem += OnDrawSubItem;
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.MouseDown += OnListMouseDown;
        _list.MouseMove += OnListMouseMove;
        _list.MouseUp += (_, _) => _dragSession = null;
        _list.KeyDown += OnListKeyDown;
        _list.DoubleClick += (_, _) =>
        {
            if (SelectedSession is { } session) SessionActivated?.Invoke(this, session);
        };

        BuildContextMenu();

        Controls.Add(_list);
        Controls.Add(filterRow);

        ExpandColumnsToView();

        _store.SessionAdded += (_, _) => RequestRefresh();
        _store.SessionUpdated += (_, _) => RequestRefresh();
        _store.Cleared += (_, _) => RequestRefresh();

        // Coalesce refreshes: the proxy can add hundreds of sessions a second and each
        // one arrives on a background thread.
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _refreshTimer.Tick += (_, _) =>
        {
            if (!_refreshPending) return;
            _refreshPending = false;
            Rebuild();
        };
        _refreshTimer.Start();
    }

    private readonly System.Windows.Forms.Timer _refreshTimer;
    private volatile bool _refreshPending;
    private bool _suppressSelectionChanged;
    private bool _expandingColumns;
    private Session? _dragSession;
    private Point _dragStart;
    private Session? _primarySelectedSession;

    /// <summary>
    /// The first session selected by the user. It remains in the inspectors while the user adds
    /// more rows to the capture selection.
    /// </summary>
    public Session? SelectedSession => _primarySelectedSession;

    public int SelectedSessionCount => _list.SelectedIndices.Count;

    public IReadOnlyList<Session> SelectedSessions
    {
        get
        {
            var result = new List<Session>(_list.SelectedIndices.Count);
            foreach (int index in _list.SelectedIndices)
                if (index < _visible.Count) result.Add(_visible[index]);
            return result;
        }
    }

    /// <summary>Master switch for keeping the newest session in view as rows arrive. Named to
    /// avoid colliding with <see cref="ScrollableControl.AutoScroll"/>. Even when true, a
    /// refresh only scrolls to the newest row if the view was already showing it -- see
    /// <see cref="IsScrolledToBottom"/> -- so scrolling up to review earlier rows pauses
    /// following until you scroll back down to the bottom yourself.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool FollowTail
    {
        get => _autoScroll;
        set => _autoScroll = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string FilterText
    {
        get => _filterBox.Text;
        set => _filterBox.Text = value;
    }

    /// <summary>Replays the selected non-tunnel request, if it is safe to do so.</summary>
    public bool ResendSelected()
    {
        if (SelectedSession is not { IsTunnel: false, Request: not null } session) return false;
        ResendRequested?.Invoke(this, session);
        return true;
    }

    /// <summary>
    /// An optional UI-level visibility filter applied in addition to the search box. The status
    /// bar's process scope uses this so switching scopes never discards captured sessions.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Session, bool>? VisibilityFilter
    {
        get => _visibilityFilter;
        set
        {
            _visibilityFilter = value;
            Rebuild();
        }
    }

    private void RequestRefresh() => _refreshPending = true;

    /// <summary>
    /// Uses extra horizontal room for the columns where request details are most likely to be
    /// truncated. At narrower widths the original minimum widths are retained, so the grid still
    /// has the usual horizontal scrolling behavior instead of crushing the compact fields.
    /// </summary>
    private void ExpandColumnsToView()
    {
        if (_expandingColumns || _list.ClientSize.Width <= 0) return;

        _expandingColumns = true;
        try
        {
            var minimumTotal = ColumnMinimumWidths.Sum();
            var remainingExtra = Math.Max(0, _list.ClientSize.Width - minimumTotal);
            var remainingWeight = ColumnGrowthWeights.Sum();

            for (var index = 0; index < ColumnMinimumWidths.Length; index++)
            {
                var weight = ColumnGrowthWeights[index];
                var extra = weight == 0 ? 0 : remainingExtra * weight / remainingWeight;
                _list.Columns[index].Width = ColumnMinimumWidths[index] + extra;
                remainingExtra -= extra;
                remainingWeight -= weight;
            }
        }
        finally
        {
            _expandingColumns = false;
        }
    }

    private void ApplyFilter()
    {
        _query = SearchQuery.Parse(_filterBox.Text);
        _filterBox.ForeColor = _query.Warnings.Count > 0 ? Palette.StatusClientError : Palette.Text;
        Rebuild();
    }

    private void Rebuild()
    {
        var previousSession = SelectedSession;
        var previousSelectionCount = _list.SelectedIndices.Count;
        HashSet<int>? previousIds = null;
        if (previousSelectionCount > 0)
        {
            previousIds = new HashSet<int>(previousSelectionCount);
            foreach (int index in _list.SelectedIndices)
                if (index >= 0 && index < _visible.Count) previousIds.Add(_visible[index].Id);
        }
        // Captured against the *old* _visible/VirtualListSize, before either changes below --
        // this is "was the user already looking at the bottom", independent of how many new
        // rows are about to arrive.
        var wasAtBottom = IsScrolledToBottom();
        _store.CopyTo(_visible);
        ApplyVisibilityFiltersInPlace();

        _list.BeginUpdate();
        try
        {
            _suppressSelectionChanged = true;
            var previousCount = _list.VirtualListSize;
            _list.VirtualListSize = _visible.Count;

            // A virtual ListView keeps its scroll offset when the row count changes underneath it.
            // Clearing the grid, or typing a filter that matches far fewer rows, leaves the view
            // parked past the last row: the rows are there, but nothing paints until a click happens
            // to scroll it back into range. Pull it to the top first whenever the old offset can no
            // longer be meaningful, then let the rules below decide where to leave it.
            if (_visible.Count > 0 && (_visible.Count < previousCount || previousCount == 0))
                _list.EnsureVisible(0);

            if (previousIds is { Count: > 0 })
            {
                _list.SelectedIndices.Clear();
                for (var index = 0; index < _visible.Count; index++)
                    if (previousIds.Contains(_visible[index].Id)) _list.SelectedIndices.Add(index);
            }
            else if (_autoScroll && wasAtBottom && _visible.Count > 0)
            {
                _list.EnsureVisible(_visible.Count - 1);
            }
        }
        finally
        {
            _list.EndUpdate();
            _suppressSelectionChanged = false;
        }

        if (previousSession is not null && IsSelected(previousSession))
            _primarySelectedSession = previousSession;
        else
            _primarySelectedSession = FirstSelectedSession();

        // Reassigning SelectedIndices to keep virtual rows selected causes WinForms to raise
        // transient selection events. Do not blank and immediately rebuild the inspector for
        // that bookkeeping operation; notify only if its primary session truly changed.
        if (!ReferenceEquals(previousSession, SelectedSession))
            SelectionChanged?.Invoke(this, SelectedSession);
        if (previousSelectionCount != _list.SelectedIndices.Count)
            SelectedSessionsChanged?.Invoke(this, EventArgs.Empty);

        _list.Invalidate();
    }

    private void ApplyVisibilityFiltersInPlace()
    {
        if (_query.IsEmpty && _visibilityFilter is null) return;

        var writeIndex = 0;
        for (var readIndex = 0; readIndex < _visible.Count; readIndex++)
        {
            var session = _visible[readIndex];
            if (!_query.IsEmpty && !_query.Matches(session)) continue;
            if (_visibilityFilter is not null && !_visibilityFilter(session)) continue;
            _visible[writeIndex++] = session;
        }

        if (writeIndex < _visible.Count)
            _visible.RemoveRange(writeIndex, _visible.Count - writeIndex);
    }

    /// <summary>True when the last row is already fully visible (or there's nothing to show
    /// yet) -- i.e. the user hasn't scrolled up to review earlier rows. Checked before a
    /// refresh adds new rows underneath whatever is currently on screen, so scrolling up never
    /// gets yanked back down, and scrolling back to the last row on your own resumes following.</summary>
    private bool IsScrolledToBottom()
    {
        if (_visible.Count == 0 || !_list.IsHandleCreated) return true;
        var lastRowBottom = _list.GetItemRect(_visible.Count - 1).Bottom;
        return lastRowBottom <= _list.ClientSize.Height;
    }

    private void OnRetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _visible.Count)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        var session = _visible[e.ItemIndex];
        var item = new ListViewItem(session.Id.ToString());
        item.SubItems.Add(session.StatusText);
        item.SubItems.Add(session.IsTunnel ? "CONNECT" : session.Method);
        item.SubItems.Add(session.Host);
        item.SubItems.Add(session.Path + session.Query);
        item.SubItems.Add(Format.ShortContentType(session.ContentType));
        item.SubItems.Add(string.IsNullOrEmpty(session.ProcessName) ? "-" : session.ProcessName);
        item.SubItems.Add(Format.Size(session.ResponseSize));
        item.SubItems.Add(session.Completed is null ? "..." : $"{session.Duration.TotalMilliseconds:N0} ms");
        item.SubItems.Add(string.Empty); // filler column
        item.Tag = session;
        e.Item = item;
    }

    private void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        if (_headerBrush.Color != Palette.SurfaceAlt) _headerBrush.Color = Palette.SurfaceAlt;
        if (_headerBorderPen.Color != Palette.Border) _headerBorderPen.Color = Palette.Border;
        e.Graphics.FillRectangle(_headerBrush, e.Bounds);
        e.Graphics.DrawLine(_headerBorderPen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, Palette.UiFont,
            Rectangle.Inflate(e.Bounds, -6, 0), Palette.TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null) return;
        var selected = e.Item.Selected;
        var session = e.Item.Tag as Session;

        if (_surfaceBrush.Color != Palette.Surface) _surfaceBrush.Color = Palette.Surface;
        if (_selectionBrush.Color != Palette.Selection) _selectionBrush.Color = Palette.Selection;
        e.Graphics.FillRectangle(selected ? _selectionBrush : _surfaceBrush, e.Bounds);

        var colour = session is null
            ? Palette.Text
            : Palette.ForStatus(session);

        if (selected) colour = Palette.Text;

        // The status column keeps its outcome colour even when the row is selected.
        if (selected && e.ColumnIndex == 1 && session is not null)
            colour = Palette.ForStatus(session);

        var alignment = e.Header?.TextAlign ?? HorizontalAlignment.Left;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | alignment switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };

        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, Palette.Mono,
            Rectangle.Inflate(e.Bounds, -5, 0), colour, flags);
    }

    private void OnListMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            var leftHit = _list.HitTest(e.Location);
            _dragSession = leftHit.Item?.Tag as Session;
            _dragStart = e.Location;
            return;
        }

        if (e.Button == MouseButtons.Right)
        {
            var rightHit = _list.HitTest(e.Location);
            // Preserve a multi-row selection when its member is right-clicked, so a context-menu
            // operation applies to the rows the user deliberately selected. An outside row starts
            // a new, one-row selection as expected.
            if (rightHit.Item is not null && !rightHit.Item.Selected)
            {
                SelectOnly(rightHit.Item);
            }
            return;
        }

        // Selecting with the middle button is not a thing; treat it as "send to composer".
        if (e.Button != MouseButtons.Middle) return;
        var hit = _list.HitTest(e.Location);
        if (hit.Item is not null && hit.Item.Tag is Session session)
            SendToComposerRequested?.Invoke(this, session);
    }

    private void OnListMouseMove(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _dragSession?.Request is null) return;

        var dragSize = SystemInformation.DragSize;
        var dragBounds = new Rectangle(
            _dragStart.X - dragSize.Width / 2,
            _dragStart.Y - dragSize.Height / 2,
            dragSize.Width,
            dragSize.Height);
        if (dragBounds.Contains(e.Location)) return;

        var session = _dragSession;
        _dragSession = null;
        _list.DoDragDrop(session, DragDropEffects.Copy);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.C)
        {
            CopyUrls();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.E)
        {
            if (SelectedSession is { } session) SendToComposerRequested?.Invoke(this, session);
            e.Handled = true;
        }
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };

        var resend = new ToolStripMenuItem("&Resend request\tCtrl+R", null, (_, _) => ResendSelected());
        menu.Items.Add(resend);
        menu.Items.Add("Send to &Composer\tCtrl+E", null, (_, _) =>
        {
            if (SelectedSession is { } session) SendToComposerRequested?.Invoke(this, session);
        });
        var autoResponder = menu.Items.Add("Create AutoResponder r&ule", null, (_, _) =>
        {
            if (SelectedSession is { } session) SendToAutoResponderRequested?.Invoke(this, session);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Copy &URL\tCtrl+C", null, (_, _) => CopyUrls());
        menu.Items.Add("Copy as c&url", null, (_, _) => CopyAsCurl());
        menu.Items.Add("Copy full &session", null, (_, _) => CopyFullSession());
        var save = new ToolStripMenuItem("&Save");
        var saveResponseBody = save.DropDownItems.Add("Response &body only...", null, (_, _) => SaveResponseBody());
        var saveSessionsAsSaz = save.DropDownItems.Add("Selected sessions as &SAZ...", null, (_, _) => SaveSelectedSessionsAsSaz());
        menu.Items.Add(save);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Filter to this &host", null, (_, _) =>
        {
            if (SelectedSession is { } session) FilterText = $"host:{session.Host}";
        });
        menu.Items.Add("&Hide this host", null, (_, _) =>
        {
            if (SelectedSession is { Host.Length: > 0 } session) HideHostRequested?.Invoke(this, session.Host);
        });
        menu.Items.Add(new ToolStripSeparator());
        var textWizard = new ToolStripMenuItem("Send URL to Text&Wizard", null,
            (_, _) => TextWizardDialog.Open(FindForm(), SelectedSession?.Url));
        menu.Items.Add(textWizard);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("&Remove selected\tDel", null, (_, _) => RemoveSelected());
        menu.Opening += (_, _) =>
        {
            textWizard.Enabled = SelectedSession is { Url.Length: > 0 };
            saveResponseBody.Enabled = SelectedSession?.Response is not null;
            saveSessionsAsSaz.Enabled = SelectedSessions.Any(session => session.Request is not null);
            save.Enabled = saveResponseBody.Enabled || saveSessionsAsSaz.Enabled;
            resend.Enabled = SelectedSession is { IsTunnel: false, Request: not null };
            autoResponder.Enabled = SelectedSession is { IsTunnel: false, Request.Url: not null };
        };

        _list.ContextMenuStrip = menu;
    }

    private void CopyUrls()
    {
        var urls = SelectedSessions.Select(s => s.Url).Where(u => u.Length > 0).ToArray();
        if (urls.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, urls));
    }

    private void CopyFullSession()
    {
        if (SelectedSession is not { } session) return;
        var sb = new System.Text.StringBuilder();
        if (session.Request is not null)
        {
            sb.Append(session.Request.HeadAsText());
            if (session.Request.Body.Length > 0) sb.AppendLine(session.Request.BodyAsText());
        }
        sb.AppendLine().AppendLine(new string('-', 60)).AppendLine();
        if (session.Response is not null)
        {
            sb.Append(session.Response.HeadAsText());
            if (session.Response.Body.Length > 0) sb.AppendLine(session.Response.BodyAsText());
        }
        Clipboard.SetText(sb.ToString());
    }

    private void SaveResponseBody()
    {
        if (SelectedSession is not { } session || session.Response is not { } response) return;

        using var dialog = new SaveFileDialog
        {
            Title = "Save response body",
            Filter = "All files (*.*)|*.*",
            FileName = SuggestedResponseFileName(session, response),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            // Preserve the response body exactly as it was captured, including a Content-Encoding
            // such as gzip. This makes the saved file faithfully match the response on the wire.
            File.WriteAllBytes(dialog.FileName, response.Body);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the response body: {ex.Message}",
                "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Prompts for a Fiddler SAZ file and saves the current capture selection.</summary>
    public bool SaveSelectedSessionsAsSaz()
    {
        var sessions = SelectedSessions.Where(session => session.Request is not null).ToArray();
        if (sessions.Length == 0) return false;

        using var dialog = new SaveFileDialog
        {
            Title = "Save selected sessions as Fiddler SAZ",
            Filter = "Fiddler SAZ captures (*.saz)|*.saz|All files (*.*)|*.*",
            DefaultExt = "saz",
            AddExtension = true,
            FileName = $"piper-capture-{DateTime.Now:yyyyMMdd-HHmmss}.saz",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;

        try
        {
            SazExporter.Export(dialog.FileName, sessions);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the selected sessions: {ex.Message}",
                "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private void OnSelectionChanged()
    {
        if (_suppressSelectionChanged) return;

        var previousSession = _primarySelectedSession;
        if (previousSession is null || !IsSelected(previousSession))
            _primarySelectedSession = FirstSelectedSession();

        if (!ReferenceEquals(previousSession, _primarySelectedSession))
            SelectionChanged?.Invoke(this, _primarySelectedSession);
        SelectedSessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsSelected(Session session)
    {
        foreach (int index in _list.SelectedIndices)
            if (index >= 0 && index < _visible.Count && ReferenceEquals(_visible[index], session))
                return true;
        return false;
    }

    private Session? FirstSelectedSession()
    {
        foreach (int index in _list.SelectedIndices)
            if (index >= 0 && index < _visible.Count)
                return _visible[index];
        return null;
    }

    private void SelectOnly(ListViewItem item)
    {
        var previousSession = SelectedSession;
        _suppressSelectionChanged = true;
        try
        {
            _list.SelectedIndices.Clear();
            item.Selected = true;
            item.Focused = true;
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        _primarySelectedSession = item.Tag as Session;
        if (!ReferenceEquals(previousSession, SelectedSession))
            SelectionChanged?.Invoke(this, SelectedSession);
        SelectedSessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string SuggestedResponseFileName(Session session, Piper.Core.Http.HttpResponseData response)
    {
        var disposition = response.Headers["Content-Disposition"];
        if (!string.IsNullOrWhiteSpace(disposition))
        {
            foreach (var part in disposition.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=');
                if (separator <= 0) continue;

                var name = part[..separator].Trim();
                if (!name.Equals("filename", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("filename*", StringComparison.OrdinalIgnoreCase)) continue;

                var value = part[(separator + 1)..].Trim().Trim('"');
                var charsetMarker = value.IndexOf("''", StringComparison.Ordinal);
                if (charsetMarker >= 0) value = value[(charsetMarker + 2)..];
                try { value = Uri.UnescapeDataString(value); }
                catch (UriFormatException) { }

                var safeName = Path.GetFileName(value);
                if (!string.IsNullOrWhiteSpace(safeName)) return safeName;
            }
        }

        var extension = response.ContentType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "application/json" => ".json",
            "text/html" => ".html",
            "text/css" => ".css",
            "text/plain" => ".txt",
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "application/pdf" => ".pdf",
            _ => ".bin",
        };
        return $"response-{session.Id}{extension}";
    }

    private void CopyAsCurl()
    {
        if (SelectedSession?.Request is not { } request) return;

        var sb = new System.Text.StringBuilder();
        sb.Append("curl -X ").Append(request.Method).Append(" \"").Append(SelectedSession.Url).Append('"');
        foreach (var header in request.Headers)
        {
            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            sb.Append(" \\\r\n  -H \"").Append(header.Name).Append(": ")
              .Append(header.Value.Replace("\"", "\\\"")).Append('"');
        }
        if (request.Body.Length > 0)
            sb.Append(" \\\r\n  --data-raw \"").Append(request.BodyAsText().Replace("\"", "\\\"")).Append('"');

        Clipboard.SetText(sb.ToString());
    }

    private void RemoveSelected()
    {
        var ids = SelectedSessions.Select(s => s.Id).ToHashSet();
        if (ids.Count == 0) return;
        _store.RemoveAll(s => ids.Contains(s.Id));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refreshTimer.Dispose();
            _surfaceBrush.Dispose();
            _selectionBrush.Dispose();
            _headerBrush.Dispose();
            _headerBorderPen.Dispose();
        }
        base.Dispose(disposing);
    }
}
