using System.Text;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

namespace Piper.App.Controls;

/// <summary>
/// Compose and replay requests, with a live search over everything already captured.
/// </summary>
/// <remarks>
/// The search pane makes finding part of composing: type a query, see matching
/// captured requests, load one, edit, send. The same <see cref="SearchQuery"/> grammar as
/// the session-list filter, so a query learned in one place works in the other.
/// </remarks>
public sealed class ComposerPanel : UserControl
{
    private readonly SessionStore _store;
    private readonly RequestExecutor _executor;

    // Search pane
    private readonly TextBox _searchBox;
    private readonly ListView _results;
    private readonly Label _resultCount;
    private readonly Label _searchHint;
    private Session[] _matches = [];

    // Editor pane
    private readonly ComboBox _method;
    private readonly TextBox _url;
    private readonly TextBox _headers;
    private readonly TextBox _body;
    private readonly TextBox _rawEditor;
    private readonly TabControl _editorTabs;
    private readonly Button _execute;
    private readonly Label _status;
    private readonly ToolTip _historyToolTip = new();
    private string? _historyToolTipText;
    // Persisted Composer history belongs in this panel, not in SessionStore. The latter drives
    // the capture list, so restoring history there made an old composed request appear as the
    // first "captured" session every time Piper started.
    private readonly List<Session> _history = [];

    private CancellationTokenSource? _inFlight;

    public ComposerPanel(SessionStore store, RequestExecutor executor)
    {
        _store = store;
        _executor = executor;

        _history.AddRange(ComposerHistoryStore.Load());

        // ---------------------------------------------------------- search pane

        _searchBox = new TextBox
        {
            Dock = DockStyle.Top,
            Font = Palette.Mono,
            PlaceholderText = "Search requests you've sent...",
        };
        _searchBox.TextChanged += (_, _) => RunSearch();
        _searchBox.KeyDown += OnSearchKeyDown;

        _searchHint = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            ForeColor = Palette.TextDim,
            Font = new Font("Segoe UI", 7.5f),
            Text = "method:POST  host:api  status:4xx  body:\"user_id\"  header:Authorization\r\n"
                 + "size:>100kb  dur:>500  is:json  -is:image  /v[0-9]+\\/orders/",
        };

