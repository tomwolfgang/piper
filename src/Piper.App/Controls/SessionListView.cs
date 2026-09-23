using System.ComponentModel;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;
using Piper.Core.Telemetry;

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
    // The order matches the real columns. The compact ones are exactly as wide as the widest text
    // they can show (see FixedColumnSamples), measured in the font they are drawn in so display
    // scaling and zoom are covered. The long ones give up width first, down to these unscaled
    // floors, and share any surplus by weight, so all nine stay on screen in a list about 720 px
    // wide at 100% -- the progress in Size and Time is no use scrolled off to the right.
    private static readonly int[] FlexibleColumnFloors = [0, 0, 0, 72, 100, 56, 56, 0, 0];
    private static readonly int[] ColumnGrowthWeights = [0, 0, 0, 3, 6, 2, 2, 0, 0];
    private const int PathColumn = 4;
    private const int SizeColumn = 7;
    private const int TimeColumn = 8;

    /// <summary>Horizontal inset of a cell's text on each side, as <see cref="OnDrawSubItem"/> draws it.</summary>
    private const int CellPadding = 5;

    // The fill behind a body still arriving: the accent, faint enough to leave the text readable on
    // every row background, with a solid edge along the bottom so its length reads at a glance.
    private const int ProgressFillAlpha = 70;
    private const int ProgressEdgeHeight = 2;

    /// <summary>Ceiling on how many matches a find selects. Marking is unlimited.</summary>
    private const int MaxSelectedMatches = 2_000;

    private readonly ListView _list;
    private readonly TextBox _filterBox;
    private readonly SessionStore _store;
    private readonly SolidBrush _surfaceBrush = new(Palette.Surface);
    private readonly SolidBrush _selectionBrush = new(Palette.Selection);
    private readonly SolidBrush _markBrush = new(FindSessionsDialog.DefaultMarkColour);
    private readonly SolidBrush _headerBrush = new(Palette.SurfaceAlt);
    private readonly Pen _headerBorderPen = new(Palette.Border);
    private readonly SolidBrush _progressFillBrush = new(Color.FromArgb(ProgressFillAlpha, Palette.Accent));
    private readonly SolidBrush _progressEdgeBrush = new(Palette.Accent);

    private readonly List<Session> _visible = new(1024);
    // Find marks, keyed by session id rather than row index: rows are rebuilt from the store
    // several times a second, and a session's id survives that where its position does not.
    private readonly Dictionary<int, Color> _marks = [];
    private SearchQuery _query = SearchQuery.Empty;
    private SearchQuery _findQuery = SearchQuery.Empty;
    private FindSessionsRequest _lastFind = FindSessionsRequest.Default;
    private Func<Session, bool>? _visibilityFilter;
    private Func<Session, bool>? _filtersetFilter;
    private bool _autoScroll = true;

    public event EventHandler<Session?>? SelectionChanged;

    /// <summary>
    /// Raised on the refresh tick for the selected session while it is in flight, so its figures
    /// can move, and once more when its state changes, with <see cref="SessionRefresh.Reload"/> set.
    /// </summary>
    public event EventHandler<SessionRefresh>? SelectedSessionRefreshed;

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
            PlaceholderText = Strings.SessionList.FilterPlaceholder,
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

        // Placeholder widths: the real ones are measured once the grid has a handle to measure with.
        _list.Columns.Add(Strings.SessionList.ColumnId, 52, HorizontalAlignment.Right);
        _list.Columns.Add(Strings.SessionList.ColumnResult, 55, HorizontalAlignment.Left);
        _list.Columns.Add(Strings.SessionList.ColumnMethod, 62, HorizontalAlignment.Left);
        _list.Columns.Add(Strings.SessionList.ColumnHost, 170, HorizontalAlignment.Left);
        _list.Columns.Add(Strings.SessionList.ColumnPath, 300, HorizontalAlignment.Left);
        _list.Columns.Add(Strings.SessionList.ColumnType, 130, HorizontalAlignment.Left);
        _list.Columns.Add(Strings.SessionList.ColumnProcess, 110, HorizontalAlignment.Left);
        _list.Columns.Add(Strings.SessionList.ColumnSize, 112, HorizontalAlignment.Right);
        _list.Columns.Add(Strings.SessionList.ColumnTime, 88, HorizontalAlignment.Right);
        DarkListView.AddFillerColumn(_list);
        _list.Resize += (_, _) => ExpandColumnsToView();
        _list.HandleCreated += (_, _) => RefitColumns();
        _list.DpiChangedAfterParent += (_, _) => RefitColumns();

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
            if (_refreshPending)
            {
                _refreshPending = false;
                Rebuild();
            }

            // A body's progress is read here rather than announced by the relay: a download raises
            // no event per chunk, so all it costs the grid is repainting the rows on screen that are
            // still receiving. Every other tick: repainting a screenful of owner-drawn rows costs
            // about as much however little of each row changes, and three updates a second is
            // plenty for a count and a fill.
            _progressTick = !_progressTick;
            if (_progressTick) InvalidateReceivingRows();
            RefreshSelectedSession();
        };
        _refreshTimer.Start();
    }

    private readonly System.Windows.Forms.Timer _refreshTimer;
    private volatile bool _refreshPending;
    private bool _progressTick;
    private bool _suppressSelectionChanged;
    private bool _expandingColumns;
    private int[] _columnMinimums = [];

    // One character of the grid font, and the padding TextRenderer adds around a run of them.
    private double _charWidth;
    private double _textPadding;
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

    /// <summary>
    /// The Filters tab's applied filterset, as a visibility filter in its own slot. It must not
    /// share <see cref="VisibilityFilter"/> (the capture scope owns that) and must not be written
    /// into <see cref="FilterText"/>: the search box is the user's to type in, and mirroring the
    /// filterset there both destroyed what they had typed and let editing the box appear to switch
    /// the filterset off while it was still discarding traffic at admission.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Session, bool>? FiltersetFilter
    {
        get => _filtersetFilter;
        set
        {
            _filtersetFilter = value;
            Rebuild();
        }
    }

    private void RequestRefresh() => _refreshPending = true;

    /// <summary>
    /// Repaints the on-screen rows whose body is still arriving, so their size, fill and time move.
    /// Only the visible rows are looked at, and nothing is repainted once none are receiving.
    /// </summary>
    private void InvalidateReceivingRows()
    {
        var count = Math.Min(_visible.Count, _list.VirtualListSize);
        if (count == 0 || !_list.IsHandleCreated || !_list.Visible) return;

        var bottom = _list.ClientSize.Height;
        for (var index = Math.Max(_list.TopItem?.Index ?? 0, 0); index < count; index++)
        {
            var bounds = _list.GetItemRect(index);
            if (bounds.Top >= bottom) break;
            if (_visible[index].State == SessionState.ReceivingBody) _list.Invalidate(bounds);
        }
    }

    // The selected session as the inspector last saw it, so a change of state can be told apart
    // from a figure that is merely moving.
    private Session? _refreshedSession;
    private SessionState _refreshedState;

    private void RaiseSelectionChanged(Session? session)
    {
        _refreshedSession = session;
        if (session is not null) _refreshedState = session.State;
        SelectionChanged?.Invoke(this, session);
    }

    private void RefreshSelectedSession()
    {
        if (SelectedSession is not { } session || !ReferenceEquals(session, _refreshedSession)) return;

        var state = session.State;
        var reload = state != _refreshedState;
        _refreshedState = state;

        if (reload || state is SessionState.Pending or SessionState.SendingRequest
                or SessionState.AwaitingResponse or SessionState.ReceivingBody)
            SelectedSessionRefreshed?.Invoke(this, new SessionRefresh(session, reload));
    }

    /// <summary>
    /// Measures the columns again and lays them out. Call after the grid font changes size, as a
    /// zoom does; a move to a monitor with a different scale refits on its own.
    /// </summary>
    public void RefitColumns()
    {
        if (!_list.IsHandleCreated) return;

        var zoom = FontScale.Multiplier;
        var minimums = new int[FlexibleColumnFloors.Length];
        using (var graphics = _list.CreateGraphics())
        {
            for (var index = 0; index < minimums.Length; index++)
            {
                minimums[index] = FixedColumnSamples(index) is { } samples
                    ? MeasureWidest(graphics, samples)
                    : (int)Math.Round(LogicalToDeviceUnits(FlexibleColumnFloors[index]) * zoom);
            }

            var ten = TextRenderer.MeasureText(graphics, new string('0', 10), Palette.Mono).Width;
            var twenty = TextRenderer.MeasureText(graphics, new string('0', 20), Palette.Mono).Width;
            _charWidth = (twenty - ten) / 10.0;
            _textPadding = ten - 10 * _charWidth;
        }

        _columnMinimums = minimums;
        ExpandColumnsToView();
    }

    /// <summary>The widest text a compact column can show, or null for a column that flexes.</summary>
    private static string[]? FixedColumnSamples(int column) => column switch
    {
        0 => ["999999"],
        1 => Format.WidestResultTexts(),
        2 => ["OPTIONS", Strings.SessionList.TunnelMethod],
        SizeColumn => Format.WidestSizeTexts(),
        TimeColumn => Format.WidestDurationTexts(),
        _ => null,
    };

    /// <summary>
    /// Measures with the font, device context and flags the cell is drawn with, so the text that is
    /// measured to fit is the text that fits: <see cref="TextRenderer"/> pads its text by default.
    /// </summary>
    private static int MeasureWidest(Graphics graphics, string[] samples)
    {
        var widest = 0;
        foreach (var sample in samples)
            widest = Math.Max(widest, TextRenderer.MeasureText(graphics, sample, Palette.Mono).Width);
        return widest + 2 * CellPadding;
    }

    /// <summary>
    /// Uses extra horizontal room for the columns where request details are most likely to be
    /// truncated. Below the minimums the grid scrolls sideways instead of crushing the compact fields.
    /// </summary>
    private void ExpandColumnsToView()
    {
        if (_expandingColumns || _columnMinimums.Length == 0 || _list.ClientSize.Width <= 0) return;

        _expandingColumns = true;
        try
        {
            var widths = ColumnLayout.Fit(_list.ClientSize.Width, _columnMinimums, ColumnGrowthWeights);
            for (var index = 0; index < widths.Length; index++)
                if (_list.Columns[index].Width != widths[index]) _list.Columns[index].Width = widths[index];
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

    /// <summary>
    /// Opens the Find Sessions dialog and applies what it asked for. Unlike the filter box this
    /// hides nothing: matches are marked in the chosen colour and, optionally, selected.
    /// </summary>
    public void ShowFindSessions()
    {
        if (FindSessionsDialog.Prompt(FindForm(), _lastFind) is not { } request) return;

        Analytics.Track(AnalyticsEvents.FeatureUsed, (AnalyticsProperties.Feature, "find"));

        _lastFind = request;
        _findQuery = SearchQuery.Parse(request.Query, request.Scope);
        ApplyFind(request);
    }

    /// <summary>Selects the next session matching the last find, wrapping at the end of the list.
    /// Opens the dialog instead when nothing has been searched for yet.</summary>
    public void FindNext()
    {
        if (_findQuery.IsEmpty)
        {
            ShowFindSessions();
            return;
        }

        var startAfter = _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[^1] : -1;
        var index = _findQuery.NextMatchIndex(_visible, startAfter);
        if (index < 0) return;

        SelectOnlyIndices([index]);
    }

    /// <summary>Removes every find mark. The sessions themselves are untouched.</summary>
    public void ClearMarks()
    {
        if (_marks.Count == 0) return;
        _marks.Clear();
        _list.Invalidate();
    }

    private void ApplyFind(FindSessionsRequest request)
    {
        // An empty query matches every session, so it would mark - or with No highlight, unmark -
        // the entire capture. The dialog's disabled button only rejects blank input; a query that
        // parses away to nothing, such as status:abc, still arrives here.
        if (_findQuery.IsEmpty)
        {
            ReportFind(Strings.SessionList.FindNothingToMatch);
            return;
        }

        var matches = new List<int>();
        for (var index = 0; index < _visible.Count; index++)
        {
            if (!_findQuery.Matches(_visible[index])) continue;
            matches.Add(index);
            if (request.Highlight is { } colour) _marks[_visible[index].Id] = colour;
            else _marks.Remove(_visible[index].Id);
        }

        if (matches.Count == 0)
        {
            // No mark changed, so nothing needs repainting.
            ReportFind(Strings.SessionList.FindNoMatches);
            return;
        }

        _list.Invalidate();

        if (request.SelectMatches) SelectOnlyIndices(matches);

        var outcome = request.Highlight is null
            ? Strings.SessionList.FindMarksRemoved(matches.Count)
            : Strings.SessionList.FindMarked(matches.Count);

        // Context-menu actions work on the selection, so a capped selection must not look like the
        // whole result: say so rather than let an export or a delete quietly cover part of it.
        if (request.SelectMatches && matches.Count > MaxSelectedMatches)
            ReportFind(outcome + Strings.SessionList.FindSelectionCapped(MaxSelectedMatches));
        else if (_findQuery.Warnings.Count > 0)
            ReportFind(outcome);
    }

    /// <summary>
    /// Reports the outcome of a find, always appending the parse warnings. A query Piper could not
    /// read the way it was typed must not come back looking like a clean result, however many
    /// sessions the terms it did understand happened to match.
    /// </summary>
    private void ReportFind(string message)
    {
        if (_findQuery.Warnings.Count > 0)
            message += Environment.NewLine + Environment.NewLine
                + string.Join(Environment.NewLine, _findQuery.Warnings);

        MessageBox.Show(FindForm(), message, Strings.SessionList.FindCaption,
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>
    /// Replaces the selection with the given rows and scrolls the first of them into view. Each
    /// virtual row costs a window message, so a find that matches a whole busy capture selects
    /// only the first <see cref="MaxSelectedMatches"/> rows rather than stalling the UI thread.
    /// Every match is still marked.
    /// </summary>
    private void SelectOnlyIndices(List<int> indices)
    {
        if (indices.Count == 0) return;

        var previousSession = SelectedSession;
        var limit = Math.Min(indices.Count, MaxSelectedMatches);

        _list.BeginUpdate();
        try
        {
            _suppressSelectionChanged = true;
            _list.SelectedIndices.Clear();
            for (var i = 0; i < limit; i++) _list.SelectedIndices.Add(indices[i]);
        }
        finally
        {
            // Clear the flag before EndUpdate: the redraw call is a P/Invoke that can throw, and a
            // flag left set would silently swallow every later selection event for this grid.
            _suppressSelectionChanged = false;
            _list.EndUpdate();
        }

        _list.EnsureVisible(indices[0]);
        _primarySelectedSession = FirstSelectedSession();
        if (!ReferenceEquals(previousSession, SelectedSession))
            RaiseSelectionChanged(SelectedSession);

        // The selection was replaced wholesale and the grid's own event was suppressed above, so
        // notify unconditionally: a same-sized selection of different rows is still a change.
        SelectedSessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Drops marks for sessions the store no longer holds, so a long capture cannot accumulate
    /// them without bound. The count check is a conservative trigger, not an exact one: a stale id
    /// matches no row and so paints nothing, which makes it cheap to leave a few behind until the
    /// marks outnumber the captured sessions, and keeps this off the common refresh path.
    /// </summary>
    private void PruneMarks(List<Session> allSessions)
    {
        if (_marks.Count == 0 || _marks.Count <= allSessions.Count) return;

        var live = new HashSet<int>(allSessions.Count);
        foreach (var session in allSessions) live.Add(session.Id);

        var stale = new List<int>();
        foreach (var id in _marks.Keys)
            if (!live.Contains(id)) stale.Add(id);
        foreach (var id in stale) _marks.Remove(id);
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
        PruneMarks(_visible);
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
            RaiseSelectionChanged(SelectedSession);
        if (previousSelectionCount != _list.SelectedIndices.Count)
            SelectedSessionsChanged?.Invoke(this, EventArgs.Empty);

        _list.Invalidate();
    }

    private void ApplyVisibilityFiltersInPlace()
    {
        if (_query.IsEmpty && _visibilityFilter is null && _filtersetFilter is null) return;

        var writeIndex = 0;
        for (var readIndex = 0; readIndex < _visible.Count; readIndex++)
        {
            var session = _visible[readIndex];
            // A check initiated by Piper must remain auditable in the grid. It is deliberately
            // visible even when an ad-hoc or capture-scope filter would otherwise omit it.
            if (!session.IsUpdateCheck && !_query.IsEmpty && !_query.Matches(session)) continue;
            if (!session.IsUpdateCheck && _visibilityFilter is not null && !_visibilityFilter(session)) continue;
            if (!session.IsUpdateCheck && _filtersetFilter is not null && !_filtersetFilter(session)) continue;
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
        var receiving = session.State == SessionState.ReceivingBody;
        var item = new ListViewItem(session.Id.ToString());
        item.SubItems.Add(receiving ? Strings.SessionList.ReceivingResult(session.StatusText) : session.StatusText);
        item.SubItems.Add(session.IsTunnel ? Strings.SessionList.TunnelMethod : session.Method);
        item.SubItems.Add(session.Host);
        item.SubItems.Add(session.Path + session.Query);
        item.SubItems.Add(Format.ShortContentType(session.ContentType));
        item.SubItems.Add(string.IsNullOrEmpty(session.ProcessName) ? Strings.SessionList.UnknownProcess : session.ProcessName);
        item.SubItems.Add(receiving && session.ExpectedResponseBytes > 0
            ? Format.SizeProgress(session.BytesReceived, session.ExpectedResponseBytes)
            : Format.Size(session.ResponseSize));
        // A body still arriving counts up; one still waiting for its head just says it is waiting.
        item.SubItems.Add(session.Completed is null && !receiving
            ? Strings.SessionList.PendingDuration
            : Strings.SessionList.Duration(session.Duration.TotalMilliseconds));
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

        var mark = Color.Empty;
        var marked = !selected && session is not null && _marks.TryGetValue(session.Id, out mark);

        if (_surfaceBrush.Color != Palette.Surface) _surfaceBrush.Color = Palette.Surface;
        if (_selectionBrush.Color != Palette.Selection) _selectionBrush.Color = Palette.Selection;
        if (marked && _markBrush.Color != mark) _markBrush.Color = mark;
        e.Graphics.FillRectangle(
            selected ? _selectionBrush : marked ? _markBrush : _surfaceBrush, e.Bounds);

        if (e.ColumnIndex == SizeColumn && session?.ResponseProgress is { } progress)
            DrawProgress(e.Graphics, e.Bounds, progress);

        var colour = session is null
            ? Palette.Text
            : Palette.ForStatus(session);

        if (selected) colour = Palette.Text;

        // The status column keeps its outcome colour even when the row is selected.
        if (selected && e.ColumnIndex == 1 && session is not null)
            colour = Palette.ForStatus(session);

        // Status colours have too little contrast on a mark colour, so a marked row draws dark.
        if (marked) colour = Palette.MarkedRowText;

        var alignment = e.Header?.TextAlign ?? HorizontalAlignment.Left;
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | alignment switch
        {
            HorizontalAlignment.Right => TextFormatFlags.Right,
            HorizontalAlignment.Center => TextFormatFlags.HorizontalCenter,
            _ => TextFormatFlags.Left,
        };

        // A narrow Path gives up its middle rather than its end, where the file name is:
        // "/fi.../create-1.20.1.jar" says what is downloading, "/files/4970/112/..." does not. The
        // grid font is monospaced, so what fits is a character count and painting measures nothing.
        var text = e.SubItem?.Text ?? string.Empty;
        if (e.ColumnIndex == PathColumn && _charWidth > 0)
            text = Format.ShortenPath(text, (int)((e.Bounds.Width - 2 * CellPadding - _textPadding) / _charWidth));

        TextRenderer.DrawText(e.Graphics, text, Palette.Mono,
            Rectangle.Inflate(e.Bounds, -CellPadding, 0), colour, flags);
    }

    /// <summary>
    /// Shades the part of the Size cell a body has covered, under its text. A translucent overlay
    /// rather than a themed bar, so one rule reads on the plain, selected and marked backgrounds in
    /// either theme, and the figure on top of it stays legible.
    /// </summary>
    private void DrawProgress(Graphics graphics, Rectangle cell, double progress)
    {
        var width = (int)(cell.Width * progress);
        if (width <= 0) return;

        var accent = Palette.Accent;
        if (_progressEdgeBrush.Color != accent)
        {
            _progressEdgeBrush.Color = accent;
            _progressFillBrush.Color = Color.FromArgb(ProgressFillAlpha, accent);
        }

        graphics.FillRectangle(_progressFillBrush, cell.X, cell.Y, width, cell.Height);
        graphics.FillRectangle(_progressEdgeBrush, cell.X, cell.Bottom - ProgressEdgeHeight, width, ProgressEdgeHeight);
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

    /// <summary>Puts the caret in the filter box with the query selected, so typing replaces it.</summary>
    private void FocusFilter()
    {
        _filterBox.Focus();
        _filterBox.SelectAll();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        // Bound here rather than on MainForm: the Composer's history list and the inspector's
        // search tabs own Ctrl+F for themselves, and a form-level binding (KeyPreview or a menu
        // accelerator) would fire first and take it from them.
        if (e.Control && e.KeyCode == Keys.F)
        {
            // Ctrl+F finds: it marks matches and keeps every captured row on screen. Ctrl+Shift+F
            // reaches the filter box, which hides everything that does not match.
            if (e.Shift) FocusFilter(); else ShowFindSessions();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.F3 && e.Modifiers == Keys.None)
        {
            // Bare F3 only. Shift+F3 conventionally means find-previous, so leave it unbound
            // rather than make it a compatibility break to add that direction later.
            FindNext();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.C)
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

        var resend = Menus.Item(Strings.SessionList.Resend, Strings.Shortcuts.CtrlR, (_, _) => ResendSelected());
        menu.Items.Add(resend);
        menu.Items.Add(Menus.Item(Strings.SessionList.SendToComposer, Strings.Shortcuts.CtrlE, (_, _) =>
        {
            if (SelectedSession is { } session) SendToComposerRequested?.Invoke(this, session);
        }));
        var autoResponder = menu.Items.Add(Strings.SessionList.CreateAutoResponderRule, null, (_, _) =>
        {
            if (SelectedSession is { } session) SendToAutoResponderRequested?.Invoke(this, session);
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item(Strings.SessionList.CopyUrl, Strings.Shortcuts.CtrlC, (_, _) => CopyUrls()));
        menu.Items.Add(Strings.SessionList.CopyAsCurl, null, (_, _) => CopyAsCurl());
        menu.Items.Add(Strings.SessionList.CopyFullSession, null, (_, _) => CopyFullSession());
        var save = new ToolStripMenuItem(Strings.SessionList.SaveMenu);
        var saveResponseBody = save.DropDownItems.Add(Strings.SessionList.SaveResponseBody, null, (_, _) => SaveResponseBody());
        var saveSessionsAsSaz = save.DropDownItems.Add(Strings.SessionList.SaveSessionsAsSaz, null, (_, _) => SaveSelectedSessionsAsSaz());
        menu.Items.Add(save);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item(Strings.SessionList.FindSessions, Strings.Shortcuts.CtrlF, (_, _) => ShowFindSessions()));
        menu.Items.Add(Menus.Item(Strings.SessionList.FindNext, Strings.Shortcuts.F3, (_, _) => FindNext()));
        var clearMarks = menu.Items.Add(Strings.SessionList.ClearFindMarks, null, (_, _) => ClearMarks());
        menu.Items.Add(Menus.Item(Strings.SessionList.FilterSessions, Strings.Shortcuts.CtrlShiftF, (_, _) => FocusFilter()));
        menu.Items.Add(Strings.SessionList.FilterToThisHost, null, (_, _) =>
        {
            if (SelectedSession is { } session) FilterText = $"host:{session.Host}";
        });
        menu.Items.Add(Strings.SessionList.HideThisHost, null, (_, _) =>
        {
            if (SelectedSession is { } session) HideHostRequested?.Invoke(this, session.Host);
        });
        menu.Items.Add(new ToolStripSeparator());
        var textWizard = new ToolStripMenuItem(Strings.SessionList.SendUrlToTextWizard, null,
            (_, _) => TextWizardDialog.Open(FindForm(), SelectedSession?.Url));
        menu.Items.Add(textWizard);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Menus.Item(Strings.SessionList.RemoveSelected, Strings.Shortcuts.Delete, (_, _) => RemoveSelected()));
        menu.Opening += (_, _) =>
        {
            textWizard.Enabled = SelectedSession is { Url.Length: > 0 };
            saveResponseBody.Enabled = SelectedSession?.Response is not null;
            saveSessionsAsSaz.Enabled = SelectedSessions.Any(session => session.Request is not null);
            save.Enabled = saveResponseBody.Enabled || saveSessionsAsSaz.Enabled;
            resend.Enabled = SelectedSession is { IsTunnel: false, Request: not null };
            clearMarks.Enabled = _marks.Count > 0;
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
            Title = Strings.SessionList.SaveResponseBodyCaption,
            Filter = Strings.Common.AllFilesFilter,
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
            MessageBox.Show(this, Strings.SessionList.SaveResponseBodyFailed(ex.Message),
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>Prompts for a Fiddler SAZ file and saves the current capture selection.</summary>
    public bool SaveSelectedSessionsAsSaz()
    {
        var sessions = SelectedSessions.Where(session => session.Request is not null).ToArray();
        if (sessions.Length == 0) return false;

        using var dialog = new SaveFileDialog
        {
            Title = Strings.SazImport.SaveCaption,
            Filter = Strings.SazImport.SaveFilter,
            DefaultExt = "saz",
            AddExtension = true,
            FileName = Strings.SazImport.SuggestedFileName(DateTime.Now),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;

        try
        {
            SazExporter.Export(dialog.FileName, sessions);
            Analytics.Track(
                AnalyticsEvents.FeatureUsed,
                (AnalyticsProperties.Feature, "session_export"),
                (AnalyticsProperties.Format, "saz"),
                (AnalyticsProperties.Count, Analytics.CountBucket(sessions.Length)));
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Strings.SessionList.SaveSessionsFailed(ex.Message),
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            RaiseSelectionChanged(_primarySelectedSession);
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
            RaiseSelectionChanged(SelectedSession);
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
        return Strings.SessionList.SuggestedResponseFileName(session.Id, extension);
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
            _markBrush.Dispose();
            _headerBrush.Dispose();
            _headerBorderPen.Dispose();
            _progressFillBrush.Dispose();
            _progressEdgeBrush.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>The selected session on a refresh tick, and whether its state changed since the last.</summary>
public readonly record struct SessionRefresh(Session Session, bool Reload);
