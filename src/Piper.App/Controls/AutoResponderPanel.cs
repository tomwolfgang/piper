using System.Text;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

namespace Piper.App.Controls;

/// <summary>
/// Edits the ordered rule list that answers requests locally instead of sending them upstream.
/// </summary>
/// <remarks>
/// Unlike the Filters tab, edits apply immediately rather than being staged until an Actions command:
/// an AutoResponder rule that looks active but silently is not would be a far worse failure than the
/// occasional unintended keystroke, and Fiddler's equivalent is live too.
///
/// The backing list is the single source of truth; the ListView is only a view of it. That is needed
/// anyway for persistence and reordering, and it avoids a whole class of bugs where the native
/// control's state and the model disagree.
/// </remarks>
public sealed class AutoResponderPanel : UserControl
{
    private readonly AutoResponder _responder;
    private readonly List<AutoResponderRule> _rules = [];

    private readonly CheckBox _enabled;
    private readonly CheckBox _passthrough;
    private readonly ListView _list;
    private readonly TextBox _match;
    private readonly TextBox _action;
    private readonly TextBox _body;
    private readonly TextBox _contentType;
    private readonly TextBox _testUrl;
    private readonly Label _testResult;
    private readonly Button _browse;
    private readonly Button _testButton;

    // Tooltips are attached to each button's holder panel, not the button: a disabled control gets
    // no mouse messages, so a tooltip set on it never appears - which is exactly when the
    // explanation is needed.
    private readonly ToolTip _tips = new() { InitialDelay = 400 };

    private bool _applyingSettings;
    private bool _loadingRule;

    /// <summary>Raised after any edit, so the rule set can be applied to the proxy and persisted.</summary>
    public event EventHandler? SettingsChanged;

    public AutoResponderPanel(AutoResponder responder)
    {
        ArgumentNullException.ThrowIfNull(responder);
        _responder = responder;

        // ---------------------------------------------------------------- header

        _enabled = new CheckBox
        {
            Text = Strings.AutoResponder.EnableRules,
            Dock = DockStyle.Fill,
            AutoSize = false,
            Padding = new Padding(6, 8, 0, 0),
            Font = Palette.UiFontBold,
        };
        _enabled.CheckedChanged += (_, _) => OnEdited();

        _passthrough = new CheckBox
        {
            Text = Strings.AutoResponder.PassthroughUnmatched,
            Dock = DockStyle.Top,
            AutoSize = false,
            Height = 28,
            Padding = new Padding(6, 6, 0, 0),
            Checked = true,
        };
        _passthrough.CheckedChanged += (_, _) => OnEdited();

        var actions = new ToolStrip
        {
            Dock = DockStyle.Right,
            AutoSize = false,
            Width = 86,
            GripStyle = ToolStripGripStyle.Hidden,
            Font = Palette.UiFont,
        };
        var actionsButton = new ToolStripDropDownButton(Strings.AutoResponder.Actions) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        actionsButton.DropDownItems.Add(Strings.AutoResponder.AddRule, null, (_, _) => AddRule(new AutoResponderRule()));
        actionsButton.DropDownItems.Add(Strings.AutoResponder.EditResponse, null, (_, _) => EditResponse());
        actionsButton.DropDownItems.Add(Strings.AutoResponder.RemoveRule, null, (_, _) => RemoveSelected());
        actionsButton.DropDownItems.Add(new ToolStripSeparator());
        actionsButton.DropDownItems.Add(Menus.Item(Strings.AutoResponder.MoveUp, Strings.Shortcuts.CtrlUp, (_, _) => MoveSelected(-1)));
        actionsButton.DropDownItems.Add(Menus.Item(Strings.AutoResponder.MoveDown, Strings.Shortcuts.CtrlDown, (_, _) => MoveSelected(1)));
        actionsButton.DropDownItems.Add(new ToolStripSeparator());
        actionsButton.DropDownItems.Add(Strings.AutoResponder.ResetHitCounts, null, (_, _) => { _responder.ResetStatistics(); RefreshStatistics(); });
        actionsButton.DropDownItems.Add(Strings.AutoResponder.ImportRules, null, (_, _) => ImportRules());
        actionsButton.DropDownItems.Add(Strings.AutoResponder.ExportRules, null, (_, _) => ExportRules());
        actionsButton.DropDownItems.Add(new ToolStripSeparator());
        actionsButton.DropDownItems.Add(Strings.AutoResponder.HelpMenu, null, (_, _) => ShowHelp());
        actions.Items.Add(actionsButton);

        // The Actions strip lives in its own row rather than being docked into the whole header,
        // which would stretch it to the full header height.
        var headerTop = new Panel { Dock = DockStyle.Top, Height = 32 };
        headerTop.Controls.Add(_enabled);
        headerTop.Controls.Add(actions);

        var header = new Panel { Dock = DockStyle.Top, Height = 62 };
        header.Controls.Add(_passthrough);
        header.Controls.Add(headerTop);

        // ------------------------------------------------------------------ list

        _list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
        };
        _list.Columns.Add(Strings.AutoResponder.ColumnOn, 44);
        _list.Columns.Add(Strings.AutoResponder.ColumnMatch, 300);
        _list.Columns.Add(Strings.AutoResponder.ColumnAction, 220);
        _list.Columns.Add(Strings.AutoResponder.ColumnHits, 54);
        _list.Columns.Add(Strings.AutoResponder.ColumnLastMatch, 104);
        // Not DarkListView.Attach: this grid draws its own rows so the enable column can carry a
        // checkbox and disabled rules can be dimmed. The header and buffering come from there.
        DarkListView.EnableDoubleBuffering(_list);
        _list.OwnerDraw = true;
        _list.BackColor = Palette.Surface;
        _list.ForeColor = Palette.Text;
        _list.BorderStyle = BorderStyle.None;
        _list.DrawColumnHeader += DarkListView.DrawHeader;
        _list.DrawItem += (_, e) => e.DrawDefault = false;
        _list.DrawSubItem += DrawRule;
        DarkListView.AddFillerColumn(_list);
        _list.MouseDown += OnListMouseDown;
        _list.KeyDown += OnListKeyDown;
        _list.SelectedIndexChanged += (_, _) => LoadSelectedRule();
        _list.DoubleClick += (_, _) => EditResponse();
        _list.ContextMenuStrip = BuildRuleMenu();

