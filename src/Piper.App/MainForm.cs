using System.Windows.Forms;
using Piper.App.Controls;
using Piper.App.Theme;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

namespace Piper.App;

public sealed class MainForm : Form
{
    private readonly SessionStore _store = new();
    private readonly ProxyOptions _options = new();
    private readonly CertificateAuthority _ca;
    private readonly ProxyServer _proxy;
    private readonly RequestExecutor _executor;

    private readonly SessionListView _sessionList;
    private readonly InspectorPanel _inspector;
    private readonly ComposerPanel _composer;
    private readonly FilterPanel _filterPanel;
    private readonly AutoResponderPanel _autoResponder;
    private TabPage _autoResponderPage = null!;
    private readonly TextBox _logView;
    private readonly DarkTabControl _rightTabs;

    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _captureStatusLabel;
    private readonly ToolStripStatusLabel _captureScopeLabel;
    private readonly ToolStripStatusLabel _breakpointsLabel;
    private readonly ToolStripStatusLabel _sessionsLabel;
    private readonly ToolStripStatusLabel _selectedSessionDetailsLabel;
    private ToolStripButton? _themeToggle;

    private static Image CaptureOnIcon = CreateDotIcon(Palette.StatusOk);
    private static Image CaptureOffIcon = CreateDotIcon(Palette.StatusServerError);
    private static Image ScopeIcon = CreateScopeIcon();
    private static Image BreakpointIcon = CreateBreakpointIcon();
    private static Image SessionsIcon = CreateSessionsIcon();

    private SystemProxy.Snapshot? _proxySnapshot;
    private CaptureScope _captureScope = CaptureScope.AllProcesses;
    private bool _captureEnabledOnStartup = true;
    private bool _captureToggleInProgress;
    private bool _restoreMaximized;
    private bool _shutdownInProgress;
    private bool _closeAfterShutdown;
    private bool _sessionsStatusUpdateQueued;

    public MainForm()
    {
        DoubleBuffered = true;
        Text = "Piper";
        Width = 1500;
        Height = 950;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        RestoreWindowBounds();
        if (_restoreMaximized) WindowState = FormWindowState.Maximized;

        if (StatusBarSettingsStore.Load() is { } statusBarSettings)
        {
            _captureEnabledOnStartup = statusBarSettings.CaptureEnabled;
            _captureScope = ParseCaptureScope(statusBarSettings.CaptureScope);
        }
        if (ProxyConfigurationSettingsStore.Load() is { } configuration)
            configuration.ApplyTo(_options);

        // Reads back the icon the compiler already embedded via <ApplicationIcon>, so the
        // title bar, taskbar and Alt-Tab all match the exe's file icon with no duplicate
        // resource to keep in sync.
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
        catch (Exception) { /* fall back to the WinForms default icon */ }

        _ca = CertificateAuthority.LoadOrCreate();
        _proxy = new ProxyServer(_options, _ca, _store);
        _executor = new RequestExecutor(_options, _store);

        _sessionList = new SessionListView(_store) { Dock = DockStyle.Fill };
        _inspector = new InspectorPanel { Dock = DockStyle.Fill };
        _composer = new ComposerPanel(_store, _executor) { Dock = DockStyle.Fill };
        _filterPanel = new FilterPanel { Dock = DockStyle.Fill };
        _autoResponder = new AutoResponderPanel(_options.AutoResponder) { Dock = DockStyle.Fill };

        _logView = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            Font = Palette.Mono,
            BorderStyle = BorderStyle.None,
        };

