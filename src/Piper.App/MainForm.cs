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

    private readonly ToolStripButton _captureButton;
    private readonly ToolStripButton _systemProxyButton;
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly ToolStripStatusLabel _connectionsLabel;

    private SystemProxy.Snapshot? _proxySnapshot;
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

        var toolbar = BuildToolbar(out _captureButton, out _systemProxyButton);
        var statusBar = BuildStatusBar(out _statusLabel, out _connectionsLabel);

        Controls.Add(split);
        Controls.Add(toolbar);
        Controls.Add(BuildMenu());
        Controls.Add(statusBar);

        _sessionList.SelectionChanged += (_, session) => _inspector.Show(session);
        _sessionList.SendToComposerRequested += (_, session) =>
        {
            _rightTabs.SelectedIndex = 1;
            _composer.LoadSession(session);
        };
        _sessionList.SessionActivated += (_, _) => _rightTabs.SelectedIndex = 0;

        // Known simplification: the Filters tab writes straight into the same FilterText the
        // grid's own ad-hoc filter box uses, so applying a filterset overwrites anything typed
        // there by hand, and the two are never combined. Accepted scope reduction, not a bug.
        _filterPanel.FilterChanged += (_, query) =>
        {
            _sessionList.FilterText = query;
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
            : "Root CA is NOT trusted. HTTPS sites will fail until you use Tools > Trust root certificate.");

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
        StartCapture();

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

    // ------------------------------------------------------------------ chrome

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip { Font = Palette.UiFont };

        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add("&Clear sessions\tCtrl+X", null, (_, _) => _store.Clear());
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add("E&xit", null, (_, _) => Close());

        var capture = new ToolStripMenuItem("&Capture");
        capture.DropDownItems.Add("&Start / stop\tF12", null, (_, _) => ToggleCapture());
        capture.DropDownItems.Add("Use as &system proxy", null, (_, _) => ToggleSystemProxy());
        capture.DropDownItems.Add(new ToolStripSeparator());
        var decrypt = new ToolStripMenuItem("&Decrypt HTTPS") { Checked = _options.DecryptHttps, CheckOnClick = true };
        decrypt.CheckedChanged += (_, _) =>
        {
            _options.DecryptHttps = decrypt.Checked;
            AppendLog($"HTTPS decryption {(decrypt.Checked ? "enabled" : "disabled")}.");
        };
        capture.DropDownItems.Add(decrypt);

        // Both only affect new connections going forward -- same caveat as Decrypt HTTPS above.
        var http2Down = new ToolStripMenuItem("Negotiate &HTTP/2 (browser)")
            { Checked = _options.EnableHttp2Downstream, CheckOnClick = true };
        http2Down.CheckedChanged += (_, _) =>
        {
            _options.EnableHttp2Downstream = http2Down.Checked;
            AppendLog($"HTTP/2 negotiation with the browser {(http2Down.Checked ? "enabled" : "disabled")}.");
        };
        capture.DropDownItems.Add(http2Down);

        var http2Up = new ToolStripMenuItem("Negotiate HTTP/2 (&origin)")
            { Checked = _options.EnableHttp2Upstream, CheckOnClick = true };
        http2Up.CheckedChanged += (_, _) =>
        {
            _options.EnableHttp2Upstream = http2Up.Checked;
            AppendLog($"HTTP/2 negotiation with origin servers {(http2Up.Checked ? "enabled" : "disabled")}.");
        };
        capture.DropDownItems.Add(http2Up);

        // Origin-side only: a browser using a system proxy tunnels over TCP and disables QUIC, so
        // there is no browser-facing HTTP/3 to offer.
        var http3Up = new ToolStripMenuItem("Attempt HTTP/&3 (origin, QUIC)")
            { Checked = _options.EnableHttp3Upstream, CheckOnClick = true };
        http3Up.CheckedChanged += (_, _) =>
        {
            _options.EnableHttp3Upstream = http3Up.Checked;
            if (http3Up.Checked && !Piper.Core.Http3.Http3ClientConnection.IsSupported)
            {
                AppendLog("HTTP/3 requested, but QUIC is not available on this machine - requests will stay on TCP.");
                return;
            }
            AppendLog(http3Up.Checked
                ? "HTTP/3 enabled. Origins are tried over QUIC only after advertising h3 via Alt-Svc; needs outbound UDP/443."
                : "HTTP/3 disabled.");
        };
        capture.DropDownItems.Add(http3Up);

        var tools = new ToolStripMenuItem("&Tools");
        tools.DropDownItems.Add("&Trust root certificate...", null, (_, _) => TrustRootCertificate());
        tools.DropDownItems.Add("&Remove trusted root certificate", null, (_, _) => UntrustRootCertificate());
        tools.DropDownItems.Add("&Export root certificate...", null, (_, _) => ExportRootCertificate());
        tools.DropDownItems.Add(new ToolStripSeparator());
        tools.DropDownItems.Add("Open certificate &folder", null, (_, _) =>
        {
            var folder = Path.GetDirectoryName(_ca.RootPfxPath);
            if (folder is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        });

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add("&Search syntax", null, (_, _) => ShowSearchHelp());
        help.DropDownItems.Add("&About", null, (_, _) => MessageBox.Show(this,
            "Piper\r\n\r\nAn HTTP(S) debugging proxy written from scratch on .NET 10.",
            "About Piper", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.AddRange([file, capture, tools, help]);
        return menu;
    }

    private ToolStrip BuildToolbar(out ToolStripButton captureButton, out ToolStripButton systemProxyButton)
    {
        var toolbar = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, Font = Palette.UiFont };

        captureButton = new ToolStripButton("Capturing")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            Checked = true,
            CheckOnClick = false,
        };
        captureButton.Click += (_, _) => ToggleCapture();

        systemProxyButton = new ToolStripButton("System proxy: off")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
        };
        systemProxyButton.Click += (_, _) => ToggleSystemProxy();

        var clear = new ToolStripButton("Clear") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        clear.Click += (_, _) => _store.Clear();

        var composer = new ToolStripButton("Composer  (Ctrl+K)") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        composer.Click += (_, _) =>
        {
            _rightTabs.SelectedIndex = 1;
            _composer.FocusSearch();
        };

        toolbar.Items.AddRange([
            captureButton, new ToolStripSeparator(),
            systemProxyButton, new ToolStripSeparator(),
            clear, new ToolStripSeparator(),
            composer,
        ]);
        return toolbar;
    }

    private static StatusStrip BuildStatusBar(out ToolStripStatusLabel status, out ToolStripStatusLabel connections)
    {
        var bar = new StatusStrip { Font = Palette.UiFont };
        status = new ToolStripStatusLabel("Starting...") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        connections = new ToolStripStatusLabel("0 connections");
        bar.Items.AddRange([status, connections]);
        return bar;
    }

    // ----------------------------------------------------------------- actions

    private void StartCapture()
    {
        try
        {
            _proxy.Start();
            _captureButton.Text = "Capturing";
            _captureButton.ForeColor = Palette.StatusOk;
        }
        catch (Exception ex)
        {
            // Reported in the log and the status bar rather than a dialog, so a busy port
            // never blocks the UI and the full exception stays available for diagnosis.
            AppendLog($"Could not listen on 127.0.0.1:{_options.Port} - {ex.GetType().Name}: {ex.Message}");
            AppendLog("Another proxy may be using the port. Change it, or stop the other proxy, then press F12.");
            _captureButton.Text = $"Port {_options.Port} unavailable";
            _captureButton.ForeColor = Palette.StatusServerError;
            _rightTabs.SelectedIndex = 3; // surface the Log tab (Inspectors, Composer, Filters, Log)
        }
    }

    private async void ToggleCapture()
    {
        if (_proxy.IsRunning)
        {
            await _proxy.StopAsync();
            _captureButton.Text = "Not capturing";
            _captureButton.ForeColor = Palette.StatusServerError;
        }
        else
        {
            StartCapture();
        }
    }

    private void ToggleSystemProxy()
    {
        if (_proxySnapshot is not null)
        {
            SystemProxy.Restore(_proxySnapshot);
            _proxySnapshot = null;
            _systemProxyButton.Text = "System proxy: off";
            _systemProxyButton.ForeColor = Palette.Text;
            AppendLog("Restored the previous system proxy settings.");
            return;
        }

        if (!_proxy.IsRunning)
        {
            MessageBox.Show(this, "Start capturing before enabling the system proxy.",
                "Piper", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var endpoint = $"127.0.0.1:{_proxy.Endpoint!.Port}";
        var answer = MessageBox.Show(this,
            $"Route this user's HTTP and HTTPS traffic through Piper at {endpoint}?\r\n\r\n"
            + "This changes the Windows per-user proxy setting. Your current settings are saved "
            + "and restored when you turn this off or close Piper.",
            "Use as system proxy", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (answer != DialogResult.OK) return;

        EnableSystemProxy();
    }

    private void EnableSystemProxy()
    {
        var endpoint = $"127.0.0.1:{_proxy.Endpoint!.Port}";
        try
        {
            _proxySnapshot = SystemProxy.Capture();
            SystemProxy.Enable(endpoint);
            _systemProxyButton.Text = $"System proxy: {endpoint}";
            _systemProxyButton.ForeColor = Palette.StatusOk;
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
        _connectionsLabel.Text = $"{_proxy.ActiveConnections} connections   -   {_store.Count:N0} sessions";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_closeAfterShutdown)
        {
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
                _connectionsLabel.Text = "Please wait";
                await Task.Yield(); // Let the status change paint before WinINET is notified.

                await Task.Run(() => SystemProxy.Restore(snapshot));
                _proxySnapshot = null;
            }

            if (_proxy.IsRunning)
            {
                _statusLabel.Text = "Stopping capture...";
                _connectionsLabel.Text = "Please wait";
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
