using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Proxy;

namespace Piper.App;

/// <summary>Persistent proxy settings and HTTPS certificate management in one place.</summary>
public sealed class ConfigurationsDialog : Form
{
    private CheckBox _captureOnStartup = null!;
    private CheckBox _wheelZoom = null!;
    private ComboBox _captureScope = null!;
    private CheckBox _decryptHttps = null!;
    private CheckBox _askToTrust = null!;
    private CheckBox _decryptWhenUntrusted = null!;
    private DarkTabControl _tabs = null!;
    private Label _trustStatus = null!;
    private readonly Func<bool> _isCertificateTrusted;
    private CheckBox _http2Downstream = null!;
    private CheckBox _http2Upstream = null!;
    private CheckBox _http3Upstream = null!;
    private CheckBox _validateUpstreamCertificates = null!;
    private CheckBox _analyticsEnabled = null!;

    public ConfigurationsDialog(ProxyOptions options, bool captureOnStartup, string captureScope,
        bool wheelZoom, bool analyticsEnabled,
        Action trustRoot, Action removeTrustedRoot, Action exportRoot, Action openCertificateFolder,
        Action openAnalyticsFolder, Func<bool> isCertificateTrusted)
    {
        _isCertificateTrusted = isCertificateTrusted;
        Text = Strings.Configurations.Caption;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(700, 600);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var tabs = _tabs = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        tabs.TabPages.Add(CreateGeneralPage(captureOnStartup, captureScope, wheelZoom));
        tabs.TabPages.Add(CreateHttpsPage(options, trustRoot, removeTrustedRoot, exportRoot, openCertificateFolder));
        tabs.TabPages.Add(CreatePrivacyPage(analyticsEnabled, openAnalyticsFolder));

        var save = new Button { Text = Strings.Common.Save, DialogResult = DialogResult.OK, Size = new Size(100, 34) };
        var cancel = new Button { Text = Strings.Common.Cancel, DialogResult = DialogResult.Cancel, Size = new Size(100, 34) };
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 68,
            Padding = new Padding(12, 12, 12, 10),
        };
        footer.Paint += DrawFooterBorder;
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 216,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        footer.Controls.Add(actions);