        _rightTabs = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        _rightTabs.TabPages.Add(NewPage("Inspectors", _inspector));
        _rightTabs.TabPages.Add(NewPage("Composer", _composer));
        var filtersPage = NewPage("Filters", _filterPanel);
        _rightTabs.TabPages.Add(filtersPage);
        _autoResponderPage = NewPage("AutoResponder", _autoResponder);
        _rightTabs.TabPages.Add(_autoResponderPage);
        _rightTabs.TabPages.Add(NewPage("Log", _logView));

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 4,
        };
        split.Panel1.Controls.Add(_sessionList);
        split.Panel2.Controls.Add(_rightTabs);
        _mainSplit = split;

        var toolbar = BuildToolbar();
        var statusBar = BuildStatusBar(out _statusLabel, out _captureStatusLabel, out _captureScopeLabel,
            out _breakpointsLabel, out _sessionsLabel, out _selectedSessionDetailsLabel);
        _captureStatusLabel.Click += (_, _) => ToggleCapture();
        _captureScopeLabel.Click += (_, _) => ShowCaptureScopeMenu();
        ApplyCaptureScope();

        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(BuildMenu());
        Controls.Add(statusBar);

        // Must stay after the controls are added: it walks the whole tree, and it is the only
        // thing that wires dropping, for both SAZ files and dragged sessions.
        EnableDrop(this);

        _sessionList.SelectionChanged += (_, session) => _inspector.Show(session);
        _sessionList.SelectedSessionsChanged += (_, _) => QueueSessionsStatusUpdate();
        _inspector.TimingChanged += (_, _) =>
        {
            _selectedSessionDetailsLabel.Text = _inspector.TimingText;
            _selectedSessionDetailsLabel.Visible = _inspector.TimingText.Length > 0;
        };
        _sessionList.SendToComposerRequested += (_, session) =>
        {
            _rightTabs.SelectedIndex = 1;
            _composer.LoadSession(session);
        };
        _sessionList.ResendRequested += (_, session) => _ = _composer.ResendAsync(session);
        _sessionList.SessionActivated += (_, _) => _rightTabs.SelectedIndex = 0;

        // Known simplification: applying a Filterset writes straight into the same FilterText
        // the grid's own ad-hoc filter box uses, so it overwrites anything typed there by hand,
        // and the two are never combined. FilterPanel stages edits until Actions applies them.
        _filterPanel.FilterChanged += (_, query) =>
        {
            _sessionList.FilterText = query;
            var admissionQuery = SearchQuery.Parse(query);
            _store.CompletedSessionFilter = admissionQuery.IsEmpty ? null : admissionQuery.Matches;
            FilterSettingsStore.Save(_filterPanel.Settings);
            _rightTabs.SetTabChecked(filtersPage, !admissionQuery.IsEmpty);
        };
        _filterPanel.SettingsChanged += (_, _) => FilterSettingsStore.Save(_filterPanel.Settings);

        // Restore the editable settings and then apply their saved enabled state so a restart
        // returns to the same filtered capture view.
        if (FilterSettingsStore.Load() is { } filterSettings)
            _filterPanel.ApplySettings(filterSettings);
        _filterPanel.ApplyCurrentFilterset();

        // Rules apply the moment they are edited, so the proxy and the panel never disagree about
        // what is live. The tab glyph is the reminder that traffic is being intercepted.
        _autoResponder.SettingsChanged += (_, _) =>
        {
            var settings = _autoResponder.Settings;
            _options.AutoResponder.Apply(settings);
            AutoResponderSettingsStore.Save(settings);
            _rightTabs.SetTabChecked(_autoResponderPage, settings.Enabled && settings.Rules.Count > 0);
            foreach (var warning in _options.AutoResponder.Warnings) AppendLog($"AutoResponder: {warning}");
        };

        if (AutoResponderSettingsStore.Load() is { } autoResponderSettings)
            _autoResponder.ApplySettings(autoResponderSettings);

        _sessionList.SendToAutoResponderRequested += (_, session) =>
        {
            _rightTabs.SelectedTab = _autoResponderPage;
            _autoResponder.AddRuleFromSession(session);
        };

        _proxy.Log += (_, message) => BeginInvoke(() => AppendLog(message));

        _statusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        _statusTimer.Start();

        KeyPreview = true;
        KeyDown += OnFormKeyDown;

        Palette.Apply(this);
        AppendLog($"Root CA: {_ca.RootPfxPath}");
        AppendLog(TrustStore.IsTrusted(_ca.RootCertificate)
            ? "Root CA is trusted by the current user. HTTPS decryption will work."
            : "Root CA is NOT trusted. HTTPS sites will fail until you use Tools > Configurations > HTTPS.");

        // Capture starts in OnShown, not here: anything that blocks in the constructor -
        // a dialog in particular - runs before Application.Run shows the window, and the
        // app comes up with no visible main form at all.
    }

    private readonly SplitContainer _mainSplit;
    private readonly System.Windows.Forms.Timer _statusTimer;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Palette.ApplyWindowChrome(this);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _mainSplit.SplitterDistance = (int)(_mainSplit.Width * 0.55);

        // A run that was killed, crashed, or was still open when Windows shut down leaves the
        // machine pointed at a Piper that is no longer listening, which the user sees as having
        // lost their connection. Undo it before anything else touches the settings.
        if (SystemProxy.RestoreLeftovers() is { } leftover)
            AppendLog($"Restored the system proxy that a previous session left pointing at {leftover}.");

        if (!EnsureTrustedRootForStartup())
        {
            UpdateCaptureStatus();
            return;
        }

        if (_captureEnabledOnStartup)
        {
            StartCapture();
        }
        else
        {
            UpdateCaptureStatus();
        }

        // Runs after the window is already visible (StartCapture doesn't block), so unlike the
        // constructor this is a safe place for something that could pop a dialog on failure.
        if (_proxy.IsRunning) EnableSystemProxy();
    }

    /// <summary>
    /// Gives the user an explicit startup choice before enabling capture. Trust installation
    /// changes Windows' certificate store, so it must never happen silently.
    /// </summary>
    private bool EnsureTrustedRootForStartup()
    {
        if (TrustStore.IsTrusted(_ca.RootCertificate)) return true;

        var answer = MessageBox.Show(this,
            "Piper's HTTPS inspection certificate is not trusted by Windows.\r\n\r\n"
            + "Click OK to install Piper's root certificate for this Windows user and restart Piper. "
            + "HTTPS capture will then work immediately.\r\n\r\n"
            + "This is a security-sensitive change: anything with access to Piper's private key "
            + "can impersonate HTTPS sites to this Windows account. Only continue on a machine you control.",
            "Trust Piper certificate",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.OK)
        {
            AppendLog("Root certificate is not trusted; startup capture remains off.");
            return false;
        }

        try
        {
            TrustStore.Install(_ca.RootCertificate);
            AppendLog($"Root certificate {_ca.RootCertificate.Thumbprint} added to the current user's trusted roots.");
            _closeAfterShutdown = true;
            Application.Restart();
            Close();
            return false;
        }
        catch (Exception ex)
        {
            AppendLog($"Could not trust the root certificate: {ex.Message}");
            MessageBox.Show(this,
                $"Piper could not install its root certificate, so capture has not started.\r\n\r\n{ex.Message}",
                "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static TabPage NewPage(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    /// <summary>
    /// Every control in the window is a drop target so a SAZ file can be dropped anywhere. In
    /// WinForms the innermost target under the pointer wins, so these handlers have to decide
    /// for both payload kinds: a file-only handler attached here swallowed dragged sessions on
    /// every control it touched, which is what stopped a session reaching the Composer body.
    /// </summary>
    private void EnableDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnDragOverAny;
        control.DragOver += OnDragOverAny;
        control.DragDrop += OnDragDropAny;
        foreach (Control child in control.Controls) EnableDrop(child);
    }

    private void OnDragOverAny(object? sender, DragEventArgs e)
    {
        switch (DropAction(e))
        {
            case DragDropAction.ImportSazFiles:
                e.Effect = DragDropEffects.Copy;
                break;
            case DragDropAction.LoadIntoComposer:
            case DragDropAction.AddAutoResponderRule:
                // Selecting on hover makes the target clear even when it was not the active tab.
                if (SessionDropTarget(e) is { } target) _rightTabs.SelectedTab = target;
                e.Effect = DragDropEffects.Copy;
                break;
            default:
                // Deliberately not DragDropEffects.None: an inner control may already have
                // claimed this payload, and the default when nobody claims it is None anyway.
                // Overwriting here would break the inspector's media drop.
                break;
        }
    }

    private void OnDragDropAny(object? sender, DragEventArgs e)
    {
        var session = DraggedSession(e.Data);
        switch (DropAction(e))
        {
            case DragDropAction.ImportSazFiles:
                ImportSazFiles(SazFilesFrom(e.Data));
                break;
            case DragDropAction.LoadIntoComposer when session is not null:
                if (SessionDropTarget(e) is { } composerTab) _rightTabs.SelectedTab = composerTab;
                _composer.LoadSession(session);
                break;
            case DragDropAction.AddAutoResponderRule when session is not null:
                _rightTabs.SelectedTab = _autoResponderPage;
                _autoResponder.AddRuleFromSession(session);
                break;
            default:
                break;
        }
    }

    private DragDropAction DropAction(DragEventArgs e) =>
        DragDropRouting.Resolve(SazFilesFrom(e.Data).Length, DraggedSession(e.Data), DropZone(e));

    /// <summary>The grid drags the <see cref="Session"/> object itself, not a serialised form.</summary>
    private static Session? DraggedSession(IDataObject? data) =>
        data?.GetData(typeof(Session)) as Session;

    private SessionDropZone DropZone(DragEventArgs e) => SessionDropTarget(e) switch
    {
        null => SessionDropZone.None,
        var target when target == _autoResponderPage => SessionDropZone.AutoResponder,
        _ => SessionDropZone.Composer,
    };

    private static string[] SazFilesFrom(IDataObject? data) => data?.GetData(DataFormats.FileDrop) is string[] paths
        ? paths.Where(SazFileRelay.IsSazFile).ToArray()
        : [];

    /// <summary>
    /// The tab a dragged session would land on, or null where a drop means nothing. Composer and
    /// AutoResponder both take sessions; every other tab refuses them.
    /// </summary>
    private TabPage? SessionDropTarget(DragEventArgs e)
    {
        var point = _rightTabs.PointToClient(new Point(e.X, e.Y));

        for (var i = 0; i < _rightTabs.TabCount; i++)
        {
            if (!_rightTabs.GetTabRect(i).Contains(point)) continue;
            var tab = _rightTabs.TabPages[i];
            return AcceptsSessions(tab) ? tab : null;
        }

        // Once hover has activated a tab that takes sessions, keep accepting the drop on its page too.
        var stripBottom = _rightTabs.TabCount > 0 ? _rightTabs.GetTabRect(0).Bottom : 0;
        return point.Y >= stripBottom && AcceptsSessions(_rightTabs.SelectedTab) ? _rightTabs.SelectedTab : null;
    }

    private bool AcceptsSessions(TabPage? tab) =>
        tab is not null && (tab == _autoResponderPage || tab.Controls.Contains(_composer));

    // ------------------------------------------------------------------ chrome

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Font = Palette.UiFont };

        var file = new ToolStripMenuItem("&File");
        var openSaz = new ToolStripMenuItem("&Open SAZ capture...", null, (_, _) => OpenSazCapture())
        {
            ShortcutKeys = Keys.Control | Keys.O,
        };
        file.DropDownItems.Add(openSaz);
        var saveSaz = new ToolStripMenuItem("&Save selected sessions as SAZ...", null,
            (_, _) => _sessionList.SaveSelectedSessionsAsSaz())
        {
            ShortcutKeys = Keys.Control | Keys.S,
        };
        file.DropDownItems.Add(saveSaz);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("&Clear sessions\tCtrl+X", null, (_, _) => _store.Clear());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());
        file.DropDownOpening += (_, _) =>
            saveSaz.Enabled = _sessionList.SelectedSessions.Any(session => session.Request is not null);

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("&Configurations...", null, (_, _) => ShowConfigurations());
        var hosts = new ToolStripMenuItem("&Hosts...", null, (_, _) => ShowHosts());
        tools.DropDownItems.Add(hosts);
        tools.DropDownItems.Add(new ToolStripSeparator());
        // Fiddler puts TextWizard on Ctrl+E; that is already send-to-Composer here, so Ctrl+T it is.
        tools.DropDownItems.Add(new ToolStripMenuItem("&TextWizard...", null, (_, _) => TextWizardDialog.Open(this))
        {
            ShortcutKeys = Keys.Control | Keys.T,
        });
        tools.DropDownOpening += (_, _) => hosts.Checked = _options.HostRemapping.Enabled;

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&Search syntax", null, (_, _) => ShowSearchHelp());
        help.DropDownItems.Add("&About", null, (_, _) => MessageBox.Show(this,
            "Piper\r\n\r\nAn HTTP(S) debugging proxy written from scratch on .NET 10.",
            "About Piper", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.AddRange([file, BuildRulesMenu(), tools, help]);
        return menu;
    }

    private ToolStripMenuItem BuildRulesMenu()
    {
        var rules = new ToolStripMenuItem("&Rules");
        rules.DropDownOpening += (_, _) =>
        {
            rules.DropDownItems.Clear();
            var userAgent = new ToolStripMenuItem("&User-Agent");
            userAgent.DropDownItems.Add(CreateUserAgentChoice("No override", null));
            userAgent.DropDownItems.Add(new ToolStripSeparator());
            foreach (var preset in UserAgentPresets)
                userAgent.DropDownItems.Add(CreateUserAgentChoice(preset.Name, preset.Value));
            userAgent.DropDownItems.Add(new ToolStripSeparator());
            userAgent.DropDownItems.Add(CreateCustomUserAgentChoice());
            rules.DropDownItems.Add(userAgent);
            rules.DropDownItems.Add(new ToolStripSeparator());

            var settings = _autoResponder.Settings;
            var automatic = new ToolStripMenuItem("Enable &automatic responses")
            {
                Checked = settings.Enabled,
                CheckOnClick = true,
            };
            automatic.Click += (_, _) => _autoResponder.SetEnabled(automatic.Checked);
            rules.DropDownItems.Add(automatic);

            var passthrough = new ToolStripMenuItem("&Unmatched requests pass through")
            {
                Checked = settings.PassthroughUnmatched,
                CheckOnClick = true,
            };
            passthrough.Click += (_, _) => _autoResponder.SetPassthroughUnmatched(passthrough.Checked);
            rules.DropDownItems.Add(passthrough);

            rules.DropDownItems.Add("A&utoResponder rules...", null, (_, _) => _rightTabs.SelectedTab = _autoResponderPage);
        };
        return rules;
    }

    private ToolStripMenuItem CreateUserAgentChoice(string name, string? value)
    {
        var choice = new ToolStripMenuItem(name)
        {
            Checked = string.Equals(_options.GlobalUserAgent, value, StringComparison.Ordinal),
        };
        choice.Click += (_, _) => SetGlobalUserAgent(value, name);
        return choice;
    }

    private ToolStripMenuItem CreateCustomUserAgentChoice()
    {
        var isPreset = _options.GlobalUserAgent is null
            || UserAgentPresets.Any(preset => string.Equals(preset.Value, _options.GlobalUserAgent, StringComparison.Ordinal));
        var choice = new ToolStripMenuItem("Custom...") { Checked = !isPreset };
        choice.Click += (_, _) => SetCustomUserAgent();
        return choice;
    }

    private void SetGlobalUserAgent(string? value, string name)
    {
        _options.GlobalUserAgent = value;
        ProxyConfigurationSettingsStore.Save(ProxyConfigurationSettings.From(_options));
        AppendLog(value is null ? "Global User-Agent override cleared." : $"Global User-Agent rule set to {name}.");
    }

    private void SetCustomUserAgent()
    {
        using var prompt = new Form
        {
            Text = "Custom User-Agent",
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(640, 180),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        var label = new Label { Dock = DockStyle.Top, Height = 38, Text = "Use this User-Agent for all proxied requests:", Padding = new Padding(14, 12, 0, 0) };
        var value = new TextBox { Dock = DockStyle.Top, Height = 30, Text = _options.GlobalUserAgent ?? string.Empty, Margin = new Padding(12), Font = Palette.Mono };
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Size = new Size(100, 34) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 34) };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(12, 12, 12, 10) };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawLine(pen, 0, 0, e.ClipRectangle.Width, 0);
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 216, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        footer.Controls.Add(actions);
        prompt.Controls.Add(value);
        prompt.Controls.Add(label);
        prompt.Controls.Add(footer);
        prompt.AcceptButton = save;
        prompt.CancelButton = cancel;
        Palette.Apply(prompt);

        if (prompt.ShowDialog(this) != DialogResult.OK) return;
        SetGlobalUserAgent(string.IsNullOrWhiteSpace(value.Text) ? null : value.Text.Trim(), "Custom");
    }

    private void ShowConfigurations()
    {
        using var dialog = new ConfigurationsDialog(_options, _captureEnabledOnStartup, _captureScope.ToString(),
            TrustRootCertificate, UntrustRootCertificate, ExportRootCertificate, OpenCertificateFolder);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        dialog.ApplyTo(_options);
        _captureEnabledOnStartup = dialog.CaptureOnStartup;
        _captureScope = ParseCaptureScope(dialog.CaptureScope);
        ApplyCaptureScope();
        StatusBarSettingsStore.Save(new StatusBarSettings
        {
            CaptureEnabled = _captureEnabledOnStartup,
            CaptureScope = _captureScope.ToString(),
        });
        ProxyConfigurationSettingsStore.Save(ProxyConfigurationSettings.From(_options));
        AppendLog("Configurations saved. HTTPS protocol changes apply to new connections.");
    }

    private void ShowHosts()
    {
        using var dialog = new HostsDialog(_options.HostRemapping.Export());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _options.HostRemapping.Apply(dialog.Settings);
        ProxyConfigurationSettingsStore.Save(ProxyConfigurationSettings.From(_options));
        AppendLog(_options.HostRemapping.Enabled
            ? "Host remapping enabled. New origin connections will use the configured mappings."
            : "Host remapping disabled.");
    }

    private void OpenCertificateFolder()
    {
        var folder = Path.GetDirectoryName(_ca.RootPfxPath);
        if (folder is not null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private static readonly (string Name, string Value)[] UserAgentPresets =
    [
        ("Chrome (Windows)", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"),
        ("Microsoft Edge (Windows)", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0"),
        ("Firefox (Windows)", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0"),
        ("Chrome (Android)", "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36"),
        ("Safari (iPhone)", "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1"),
    ];

    private void OpenSazCapture()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Open Fiddler SAZ capture",
            Filter = "Fiddler SAZ captures (*.saz)|*.saz|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) ImportSazFiles(dialog.FileNames);
    }

    /// <summary>Imports SAZ files on a worker thread, then adds their completed sessions to the UI store.</summary>
    public async void ImportSazFiles(IEnumerable<string> filePaths)
    {
        var paths = filePaths.Where(SazFileRelay.IsSazFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) return;

        BringToFront();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();

        foreach (var path in paths)
        {
            var result = await Task.Run(() => SazImporter.Import(path));
            foreach (var session in result.Sessions) _store.Add(session);

            AppendLog($"Imported {result.Sessions.Count:N0} session(s) from {Path.GetFileName(path)}.");
            foreach (var warning in result.Warnings)
                AppendLog($"SAZ import warning ({Path.GetFileName(path)}): {warning}");
        }
        _rightTabs.SelectedIndex = 0;
    }

    private ToolStrip BuildToolbar()
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Font = Palette.UiFont };

        var clear = new ToolStripButton("Clear") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        clear.Click += (_, _) => _store.Clear();

        var composer = new ToolStripButton("Composer  (Ctrl+K)") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        composer.Click += (_, _) =>
        {
            _rightTabs.SelectedIndex = 1;
            _composer.FocusSearch();
        };

        _themeToggle = new ToolStripButton
        {
            Alignment = ToolStripItemAlignment.Right,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
        };
        _themeToggle.Click += (_, _) => ToggleTheme();
        UpdateThemeToggle();

        toolbar.Items.AddRange([
            clear, new ToolStripSeparator(),
            composer,
            _themeToggle,
        ]);
        return toolbar;
    }

    private void ToggleTheme()
    {
        Palette.ToggleMode();
        Palette.Apply(this);
        RefreshStatusIcons();
        UpdateThemeToggle();
        InvalidateTheme(this);
    }

    private void UpdateThemeToggle()
    {
        if (_themeToggle is null) return;

        var target = Palette.IsLightMode ? "Dark" : "Light";
        _themeToggle.Text = target + " mode";
        _themeToggle.ToolTipText = "Switch to " + target.ToLowerInvariant() + " mode";
    }

    private void RefreshStatusIcons()
    {
        var oldCaptureOn = CaptureOnIcon;
        var oldCaptureOff = CaptureOffIcon;
        var oldScope = ScopeIcon;
        var oldBreakpoints = BreakpointIcon;
        var oldSessions = SessionsIcon;

        CaptureOnIcon = CreateDotIcon(Palette.StatusOk);
        CaptureOffIcon = CreateDotIcon(Palette.StatusServerError);
        ScopeIcon = CreateScopeIcon();
        BreakpointIcon = CreateBreakpointIcon();
        SessionsIcon = CreateSessionsIcon();
        _captureScopeLabel.Image = ScopeIcon;
        _breakpointsLabel.Image = BreakpointIcon;
        _sessionsLabel.Image = SessionsIcon;
        UpdateCaptureStatus();

        oldCaptureOn.Dispose();
        oldCaptureOff.Dispose();
        oldScope.Dispose();
        oldBreakpoints.Dispose();
        oldSessions.Dispose();
    }

    private static void InvalidateTheme(Control control)
    {
        control.Invalidate(true);
        foreach (Control child in control.Controls) InvalidateTheme(child);
    }

    private static StatusStrip BuildStatusBar(out ToolStripStatusLabel status,
        out ToolStripStatusLabel capture, out ToolStripStatusLabel scope,
        out ToolStripStatusLabel breakpoints, out ToolStripStatusLabel sessions,
        out ToolStripStatusLabel selectedSessionDetails)
    {
        var bar = new StatusStrip { Font = Palette.UiFont };
        status = new ToolStripStatusLabel("Starting...") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        capture = NewStatusAction("Capturing", CaptureOnIcon, "Click to start or stop proxy capture.");
        scope = NewStatusAction("All Processes", ScopeIcon, "Click to choose which processes are shown.");
        breakpoints = new ToolStripStatusLabel("Breakpoints: None", BreakpointIcon)
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left,
            ToolTipText = "Breakpoint controls are coming soon.",
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
        };
        sessions = new ToolStripStatusLabel("0 sessions", SessionsIcon)
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left,
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
        };
        selectedSessionDetails = new ToolStripStatusLabel
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left,
            Font = Palette.Mono,
            ForeColor = Palette.TextDim,
            Visible = false,
            ToolTipText = "Timing and transfer details for the selected session.",
        };
        // The live proxy status expands through the centre; placing selected-session details
        // after it pins the timing/transfer summary to the status bar's right edge.
        bar.Items.AddRange([capture, scope, breakpoints, sessions, status, selectedSessionDetails]);
        return bar;
    }

    private void RestoreWindowBounds()
    {
        if (WindowLayoutStore.Load() is not { } layout) return;
        var bounds = layout.ToRectangle();
        if (bounds.Width < MinimumSize.Width || bounds.Height < MinimumSize.Height) return;

        // A monitor may have been disconnected since the last run. Require a meaningful visible
        // portion before restoring, otherwise retain the centred default window.
        var visible = Screen.AllScreens
            .Select(screen => Rectangle.Intersect(screen.WorkingArea, bounds))
            .Any(area => area.Width >= 48 && area.Height >= 48);
        if (!visible) return;

        StartPosition = FormStartPosition.Manual;
        Bounds = bounds;
        _restoreMaximized = layout.Maximized;
    }

    private void SaveWindowBounds()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (bounds.Width >= MinimumSize.Width && bounds.Height >= MinimumSize.Height)
            WindowLayoutStore.Save(bounds, WindowState == FormWindowState.Maximized);
    }

    private static ToolStripStatusLabel NewStatusAction(string text, Image image, string tooltip) => new(text, image)
    {
        BorderSides = ToolStripStatusLabelBorderSides.Left,
        ToolTipText = tooltip,
        DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
        TextImageRelation = TextImageRelation.ImageBeforeText,
    };

    private static Bitmap CreateDotIcon(Color color)
    {
        var image = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 3, 3, 10, 10);
        using var pen = new Pen(Palette.Border);
        graphics.DrawEllipse(pen, 3, 3, 10, 10);
        return image;
    }

    private static Bitmap CreateScopeIcon()
    {
        var image = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(image);
        using var brush = new SolidBrush(Palette.TextDim);
        graphics.FillEllipse(brush, 2, 2, 4, 4);
        graphics.FillEllipse(brush, 10, 2, 4, 4);
        graphics.FillEllipse(brush, 6, 10, 4, 4);
        using var pen = new Pen(Palette.TextDim);
        graphics.DrawLine(pen, 4, 6, 8, 10);
        graphics.DrawLine(pen, 12, 6, 8, 10);
        return image;
    }

    private static Bitmap CreateBreakpointIcon()
    {
        var image = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(image);
        using var brush = new SolidBrush(Palette.StatusClientError);
        graphics.FillRectangle(brush, 3, 3, 3, 10);
        graphics.FillRectangle(brush, 10, 3, 3, 10);
        return image;
    }

    private static Bitmap CreateSessionsIcon()
    {
        var image = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(image);
        using var pen = new Pen(Palette.TextDim, 2);
        graphics.DrawLine(pen, 3, 4, 13, 4);
        graphics.DrawLine(pen, 3, 8, 13, 8);
        graphics.DrawLine(pen, 3, 12, 13, 12);
        return image;
    }

    // ----------------------------------------------------------------- actions

    private void StartCapture()
    {
        try
        {
            _proxy.Start();
            UpdateCaptureStatus();
        }
        catch (Exception ex)
        {
            // Reported in the log and the status bar rather than a dialog, so a busy port
            // never blocks the UI and the full exception stays available for diagnosis.
            AppendLog($"Could not listen on 127.0.0.1:{_options.Port} - {ex.GetType().Name}: {ex.Message}");
            AppendLog("Another proxy may be using the port. Change it, or stop the other proxy, then press F12.");
            UpdateCaptureStatus();
            _rightTabs.SelectedIndex = 4; // surface the Log tab (Inspectors, Composer, Filters, AutoResponder, Log)
        }
    }

    private async void ToggleCapture()
    {
        if (_captureToggleInProgress) return;

        _captureToggleInProgress = true;
        var enabling = !_proxy.IsRunning;
        _captureStatusLabel.Text = enabling ? "Enabling proxy..." : "Disabling proxy...";
        _captureStatusLabel.ForeColor = Palette.TextDim;
        _captureStatusLabel.Enabled = false;

        // Starting capture is synchronous, so yield once to let the transition label paint before
        // doing the work that may briefly block the UI thread.
        await Task.Yield();

        try
        {
            if (_proxy.IsRunning)
            {
                await _proxy.StopAsync();
                if (RestoreSystemProxy())
                    AppendLog("Capture stopped and the previous system proxy settings were restored.");
            }
            else
            {
                StartCapture();
                if (_proxy.IsRunning && _proxySnapshot is null) EnableSystemProxy();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Could not change capture state: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _captureToggleInProgress = false;
            _captureStatusLabel.Enabled = true;
            UpdateCaptureStatus();
            SaveStatusBarSettings();
        }
    }

    private void UpdateCaptureStatus()
    {
        if (_captureToggleInProgress) return;
        _captureStatusLabel.Text = _proxy.IsRunning ? "Capturing" : "Not Capturing";
        _captureStatusLabel.ForeColor = _proxy.IsRunning ? Palette.StatusOk : Palette.StatusServerError;
        _captureStatusLabel.Image = _proxy.IsRunning ? CaptureOnIcon : CaptureOffIcon;
    }

    private void ShowCaptureScopeMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        foreach (var scope in Enum.GetValues<CaptureScope>())
        {
            var choice = scope;
            var item = new ToolStripMenuItem(CaptureScopeText(choice)) { Checked = choice == _captureScope };
            item.Click += (_, _) => SetCaptureScope(choice);
            menu.Items.Add(item);
        }

        menu.Show(Cursor.Position);
    }

    private void SetCaptureScope(CaptureScope scope)
    {
        _captureScope = scope;
        ApplyCaptureScope();
        SaveStatusBarSettings();
        AppendLog($"Capture scope set to {CaptureScopeText(scope)}.");
    }

    private void ApplyCaptureScope()
    {
        _captureScopeLabel.Text = CaptureScopeText(_captureScope);
        Func<Session, bool>? scopeFilter = _captureScope switch
        {
            CaptureScope.AllProcesses => null,
            CaptureScope.WebBrowsers => session => IsBrowserProcess(session.ProcessName),
            CaptureScope.NonBrowsers => session => !IsBrowserProcess(session.ProcessName),
            CaptureScope.HideAll => _ => false,
            _ => null,
        };
        // Composer sends are user-initiated requests. Keep them visible regardless of a process
        // scope (the Composer is Piper itself, not a browser process), except when the user has
        // explicitly chosen to hide every session.
        var sessionFilter = _captureScope == CaptureScope.HideAll || scopeFilter is null
            ? scopeFilter
            : session => session.IsComposed || scopeFilter(session);
        _store.CaptureFilter = sessionFilter;
        _sessionList.VisibilityFilter = sessionFilter;
    }

    private void SaveStatusBarSettings() => StatusBarSettingsStore.Save(new StatusBarSettings
    {
        CaptureEnabled = _proxy.IsRunning,
        CaptureScope = _captureScope.ToString(),
    });

    private static CaptureScope ParseCaptureScope(string? value) =>
        Enum.TryParse<CaptureScope>(value, ignoreCase: false, out var scope)
        && Enum.IsDefined(scope)
            ? scope
            : CaptureScope.AllProcesses;

    private static string CaptureScopeText(CaptureScope scope) => scope switch
    {
        CaptureScope.AllProcesses => "All Processes",
        CaptureScope.WebBrowsers => "Web Browsers",
        CaptureScope.NonBrowsers => "Non-Browsers",
        CaptureScope.HideAll => "Hide All",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    private static bool IsBrowserProcess(string processName) => processName.ToLowerInvariant() is
        "chrome" or "msedge" or "firefox" or "brave" or "opera" or "vivaldi" or "iexplore" or "browser";

    private void EnableSystemProxy()
    {
        var endpoint = $"127.0.0.1:{_proxy.Endpoint!.Port}";
        try
        {
            // Captured against our own endpoint so settings a previous Piper left behind are never
            // mistaken for the user's own, and written to disk before the registry changes so an
            // abrupt exit still leaves something to undo.
            _proxySnapshot = SystemProxy.Capture(endpoint);
            SystemProxyBackupStore.Save(SystemProxyBackup.From(endpoint, _proxySnapshot));
            SystemProxy.Enable(endpoint);
            AppendLog($"System proxy set to {endpoint}.");
        }
        catch (Exception ex)
        {
            // Enable writes several values, so put back whatever was captured rather than leaving
            // the machine half-pointed at a proxy that is not running.
            try { RestoreSystemProxy(); }
            catch (Exception restoreFailure) { AppendLog($"Could not undo the partial change: {restoreFailure.Message}"); }

            AppendLog($"Could not set the system proxy: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    /// <summary>
    /// Puts the system proxy back to what it was before Piper took it over and drops the on-disk
    /// undo record. Returns false when Piper was not holding it. Safe to call more than once.
    /// </summary>
    private bool RestoreSystemProxy()
    {
        if (_proxySnapshot is not { } snapshot) return false;

        SystemProxy.Restore(snapshot);
        _proxySnapshot = null;
        SystemProxyBackupStore.Clear();
        return true;
    }

    private void TrustRootCertificate()
    {
        if (TrustStore.IsTrusted(_ca.RootCertificate))
        {
            MessageBox.Show(this, "The Piper root certificate is already trusted.",
                "Piper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            "Add the Piper root certificate to your user's Trusted Root store?\r\n\r\n"
            + "This is required to inspect HTTPS traffic, and it is a real security tradeoff: "
            + "anything holding the matching private key - which is stored unencrypted in your "
            + "user profile - can impersonate any HTTPS site to this Windows account.\r\n\r\n"
            + "Only do this on a machine you control, and use Tools > Remove trusted root "
            + "certificate when you are finished.\r\n\r\n"
            + $"Private key location:\r\n{_ca.RootPfxPath}\r\n\r\n"
            + $"Thumbprint:\r\n{_ca.RootCertificate.Thumbprint}",
            "Trust the Piper root certificate",
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.OK) return;

        try
        {
            TrustStore.Install(_ca.RootCertificate);
            AppendLog($"Root certificate {_ca.RootCertificate.Thumbprint} added to the current user's trusted roots.");
        }
        catch (Exception ex)
        {
            AppendLog($"Trusting the root failed: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UntrustRootCertificate()
    {
        try
        {
            var removed = TrustStore.Uninstall();
            AppendLog($"Removed {removed} Piper root certificate(s) from the trusted roots.");
            MessageBox.Show(this, $"Removed {removed} Piper root certificate(s).",
                "Piper", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportRootCertificate()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Export the Piper root certificate",
            Filter = "Certificate (*.cer)|*.cer|All files (*.*)|*.*",
            FileName = "Piper-Root.cer",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _ca.ExportRootTo(dialog.FileName);
            AppendLog($"Root certificate exported to {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowSearchHelp()
    {
        const string help = """
            The same query grammar works in the session filter and the Composer search.
            Terms are combined with AND.

              checkout               substring across URL, headers and text bodies
              "exact phrase"         quoted literal
              /orders\/[0-9]+/       regular expression

              method:POST            also m:
              method:GET|POST        alternatives
              host:api.example.com   also h:
              path:/v2/users
              url:token
              status:404             also s:
              status:4xx             class shorthand
              status:>=400           comparisons: > >= < <=
              status:200..299        ranges
              ct:json                content type

              header:Authorization   name or value, request or response
              header:Accept=json     specific header, specific value
              reqheader: respheader: restrict to one side

              body:user_id           either body
              req:  resp:            restrict to one side

              size:>100kb            response size; b/kb/mb/gb suffixes
              reqsize:>0
              dur:>500               duration in milliseconds
              id:1234

              is:https  is:http  is:tunnel  is:composed  is:captured
              is:error  is:ok  is:redirect  is:pending  is:complete
              is:json  is:xml  is:html  is:image  is:script  is:css
              is:slow  is:cached  is:body

              -host:cdn.example.com  negate any term with - or !

            Example:
              method:POST host:api status:>=400 -is:image body:"order"
            """;

        using var dialog = new Form
        {
            Text = "Search syntax",
            Width = 660,
            Height = 720,
            StartPosition = FormStartPosition.CenterParent,
        };
        var text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Palette.Mono,
            Text = help.ReplaceLineEndings("\r\n"),
            BorderStyle = BorderStyle.None,
        };
        dialog.Controls.Add(text);
        Palette.Apply(dialog);
        dialog.ShowDialog(this);
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F12)
        {
            ToggleCapture();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.K)
        {
            _rightTabs.SelectedIndex = 1;
            _composer.FocusSearch();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode == Keys.R && _sessionList.ResendSelected())
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.X)
        {
            _store.Clear();
            e.Handled = true;
        }
    }

    private void AppendLog(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}";
        if (_logView.TextLength > 200_000) _logView.Clear();
        _logView.AppendText(line);
    }

    private void UpdateStatus()
    {
        _statusLabel.Text = _proxy.IsRunning
            ? $"Listening on {_proxy.Endpoint}   -   HTTPS decryption {(_options.DecryptHttps ? "on" : "off")}"
            : "Not capturing";
        UpdateCaptureStatus();
        UpdateSessionsStatus();
        _autoResponder.RefreshStatistics();
    }

    private void UpdateSessionsStatus()
    {
        var total = _store.Count;
        var selected = _sessionList.SelectedSessionCount;
        _sessionsLabel.Text = selected == 0
            ? $"{total:N0} sessions"
            : $"{selected:N0} / {total:N0} sessions";
    }

    private void QueueSessionsStatusUpdate()
    {
        if (_sessionsStatusUpdateQueued) return;
        if (!IsHandleCreated)
        {
            UpdateSessionsStatus();
            return;
        }

        _sessionsStatusUpdateQueued = true;
        BeginInvoke(() =>
        {
            _sessionsStatusUpdateQueued = false;
            if (!IsDisposed) UpdateSessionsStatus();
        });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closeAfterShutdown)
        {
            base.OnFormClosing(e);
            return;
        }

        SaveWindowBounds();

        // Windows shutdown, Task Manager and Application.Exit do not honour a cancelled close:
        // deferring the cleanup to a continuation there would let the process go away with the
        // system proxy still pointed at Piper. Take the blocking path instead.
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing
            or CloseReason.ApplicationExitCall)
        {
            _closeAfterShutdown = true;
            FilterSettingsStore.Save(_filterPanel.Settings);
            SaveStatusBarSettings();
            try { RestoreSystemProxy(); }
            catch (Exception ex) { AppendLog($"Could not restore the system proxy: {ex.Message}"); }

            base.OnFormClosing(e);
            return;
        }

        // Restoring a system proxy can make WinINET notify several applications, which may
        // briefly block. Keep the window responsive and make the required cleanup explicit
        // instead of making the close gesture look like the application has frozen.
        e.Cancel = true;
        if (_shutdownInProgress)
        {
            base.OnFormClosing(e);
            return;
        }

        FilterSettingsStore.Save(_filterPanel.Settings);
        SaveStatusBarSettings();
        _shutdownInProgress = true;
        _ = ShutdownAndCloseAsync();
        base.OnFormClosing(e);
    }

    private async Task ShutdownAndCloseAsync()
    {
        _statusTimer.Stop();
        Enabled = false;
        Text = "Piper - Closing...";

        try
        {
            if (_proxySnapshot is { } snapshot)
            {
                _statusLabel.Text = "Restoring your system proxy...";
                _sessionsLabel.Text = "Please wait";
                await Task.Yield(); // Let the status change paint before WinINET is notified.

                await Task.Run(() => SystemProxy.Restore(snapshot));
                _proxySnapshot = null;
                SystemProxyBackupStore.Clear();
            }

            if (_proxy.IsRunning)
            {
                _statusLabel.Text = "Stopping capture...";
                _sessionsLabel.Text = "Please wait";
                await Task.Yield();
                await _proxy.StopAsync();
            }

            _closeAfterShutdown = true;
            Close();
        }
        catch (Exception ex)
        {
            // Do not silently exit if the Windows proxy could not be restored: leaving it
            // pointed at a closed Piper instance would break the user's network access.
            _shutdownInProgress = false;
            Enabled = true;
            Text = "Piper";
            _statusTimer.Start();
            UpdateStatus();
            AppendLog($"Could not finish shutdown: {ex.Message}");
            MessageBox.Show(this,
                $"Piper could not restore the system proxy, so it will remain open.\r\n\r\n{ex.Message}",
                "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private enum CaptureScope
    {
        AllProcesses,
        WebBrowsers,
        NonBrowsers,
        HideAll,
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _statusTimer.Dispose();
            _ca.Dispose();
        }
        base.Dispose(disposing);
    }
}
