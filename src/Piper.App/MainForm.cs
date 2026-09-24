using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Windows.Forms;
using Piper.App.Controls;
using Piper.App.Theme;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;
using Piper.Core.Telemetry;

namespace Piper.App;

public sealed class MainForm : Form, IMessageFilter
{
    private readonly SessionStore _store = new();
    private bool _reportedFirstSession;
    private int _lastScannedSessionCount;
    private readonly ProxyOptions _options = new();
    private readonly CertificateAuthority _ca;
    private readonly ProxyServer _proxy;
    private readonly RequestExecutor _executor;
    private readonly UpdateService _updates;

    private readonly SessionListView _sessionList;
    private readonly InspectorPanel _inspector;
    private readonly ComposerPanel _composer;
    private readonly FilterPanel _filterPanel;
    private readonly AutoResponderPanel _autoResponder;
    private TabPage _autoResponderPage = null!;
    private readonly TextBox _logView;
    private readonly DarkTabControl _rightTabs;

    private readonly ToolStripStatusLabel _zoomLabel;
    private ToolStripMenuItem? _zoomInItem;
    private ToolStripMenuItem? _zoomOutItem;
    private ToolStripMenuItem? _zoomResetItem;
    private int _wheelRemainder;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _captureStatusLabel;
    private readonly ToolStripStatusLabel _captureScopeLabel;
    private readonly ToolStripStatusLabel _breakpointsLabel;
    private readonly ToolStripStatusLabel _sessionsLabel;
    private readonly ToolStripStatusLabel _selectedSessionDetailsLabel;
    private ToolStripButton? _themeToggle;
    private ToolStripMenuItem? _checkForUpdatesMenuItem;
    private readonly CancellationTokenSource _updatesCancellation = new();

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
    private bool _updateCheckInProgress;
    private bool _resourcesDisposed;

    public MainForm()
    {
        // Before any control exists: every control that asks the palette for a font during
        // construction then gets the saved size straight away, so the existing Palette.Apply below
        // has nothing left to correct.
        if (FontScaleSettingsStore.Load() is { } fontScale)
        {
            FontScale.SetStep(fontScale.Step);
            FontScale.WheelEnabled = fontScale.WheelZoomEnabled;
        }
        Palette.RescaleFonts();

        DoubleBuffered = true;
        Text = Strings.App.Name;
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
        // Update traffic is still recorded in the shared store, but it must not inherit mutable
        // proxy settings such as host remapping or disabled certificate validation.
        _updates = new UpdateService(new RequestExecutor(new ProxyOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            EnableHttp2Upstream = false,
            ValidateUpstreamCertificates = true,
        }, _store));