        _resultCount = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 18,
            ForeColor = Palette.TextDim,
            Padding = new Padding(4, 2, 0, 0),
        };

        _results = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            VirtualMode = true,
            OwnerDraw = true,
            HideSelection = false,
            MultiSelect = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = Palette.Mono,
        };
        DarkListView.EnableDoubleBuffering(_results);
        _results.Columns.Add("#", 46, HorizontalAlignment.Right);
        _results.Columns.Add("St", 40, HorizontalAlignment.Left);
        _results.Columns.Add("Method", 58, HorizontalAlignment.Left);
        _results.Columns.Add("Host", 130, HorizontalAlignment.Left);
        _results.Columns.Add("Path", 260, HorizontalAlignment.Left);
        DarkListView.AddFillerColumn(_results);
        _results.RetrieveVirtualItem += OnRetrieveResult;
        _results.DrawColumnHeader += OnDrawResultHeader;
        _results.DrawSubItem += OnDrawResultSubItem;
        _results.MouseMove += OnResultsMouseMove;
        _results.MouseLeave += (_, _) => SetHistoryToolTip(null);
        _results.DoubleClick += (_, _) => LoadSelectedResult();
        _results.MouseDown += OnResultsMouseDown;
        _results.ContextMenuStrip = BuildHistoryMenu();
        _results.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F)
            {
                FocusSearch();
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }
            if (e.KeyCode == Keys.Delete)
            {
                RemoveSelectedHistory();
                e.Handled = true;
                return;
            }
            if (e.KeyCode != Keys.Enter) return;
            LoadSelectedResult();
            e.Handled = true;
        };

        var searchPane = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4) };
        searchPane.Controls.Add(_results);
        searchPane.Controls.Add(_resultCount);
        searchPane.Controls.Add(_searchHint);
        searchPane.Controls.Add(_searchBox);

        var searchHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "  Composer History",
            ForeColor = Palette.Text,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Padding = new Padding(0, 5, 0, 0),
        };

        var searchContainer = new Panel { Dock = DockStyle.Fill };
        searchContainer.Controls.Add(searchPane);
        searchContainer.Controls.Add(searchHeader);

        // ---------------------------------------------------------- editor pane

        _method = new ComboBox
        {
            Width = 90,
            DropDownStyle = ComboBoxStyle.DropDown,
            Font = Palette.Mono,
        };
        _method.Items.AddRange(["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS", "TRACE"]);
        _method.SelectedIndex = 0;
        _method.TextChanged += (_, _) => UpdateBodyWarning();

        _url = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = Palette.Mono,
            PlaceholderText = "https://example.com/api/v1/resource",
        };
        _url.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            _ = ExecuteAsync();
        };

        _execute = new Button { Width = 90, Dock = DockStyle.Right, Text = "Send" };
        _execute.Click += (_, _) => _ = ExecuteAsync();

        var urlRow = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2) };
        urlRow.Controls.Add(_url);
        urlRow.Controls.Add(_execute);

        var methodRow = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(2) };
        _method.Dock = DockStyle.Left;
        methodRow.Controls.Add(urlRow);
        methodRow.Controls.Add(_method);

        _headers = MakeEditor("Host: example.com\r\nAccept: application/json");
        _body = MakeEditor(string.Empty);
        _body.TextChanged += (_, _) => UpdateBodyWarning();
        _rawEditor = MakeEditor(string.Empty);

        _editorTabs = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        _editorTabs.TabPages.Add(NewPage("Headers", _headers));
        _editorTabs.TabPages.Add(NewPage("Raw", _rawEditor));
        _editorTabs.Selecting += OnEditorTabSelecting;
        _editorTabs.Deselecting += OnEditorTabDeselecting;

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 20,
            ForeColor = Palette.TextDim,
            Font = Palette.Mono,
            Padding = new Padding(4, 3, 0, 0),
        };

        var bodyHeader = new Label
        {
            Dock = DockStyle.Top,
            Height = 22,
            Text = "  Body",
            ForeColor = Palette.Text,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            Padding = new Padding(0, 4, 0, 0),
        };
        var bodyPane = new Panel { Dock = DockStyle.Fill };
        bodyPane.Controls.Add(_body);
        bodyPane.Controls.Add(bodyHeader);

        var editorSplit = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 4,
        };
        editorSplit.Panel1.Controls.Add(_editorTabs);
        editorSplit.Panel2.Controls.Add(bodyPane);

        var editorPane = new Panel { Dock = DockStyle.Fill };
        editorPane.Controls.Add(editorSplit);
        editorPane.Controls.Add(_status);
        editorPane.Controls.Add(methodRow);

        // ------------------------------------------------------------- assembly

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 4,
        };
        split.Panel1MinSize = 260;
        split.Panel1.Controls.Add(searchContainer);
        split.Panel2.Controls.Add(editorPane);
        Controls.Add(split);

        _split = split;

        _store.SessionAdded += (_, _) => _searchDirty = true;
        _store.SessionUpdated += (_, _) => _searchDirty = true;
        _store.Cleared += (_, _) => _searchDirty = true;

        _searchTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _searchTimer.Tick += (_, _) =>
        {
            if (!_searchDirty) return;
            _searchDirty = false;
            RunSearch();
        };
        _searchTimer.Start();

        RunSearch();
        UpdateBodyWarning();
    }

    private readonly SplitContainer _split;
    private readonly System.Windows.Forms.Timer _searchTimer;
    private volatile bool _searchDirty;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Set the splitter once the control has a real width; doing it in the constructor
        // clamps against the design-time size.
        if (_split.Width > 400) _split.SplitterDistance = Math.Min(380, _split.Width / 3);
    }

    private static TextBox MakeEditor(string initial) => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = Palette.Mono,
        BorderStyle = BorderStyle.None,
        AcceptsTab = true,
        Text = initial,
    };

    private static TabPage NewPage(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    // ------------------------------------------------------------------ search

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        // Down/Enter from the search box moves into the results without touching the mouse.
        if (e.KeyCode == Keys.Down && _matches.Length > 0)
        {
            _results.Focus();
            if (_results.SelectedIndices.Count == 0) _results.SelectedIndices.Add(0);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter && _matches.Length > 0)
        {
            if (_results.SelectedIndices.Count == 0) _results.SelectedIndices.Add(0);
            LoadSelectedResult();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    private void RunSearch()
    {
        var query = SearchQuery.Parse(_searchBox.Text);
        _searchBox.ForeColor = query.Warnings.Count > 0 ? Palette.StatusClientError : Palette.Text;

        // Only requests are useful here, so drop undecrypted tunnels. This pane is the
        // Composer's own history, not a window into all captured traffic, so only sessions
        // actually sent from the Composer belong here.
        _matches = _history
            .Where(s => !s.IsTunnel && s.Request is not null && query.Matches(s))
            .OrderByDescending(s => s.Completed ?? s.Started)
            .ToArray();

        _results.BeginUpdate();
        _results.VirtualListSize = _matches.Length;
        _results.EndUpdate();
        _results.Invalidate();

        _resultCount.Text = query.Warnings.Count > 0
            ? $"{_matches.Length:N0} matches - {query.Warnings[0]}"
            : $"{_matches.Length:N0} sent requests";
    }

    private void OnRetrieveResult(object? sender, RetrieveVirtualItemEventArgs e)
    {
        if (e.ItemIndex < 0 || e.ItemIndex >= _matches.Length)
        {
            e.Item = new ListViewItem(string.Empty);
            return;
        }

        var session = _matches[e.ItemIndex];
        var item = new ListViewItem(session.Id.ToString());
        item.SubItems.Add(session.StatusText);
        item.SubItems.Add(session.Method);
        item.SubItems.Add(session.Host);
        item.SubItems.Add(session.Path);
        item.SubItems.Add(string.Empty); // filler column
        item.Tag = session;
        e.Item = item;
    }

    private void OnResultsMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || _results.GetItemAt(e.X, e.Y) is not { } item) return;
        _results.SelectedIndices.Clear();
        item.Selected = true;
        item.Focused = true;
    }

    private void OnResultsMouseMove(object? sender, MouseEventArgs e)
    {
        var hit = _results.HitTest(e.Location);
        if (hit.Item is null || hit.SubItem is null)
        {
            SetHistoryToolTip(null);
            return;
        }

        var columnIndex = hit.Item.SubItems.IndexOf(hit.SubItem);
        var text = hit.SubItem.Text;
        var availableWidth = _results.Columns[columnIndex].Width - 8;
        var textWidth = TextRenderer.MeasureText(text, Palette.Mono, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;

        SetHistoryToolTip(textWidth > availableWidth ? text : null);
    }

    private void SetHistoryToolTip(string? text)
    {
        if (_historyToolTipText == text) return;
        _historyToolTipText = text;
        _historyToolTip.SetToolTip(_results, text);
    }

    private ContextMenuStrip BuildHistoryMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var remove = new ToolStripMenuItem("&Remove from history\tDel", null, (_, _) => RemoveSelectedHistory());
        menu.Items.Add(remove);
        menu.Opening += (_, _) => remove.Enabled = _results.SelectedIndices.Cast<int>()
            .Any(index => index >= 0 && index < _matches.Length);
        return menu;
    }

    private void RemoveSelectedHistory()
    {
        var selected = _results.SelectedIndices.Cast<int>()
            .Where(index => index >= 0 && index < _matches.Length)
            .Select(index => _matches[index])
            .ToHashSet();
        if (selected.Count == 0) return;

        _history.RemoveAll(selected.Contains);
        ComposerHistoryStore.Save(_history);
        RunSearch();
    }

    private static void OnDrawResultHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var background = new SolidBrush(Palette.SurfaceAlt);
        e.Graphics.FillRectangle(background, e.Bounds);
        TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? string.Empty, Palette.UiFont,
            Rectangle.Inflate(e.Bounds, -5, 0), Palette.TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static void OnDrawResultSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item is null) return;
        var session = e.Item.Tag as Session;
        var selected = e.Item.Selected;

        using var background = new SolidBrush(selected ? Palette.Selection : Palette.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);

        var colour = session is null || selected
            ? Palette.Text
            : Palette.ForStatus(session.StatusCode, session.IsTunnel, session.State == SessionState.Failed, session.IsComposed);

        if (e.ColumnIndex == 1 && session is not null)
            colour = Palette.ForStatus(session.StatusCode, session.IsTunnel, session.State == SessionState.Failed, session.IsComposed);

        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, Palette.Mono,
            Rectangle.Inflate(e.Bounds, -4, 0), colour,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void LoadSelectedResult()
    {
        if (_results.SelectedIndices.Count == 0) return;
        var index = _results.SelectedIndices[0];
        if (index < 0 || index >= _matches.Length) return;
        LoadSession(_matches[index]);
    }

    // ------------------------------------------------------------------ editor

    /// <summary>Fills the editor from a captured session. Also used by the grid's context menu.</summary>
    public void LoadSession(Session session)
    {
        if (session.Request is not { } request) return;

        _method.Text = request.Method;
        _url.Text = session.Url;

        var headerText = new StringBuilder();
        foreach (var header in request.Headers)
        {
            // Content-Length is recalculated at send time; keeping a stale one is a footgun.
            // Host is always re-derived from the URL box at send time too (see
            // RequestExecutor.PrepareHeaders) -- showing a stale one here would be misleading.
            if (header.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (header.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
            headerText.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        }
        _headers.Text = headerText.ToString();

        _body.Text = request.Body.Length > 0 && ContentCodec.LooksTextual(request.ContentType, request.DecodedBody)
            ? request.BodyAsText()
            : string.Empty;

        _rawEditor.Text = BuildRawText();
        _status.Text = $"Loaded #{session.Id} - edit and press Send (or Enter in the URL box).";
        _editorTabs.SelectedIndex = 0;
        _url.Focus();
    }

    /// <summary>Keeps the Raw tab in sync when it is opened from the structured tabs.</summary>
    private void OnEditorTabSelecting(object? sender, TabControlCancelEventArgs e)
    {
        if (e.TabPageIndex == 1) _rawEditor.Text = BuildRawText();
    }

    /// <summary>Parses the Raw tab back into the structured fields when leaving it.</summary>
    private void OnEditorTabDeselecting(object? sender, TabControlCancelEventArgs e)
    {
        if (e.TabPageIndex != 1) return;
        if (!RequestExecutor.TryParseRaw(_rawEditor.Text, out var parsed, out _)) return;

        _method.Text = parsed.Method;
        _url.Text = parsed.Url?.ToString() ?? parsed.RequestTarget;

        var headerText = new StringBuilder();
        foreach (var header in parsed.Headers)
            headerText.Append(header.Name).Append(": ").Append(header.Value).Append("\r\n");
        _headers.Text = headerText.ToString();
        _body.Text = parsed.Body.Length > 0 ? Encoding.UTF8.GetString(parsed.Body) : string.Empty;
    }

    private string BuildRawText()
    {
        var sb = new StringBuilder();
        sb.Append(_method.Text.Trim().ToUpperInvariant()).Append(' ')
          .Append(_url.Text.Trim()).Append(" HTTP/1.1\r\n");
        sb.Append(_headers.Text.TrimEnd()).Append("\r\n\r\n");
        sb.Append(_body.Text);
        return sb.ToString();
    }

    private bool TryBuildRequest(out HttpRequestData request, out string error)
    {
        request = new HttpRequestData
        {
            Method = _method.Text.Trim().ToUpperInvariant(),
            HttpVersion = "HTTP/1.1",
            Headers = HeaderCollection.Parse(_headers.Text),
        };

        error = string.Empty;
        var url = _url.Text.Trim();
        if (url.Length == 0)
        {
            error = "Enter a URL.";
            return false;
        }

        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            error = $"'{url}' is not a valid absolute URL.";
            return false;
        }

        request.Url = parsed;
        request.RequestTarget = parsed.PathAndQuery;

        var bodyText = _body.Text;
        request.Body = bodyText.Length > 0 ? Encoding.UTF8.GetBytes(bodyText) : [];
        return true;
    }

    /// <summary>PUT/POST usually shouldn't go out with an empty body -- flag it before the user
    /// finds out the hard way from the far side.</summary>
    private void UpdateBodyWarning()
    {
        var method = _method.Text.Trim().ToUpperInvariant();
        var likelyMistake = method is "POST" or "PUT" && _body.Text.Length == 0;
        _body.BackColor = likelyMistake ? Palette.SurfaceWarning : Palette.Surface;
    }

    private async Task ExecuteAsync()
    {
        if (_inFlight is not null)
        {
            await _inFlight.CancelAsync();
            return;
        }

        if (!TryBuildRequest(out var template, out var error))
        {
            _status.Text = error;
            _status.ForeColor = Palette.StatusServerError;
            return;
        }

        await ExecuteRequestAsync(template, addToHistory: true);
    }

    /// <summary>Replays a captured request without changing the Composer editor fields.</summary>
    public Task ResendAsync(Session session)
    {
        if (session.Request is null) return Task.CompletedTask;

        var replay = session.Request.Clone();
        // The Composer deliberately writes an HTTP/1.1 request over its upstream TCP
        // connection. Reusing an HTTP/2 or HTTP/3 capture's request-line version here makes
        // an h1 origin correctly reject it with 505 HTTP Version Not Supported.
        replay.HttpVersion = "HTTP/1.1";
        return ExecuteRequestAsync(replay, addToHistory: false);
    }

    private async Task ExecuteRequestAsync(HttpRequestData template, bool addToHistory)
    {
        if (_inFlight is not null)
        {
            await _inFlight.CancelAsync();
            return;
        }

        _inFlight = new CancellationTokenSource();
        _execute.Text = "Cancel";
        _status.ForeColor = Palette.TextDim;
        _status.Text = "Sending...";

        try
        {
            var session = await _executor.ExecuteAsync(template, _inFlight.Token);
            if (addToHistory)
            {
                // The explicit Composer Send action owns this history. Ctrl+R replays are
                // captured in the session list, but intentionally do not become history.
                _history.Add(session);
                ComposerHistoryStore.Save(_history);
                _searchDirty = true;
            }

            if (session.State == SessionState.Failed)
            {
                _status.Text = $"Failed: {session.Error}";
                _status.ForeColor = Palette.StatusServerError;
            }
            else
            {
                _status.ForeColor = Palette.ForStatus(session.StatusCode, false, false, false);
                _status.Text = $"#{session.Id}  {session.StatusCode}  {session.Duration.TotalMilliseconds:N0} ms  {Format.Size(session.ResponseSize)}";
            }
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Error: {ex.Message}";
            _status.ForeColor = Palette.StatusServerError;
        }
        finally
        {
            _inFlight?.Dispose();
            _inFlight = null;
            _execute.Text = "Send";
        }
    }

    /// <summary>Puts focus in the search box; used by the Ctrl+K shortcut.</summary>
    public void FocusSearch()
    {
        _searchBox.Focus();
        _searchBox.SelectAll();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _searchTimer.Dispose();
            _inFlight?.Dispose();
            _historyToolTip.Dispose();
        }
        base.Dispose(disposing);
    }
}