        // ---------------------------------------------------------------- editor

        _match = NewEditor();
        _action = NewEditor();
        _body = NewEditor();
        _contentType = NewEditor();

        _browse = new Button { Text = Strings.AutoResponder.BrowseButton, Width = 76, FlatStyle = FlatStyle.Flat, Enabled = false };
        _browse.Click += (_, _) => BrowseForFile();

        var editor = new Panel { Dock = DockStyle.Bottom, Height = 132, Padding = new Padding(6, 4, 6, 4) };
        editor.Controls.Add(EditorRow(Strings.AutoResponder.LabelContentType, _contentType, null));
        editor.Controls.Add(EditorRow(Strings.AutoResponder.LabelBody, _body, null));
        editor.Controls.Add(EditorRow(Strings.AutoResponder.LabelAction, _action, _browse));
        editor.Controls.Add(EditorRow(Strings.AutoResponder.LabelMatch, _match, null));

        // ---------------------------------------------------------------- tester

        _testButton = new Button { Text = Strings.AutoResponder.TestButton, Width = 76, FlatStyle = FlatStyle.Flat, Enabled = false };
        _testButton.Click += (_, _) => _ = RunTestAsync();

        _testUrl = new TextBox { Dock = DockStyle.Fill, Font = Palette.Mono };
        _testUrl.TextChanged += (_, _) => UpdateTestButton();
        _testUrl.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || !_testButton.Enabled) return;
            e.Handled = true;
            e.SuppressKeyPress = true;
            _ = RunTestAsync();
        };

        _testResult = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 26,
            Padding = new Padding(6, 5, 0, 0),
            ForeColor = Palette.TextDim,
            AutoEllipsis = true,
        };

        var testInput = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(0, 2, 6, 2) };
        testInput.Controls.Add(_testUrl);
        testInput.Controls.Add(Gutter(_testButton));
        testInput.Controls.Add(new Label
        {
            Text = Strings.AutoResponder.LabelTestUrl,
            Dock = DockStyle.Left,
            Width = 106,
            Padding = new Padding(0, 5, 0, 0),
        });

        // Tall enough for both rows plus the padding: the result label docks Bottom and is front-most
        // in z-order, so anything it overlaps gets painted over -- which clipped the Test button.
        var tester = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(6, 4, 6, 4) };
        tester.Controls.Add(_testResult);
        tester.Controls.Add(testInput);

        Controls.Add(_list);
        Controls.Add(editor);
        Controls.Add(tester);
        Controls.Add(header);

        // Accept sessions dropped anywhere on the panel, not just on the tab above it. Dropping onto
        // the rules list is what Fiddler does and what people reach for; the tab strip alone is a
        // 20px target nobody finds.
        foreach (var target in new Control[] { this, _list })
        {
            target.AllowDrop = true;
            target.DragEnter += OnSessionDragOver;
            target.DragOver += OnSessionDragOver;
            target.DragDrop += OnSessionDrop;
        }

        LoadSelectedRule();
        UpdateTestButton();
        Palette.Apply(this);
    }

    // ------------------------------------------------------------------ settings

    /// <summary>The rule set as currently edited.</summary>
    public AutoResponderSettings Settings => new()
    {
        Enabled = _enabled.Checked,
        PassthroughUnmatched = _passthrough.Checked,
        Rules = [.. _rules.Select(rule => rule.Clone())],
    };

    /// <summary>Flips the master toggle from outside, for the Rules menu.</summary>
    public void SetEnabled(bool enabled) => _enabled.Checked = enabled;

    /// <summary>Flips the unmatched-passthrough toggle from outside, for the Rules menu.</summary>
    public void SetPassthroughUnmatched(bool passthrough) => _passthrough.Checked = passthrough;

    public void ApplySettings(AutoResponderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _applyingSettings = true;
        try
        {
            _enabled.Checked = settings.Enabled;
            _passthrough.Checked = settings.PassthroughUnmatched;
            _rules.Clear();
            _rules.AddRange(settings.Rules.Select(rule => rule.Clone()));
            RebuildList();
        }
        finally
        {
            _applyingSettings = false;
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Turns a captured session into a rule that replays exactly what came back, so "make this
    /// happen again" needs no typing. The response is stored beside the rules as raw HTTP.
    /// </summary>
    public void AddRuleFromSession(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Request?.Url is not { } url) return;

        var rule = new AutoResponderRule { Match = $"EXACT:{url}" };

        if (session.Response is { } response)
        {
            try
            {
                Directory.CreateDirectory(AutoResponderSettingsStore.ResponseDirectory);
                var path = Path.Combine(AutoResponderSettingsStore.ResponseDirectory, $"{rule.Id}.txt");
                File.WriteAllBytes(path, HttpWireFormat.Serialize(response));
                rule.Action = $"*raw:{path}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                rule.Action = $"*{response.StatusCode}";
                rule.Comment = Strings.AutoResponder.CapturedResponseSaveFailed(ex.Message);
            }
        }
        else
        {
            rule.Action = "*200";
        }

        AddRule(rule);
    }

    private ContextMenuStrip BuildRuleMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };

        var edit = new ToolStripMenuItem(Strings.AutoResponder.MenuEditResponse, null, (_, _) => EditResponse());
        var toggle = new ToolStripMenuItem(Strings.AutoResponder.MenuDisableRule, null, (_, _) => ToggleSelected());
        var up = Menus.Item(Strings.AutoResponder.MenuMoveUp, Strings.Shortcuts.CtrlUp, (_, _) => MoveSelected(-1));
        var down = Menus.Item(Strings.AutoResponder.MenuMoveDown, Strings.Shortcuts.CtrlDown, (_, _) => MoveSelected(1));
        var remove = Menus.Item(Strings.AutoResponder.MenuRemoveRule, Strings.Shortcuts.Delete, (_, _) => RemoveSelected());

        menu.Items.Add(new ToolStripMenuItem(Strings.AutoResponder.MenuAddRule, null, (_, _) => AddRule(new AutoResponderRule())));
        menu.Items.Add(edit);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(toggle);
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(remove);

        menu.Opening += (_, _) =>
        {
            var index = SelectedIndex;
            var rule = index is { } i ? _rules[i] : null;

            edit.Enabled = toggle.Enabled = remove.Enabled = rule is not null;
            up.Enabled = index is > 0;
            down.Enabled = index is { } position && position < _rules.Count - 1;
            toggle.Text = rule is { Enabled: false }
                ? Strings.AutoResponder.MenuEnableRule
                : Strings.AutoResponder.MenuDisableRule;
        };

        return menu;
    }

    private void ToggleSelected()
    {
        if (SelectedIndex is not { } index) return;
        _rules[index].Enabled = !_rules[index].Enabled;
        _list.Invalidate();
        OnEdited();
    }

    /// <summary>
    /// Opens the rule's canned response for editing and, on save, points the rule at the result.
    /// A rule that has no stored response yet gets a sensible starting one rather than an empty box.
    /// </summary>
    private void EditResponse()
    {
        if (SelectedIndex is not { } index) return;
        var rule = _rules[index];

        var editor = new AutoResponderResponseDialog(
            string.IsNullOrWhiteSpace(rule.Match) ? Strings.AutoResponder.NewRuleDescription : rule.Match,
            HttpWireFormat.ToEditableText(CurrentResponseFor(rule)));

        using (editor)
        {
            if (editor.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                Directory.CreateDirectory(AutoResponderSettingsStore.ResponseDirectory);
                var path = Path.Combine(AutoResponderSettingsStore.ResponseDirectory, $"{rule.Id}.txt");
                File.WriteAllBytes(path, editor.RawResponse);
                rule.Action = $"*raw:{path}";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, Strings.AutoResponder.ResponseSaveFailed(ex.Message),
                    Strings.AutoResponder.EditResponseCaption, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }

        _list.Items[index].SubItems[2].Text = rule.Action;
        LoadSelectedRule();
        OnEdited();
    }

    /// <summary>Whatever this rule currently answers with, so editing starts from what it does today.</summary>
    private static HttpResponseData? CurrentResponseFor(AutoResponderRule rule)
    {
        var action = rule.Action.Trim();

        if (action.StartsWith("*raw:", StringComparison.OrdinalIgnoreCase)
            || action.StartsWith("*replay:", StringComparison.OrdinalIgnoreCase))
        {
            var path = action[(action.IndexOf(':') + 1)..].Trim();
            try
            {
                if (File.Exists(path)
                    && HttpWireFormat.TryParseResponse(File.ReadAllBytes(path), out var stored, out _))
                    return stored;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through to a fresh response rather than blocking the edit.
            }
        }

        if (rule.Body is { } body)
            return HttpResponseData.Canned(200, Encoding.UTF8.GetBytes(body),
                rule.ContentType ?? "text/plain; charset=utf-8");

        // A status-only action still has a response worth starting from.
        var status = action.StartsWith('*') && int.TryParse(action[1..], out var code) ? code : 200;
        return HttpResponseData.Canned(status, []);
    }

    private static void OnSessionDragOver(object? sender, DragEventArgs e)
    {
        if (DraggedSession(e) is not null) e.Effect = DragDropEffects.Copy;
    }

    private void OnSessionDrop(object? sender, DragEventArgs e)
    {
        if (DraggedSession(e) is { } session) AddRuleFromSession(session);
    }

    /// <summary>The grid drags the <see cref="Session"/> object itself, not a serialised form of it.</summary>
    private static Session? DraggedSession(DragEventArgs e) =>
        e.Data?.GetData(typeof(Session)) is Session { Request.Url: not null } session ? session : null;

    /// <summary>Refreshes the Hits and Last match columns from the live engine.</summary>
    public void RefreshStatistics()
    {
        if (_rules.Count == 0 || _list.Items.Count != _rules.Count) return;

        for (var i = 0; i < _rules.Count; i++)
        {
            var stats = _responder.StatsFor(_rules[i].Id);
            var item = _list.Items[i];
            var hits = stats.Hits.ToString("N0");
            var last = stats.LastMatched?.ToString("HH:mm:ss") ?? string.Empty;
            if (item.SubItems[3].Text == hits && item.SubItems[4].Text == last) continue;

            item.SubItems[3].Text = hits;
            item.SubItems[4].Text = last;
        }
    }

    // --------------------------------------------------------------- rule editing

    private void AddRule(AutoResponderRule rule)
    {
        _rules.Add(rule);
        RebuildList();
        _list.Items[^1].Selected = true;
        _match.Focus();
        OnEdited();
    }

    private void RemoveSelected()
    {
        if (SelectedIndex is not { } index) return;
        _rules.RemoveAt(index);
        RebuildList();
        if (_list.Items.Count > 0) _list.Items[Math.Min(index, _list.Items.Count - 1)].Selected = true;
        OnEdited();
    }

    private void MoveSelected(int offset)
    {
        if (SelectedIndex is not { } index) return;
        var target = index + offset;
        if (target < 0 || target >= _rules.Count) return;

        (_rules[index], _rules[target]) = (_rules[target], _rules[index]);
        RebuildList();
        _list.Items[target].Selected = true;
        OnEdited();
    }

    private int? SelectedIndex => _list.SelectedIndices.Count > 0 ? _list.SelectedIndices[0] : null;

    private void LoadSelectedRule()
    {
        _loadingRule = true;
        try
        {
            var rule = SelectedIndex is { } index ? _rules[index] : null;
            _match.Text = rule?.Match ?? string.Empty;
            _action.Text = rule?.Action ?? string.Empty;
            _body.Text = rule?.Body ?? string.Empty;
            _contentType.Text = rule?.ContentType ?? string.Empty;

            var editable = rule is not null;
            _match.Enabled = _action.Enabled = _body.Enabled = _contentType.Enabled = editable;

            _browse.Enabled = editable;
            _tips.SetToolTip(_browse.Parent!, editable
                ? Strings.AutoResponder.BrowseTooltip
                : Strings.AutoResponder.BrowseDisabledTooltip);
        }
        finally
        {
            _loadingRule = false;
        }
    }

    /// <summary>The Test button only lights up for something the tester can actually resolve.</summary>
    private void UpdateTestButton()
    {
        var text = _testUrl.Text.Trim();
        var testable = IsTestableUrl(text);
        _testButton.Enabled = testable;

        _tips.SetToolTip(_testButton.Parent!, testable
            ? Strings.AutoResponder.TestTooltip
            : text.Length == 0
                ? Strings.AutoResponder.TestEmptyTooltip
                : Strings.AutoResponder.TestInvalidTooltip);

        // Clear a stale verdict as soon as the URL it described is edited.
        if (!testable) ShowTestResult(string.Empty, isProblem: false);
    }

    private static bool IsTestableUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https";

    private void OnEditorChanged()
    {
        if (_loadingRule || SelectedIndex is not { } index) return;

        var rule = _rules[index];
        rule.Match = _match.Text;
        rule.Action = _action.Text;
        rule.Body = _body.Text.Length == 0 ? null : _body.Text;
        rule.ContentType = _contentType.Text.Length == 0 ? null : _contentType.Text;

        _list.Items[index].SubItems[1].Text = rule.Match;
        _list.Items[index].SubItems[2].Text = rule.Action;
        OnEdited();
    }

    private void RebuildList()
    {
        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var rule in _rules)
            {
                var stats = _responder.StatsFor(rule.Id);
                var item = new ListViewItem(string.Empty) { Tag = rule };
                item.SubItems.Add(rule.Match);
                item.SubItems.Add(rule.Action);
                item.SubItems.Add(stats.Hits.ToString("N0"));
                item.SubItems.Add(stats.LastMatched?.ToString("HH:mm:ss") ?? string.Empty);
                _list.Items.Add(item);
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        LoadSelectedRule();
    }

    private void OnEdited()
    {
        if (_applyingSettings) return;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------- drawing

    private void DrawRule(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.Item?.Tag is not AutoResponderRule rule) return;

        using var background = new SolidBrush(e.Item.Selected ? Palette.Selection : Palette.Surface);
        e.Graphics.FillRectangle(background, e.Bounds);

        if (e.ColumnIndex == 0)
        {
            var glyph = new Rectangle(e.Bounds.X + (e.Bounds.Width - 13) / 2, e.Bounds.Y + (e.Bounds.Height - 13) / 2, 13, 13);
            DarkListView.DrawCheckGlyph(e.Graphics, glyph, rule.Enabled);
            return;
        }

        // A disabled rule stays readable but visibly inert, which is the state people forget about.
        TextRenderer.DrawText(e.Graphics, e.SubItem?.Text ?? string.Empty, Palette.Mono,
            Rectangle.Inflate(e.Bounds, -5, 0), rule.Enabled ? Palette.Text : Palette.TextDim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private void OnListMouseDown(object? sender, MouseEventArgs e)
    {
        // Only a left click toggles. A right click is how the context menu opens, and it used to
        // flip (and immediately save) whichever rule it landed on.
        if (e.Button != MouseButtons.Left) return;

        var hit = _list.HitTest(e.Location);
        if (hit.Item?.Tag is not AutoResponderRule rule) return;

        // Column 0 is the enable checkbox; anywhere else is an ordinary selection. Ask the hit test
        // which cell was clicked rather than comparing e.X to the column width, which ignores
        // horizontal scrolling and toggled a rule whose checkbox was scrolled out of view.
        if (!ReferenceEquals(hit.SubItem, hit.Item.SubItems[0])) return;

        rule.Enabled = !rule.Enabled;
        _list.Invalidate(hit.Item.Bounds);
        OnEdited();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            ToggleSelected();
        }
        else if (e.KeyCode == Keys.F2 || (e.KeyCode == Keys.Enter && SelectedIndex is not null))
        {
            EditResponse();
        }
        else if (e.KeyCode == Keys.Delete)
        {
            RemoveSelected();
        }
        else if (e.Control && e.KeyCode is Keys.Up or Keys.Down)
        {
            MoveSelected(e.KeyCode == Keys.Up ? -1 : 1);
        }
        else
        {
            return;
        }

        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    // -------------------------------------------------------------------- tester

    /// <summary>
    /// Answers "which rule wins for this URL, and what would it return" without issuing a request.
    /// Fiddler makes you guess; a regex rule that silently never fires is the usual reason people
    /// give up on the feature.
    /// </summary>
    private async Task RunTestAsync()
    {
        var text = _testUrl.Text.Trim();
        if (!IsTestableUrl(text)) return;
        var url = new Uri(text);

        var probe = new Session
        {
            Request = new HttpRequestData { Method = "GET", Url = url, RequestTarget = url.PathAndQuery },
            IsHttps = url.Scheme == "https",
            State = SessionState.SendingRequest,
        };

        // The rules currently typed into the panel, not the last set applied to the proxy, and
        // forced on so the answer is "what would these rules do" even while the master toggle is off.
        var settings = Settings;
        var live = settings.Enabled;
        settings.Enabled = true;

        var scratch = new AutoResponder();
        scratch.Apply(settings);
        var decision = scratch.Evaluate(probe, recordHit: false);

        var prefix = live ? string.Empty : Strings.AutoResponder.RulesOffPrefix;
        if (decision.Outcome == AutoResponderOutcome.Passthrough && decision.Rule is null)
        {
            ShowTestResult(Strings.AutoResponder.NoRuleMatches(prefix), isProblem: false);
            return;
        }

        var summary = prefix + decision.Description;
        if (decision.Delay > TimeSpan.Zero) summary += Strings.AutoResponder.TestDelay(decision.Delay.TotalMilliseconds);

        switch (decision.Outcome)
        {
            case AutoResponderOutcome.Respond:
            {
                var response = await decision.Action!
                    .BuildResponseAsync(decision.Rule!, decision.Match, probe.Request!, 128L * 1024 * 1024, default)
                    .ConfigureAwait(true);
                var preview = Preview(response);
                ShowTestResult(
                    Strings.AutoResponder.TestRespond(summary, response.StatusCode, response.ReasonPhrase, preview),
                    isProblem: response.StatusCode == 502);
                break;
            }

            case AutoResponderOutcome.Redirect:
                ShowTestResult(
                    Strings.AutoResponder.TestRedirect(summary, decision.Action!.ResolveTarget(decision.Match, url)), false);
                break;

            case AutoResponderOutcome.Drop:
            case AutoResponderOutcome.Reset:
                ShowTestResult(Strings.AutoResponder.TestKilled(summary), false);
                break;

            default:
                ShowTestResult(Strings.AutoResponder.TestPassthrough(summary), false);
                break;
        }
    }

    private static string Preview(HttpResponseData response)
    {
        if (response.Body.Length == 0) return Strings.AutoResponder.PreviewNoBody;

        var text = ContentCodec.LooksTextual(response.ContentType, response.Body)
            ? response.BodyAsText().ReplaceLineEndings(" ")
            : Strings.AutoResponder.PreviewBinary(response.Body.Length);

        return Strings.AutoResponder.Preview(text);
    }

    private void ShowTestResult(string text, bool isProblem)
    {
        _testResult.Text = text;
        _testResult.ForeColor = isProblem ? Palette.StatusServerError : Palette.TextDim;
    }

    // ------------------------------------------------------------ import / export

    private void ImportRules()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Strings.AutoResponder.ImportCaption,
            Filter = Strings.AutoResponder.RulesFilter,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var imported = AutoResponderSettingsStore.Load(dialog.FileName);
        if (imported is null)
        {
            MessageBox.Show(this, Strings.AutoResponder.UnreadableRuleSet,
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        ApplySettings(imported);
    }

    private void ExportRules()
    {
        using var dialog = new SaveFileDialog
        {
            Title = Strings.AutoResponder.ExportCaption,
            Filter = Strings.AutoResponder.RulesFilter,
            FileName = Strings.AutoResponder.ExportFileName,
            DefaultExt = "json",
            AddExtension = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        AutoResponderSettingsStore.Save(Settings, dialog.FileName);
    }

    private void BrowseForFile()
    {
        if (SelectedIndex is null) return;

        using var dialog = new OpenFileDialog { Title = Strings.AutoResponder.ChooseFileCaption };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _action.Text = dialog.FileName;
    }

    private void ShowHelp() => MessageBox.Show(this, Strings.AutoResponder.HelpBody,
        Strings.AutoResponder.HelpCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);

    // ------------------------------------------------------------------- plumbing

    /// <summary>
    /// Puts a trailing button in a holder that keeps a gap between it and the text box beside it.
    /// Docked controls sit flush against each other and ignore Margin, and a flat button touching a
    /// bordered text box reads as one clipped control rather than two.
    /// </summary>
    private static Panel Gutter(Control button)
    {
        var holder = new Panel { Dock = DockStyle.Right, Width = 82, Padding = new Padding(6, 0, 0, 0) };
        button.Dock = DockStyle.Fill;
        holder.Controls.Add(button);
        return holder;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _tips.Dispose();
        base.Dispose(disposing);
    }

    private TextBox NewEditor()
    {
        var box = new TextBox { Dock = DockStyle.Fill, Font = Palette.Mono, Enabled = false };
        box.TextChanged += (_, _) => OnEditorChanged();
        return box;
    }

    private static Panel EditorRow(string label, Control editor, Control? trailing)
    {
        // 30, not 28: a single-line TextBox forces its own height (26 here) whatever the dock says,
        // so a shorter row leaves the box and the button beside it at different heights and the
        // button looks clipped. Matches the tester row below.
        var row = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(0, 2, 6, 2) };
        row.Controls.Add(editor);
        if (trailing is not null) row.Controls.Add(Gutter(trailing));
        row.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Left,
            Width = 106,
            Padding = new Padding(0, 5, 0, 0),
        });
        return row;
    }
}
