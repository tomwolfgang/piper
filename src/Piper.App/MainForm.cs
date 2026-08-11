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
    private readonly TextBox _logView;
    private readonly DarkTabControl _rightTabs;

    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _captureStatusLabel;
    private readonly ToolStripStatusLabel _captureScopeLabel;
    private readonly ToolStripStatusLabel _breakpointsLabel;
    private readonly ToolStripStatusLabel _sessionsLabel;
    private readonly ToolStripStatusLabel _selectedSessionDetailsLabel;

    private static readonly Image CaptureOnIcon = CreateDotIcon(Palette.StatusOk);
    private static readonly Image CaptureOffIcon = CreateDotIcon(Palette.StatusServerError);
    private static readonly Image ScopeIcon = CreateScopeIcon();
    private static readonly Image BreakpointIcon = CreateBreakpointIcon();
    private static readonly Image SessionsIcon = CreateSessionsIcon();

    private SystemProxy.Snapshot? _proxySnapshot;
    private CaptureScope _captureScope = CaptureScope.AllProcesses;
    private bool _captureEnabledOnStartup = true;
    private bool _captureToggleInProgress;
    private bool _restoreMaximized;
    private bool _shutdownInProgress;
    private bool _closeAfterShutdown;

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
        _rightTabs.AllowDrop = true;
        _rightTabs.DragEnter += OnComposerDragEnter;
        _rightTabs.DragOver += OnComposerDragOver;
        _rightTabs.DragDrop += OnComposerDragDrop;

        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(BuildMenu());
        Controls.Add(statusBar);
        EnableSazFileDrop(this);

        _sessionList.SelectionChanged += (_, session) => _inspector.Show(session);
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

        // Known simplification: the Filters tab writes straight into the same FilterText the
        // grid's own ad-hoc filter box uses, so applying a filterset overwrites anything typed
        // there by hand, and the two are never combined. Accepted scope reduction, not a bug.
        _filterPanel.FilterChanged += (_, query) =>
        {
            _sessionList.FilterText = query;
            var admissionQuery = SearchQuery.Parse(query);
            _store.CompletedSessionFilter = admissionQuery.IsEmpty ? null : admissionQuery.Matches;
            FilterSettingsStore.Save(_filterPanel.Settings);
            _rightTabs.SetTabChecked(filtersPage, _filterPanel.Settings.UseFilters);
        };

        // Restore after subscribing so the stored query is applied to the session grid too.
        if (FilterSettingsStore.Load() is { } filterSettings)
            _filterPanel.ApplySettings(filterSettings);

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

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _mainSplit.SplitterDistance = (int)(_mainSplit.Width * 0.55);
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

    private static TabPage NewPage(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    private void OnComposerDragEnter(object? sender, DragEventArgs e) => SetComposerDragEffect(e);

    private void OnComposerDragOver(object? sender, DragEventArgs e) => SetComposerDragEffect(e);

    private void SetComposerDragEffect(DragEventArgs e)
    {
        if (!HasCapturedRequest(e.Data) || !IsComposerDropTarget(e))
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        // Selecting on hover makes the Composer target clear even when it was not the active tab.
        _rightTabs.SelectedIndex = 1;
        e.Effect = DragDropEffects.Copy;
    }

    private void OnComposerDragDrop(object? sender, DragEventArgs e)
    {
        if (!IsComposerDropTarget(e)
            || e.Data?.GetData(typeof(Session)) is not Session session
            || session.Request is null)
            return;

        _rightTabs.SelectedIndex = 1;
        _composer.LoadSession(session);
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
        if (paths.Length > 0) ImportSazFiles(paths);
    }

    private bool IsComposerDropTarget(DragEventArgs e)
    {
        var point = _rightTabs.PointToClient(new Point(e.X, e.Y));
        var composerTab = _rightTabs.GetTabRect(1);
        if (composerTab.Contains(point)) return true;

        // Once hover has activated Composer, continue accepting the drop inside its page too.
        return _rightTabs.SelectedIndex == 1 && point.Y >= composerTab.Bottom;
    }

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
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("&Clear sessions\tCtrl+X", null, (_, _) => _store.Clear());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("&Configurations...", null, (_, _) => ShowConfigurations());
        var hosts = new ToolStripMenuItem("&Hosts...", null, (_, _) => ShowHosts());
        tools.DropDownItems.Add(hosts);
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

        toolbar.Items.AddRange([
            clear, new ToolStripSeparator(),
            composer,
        ]);
        return toolbar;
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
            _rightTabs.SelectedIndex = 3; // surface the Log tab (Inspectors, Composer, Filters, Log)
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
                if (_proxySnapshot is { } snapshot)
                {
                    SystemProxy.Restore(snapshot);
                    _proxySnapshot = null;
                    AppendLog("Capture stopped and the previous system proxy settings were restored.");
                }
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
            _proxySnapshot = SystemProxy.Capture();
            SystemProxy.Enable(endpoint);
            AppendLog($"System proxy set to {endpoint}.");
        }
        catch (Exception ex)
        {
            _proxySnapshot = null;
            AppendLog($"Could not set the system proxy: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
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
        _sessionsLabel.Text = $"{_store.Count:N0} sessions";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closeAfterShutdown)
        {
            base.OnFormClosing(e);
            return;
        }

        SaveWindowBounds();

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