        Controls.Add(tabs);
        Controls.Add(footer);
        AcceptButton = save;
        CancelButton = cancel;
        Palette.Apply(this);
        // After the theme walk, which resets label colours.
        RefreshTrustStatus();
    }

    /// <summary>Opens on the HTTPS tab, where the certificate controls are.</summary>
    public void SelectHttpsTab() => _tabs.SelectedIndex = 1;

    public bool CaptureOnStartup => _captureOnStartup.Checked;

    /// <summary>Whether Ctrl+MouseWheel should resize the UI.</summary>
    public bool WheelZoom => _wheelZoom.Checked;

    public bool AnalyticsEnabled => _analyticsEnabled.Checked;

    public string CaptureScope => (_captureScope.SelectedItem as CaptureScopeChoice)?.Value ?? "AllProcesses";

    public void ApplyTo(ProxyOptions options)
    {
        options.DecryptHttps = _decryptHttps.Checked;
        options.AskToTrustCertificate = _askToTrust.Checked;
        options.DecryptWhenUntrusted = _decryptWhenUntrusted.Checked;
        options.EnableHttp2Downstream = _http2Downstream.Checked;
        options.EnableHttp2Upstream = _http2Upstream.Checked;
        options.EnableHttp3Upstream = _http3Upstream.Checked;
        options.ValidateUpstreamCertificates = _validateUpstreamCertificates.Checked;
    }

    private TabPage CreateGeneralPage(bool captureOnStartup, string captureScope, bool wheelZoom)
    {
        var page = new TabPage(Strings.Configurations.GeneralTab);
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 6,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var explanation = new Label
        {
            Text = Strings.Configurations.SavedForFutureSessions,
            AutoSize = true,
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 0, 0, 14),
        };
        panel.Controls.Add(explanation, 0, 0);
        panel.SetColumnSpan(explanation, 2);

        _captureOnStartup = new CheckBox
        {
            Text = Strings.Configurations.CaptureOnStartup,
            Checked = captureOnStartup,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        panel.Controls.Add(_captureOnStartup, 0, 1);
        panel.SetColumnSpan(_captureOnStartup, 2);

        var scopeLabel = new Label { Text = Strings.Configurations.CaptureScopeLabel, AutoSize = true, Anchor = AnchorStyles.Left };
        _captureScope = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220,
            Anchor = AnchorStyles.Left,
        };
        _captureScope.Items.AddRange([
            new CaptureScopeChoice("AllProcesses", Strings.Configurations.ScopeAllProcesses),
            new CaptureScopeChoice("WebBrowsers", Strings.Configurations.ScopeWebBrowsers),
            new CaptureScopeChoice("NonBrowsers", Strings.Configurations.ScopeNonBrowsers),
            new CaptureScopeChoice("HideAll", Strings.Configurations.ScopeHideAll),
        ]);
        _captureScope.SelectedItem = _captureScope.Items.Cast<CaptureScopeChoice>()
            .FirstOrDefault(item => item.Value == captureScope) ?? _captureScope.Items[0];
        panel.Controls.Add(scopeLabel, 0, 2);
        panel.Controls.Add(_captureScope, 1, 2);

        var note = new Label
        {
            Text = Strings.Configurations.CaptureScopeNote,
            AutoSize = true,
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 10, 0, 0),
        };
        panel.Controls.Add(note, 0, 3);
        panel.SetColumnSpan(note, 2);

        _wheelZoom = new CheckBox
        {
            Text = Strings.Configurations.WheelZoom,
            Checked = wheelZoom,
            AutoSize = true,
            Margin = new Padding(0, 18, 0, 0),
        };
        panel.Controls.Add(_wheelZoom, 0, 4);
        panel.SetColumnSpan(_wheelZoom, 2);

        var zoomNote = new Label
        {
            Text = Strings.Configurations.WheelZoomNote,
            AutoSize = true,
            ForeColor = Palette.TextDim,
            Margin = new Padding(22, 2, 0, 0),
        };
        panel.Controls.Add(zoomNote, 0, 5);
        panel.SetColumnSpan(zoomNote, 2);

        page.Controls.Add(panel);
        return page;
    }

    private TabPage CreateHttpsPage(ProxyOptions options, Action trustRoot, Action removeTrustedRoot,
        Action exportRoot, Action openCertificateFolder)
    {
        var page = new TabPage(Strings.Configurations.HttpsTab);
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16),
        };

        _decryptHttps = AddOption(panel, Strings.Configurations.DecryptHttps, options.DecryptHttps,
            Strings.Configurations.DecryptHttpsNote);
        _askToTrust = AddOption(panel, Strings.Configurations.AskToTrust, options.AskToTrustCertificate,
            Strings.Configurations.AskToTrustNote, descriptionMaxWidth: 560);
        _decryptWhenUntrusted = AddOption(panel, Strings.Configurations.DecryptWhenUntrusted, options.DecryptWhenUntrusted,
            Strings.Configurations.DecryptWhenUntrustedNote, descriptionMaxWidth: 560);
        _http2Downstream = AddOption(panel, Strings.Configurations.Http2Downstream, options.EnableHttp2Downstream,
            Strings.Configurations.Http2DownstreamNote);
        _http2Upstream = AddOption(panel, Strings.Configurations.Http2Upstream, options.EnableHttp2Upstream,
            Strings.Configurations.Http2UpstreamNote);
        _http3Upstream = AddOption(panel, Strings.Configurations.Http3Upstream, options.EnableHttp3Upstream,
            Strings.Configurations.Http3UpstreamNote);
        _validateUpstreamCertificates = AddOption(panel, Strings.Configurations.ValidateUpstream,
            options.ValidateUpstreamCertificates,
            Strings.Configurations.ValidateUpstreamNote,
            Palette.StatusServerError, descriptionMaxWidth: 560);

        var certificates = new GroupBox
        {
            Text = Strings.Configurations.CertificatesGroup,
            Width = 590,
            Height = 172,
            Margin = new Padding(0, 14, 0, 0),
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        actions.Controls.Add(new Button { Text = Strings.Configurations.TrustRoot, AutoSize = true });
        actions.Controls.Add(new Button { Text = Strings.Configurations.RemoveTrustedRoot, AutoSize = true });
        actions.Controls.Add(new Button { Text = Strings.Configurations.ExportRoot, AutoSize = true });
        actions.Controls.Add(new Button { Text = Strings.Configurations.OpenCertificateFolder, AutoSize = true });
        // The status line is read again after each action: Trust and Remove change it at once, and
        // they take effect at once whether or not this dialog is later saved.
        actions.Controls[0].Click += (_, _) => { trustRoot(); RefreshTrustStatus(); };
        actions.Controls[1].Click += (_, _) => { removeTrustedRoot(); RefreshTrustStatus(); };
        actions.Controls[2].Click += (_, _) => exportRoot();
        actions.Controls[3].Click += (_, _) => openCertificateFolder();
        _trustStatus = new Label { Dock = DockStyle.Top, Height = 30, Padding = new Padding(8, 8, 8, 0) };
        var actionsNote = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 28,
            Padding = new Padding(8, 0, 8, 0),
            Text = Strings.Configurations.CertificateActionsNote,
            ForeColor = Palette.TextDim,
        };
        certificates.Controls.Add(actions);
        certificates.Controls.Add(_trustStatus);
        certificates.Controls.Add(actionsNote);
        panel.Controls.Add(certificates);

        page.Controls.Add(panel);
        return page;
    }


    /// <summary>
    /// The permanent home of the usage-reporting switch, and of the button that shows the user the
    /// actual queued file rather than asking them to take a description of it on trust.
    /// </summary>
    private TabPage CreatePrivacyPage(bool analyticsEnabled, Action openAnalyticsFolder)
    {
        var page = new TabPage(Strings.Configurations.PrivacyTab);
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16),
        };

        _analyticsEnabled = AddOption(panel, Strings.Configurations.AnalyticsEnabled, analyticsEnabled,
            Strings.Configurations.AnalyticsEnabledNote,
            descriptionMaxWidth: 560);

        var queued = new GroupBox
        {
            Text = Strings.Configurations.PendingFeedback,
            Width = 590,
            Height = 96,
            Margin = new Padding(0, 14, 0, 0),
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        var note = new Label
        {
            Text = Strings.Configurations.PendingFeedbackNote,
            AutoSize = true,
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 6, 0, 8),
        };
        var open = new Button { Text = Strings.Configurations.OpenReportsFolder, AutoSize = true };
        open.Click += (_, _) => openAnalyticsFolder();
        actions.Controls.Add(note);
        actions.Controls.Add(open);
        queued.Controls.Add(actions);
        panel.Controls.Add(queued);

        page.Controls.Add(panel);
        return page;
    }

    /// <summary>Shows whether Windows trusts the Piper root right now.</summary>
    private void RefreshTrustStatus()
    {
        var trusted = _isCertificateTrusted();
        _trustStatus.Text = trusted ? Strings.Configurations.CertificateTrusted : Strings.Configurations.CertificateNotTrusted;
        _trustStatus.ForeColor = trusted ? Palette.StatusOk : Palette.StatusClientError;
    }

    private static CheckBox AddOption(FlowLayoutPanel panel, string text, bool value, string description,
        Color? descriptionColor = null, int? descriptionMaxWidth = null)
    {
        var option = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
        var note = new Label
        {
            Text = description,
            AutoSize = true,
            ForeColor = descriptionColor ?? Palette.TextDim,
            Margin = new Padding(22, 0, 0, 10),
            MaximumSize = descriptionMaxWidth is { } width ? new Size(width, 0) : Size.Empty,
        };
        panel.Controls.Add(option);
        panel.Controls.Add(note);
        return option;
    }

    private static void DrawFooterBorder(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(Palette.Border);
        e.Graphics.DrawLine(pen, 0, 0, e.ClipRectangle.Width, 0);
    }

    private sealed record CaptureScopeChoice(string Value, string Text)
    {
        public override string ToString() => Text;
    }
}
