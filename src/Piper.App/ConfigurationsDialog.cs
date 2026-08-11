using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Proxy;

namespace Piper.App;

/// <summary>Persistent proxy settings and HTTPS certificate management in one place.</summary>
public sealed class ConfigurationsDialog : Form
{
    private CheckBox _captureOnStartup = null!;
    private ComboBox _captureScope = null!;
    private CheckBox _decryptHttps = null!;
    private CheckBox _http2Downstream = null!;
    private CheckBox _http2Upstream = null!;
    private CheckBox _http3Upstream = null!;

    public ConfigurationsDialog(ProxyOptions options, bool captureOnStartup, string captureScope,
        Action trustRoot, Action removeTrustedRoot, Action exportRoot, Action openCertificateFolder)
    {
        Text = "Configurations";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(700, 540);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        var tabs = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        tabs.TabPages.Add(CreateGeneralPage(captureOnStartup, captureScope));
        tabs.TabPages.Add(CreateHttpsPage(options, trustRoot, removeTrustedRoot, exportRoot, openCertificateFolder));

        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Size = new Size(100, 34) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 34) };
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
    }

    public bool CaptureOnStartup => _captureOnStartup.Checked;

    public string CaptureScope => (_captureScope.SelectedItem as CaptureScopeChoice)?.Value ?? "AllProcesses";

    public void ApplyTo(ProxyOptions options)
    {
        options.DecryptHttps = _decryptHttps.Checked;
        options.EnableHttp2Downstream = _http2Downstream.Checked;
        options.EnableHttp2Upstream = _http2Upstream.Checked;
        options.EnableHttp3Upstream = _http3Upstream.Checked;
    }

    private TabPage CreateGeneralPage(bool captureOnStartup, string captureScope)
    {
        var page = new TabPage("General");
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 4,
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var explanation = new Label
        {
            Text = "These settings are saved for future Piper sessions.",
            AutoSize = true,
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 0, 0, 14),
        };
        panel.Controls.Add(explanation, 0, 0);
        panel.SetColumnSpan(explanation, 2);

        _captureOnStartup = new CheckBox
        {
            Text = "Start capturing when Piper opens",
            Checked = captureOnStartup,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 12),
        };
        panel.Controls.Add(_captureOnStartup, 0, 1);
        panel.SetColumnSpan(_captureOnStartup, 2);

        var scopeLabel = new Label { Text = "Capture scope:", AutoSize = true, Anchor = AnchorStyles.Left };
        _captureScope = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 220,
            Anchor = AnchorStyles.Left,
        };
        _captureScope.Items.AddRange([
            new CaptureScopeChoice("AllProcesses", "All processes"),
            new CaptureScopeChoice("WebBrowsers", "Web browsers"),
            new CaptureScopeChoice("NonBrowsers", "Non-browsers"),
            new CaptureScopeChoice("HideAll", "Hide all"),
        ]);
        _captureScope.SelectedItem = _captureScope.Items.Cast<CaptureScopeChoice>()
            .FirstOrDefault(item => item.Value == captureScope) ?? _captureScope.Items[0];
        panel.Controls.Add(scopeLabel, 0, 2);
        panel.Controls.Add(_captureScope, 1, 2);

        var note = new Label
        {
            Text = "Capture scope controls which sessions are collected and shown.",
            AutoSize = true,
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 10, 0, 0),
        };
        panel.Controls.Add(note, 0, 3);
        panel.SetColumnSpan(note, 2);

        page.Controls.Add(panel);
        return page;
    }

    private TabPage CreateHttpsPage(ProxyOptions options, Action trustRoot, Action removeTrustedRoot,
        Action exportRoot, Action openCertificateFolder)
    {
        var page = new TabPage("HTTPS");
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(16),
        };

        _decryptHttps = AddOption(panel, "Decrypt HTTPS traffic", options.DecryptHttps,
            "Requires the Piper root certificate to be trusted.");
        _http2Downstream = AddOption(panel, "Negotiate HTTP/2 with browsers", options.EnableHttp2Downstream,
            "Applies to new decrypted browser connections.");
        _http2Upstream = AddOption(panel, "Negotiate HTTP/2 with origin servers", options.EnableHttp2Upstream,
            "Applies to new origin connections.");
        _http3Upstream = AddOption(panel, "Attempt HTTP/3 with origin servers (QUIC)", options.EnableHttp3Upstream,
            "Origins are tried over QUIC only after advertising HTTP/3 through Alt-Svc.");

        var certificates = new GroupBox
        {
            Text = "Piper root certificate",
            Width = 590,
            Height = 118,
            Margin = new Padding(0, 14, 0, 0),
        };
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8) };
        actions.Controls.Add(new Button { Text = "Trust root certificate...", AutoSize = true });
        actions.Controls.Add(new Button { Text = "Remove trusted root", AutoSize = true });
        actions.Controls.Add(new Button { Text = "Export root certificate...", AutoSize = true });
        actions.Controls.Add(new Button { Text = "Open certificate folder", AutoSize = true });
        actions.Controls[0].Click += (_, _) => trustRoot();
        actions.Controls[1].Click += (_, _) => removeTrustedRoot();
        actions.Controls[2].Click += (_, _) => exportRoot();
        actions.Controls[3].Click += (_, _) => openCertificateFolder();
        certificates.Controls.Add(actions);
        panel.Controls.Add(certificates);

        page.Controls.Add(panel);
        return page;
    }

    private static CheckBox AddOption(FlowLayoutPanel panel, string text, bool value, string description)
    {
        var option = new CheckBox { Text = text, Checked = value, AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
        var note = new Label { Text = description, AutoSize = true, ForeColor = Palette.TextDim, Margin = new Padding(22, 0, 0, 10) };
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
