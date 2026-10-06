using System.Windows.Forms;
using Piper.App.Theme;

namespace Piper.App;

/// <summary>A compact, themed summary of Piper and the precise build that is running.</summary>
public sealed class AboutDialog : Form
{
    private readonly Bitmap _logo;
    private readonly Font _headingFont;

    public AboutDialog()
    {
        Text = Strings.App.AboutCaption;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(520, 250);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = Palette.UiFont;

        _logo = LoadLogo();
        _headingFont = new Font(Palette.UiFontBold.FontFamily, Palette.UiFontBold.SizeInPoints * 1.8f,
            Palette.UiFontBold.Style, GraphicsUnit.Point);

        var heading = new Label
        {
            Text = Strings.App.Name,
            Font = _headingFont,
            ForeColor = Palette.Accent,
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
        };
        var version = new Label
        {
            Text = Strings.App.AboutVersion(CurrentVersion),
            AutoSize = true,
            ForeColor = Palette.TextDim,
            Margin = new Padding(0, 0, 0, 14),
        };
        var description = new Label
        {
            Text = Strings.App.AboutDescription,
            AutoSize = true,
            MaximumSize = new Size((int)Math.Round(360 * FontScale.Multiplier), 0),
            ForeColor = Palette.TextDim,
            Margin = Padding.Empty,
        };

        var details = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(8, 0, 0, 0),
        };
        details.Controls.AddRange([heading, version, description]);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 22, 24, 16),
            ColumnCount = 2,
            RowCount = 1,
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.Controls.Add(new PictureBox
        {
            Image = _logo,
            SizeMode = PictureBoxSizeMode.CenterImage,
            Size = new Size(64, 64),
            Anchor = AnchorStyles.Top | AnchorStyles.Left,
        }, 0, 0);
        content.Controls.Add(details, 1, 0);

        var close = new Button
        {
            Text = Strings.App.AboutClose,
            DialogResult = DialogResult.OK,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 5, 14, 5),
        };
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = close.PreferredSize.Height + 28,
            Padding = new Padding(12, 10, 12, 10),
        };
        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(Palette.Border);
            e.Graphics.DrawLine(pen, 0, 0, e.ClipRectangle.Width, 0);
        };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actions.Controls.Add(close);
        footer.Controls.Add(actions);

        Controls.Add(content);
        Controls.Add(footer);
        AcceptButton = close;
        CancelButton = close;

        Palette.ScaleDialogSize(this);
        Palette.Apply(this);

        // Palette.Apply normalizes all labels to the regular text colour; these remain a title and
        // supporting information after that walk.
        heading.ForeColor = Palette.Accent;
        version.ForeColor = Palette.TextDim;
        description.ForeColor = Palette.TextDim;
    }

    private static string CurrentVersion =>
        typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static Bitmap LoadLogo()
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            return icon?.ToBitmap() ?? SystemIcons.Information.ToBitmap();
        }
        catch (ArgumentException)
        {
            // An extracted icon is decorative. The generic information icon keeps the dialog useful
            // for hosts that do not have a conventional executable path, such as UI tests.
            return SystemIcons.Information.ToBitmap();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _logo.Dispose();
            _headingFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