        _sessionList = new SessionListView(_store) { Dock = DockStyle.Fill };
        _inspector = new InspectorPanel { Dock = DockStyle.Fill };
        _composer = new ComposerPanel(_executor) { Dock = DockStyle.Fill };
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
        var inspectorsPage = NewPage(Strings.Tabs.Inspectors, _inspector);
        _rightTabs.TabPages.Add(inspectorsPage);
        _rightTabs.TabPages.Add(NewPage(Strings.Tabs.Composer, _composer));
        var filtersPage = NewPage(Strings.Tabs.Filters, _filterPanel);
        _rightTabs.TabPages.Add(filtersPage);
        _autoResponderPage = NewPage(Strings.Tabs.AutoResponder, _autoResponder);
        _rightTabs.TabPages.Add(_autoResponderPage);
        _rightTabs.TabPages.Add(NewPage(Strings.Tabs.Log, _logView));

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
            out _breakpointsLabel, out _sessionsLabel, out _selectedSessionDetailsLabel, out _zoomLabel);
        _captureStatusLabel.Click += (_, _) => ToggleCapture();
        _captureScopeLabel.Click += (_, _) => ShowCaptureScopeMenu();
        ApplyCaptureScope();
        _rightTabs.AllowDrop = true;
        _rightTabs.DragEnter += OnSessionDragEnter;
        _rightTabs.DragOver += OnSessionDragOver;
        _rightTabs.DragDrop += OnSessionDragDrop;

        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(BuildMenu());
        Controls.Add(statusBar);
        EnableSazFileDrop(this);

        _sessionList.SelectionChanged += (_, session) => _inspector.Show(session);
        _sessionList.SelectedSessionRefreshed += (_, refresh) => _inspector.Refresh(refresh.Session, refresh.Reload);
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
        // Double-click means "show me this", so it reveals the inspector the selection has
        // already been loaded into. Sending the row to the Composer instead made the gesture a
        // duplicate of middle-click and Ctrl+E, and left no row gesture that brought the
        // inspector back.
        _sessionList.SessionActivated += (_, _) => _rightTabs.SelectedTab = inspectorsPage;

        // The filterset gets its own visibility slot rather than the grid's ad-hoc filter box.
        // Writing it into FilterText destroyed whatever the user had typed there, and worse, let
        // them edit or clear the box and believe the filterset was off while CompletedSessionFilter
        // kept discarding traffic at admission. Both now come from the same query and the search
        // box stays theirs. FilterPanel stages criteria edits until Actions or the Use Filters
        // switch applies them.
        _filterPanel.FilterChanged += (_, query) =>
        {
            var admissionQuery = SearchQuery.Parse(query);
            _sessionList.FiltersetFilter = admissionQuery.IsEmpty ? null : admissionQuery.Matches;
            _store.CompletedSessionFilter = admissionQuery.IsEmpty ? null : admissionQuery.Matches;
            // Keep this save unconditional. The Use Filters checkbox no longer raises
            // SettingsChanged, so this is the only path that persists it: making the save depend
            // on a non-empty query would stop unticking from being written, and the filterset
            // would come back applied on the next start.
            FilterSettingsStore.Save(_filterPanel.Settings);
            _rightTabs.SetTabChecked(filtersPage, !admissionQuery.IsEmpty);
            // Running or stopping the filterset makes it the authority on what is hidden, so the
            // "Hide this host" previews give way to it: a running hide-mode list keeps hiding them,
            // and Show all sessions shows them again.
            ClearSessionHiddenHosts();
        };
        _filterPanel.SettingsChanged += (_, _) =>
        {
            FilterSettingsStore.Save(_filterPanel.Settings);
            PruneSessionHiddenHosts();
        };
        _sessionList.HideHostRequested += (_, host) => HideHost(host);
        _sessionList.ShowSessionHiddenHostsRequested += (_, _) =>
        {
            ClearSessionHiddenHosts();
            AppendLog(Strings.Log.SessionHiddenHostsShown);
        };

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
            foreach (var warning in _options.AutoResponder.Warnings) AppendLog(Strings.Log.AutoResponderWarning(warning));
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

        // Ctrl+wheel cannot be picked up by KeyPreview or ProcessCmdKey, and WM_MOUSEWHEEL goes to
        // whichever control has focus, so a message filter is the only hook that covers the whole
        // window -- including the third-party hex viewer -- without subscribing to every control.
        Application.AddMessageFilter(this);

        Palette.Apply(this);
        RefreshStatusColours();
        UpdateZoomStatus();
        AppendLog(DescribeEnvironment());
        // Windows refuses drags from an unelevated Explorer to an elevated window and reports
        // nothing to either side, so an elevated run has to be visible in the log before a
        // "drag and drop does nothing" report can be read.
        if (IsElevated()) AppendLog(Strings.Log.RunningElevated);
        AppendLog(Strings.Log.RootCaPath(_ca.RootPfxPath));
        AppendLog(TrustStore.IsTrusted(_ca.RootCertificate)
            ? Strings.Log.RootCaTrusted
            : Strings.Log.RootCaNotTrusted);
        // The toggle persists across restarts, so say so on every start rather than leaving a
        // disabled origin-certificate check to be remembered.
        if (!_options.ValidateUpstreamCertificates)
            AppendLog(Strings.Log.UpstreamValidationOffAtStartup);

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
            AppendLog(Strings.Log.RestoredLeftoverProxy(leftover));

        AskAnalyticsConsentIfNeeded();

        if (!EnsureTrustedRootForStartup())
        {
            UpdateCaptureStatus();
            QueueStartupUpdateCheck();
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
        QueueStartupUpdateCheck();
    }

    private void QueueStartupUpdateCheck()
    {
        if (!_closeAfterShutdown)
            BeginInvoke(new Action(() => _ = CheckForUpdatesAsync(manual: false)));
    }

    /// <summary>
    /// Asks, once, whether the user wants to send anonymous feedback. Collection is opt-in and stays
    /// off unless they say yes here or turn it on later, so declining - or dismissing the dialog
    /// without reading it - leaves Piper gathering nothing.
    /// </summary>
    private void AskAnalyticsConsentIfNeeded()
    {
        // A reporting subsystem that failed to start collects nothing, so there is nothing to ask
        // about - and without this the dialog would return on every launch with no way to settle it.
        if (Analytics.SpoolPath is null || Analytics.NoticeShown) return;

        bool optedIn;
        using (var dialog = new AnalyticsConsentDialog())
        {
            // The dialog result is the answer: only the accept button yields OK, so Escape, the
            // window's X and the decline button all arrive here as a refusal rather than as an
            // unanswered question, and are recorded as such below.
            dialog.ShowDialog(this);
            optedIn = dialog.AnalyticsEnabled;
        }

        Analytics.SetEnabled(optedIn);
        if (optedIn)
        {
            // Startup already tried to record this and was correctly refused, reporting being off
            // at the time. Without replaying it the run in which someone opts in is the one run
            // missing the first step of its own funnel.
            Analytics.Track(AnalyticsEvents.AppStarted);
        }

        // Recorded either way: the question is asked once, not repeated until the answer is yes.
        Analytics.RecordNoticeShown(CurrentVersion.ToString(3));
        AppendLog(optedIn ? Strings.Log.AnalyticsConsentOn : Strings.Log.AnalyticsConsentOff);
    }

    /// <summary>
    /// Gives the user an explicit startup choice before enabling capture. Trust installation
    /// changes Windows' certificate store, so it must never happen silently.
    /// </summary>
    private bool EnsureTrustedRootForStartup()
    {
        if (TrustStore.IsTrusted(_ca.RootCertificate)) return true;

        // Worth a line of its own: this dialog is modal, so while it is up the main window is
        // disabled and refuses every drop. That reads as "drag and drop is broken" to a user who
        // has not noticed the prompt, and it is what a fresh install shows on first run.
        AppendLog(Strings.Log.ShowingTrustPrompt);

        var answer = MessageBox.Show(this,
            Strings.Certificates.StartupTrustBody,
            Strings.Certificates.StartupTrustCaption,
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.OK)
        {
            AppendLog(Strings.Log.StartupCaptureOff);
            return false;
        }

        try
        {
            TrustStore.Install(_ca.RootCertificate);
        }
        catch (Exception ex)
        {
            AppendLog(Strings.Log.TrustRootFailed(ex.Message));
            MessageBox.Show(this,
                Strings.Certificates.StartupInstallFailed(ex.Message),
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        // Outside the try above, because the certificate is already in the store by this point.
        // Reporting failing here must not be reported to the user as the trust install failing, and
        // must not skip the restart the installed certificate now requires.
        try
        {
            Analytics.Track(AnalyticsEvents.CertTrusted, (AnalyticsProperties.Source, "startup"));

            // Restarting ends the process without running the shutdown flush, and the timer has not
            // ticked this early in the run, so the event above only survives if it is written now.
            Analytics.FlushToDisk();
        }
        catch (Exception reportingFailure)
        {
            Debug.WriteLine($"Analytics failed on the trust path: {reportingFailure.GetType().Name}");
        }

        try
        {
            AppendLog(Strings.Log.RootTrusted(_ca.RootCertificate.Thumbprint));
            _closeAfterShutdown = true;
            Application.Restart();
            Close();
            return false;
        }
        catch (Exception ex)
        {
            AppendLog(Strings.Log.TrustRootFailed(ex.Message));
            MessageBox.Show(this,
                Strings.Certificates.StartupInstallFailed(ex.Message),
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    private static TabPage NewPage(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    private void OnSessionDragEnter(object? sender, DragEventArgs e) => SetSessionDragEffect(e);

    private void OnSessionDragOver(object? sender, DragEventArgs e) => SetSessionDragEffect(e);

    private void SetSessionDragEffect(DragEventArgs e)
    {
        if (!HasCapturedRequest(e.Data) || SessionDropTarget(e) is not { } target)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        // Selecting on hover makes the target clear even when it was not the active tab.
        _rightTabs.SelectedTab = target;
        e.Effect = DragDropEffects.Copy;
    }

    private void OnSessionDragDrop(object? sender, DragEventArgs e)
    {
        if (SessionDropTarget(e) is not { } target
            || e.Data?.GetData(typeof(Session)) is not Session session
            || session.Request is null)
            return;

        _rightTabs.SelectedTab = target;
        if (target == _autoResponderPage) _autoResponder.AddRuleFromSession(session);
        else _composer.LoadSession(session);
    }

    private static bool HasCapturedRequest(IDataObject? data) =>
        data?.GetData(typeof(Session)) is Session { Request: not null };

    private void EnableSazFileDrop(Control control)
    {
        control.AllowDrop = true;
        control.DragEnter += OnSazFileDragEnter;
        control.DragOver += OnSazFileDragEnter;
        control.DragDrop += OnSazFileDragDrop;
        foreach (Control child in control.Controls) EnableSazFileDrop(child);
    }

    private static string[] SazFilesFrom(IDataObject? data) => data?.GetData(DataFormats.FileDrop) is string[] paths
        ? paths.Where(SazFileRelay.IsSazFile).ToArray()
        : [];

    private static void OnSazFileDragEnter(object? sender, DragEventArgs e)
    {
        if (SazFilesFrom(e.Data).Length > 0) e.Effect = DragDropEffects.Copy;
    }

    private void OnSazFileDragDrop(object? sender, DragEventArgs e)
    {
        var paths = SazFilesFrom(e.Data);
        if (paths.Length > 0)
        {
            ImportSazFiles(paths);
            return;
        }

        // A refused file drop used to do nothing at all: no cursor feedback while dragging and no
        // trace afterwards, which is indistinguishable from the window ignoring the mouse. Say why
        // it was refused, because the answer is usually the drag source rather than the file.
        // Every control is registered for both drags, so a session dropped on the Composer or the
        // AutoResponder reaches here too. That one is handled elsewhere and is not a refusal.
        if (e.Data?.GetData(typeof(Session)) is Session) return;
        AppendLog(Strings.Log.IgnoredDrop(DescribeRefusedDrop(e.Data)));
    }

    /// <summary>
    /// Why a drop carried nothing importable. Only file names are recorded, never directories: the
    /// log is meant to be exported, and the path a user keeps captures in is not ours to publish.
    /// </summary>
    private static string DescribeRefusedDrop(IDataObject? data)
    {
        if (data is null) return Strings.Log.DropNoData;

        var formats = data.GetFormats();
        if (data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            // Outlook, 7-Zip, Teams and browser download shelves hand over virtual files
            // (FileGroupDescriptor) that have no path on disk, so there is nothing to open.
            return formats.Length == 0
                ? Strings.Log.DropNoRecognisedFormat
                : Strings.Log.DropNoFileOnly(
                    DiagnosticsBundle.Summarise(formats, formats.Length, MaxLoggedNames));
        }

        // Only the names that will be printed are examined. The count comes from the drag source,
        // and probing every path of a dropped folder would stall the drop handler on the UI thread
        // - for as long as an unreachable network share takes to time out, once per file.
        var rejected = paths.Select(path => Path.GetFileName(path) switch
        {
            var name when Directory.Exists(path) => Strings.Log.DropRejectedFolder(name),
            var name when !File.Exists(path) => Strings.Log.DropRejectedMissing(name),
            var name => Strings.Log.DropRejectedWrongType(name),
        });
        return Strings.Log.DropNoneImportable(paths.Length,
            DiagnosticsBundle.Summarise(rejected, paths.Length, MaxLoggedNames));
    }

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

        var file = new ToolStripMenuItem(Strings.Menu.File);
        var openSaz = new ToolStripMenuItem(Strings.Menu.OpenSaz, null, (_, _) => OpenSazCapture())
        {
            ShortcutKeys = Keys.Control | Keys.O,
        };
        file.DropDownItems.Add(openSaz);
        var saveSaz = new ToolStripMenuItem(Strings.Menu.SaveSaz, null,
            (_, _) => _sessionList.SaveSelectedSessionsAsSaz())
        {
            ShortcutKeys = Keys.Control | Keys.S,
        };
        file.DropDownItems.Add(saveSaz);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Menus.Item(Strings.Menu.ClearSessions, Strings.Shortcuts.CtrlX, (_, _) => _store.Clear()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Strings.Menu.Exit, null, (_, _) => Close());
        file.DropDownOpening += (_, _) =>
            saveSaz.Enabled = _sessionList.SelectedSessions.Any(session => session.Request is not null);
        // A disabled item ignores its ShortcutKeys, so leaving it disabled after the menu closes made
        // Ctrl+S do nothing until the File menu happened to be opened again with a selection.
        // SaveSelectedSessionsAsSaz already does nothing when there is nothing to save.
        file.DropDownClosed += (_, _) => saveSaz.Enabled = true;

        var tools = new ToolStripMenuItem(Strings.Menu.Tools);
        tools.DropDownItems.Add(Strings.Menu.Configurations, null, (_, _) => ShowConfigurations());
        var hosts = new ToolStripMenuItem(Strings.Menu.Hosts, null, (_, _) => ShowHosts());
        tools.DropDownItems.Add(hosts);
        tools.DropDownItems.Add(new ToolStripSeparator());
        // Fiddler puts TextWizard on Ctrl+E; that is already send-to-Composer here, so Ctrl+T it is.
        // Ctrl+F is deliberately not a menu shortcut: a menu accelerator fires before the focused
        // control sees the key, and the Composer and inspector searches own Ctrl+F for themselves.
        tools.DropDownItems.Add(Menus.Item(Strings.Menu.FindSessions, Strings.Shortcuts.CtrlF,
            (_, _) => _sessionList.ShowFindSessions()));
        tools.DropDownItems.Add(new ToolStripMenuItem(Strings.Menu.TextWizard, null, (_, _) => TextWizardDialog.Open(this))
        {
            ShortcutKeys = Keys.Control | Keys.T,
        });
        tools.DropDownOpening += (_, _) => hosts.Checked = _options.HostRemapping.Enabled;

        var help = new ToolStripMenuItem(Strings.Menu.Help);
        help.DropDownItems.Add(Strings.Menu.SearchSyntax, null, (_, _) => ShowSearchHelp());
        _checkForUpdatesMenuItem = new ToolStripMenuItem(Strings.Menu.CheckForUpdates, null,
            async (_, _) => await CheckForUpdatesAsync(manual: true));
        help.DropDownItems.Add(_checkForUpdatesMenuItem);
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Strings.Menu.SaveDiagnostics, null, (_, _) => SaveDiagnostics());
        help.DropDownItems.Add(new ToolStripSeparator());
        help.DropDownItems.Add(Strings.Menu.About, null, (_, _) => MessageBox.Show(this,
            Strings.App.AboutBody,
            Strings.App.AboutCaption, MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.AddRange([file, BuildRulesMenu(), BuildViewMenu(), tools, help]);
        return menu;
    }

    /// <summary>
    /// The discoverable half of font zoom. Menu accelerators route through ProcessCmdKey, so these
    /// fire even with a text box focused, and the shortcuts are written down where a wheel gesture
    /// could not be.
    /// </summary>
    private ToolStripMenuItem BuildViewMenu()
    {
        var view = new ToolStripMenuItem(Strings.Menu.View);
        _zoomInItem = NewZoomItem(Strings.Menu.ZoomIn, Keys.Control | Keys.Oemplus, Strings.Shortcuts.ZoomIn, FontScale.ZoomIn);
        _zoomOutItem = NewZoomItem(Strings.Menu.ZoomOut, Keys.Control | Keys.OemMinus, Strings.Shortcuts.ZoomOut, FontScale.ZoomOut);
        _zoomResetItem = NewZoomItem(Strings.Menu.ResetZoom, Keys.Control | Keys.D0, Strings.Shortcuts.ZoomReset, FontScale.Reset);
        view.DropDownItems.AddRange([_zoomInItem, _zoomOutItem, new ToolStripSeparator(), _zoomResetItem]);
        UpdateZoomMenu();
        return view;
    }

    /// <summary>
    /// A zoom command. The display string is set by hand because WinForms renders
    /// <see cref="Keys.Oemplus"/> and <see cref="Keys.OemMinus"/> under those names rather than as
    /// the "+" and "-" printed on the key.
    /// </summary>
    private ToolStripMenuItem NewZoomItem(string text, Keys shortcut, string display, Func<bool> change) =>
        new(text, null, (_, _) => ChangeFontScale(change()))
        {
            ShortcutKeys = shortcut,
            ShortcutKeyDisplayString = display,
        };

    private ToolStripMenuItem BuildRulesMenu()
    {
        var rules = new ToolStripMenuItem(Strings.Menu.Rules);
        rules.DropDownOpening += (_, _) =>
        {
            rules.DropDownItems.Clear();
            var userAgent = new ToolStripMenuItem(Strings.Menu.UserAgent);
            userAgent.DropDownItems.Add(CreateUserAgentChoice(Strings.Menu.UserAgentNoOverride, null));
            userAgent.DropDownItems.Add(new ToolStripSeparator());
            foreach (var preset in UserAgentPresets)
                userAgent.DropDownItems.Add(CreateUserAgentChoice(preset.Name, preset.Value));
            userAgent.DropDownItems.Add(new ToolStripSeparator());
            userAgent.DropDownItems.Add(CreateCustomUserAgentChoice());
            rules.DropDownItems.Add(userAgent);
            rules.DropDownItems.Add(new ToolStripSeparator());

            var settings = _autoResponder.Settings;
            var automatic = new ToolStripMenuItem(Strings.Menu.EnableAutomaticResponses)
            {
                Checked = settings.Enabled,
                CheckOnClick = true,
            };
            automatic.Click += (_, _) => _autoResponder.SetEnabled(automatic.Checked);
            rules.DropDownItems.Add(automatic);

            var passthrough = new ToolStripMenuItem(Strings.Menu.UnmatchedRequestsPassThrough)
            {
                Checked = settings.PassthroughUnmatched,
                CheckOnClick = true,
            };
            passthrough.Click += (_, _) => _autoResponder.SetPassthroughUnmatched(passthrough.Checked);
            rules.DropDownItems.Add(passthrough);

            rules.DropDownItems.Add(Strings.Menu.AutoResponderRules, null, (_, _) => _rightTabs.SelectedTab = _autoResponderPage);
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
        var choice = new ToolStripMenuItem(Strings.Menu.UserAgentCustom) { Checked = !isPreset };
        choice.Click += (_, _) => SetCustomUserAgent();
        return choice;
    }

    private void SetGlobalUserAgent(string? value, string name)
    {
        _options.GlobalUserAgent = value;
        ProxyConfigurationSettingsStore.Save(ProxyConfigurationSettings.From(_options));
        AppendLog(value is null ? Strings.Log.GlobalUserAgentCleared : Strings.Log.GlobalUserAgentSet(name));
    }

    private void SetCustomUserAgent()
    {
        using var prompt = new Form
        {
            Text = Strings.UserAgents.PromptCaption,
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ClientSize = new Size(640, 180),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
        };
        Palette.ScaleDialogSize(prompt);
        var label = new Label { Dock = DockStyle.Top, Height = 38, Text = Strings.UserAgents.PromptLabel, Padding = new Padding(14, 12, 0, 0) };
        var value = new TextBox { Dock = DockStyle.Top, Height = 30, Text = _options.GlobalUserAgent ?? string.Empty, Margin = new Padding(12), Font = Palette.Mono };
        var save = new Button { Text = Strings.Common.Save, DialogResult = DialogResult.OK, Size = new Size(100, 34) };
        var cancel = new Button { Text = Strings.Common.Cancel, DialogResult = DialogResult.Cancel, Size = new Size(100, 34) };
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
        SetGlobalUserAgent(string.IsNullOrWhiteSpace(value.Text) ? null : value.Text.Trim(), Strings.UserAgents.CustomName);
    }

    private void ShowConfigurations()
    {
        using var dialog = new ConfigurationsDialog(_options, _captureEnabledOnStartup, _captureScope.ToString(),
            FontScale.WheelEnabled, Analytics.IsEnabled,
            TrustRootCertificate, UntrustRootCertificate, ExportRootCertificate, OpenCertificateFolder,
            OpenAnalyticsFolder);
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
        FontScale.WheelEnabled = dialog.WheelZoom;
        SaveFontScaleSettings();
        if (dialog.AnalyticsEnabled != Analytics.IsEnabled)
        {
            var forgotten = Analytics.SetEnabled(dialog.AnalyticsEnabled);

            // Replayed for the same reason the consent dialog replays it: startup recorded this and
            // was correctly refused while reporting was off, so without it a run where someone opts
            // in from here is missing the first step of its own funnel. Reporting it more than once
            // per run is prevented by the client, not by this call site.
            if (dialog.AnalyticsEnabled) Analytics.Track(AnalyticsEvents.AppStarted);

            // Reporting that failed to start cannot be switched on, and SetEnabled is a no-op then.
            // Logging success regardless would tell the user the opposite of the truth about a
            // privacy control, and the setting would be back to its old value next time they look.
            AppendLog(Analytics.SpoolPath is null ? Strings.Log.AnalyticsUnavailable
                : dialog.AnalyticsEnabled ? Strings.Log.AnalyticsOn
                : forgotten ? Strings.Log.AnalyticsOff
                : Strings.Log.AnalyticsOffIdentifierKept);
        }

        AppendLog(Strings.Log.ConfigurationsSaved);
        if (!_options.ValidateUpstreamCertificates)
            AppendLog(Strings.Log.UpstreamValidationOffAfterSave);
    }

    private void ShowHosts()
    {
        Analytics.Track(AnalyticsEvents.FeatureUsed, (AnalyticsProperties.Feature, "hosts"));
        using var dialog = new HostsDialog(_options.HostRemapping.Export());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _options.HostRemapping.Apply(dialog.Settings);
        ProxyConfigurationSettingsStore.Save(ProxyConfigurationSettings.From(_options));
        AppendLog(_options.HostRemapping.Enabled
            ? Strings.Log.HostRemappingEnabled
            : Strings.Log.HostRemappingDisabled);
    }

    /// <summary>
    /// Opens the folder holding the pending-report file, so "here is what we send" is something the
    /// user can check rather than something they have to believe.
    /// </summary>
    private void OpenAnalyticsFolder()
    {
        var folder = AnalyticsSettingsStore.DefaultSpoolDirectory;
        try
        {
            Directory.CreateDirectory(folder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, ex.Message, Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenCertificateFolder()
    {
        var folder = Path.GetDirectoryName(_ca.RootPfxPath);
        if (folder is not null)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private static readonly (string Name, string Value)[] UserAgentPresets =
    [
        (Strings.UserAgents.ChromeWindows, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36"),
        (Strings.UserAgents.EdgeWindows, "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0"),
        (Strings.UserAgents.FirefoxWindows, "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:133.0) Gecko/20100101 Firefox/133.0"),
        (Strings.UserAgents.ChromeAndroid, "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36"),
        (Strings.UserAgents.SafariIPhone, "Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1"),
    ];

    private void OpenSazCapture()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Strings.SazImport.OpenCaption,
            Filter = Strings.SazImport.OpenFilter,
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK) ImportSazFiles(dialog.FileNames);
    }

    /// <summary>Imports SAZ/RAZ files on a worker thread. A ".saz" (full session capture) adds its
    /// sessions to the main request list; a ".raz" (request-only capture, no responses) instead
    /// appends its requests to the Composer's persisted history, alongside what's already there.</summary>
    public async void ImportSazFiles(IEnumerable<string> filePaths)
    {
        var requested = filePaths.ToArray();
        var paths = requested.Where(SazFileRelay.IsSazFile).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
        {
            // Silence here covers a file association, a command line and a drop alike, so the
            // reason one of them produced nothing has to be written down. File names only: the
            // directory a user keeps captures in does not belong in an exported log.
            if (requested.Length > 0)
                AppendLog(Strings.Log.NoSazToImport(DiagnosticsBundle.Summarise(
                    requested.Select(Path.GetFileName)!, requested.Length, MaxLoggedNames)));
            return;
        }

        BringToFront();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();

        var importedToComposer = false;
        foreach (var path in paths)
        {
            var result = await Task.Run(() => SazImporter.Import(path));
            var isComposerImport = path.EndsWith(".raz", StringComparison.OrdinalIgnoreCase);
            if (isComposerImport)
            {
                _composer.AppendToHistory(result.Sessions);
                importedToComposer = true;
            }
            else
            {
                foreach (var session in result.Sessions) _store.Add(session);
            }

            Analytics.Track(
                AnalyticsEvents.FeatureUsed,
                (AnalyticsProperties.Feature, "session_import"),
                (AnalyticsProperties.Format, isComposerImport ? "raz" : "saz"),
                (AnalyticsProperties.Count, Analytics.CountBucket(result.Sessions.Count)));
            AppendLog(Strings.Log.ImportedSessions(result.Sessions.Count, Path.GetFileName(path), isComposerImport));
            foreach (var warning in result.Warnings)
                AppendLog(Strings.Log.SazImportWarning(Path.GetFileName(path), warning));
        }
        _rightTabs.SelectedIndex = importedToComposer ? 1 : 0;
    }

    private ToolStrip BuildToolbar()
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Font = Palette.UiFont };

        var clear = new ToolStripButton(Strings.Toolbar.Clear) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        clear.Click += (_, _) => _store.Clear();

        var composer = new ToolStripButton(Strings.Toolbar.Composer) { DisplayStyle = ToolStripItemDisplayStyle.Text };
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

        var target = Palette.IsLightMode ? Strings.Toolbar.DarkMode : Strings.Toolbar.LightMode;
        _themeToggle.Text = Strings.Toolbar.ThemeToggle(target);
        _themeToggle.ToolTipText = Strings.Toolbar.ThemeToggleTooltip(target);
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
        RefreshStatusColours();

        oldCaptureOn.Dispose();
        oldCaptureOff.Dispose();
        oldScope.Dispose();
        oldBreakpoints.Dispose();
        oldSessions.Dispose();
    }

    /// <summary>
    /// Puts back the status labels' meaning colours, which every <see cref="Palette.Apply"/> walk
    /// resets to the plain text colour. Call after each walk over this form.
    /// </summary>
    private void RefreshStatusColours()
    {
        UpdateCaptureStatus();
        _selectedSessionDetailsLabel.ForeColor = Palette.TextDim;
    }

    private static void InvalidateTheme(Control control)
    {
        control.Invalidate(true);
        foreach (Control child in control.Controls) InvalidateTheme(child);
    }

    private static StatusStrip BuildStatusBar(out ToolStripStatusLabel status,
        out ToolStripStatusLabel capture, out ToolStripStatusLabel scope,
        out ToolStripStatusLabel breakpoints, out ToolStripStatusLabel sessions,
        out ToolStripStatusLabel selectedSessionDetails, out ToolStripStatusLabel zoom)
    {
        var bar = new StatusStrip { Font = Palette.UiFont };
        status = new ToolStripStatusLabel(Strings.StatusBar.Starting) { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        capture = NewStatusAction(Strings.StatusBar.Capturing, CaptureOnIcon, Strings.StatusBar.CaptureTooltip);
        scope = NewStatusAction(Strings.CaptureScopes.AllProcesses, ScopeIcon, Strings.StatusBar.ScopeTooltip);
        breakpoints = new ToolStripStatusLabel(Strings.StatusBar.BreakpointsNone, BreakpointIcon)
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left,
            ToolTipText = Strings.StatusBar.BreakpointsTooltip,
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            TextImageRelation = TextImageRelation.ImageBeforeText,
        };
        sessions = new ToolStripStatusLabel(Strings.StatusBar.NoSessions, SessionsIcon)
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
            ToolTipText = Strings.StatusBar.SelectedSessionTooltip,
        };
        zoom = new ToolStripStatusLabel
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left,
            Visible = false,
            ToolTipText = Strings.StatusBar.ZoomTooltip,
        };
        // The live proxy status expands through the centre; placing selected-session details
        // after it pins the timing/transfer summary to the status bar's right edge.
        bar.Items.AddRange([capture, scope, breakpoints, sessions, status, selectedSessionDetails, zoom]);
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

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckInProgress)
        {
            if (manual)
                MessageBox.Show(this, Strings.Updates.AlreadyChecking, Strings.App.Name,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _updateCheckInProgress = true;
        if (_checkForUpdatesMenuItem is not null) _checkForUpdatesMenuItem.Enabled = false;
        try
        {
            var result = await _updates.CheckAsync(CurrentVersion, _updatesCancellation.Token);
            if (IsDisposed) return;

            if (result.Error is not null)
            {
                AppendLog(Strings.Log.UpdateCheckFailed(result.Error));
                if (manual)
                    MessageBox.Show(this, result.Error, Strings.Updates.CheckCaption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!result.IsUpdateAvailable)
            {
                AppendLog(Strings.Log.UpToDate(CurrentVersion));
                if (manual)
                    MessageBox.Show(this, Strings.Updates.UpToDate(CurrentVersion), Strings.Updates.CheckCaption,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var release = result.Release!;
            AppendLog(Strings.Log.UpdateAvailable(release.Version));
            var answer = MessageBox.Show(this,
                Strings.Updates.AvailableBody(release.Version),
                Strings.Updates.AvailableCaption, MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return;

            await DownloadAndInstallUpdateAsync(release);
        }
        catch (OperationCanceledException) when (_updatesCancellation.IsCancellationRequested)
        {
            // The application is closing. There is no useful update status to show.
        }
        catch (Exception ex)
        {
            AppendLog(Strings.Log.UpdateCheckFailed(ex.Message));
            if (manual && !IsDisposed)
                MessageBox.Show(this, ex.Message, Strings.Updates.CheckCaption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _updateCheckInProgress = false;
            if (!IsDisposed && _checkForUpdatesMenuItem is not null) _checkForUpdatesMenuItem.Enabled = true;
        }
    }

    private async Task DownloadAndInstallUpdateAsync(UpdateRelease release)
    {
        AppendLog(Strings.Log.DownloadingInstaller(release.Version));
        var result = await _updates.DownloadAndVerifyInstallerAsync(release, _updatesCancellation.Token);
        if (IsDisposed) return;
        if (!result.IsDownloaded)
        {
            AppendLog(Strings.Log.UpdateDownloadFailed(result.Error));
            MessageBox.Show(this, result.Error, Strings.Updates.InstallCaption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = result.InstallerPath!,
                Arguments = $"/WAITPID={Environment.ProcessId}",
                WorkingDirectory = Path.GetDirectoryName(result.InstallerPath),
                UseShellExecute = true,
            });
            AppendLog(Strings.Log.InstallerStarted(release.Version));
            Close();
        }
        catch (Exception ex)
        {
            AppendLog(Strings.Log.InstallerStartFailed(ex.Message));
            MessageBox.Show(this, ex.Message, Strings.Updates.InstallCaption, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Version CurrentVersion
    {
        get
        {
            var version = typeof(MainForm).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0));
        }
    }

    private void StartCapture()
    {
        try
        {
            _proxy.Start();
            UpdateCaptureStatus();
            Analytics.Track(AnalyticsEvents.CaptureStarted, (AnalyticsProperties.Result, "ok"));
        }
        catch (Exception ex)
        {
            Analytics.TrackError("capture_start", ex);
            // Reported in the log and the status bar rather than a dialog, so a busy port
            // never blocks the UI and the full exception stays available for diagnosis.
            AppendLog(Strings.Log.ListenFailed(_options.Port, ex.GetType().Name, ex.Message));
            AppendLog(Strings.Log.PortInUseHint);
            UpdateCaptureStatus();
            _rightTabs.SelectedIndex = 4; // surface the Log tab (Inspectors, Composer, Filters, AutoResponder, Log)
        }
    }

    private async void ToggleCapture()
    {
        if (_captureToggleInProgress) return;

        _captureToggleInProgress = true;
        var enabling = !_proxy.IsRunning;
        _captureStatusLabel.Text = enabling ? Strings.StatusBar.EnablingProxy : Strings.StatusBar.DisablingProxy;
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
                    AppendLog(Strings.Log.CaptureStoppedProxyRestored);
            }
            else
            {
                StartCapture();
                if (_proxy.IsRunning && _proxySnapshot is null) EnableSystemProxy();
            }
        }
        catch (Exception ex)
        {
            AppendLog(Strings.Log.CaptureStateFailed(ex.Message));
            MessageBox.Show(this, ex.Message, Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        _captureStatusLabel.Text = _proxy.IsRunning ? Strings.StatusBar.Capturing : Strings.StatusBar.NotCapturing;
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

        // Built per click, so it is themed here rather than by the form's walk, and released once it
        // closes: a menu shown without an owner control is never disposed by anything else. Deferred,
        // because Closed is raised before the clicked item's Click handler runs.
        Palette.Apply(menu);
        menu.Closed += (_, _) => BeginInvoke(menu.Dispose);
        menu.Show(Cursor.Position);
    }

    private void SetCaptureScope(CaptureScope scope)
    {
        _captureScope = scope;
        ApplyCaptureScope();
        SaveStatusBarSettings();
        AppendLog(Strings.Log.CaptureScopeSet(CaptureScopeText(scope)));
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
        CaptureScope.AllProcesses => Strings.CaptureScopes.AllProcesses,
        CaptureScope.WebBrowsers => Strings.CaptureScopes.WebBrowsers,
        CaptureScope.NonBrowsers => Strings.CaptureScopes.NonBrowsers,
        CaptureScope.HideAll => Strings.CaptureScopes.HideAll,
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
            AppendLog(Strings.Log.SystemProxySet(endpoint));
        }
        catch (Exception ex)
        {
            // Enable writes several values, so put back whatever was captured rather than leaving
            // the machine half-pointed at a proxy that is not running.
            try { RestoreSystemProxy(); }
            catch (Exception restoreFailure) { AppendLog(Strings.Log.PartialChangeUndoFailed(restoreFailure.Message)); }

            AppendLog(Strings.Log.SystemProxyFailed(ex.Message));
            MessageBox.Show(this, ex.Message, Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            MessageBox.Show(this, Strings.Certificates.AlreadyTrusted,
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var answer = MessageBox.Show(this,
            Strings.Certificates.TrustBody(_ca.RootPfxPath, _ca.RootCertificate.Thumbprint),
            Strings.Certificates.TrustCaption,
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);

        if (answer != DialogResult.OK) return;

        try
        {
            TrustStore.Install(_ca.RootCertificate);
            Analytics.Track(AnalyticsEvents.CertTrusted, (AnalyticsProperties.Source, "manual"));
            AppendLog(Strings.Log.RootTrusted(_ca.RootCertificate.Thumbprint));
        }
        catch (Exception ex)
        {
            AppendLog(Strings.Log.TrustingRootFailed(ex.Message));
            MessageBox.Show(this, ex.Message, Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UntrustRootCertificate()
    {
        try
        {
            var removed = TrustStore.Uninstall();
            AppendLog(Strings.Log.RootsRemoved(removed));
            MessageBox.Show(this, Strings.Certificates.Removed(removed),
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportRootCertificate()
    {
        using var dialog = new SaveFileDialog
        {
            Title = Strings.Certificates.ExportCaption,
            Filter = Strings.Certificates.ExportFilter,
            FileName = Strings.Certificates.ExportFileName,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _ca.ExportRootTo(dialog.FileName);
            AppendLog(Strings.Log.RootExported(dialog.FileName));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowSearchHelp()
    {
        using var dialog = new Form
        {
            Text = Strings.SearchHelp.Caption,
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
            Text = Strings.SearchHelp.Body.ReplaceLineEndings("\r\n"),
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
        // KeyPreview hands this form every key before the focused control sees it, so a chord that
        // means something inside a text box has to leave it alone there: Ctrl+X cut the selected text
        // and cleared the whole capture with it, and Ctrl+R replayed a captured request while the
        // user was typing in the Composer.
        else if (e.Control && e.KeyCode == Keys.R && !IsTextInputFocused() && _sessionList.ResendSelected())
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.X && !IsTextInputFocused())
        {
            _store.Clear();
            e.Handled = true;
        }
        else if (e.Control && e.KeyCode is Keys.Add or Keys.Subtract or Keys.NumPad0)
        {
            ChangeFontScale(e.KeyCode switch
            {
                Keys.Add => FontScale.ZoomIn(),
                Keys.Subtract => FontScale.ZoomOut(),
                _ => FontScale.Reset(),
            });
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.Shift && e.KeyCode is Keys.Oemplus or Keys.OemMinus)
        {
            // The menu accelerators are the unshifted keys, which ProcessCmdKey has already had a
            // chance at. On a layout where "+" is Shift+=, the shifted chord reaches here instead,
            // and the shortcut the README advertises would otherwise do nothing.
            ChangeFontScale(e.KeyCode == Keys.Oemplus ? FontScale.ZoomIn() : FontScale.ZoomOut());
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
    }

    /// <summary>
    /// Whether keyboard focus is in a control that edits text, where Ctrl+X, Ctrl+R and friends
    /// belong to the control. <see cref="ContainerControl.ActiveControl"/> stops at the nearest
    /// container (every panel here is a UserControl), so walk down to the control that has focus.
    /// </summary>
    private bool IsTextInputFocused()
    {
        Control? focused = ActiveControl;
        while (focused is ContainerControl { ActiveControl: { } inner } and not UpDownBase) focused = inner;

        // Read-only boxes count too: someone reading a body in the inspector who reaches for Ctrl+X
        // expects nothing, or at most a copy, not an empty capture.
        return focused switch
        {
            TextBoxBase => true,
            ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList } => true,
            UpDownBase => true,
            _ => false,
        };
    }

    /// <summary>
    /// Ctrl+MouseWheel resizes the UI. The wheel message goes to the focused control rather than the
    /// one under the pointer, so this filter is what makes the gesture work anywhere in the window;
    /// swallowing the message stops that control from scrolling at the same time.
    /// </summary>
    /// <remarks>
    /// The video preview hosts its own browser window out of process, so a Ctrl+wheel over that tab
    /// never reaches this filter and keeps the player's own behaviour.
    /// </remarks>
    public bool PreFilterMessage(ref Message m)
    {
        const int WmMouseWheel = 0x020A;
        const int WheelDelta = 120;
        if (m.Msg != WmMouseWheel || ModifierKeys != Keys.Control) return false;
        if (!FontScale.WheelEnabled) return false;

        var delta = (short)((long)m.WParam >> 16);
        if (delta == 0) return false;

        // Accumulated against one detent rather than treated as a direction: a precision touchpad
        // reports fractions of a notch, and stepping a full 10% per message would run the whole
        // range in one gesture. Reversing direction drops the partial notch so the pointer does not
        // feel sticky.
        if (Math.Sign(delta) != Math.Sign(_wheelRemainder)) _wheelRemainder = 0;
        _wheelRemainder += delta;

        while (Math.Abs(_wheelRemainder) >= WheelDelta)
        {
            var up = _wheelRemainder > 0;
            _wheelRemainder -= up ? WheelDelta : -WheelDelta;
            ChangeFontScale(up ? FontScale.ZoomIn() : FontScale.ZoomOut());
        }

        return true;
    }

    /// <summary>
    /// Pushes a new font size onto the live control tree. Mirrors <see cref="ToggleTheme"/>: the
    /// palette re-walks the tree and everything repaints.
    /// </summary>
    private void ChangeFontScale(bool changed)
    {
        if (!changed) return;

        Palette.RescaleFonts();
        Palette.Apply(this);
        RefreshStatusColours();
        _sessionList.RefitColumns();
        UpdateZoomStatus();
        UpdateZoomMenu();
        InvalidateTheme(this);
    }

    /// <summary>
    /// Keeps the View menu's zoom commands in step with the current size.
    /// </summary>
    /// <remarks>
    /// This has to run on every change, not just when the menu opens: a disabled
    /// <see cref="ToolStripMenuItem"/> does not fire its <see cref="ToolStripMenuItem.ShortcutKeys"/>,
    /// so refreshing only in <c>DropDownOpening</c> would leave Ctrl+0 dead after the menu had once
    /// been opened at 100%, and Ctrl+Plus dead after it had been opened at the top clamp.
    /// </remarks>
    private void UpdateZoomMenu()
    {
        if (_zoomInItem is null || _zoomOutItem is null || _zoomResetItem is null) return;

        // Greying out at the clamp says the range is deliberate rather than the key being broken.
        _zoomInItem.Enabled = FontScale.Step < FontScaleSettingsStore.MaxStep;
        _zoomOutItem.Enabled = FontScale.Step > FontScaleSettingsStore.MinStep;
        _zoomResetItem.Enabled = !FontScale.IsDefault;
        _zoomResetItem.Text = FontScale.IsDefault
            ? Strings.Menu.ResetZoom
            : Strings.Menu.ResetZoomTo(FontScale.Percent);
    }

    private static void SaveFontScaleSettings() => FontScaleSettingsStore.Save(new FontScaleSettings
    {
        Step = FontScale.Step,
        WheelZoomEnabled = FontScale.WheelEnabled,
    });

    private void UpdateZoomStatus()
    {
        // Only worth the space when it is not the default, so a stray Ctrl+wheel is explainable
        // rather than mysterious. The text is cleared rather than left stale behind a hidden label,
        // because accessibility tools still report the text of one that is merely not visible.
        _zoomLabel.Text = FontScale.IsDefault ? string.Empty : Strings.StatusBar.Zoom(FontScale.Percent);
        _zoomLabel.Visible = !FontScale.IsDefault;
    }

    /// <summary>
    /// Routes the capture list's "Hide this host" into the Filters tab's persisted Hosts list, so
    /// the choice is still there after a restart, and hides the host in the grid straight away.
    /// Following Fiddler Classic, this records the host but never ticks "Use Filters" for the user:
    /// running a filterset stays their explicit action.
    /// </summary>
    /// <remarks>
    /// The immediate hide lives in the grid's own session slot (<see cref="SessionListView.SessionHiddenHostsFilter"/>),
    /// not in the filter box. Writing a term into the box took the box away from the user, and gave
    /// one hide two places to undo it: unticking the entry in the Filters tab, as the help says to,
    /// left the host hidden by the term.
    /// </remarks>
    private void HideHost(string host)
    {
        // Session.Host is the raw Host header whenever the request line had no parseable URL, so it
        // is attacker-controlled. Composing that into a query -- transiently or persisted -- would
        // let it inject terms of its own, so refuse it before it reaches either.
        if (!HostFilterTerm.IsFilterableHost(host))
        {
            AppendLog(Strings.Log.HideHostNotFilterable);
            return;
        }

        // Leave the filterset staged rather than running it: recomposing would start dropping this
        // host at admission (SessionStore.CompletedSessionFilter), which unticking the entry later
        // cannot undo. As in Fiddler Classic, running a filterset stays an explicit action.
        var settings = _filterPanel.Settings;
        var wasShowOnly = settings.HostsMode != 1;
        var recorded = settings.HideHost(host);
        if (recorded) _filterPanel.ApplySettings(settings);

        // After ApplySettings, whose SettingsChanged would otherwise prune the new entry as unlisted.
        _sessionHiddenHosts[host] = recorded;
        ApplySessionHiddenHosts();

        if (!recorded)
        {
            AppendLog(Strings.Log.HideHostShowOnlyConflict(host));
            return;
        }

        if (wasShowOnly && settings.HostsMode == 1)
            AppendLog(Strings.Log.HideHostSwitchedToHideMode);

        AppendLog(Strings.Log.HideHostAdded(host));
    }

    /// <summary>
    /// Hosts hidden with "Hide this host" since the filterset last ran, and whether the Filters tab
    /// list is what records each one. A recorded host stays hidden only while an enabled hide entry
    /// in that list still covers it; one the list could not take (it was showing only specific
    /// hosts) is hidden for the session alone.
    /// </summary>
    private readonly Dictionary<string, bool> _sessionHiddenHosts = new(StringComparer.OrdinalIgnoreCase);

    private void ApplySessionHiddenHosts()
    {
        var hidden = _sessionHiddenHosts.Keys.ToArray();
        _sessionList.SessionHiddenHostsFilter = hidden.Length == 0
            ? null
            : session => !hidden.Any(pattern => HostFilterTerm.Covers(pattern, session.Host));
    }

    /// <summary>
    /// Shows a recorded host again once the Filters tab no longer hides it: its entry was unticked or
    /// removed, or the list switched to showing only specific hosts. That is the undo the Filters
    /// help promises, and it has to work before the filterset is ever run.
    /// </summary>
    private void PruneSessionHiddenHosts()
    {
        if (_sessionHiddenHosts.Count == 0) return;

        var settings = _filterPanel.Settings;
        var released = _sessionHiddenHosts
            .Where(pair => pair.Value && !settings.Hides(pair.Key))
            .Select(pair => pair.Key)
            .ToArray();
        if (released.Length == 0) return;

        foreach (var host in released) _sessionHiddenHosts.Remove(host);
        ApplySessionHiddenHosts();
    }

    /// <summary>Forgets every session-only hide, for the grid's "show hidden hosts" command and a filterset run.</summary>
    private void ClearSessionHiddenHosts()
    {
        if (_sessionHiddenHosts.Count == 0) return;
        _sessionHiddenHosts.Clear();
        ApplySessionHiddenHosts();
    }

    /// <summary>
    /// Adds one line to the Log tab.
    /// </summary>
    /// <remarks>
    /// Everything written here can leave the machine: Help &gt; Save diagnostics copies this text
    /// into a zip the user forwards. Write call sites accordingly - no captured traffic, no
    /// credentials, no key material - and keep in mind that a message may still name a host the
    /// user browsed or a rule they wrote. <see cref="DiagnosticsBundle.SanitizeLogMessage"/> only
    /// removes the account name and flattens control characters; it cannot judge content.
    /// </remarks>
    private void AppendLog(string message)
    {
        // Sanitised before it reaches the catalogue's line format, so the account name and control
        // characters are stripped from the message whatever wording wraps it.
        var line = Strings.Log.Line(DateTime.Now, DiagnosticsBundle.SanitizeLogMessage(message));
        // Drop the oldest half rather than clearing. A long-running session used to reach the cap
        // and throw away every line, which left the diagnostics export empty for exactly the users
        // whose problem took hours to show up.
        //
        // TextLength gates it because reading Text copies the whole control text - a large-object
        // allocation on every line once the log is big. The trim then works off that copy alone:
        // the two come from separate native calls and need not agree, and indexing one by the
        // other could throw inside the one path that must never fail.
        if (_logView.TextLength > LogCharacterCap)
        {
            var existing = _logView.Text;
            if (existing.Length > LogCharacterCap)
                _logView.Text = DiagnosticsBundle.TrimToNewestLines(existing, LogCharacterCap);
        }
        _logView.AppendText(line);
    }

    private const int LogCharacterCap = 200_000;

    /// <summary>How many names a log line lists before falling back to a count.</summary>
    private const int MaxLoggedNames = 10;

    /// <summary>
    /// One line of machine state, logged at startup so that every exported diagnostics bundle
    /// carries it. Deliberately state only - no listening port, upstream proxy, host remapping or
    /// rule text - because this text is written to a file the user sends on to someone else.
    /// </summary>
    private string DescribeEnvironment() => Strings.Diagnostics.Environment(
        typeof(MainForm).Assembly.GetName().Version?.ToString(3),
        Environment.OSVersion.VersionString, RuntimeInformation.OSArchitecture, Environment.Version,
        Environment.Is64BitProcess, IsElevated(), DeviceDpi * 100 / 96, CultureInfo.CurrentCulture.Name);

    /// <summary>
    /// Whether this process runs with administrator rights. Reported, never acted on: Piper has no
    /// reason to elevate itself, and an elevated run is a symptom rather than a setting.
    /// </summary>
    private static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (SecurityException)
        {
            // An identity Piper may not query is not an identity Piper can call elevated.
            return false;
        }
    }

    /// <summary>
    /// Writes the log, the crash log and the machine summary to a zip the user chooses, for
    /// attaching to a bug report. Nothing captured goes in it - see <see cref="DiagnosticsBundle"/>.
    /// </summary>
    private void SaveDiagnostics()
    {
        using var dialog = new SaveFileDialog
        {
            Title = Strings.Diagnostics.SaveCaption,
            Filter = Strings.Diagnostics.SaveFilter,
            DefaultExt = "zip",
            AddExtension = true,
            FileName = Strings.Diagnostics.SaveFileName(DateTime.Now),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        // Logged before the write so that the bundle records the export that produced it.
        AppendLog(Strings.Log.WritingDiagnostics(Path.GetFileName(dialog.FileName)));
        try
        {
            using (var file = new FileStream(dialog.FileName, FileMode.Create, FileAccess.Write, FileShare.None))
                DiagnosticsBundle.Write(file, DescribeEnvironment(), _logView.Text, Program.CrashLogPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog(Strings.Log.DiagnosticsWriteFailed(ex.Message));
            MessageBox.Show(this, Strings.Diagnostics.WriteFailedBody(ex.Message),
                Strings.Diagnostics.WriteFailedCaption, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // States only what the code enforces. The bundle's allowlist is per file, not per line, so
        // it cannot promise anything about what a log message says - and some of them name a host
        // the user filtered or a rule they wrote. Claiming more than that would be a promise the
        // next AppendLog call site could quietly break.
        MessageBox.Show(this,
            Strings.Diagnostics.SavedBody(Path.GetFileName(dialog.FileName), DiagnosticsBundle.Contents),
            Strings.Diagnostics.SavedCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void UpdateStatus()
    {
        _statusLabel.Text = _proxy.IsRunning
            ? Strings.StatusBar.Listening(_proxy.Endpoint, _options.DecryptHttps)
            : Strings.StatusBar.NotCapturingStatus;
        UpdateCaptureStatus();
        UpdateSessionsStatus();
        _autoResponder.RefreshStatistics();
    }

    private void UpdateSessionsStatus()
    {
        var total = _store.Count;
        // Reporting is checked before the scan, not after: a user who declined should not pay for
        // copying the session store, and the latch must not close while reporting is off - otherwise
        // someone who opts in mid-run could never report this step for the rest of the run. The
        // count is checked too, so the scan runs only when a session has arrived since the last one,
        // rather than on every status update during the window before real traffic appears.
        if (total > _lastScannedSessionCount && !_reportedFirstSession && Analytics.IsEnabled
            && _store.Snapshot().Any(session => !session.IsUpdateCheck))
        {
            // Completes the activation funnel: installed, trusted, capturing, and now actually
            // seeing traffic. Reported once per run, and only ever as the fact that it happened.
            //
            // Piper's own startup update check lands in the session store like anything else, so a
            // bare count is not evidence that capture works - it fires on every run, even with
            // capture off and the certificate untrusted, which would make the funnel read as if
            // every user succeeded.
            _reportedFirstSession = true;
            Analytics.Track(AnalyticsEvents.FirstSessionCaptured);
        }

        _lastScannedSessionCount = total;

        var selected = _sessionList.SelectedSessionCount;
        _sessionsLabel.Text = selected == 0
            ? Strings.StatusBar.Sessions(total)
            : Strings.StatusBar.Sessions(selected, total);
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
        SaveFontScaleSettings();

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
            catch (Exception ex) { AppendLog(Strings.Log.SystemProxyRestoreFailed(ex.Message)); }

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
        Text = Strings.App.ClosingTitle;

        try
        {
            if (_proxySnapshot is { } snapshot)
            {
                _statusLabel.Text = Strings.StatusBar.RestoringSystemProxy;
                _sessionsLabel.Text = Strings.StatusBar.PleaseWait;
                await Task.Yield(); // Let the status change paint before WinINET is notified.

                await Task.Run(() => SystemProxy.Restore(snapshot));
                _proxySnapshot = null;
                SystemProxyBackupStore.Clear();
            }

            if (_proxy.IsRunning)
            {
                _statusLabel.Text = Strings.StatusBar.StoppingCapture;
                _sessionsLabel.Text = Strings.StatusBar.PleaseWait;
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
            Text = Strings.App.Name;
            _statusTimer.Start();
            UpdateStatus();
            AppendLog(Strings.Log.ShutdownFailed(ex.Message));
            MessageBox.Show(this,
                Strings.Shutdown.ProxyRestoreFailed(ex.Message),
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
        if (disposing && !_resourcesDisposed)
        {
            _resourcesDisposed = true;
            Application.RemoveMessageFilter(this);
            _updatesCancellation.Cancel();
            _updatesCancellation.Dispose();
            _statusTimer.Dispose();
            _ca.Dispose();
        }
        base.Dispose(disposing);
    }
}
