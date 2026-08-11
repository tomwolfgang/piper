using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Proxy;

namespace Piper.App;

/// <summary>Edits origin overrides in the same compact form used by Fiddler's Host Remapping tool.</summary>
public sealed class HostsDialog : Form
{
    private readonly CheckBox _enabled;
    private readonly TextBox _mappings;

    public HostsDialog(HostRemappingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Text = "Host Remapping";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(680, 460);
        ClientSize = new Size(800, 560);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        _enabled = new CheckBox
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(12, 10, 8, 0),
            Text = "Enable remapping of requests from one host to a different host or IP, overriding DNS.",
            Checked = settings.Enabled,
        };

        _mappings = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = Palette.Mono,
            Text = settings.Mappings,
            Margin = new Padding(12, 0, 12, 0),
        };

        var editorPadding = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        editorPadding.Controls.Add(_mappings);

        var example = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 62,
            Padding = new Padding(12, 6, 0, 0),
            ForeColor = Palette.TextDim,
            Text = "# Destination (IP/host)    Requested host\r\nwww.example.com    www.example2.io\r\n# Lines from a Windows hosts file (IP followed by host names) are also supported.",
        };

        var import = new Button { Text = "Import Windows Hosts File", Size = new Size(190, 34) };
        import.Click += (_, _) => ImportWindowsHostsFile();
        var save = new Button { Text = "Save", DialogResult = DialogResult.OK, Size = new Size(100, 34) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 34) };
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 68,
            Padding = new Padding(12, 12, 12, 10),
        };
        footer.Paint += DrawFooterBorder;
        var importActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            Width = 200,
            WrapContents = false,
        };
        importActions.Controls.Add(import);
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 216,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        footer.Controls.Add(importActions);
        footer.Controls.Add(actions);
        actions.Controls.Add(import);

        Controls.Add(editorPadding);
        Controls.Add(_enabled);
        Controls.Add(example);
        Controls.Add(footer);
        AcceptButton = save;
        CancelButton = cancel;
        Palette.Apply(this);
    }

    public HostRemappingSettings Settings => new()
    {
        Enabled = _enabled.Checked,
        Mappings = _mappings.Text,
    };

    private void ImportWindowsHostsFile()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc", "hosts");
        try
        {
            _mappings.Text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Piper could not read the Windows hosts file:\r\n{path}\r\n\r\n{ex.Message}",
                "Import Windows Hosts File", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void DrawFooterBorder(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(Palette.Border);
        e.Graphics.DrawLine(pen, 0, 0, e.ClipRectangle.Width, 0);
    }
}
