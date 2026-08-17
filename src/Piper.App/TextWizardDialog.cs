using System.Text.Json;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Text;

namespace Piper.App;

/// <summary>
/// Fiddler's TextWizard: paste a value, pick a transform, read the result. Opened from the Tools menu or
/// from the inspector context menus, which is where the encoded values in captured traffic actually live.
/// </summary>
public sealed class TextWizardDialog : Form
{
    /// <summary>
    /// Text can arrive here straight from an origin response through the inspector menus, so the input is
    /// bounded. Every transform is O(n), so a megabyte stays well inside a UI-thread frame.
    /// </summary>
    private const int MaxInputLength = 1024 * 1024;

    private static readonly (string Label, TextTransform Transform)[] Choices =
    [
        ("URL encode", TextTransform.UrlEncode),
        ("URL decode", TextTransform.UrlDecode),
        ("HTML encode", TextTransform.HtmlEncode),
        ("HTML decode", TextTransform.HtmlDecode),
        ("To Base64", TextTransform.Base64Encode),
        ("From Base64", TextTransform.Base64Decode),
        ("To Base64Url (JWT)", TextTransform.Base64UrlEncode),
        ("From Base64Url (JWT)", TextTransform.Base64UrlDecode),
        ("To hex", TextTransform.HexEncode),
        ("From hex", TextTransform.HexDecode),
        ("To JSON string", TextTransform.JsonStringEncode),
        ("From JSON string", TextTransform.JsonStringDecode),
        ("MD5 hash", TextTransform.Md5),
        ("SHA-1 hash", TextTransform.Sha1),
        ("SHA-256 hash", TextTransform.Sha256),
        ("SHA-512 hash", TextTransform.Sha512),
    ];

    private static TextWizardDialog? _open;

    private readonly TextBox _input;
    private readonly ListBox _choices;
    private readonly TextBox _output;
    private readonly Label _status;

    private TextWizardDialog()
    {
        Text = "TextWizard";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(640, 480);
        ClientSize = new Size(860, 620);
        MinimizeBox = false;
        ShowInTaskbar = false;

        _input = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = Palette.Mono,
            MaxLength = MaxInputLength,
            AccessibleName = "TextWizard input",
        };
        _input.TextChanged += (_, _) => Run();

        _output = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = Palette.Mono,
            AccessibleName = "TextWizard output",
        };

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            Padding = new Padding(2, 4, 0, 0),
            ForeColor = Palette.TextDim,
            AccessibleName = "TextWizard status",
        };

        _choices = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            Font = Palette.UiFont,
            AccessibleName = "TextWizard transform",
        };
        foreach (var choice in Choices) _choices.Items.Add(choice.Label);
        _choices.SelectedIndex = 0;
        _choices.SelectedIndexChanged += (_, _) => Run();

        // No SplitterDistance: at construction the container is still its default 150x100, so any distance
        // worth asking for is out of range. An even split of input and output is the right default anyway.
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        split.Panel1.Controls.Add(_input);
        split.Panel1.Controls.Add(Caption("Input"));
        split.Panel2.Controls.Add(_output);
        split.Panel2.Controls.Add(Caption("Output"));
        split.Panel2.Controls.Add(_status);

        var chooser = new Panel { Dock = DockStyle.Left, Width = 200, Padding = new Padding(12, 0, 8, 0) };
        chooser.Controls.Add(_choices);
        chooser.Controls.Add(Caption("Transform"));

        var editors = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 0, 12, 0) };
        editors.Controls.Add(split);

        var copy = new Button { Text = "Copy Output", Size = new Size(120, 34) };
        copy.Click += (_, _) => CopyOutput();
        var chain = new Button { Text = "Output to Input", Size = new Size(140, 34) };
        chain.Click += (_, _) => _input.Text = _output.Text;
        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Size = new Size(100, 34) };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 390,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actions.Controls.Add(close);
        actions.Controls.Add(chain);
        actions.Controls.Add(copy);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(12, 12, 12, 10) };
        footer.Paint += DrawFooterBorder;
        footer.Controls.Add(actions);

        Controls.Add(editors);
        Controls.Add(chooser);
        Controls.Add(footer);
        CancelButton = close;
        Palette.Apply(this);
    }

    /// <summary>
    /// Shows the single shared TextWizard window, optionally seeded with <paramref name="input"/>. The
    /// window is modeless so the user can keep reading sessions behind it, and owned so it closes with them.
    /// </summary>
    public static void Open(IWin32Window? owner, string? input = null)
    {
        if (_open is null || _open.IsDisposed)
        {
            var wizard = new TextWizardDialog();
            wizard.FormClosed += (_, _) => _open = null;
            _open = wizard;
            wizard.Show(owner);
        }

        if (input is not null) _open.SetInput(input);
        if (_open.WindowState == FormWindowState.Minimized) _open.WindowState = FormWindowState.Normal;
        _open.Activate();
    }

    private void SetInput(string text)
    {
        // MaxLength bounds typing and pasting but not an assignment, so oversized text is cut here too.
        _input.Text = text.Length > MaxInputLength ? text[..MaxInputLength] : text;
        _input.Select(0, 0);
        _input.Focus();
    }

    private void Run()
    {
        var index = _choices.SelectedIndex;
        if (index < 0 || index >= Choices.Length) return;

        try
        {
            _output.Text = TextTransforms.Apply(Choices[index].Transform, _input.Text);
            _status.ForeColor = Palette.TextDim;
            _status.Text = _input.TextLength >= MaxInputLength
                ? $"Input reached the {MaxInputLength / 1024 / 1024} MiB limit and was truncated."
                : string.Empty;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            // Malformed input is the normal case for a decoder, not a crash: say so and show nothing.
            _output.Clear();
            _status.ForeColor = Palette.StatusClientError;
            _status.Text = $"{_choices.Text}: {ex.Message}";
        }
    }

    private void CopyOutput()
    {
        if (_output.TextLength == 0) return;
        Clipboard.SetText(_output.Text);
    }

    private static Label Caption(string text) => new()
    {
        Dock = DockStyle.Top,
        Height = 24,
        Padding = new Padding(2, 4, 0, 0),
        ForeColor = Palette.TextDim,
        Text = text,
    };

    private static void DrawFooterBorder(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(Palette.Border);
        e.Graphics.DrawLine(pen, 0, 0, e.ClipRectangle.Width, 0);
    }
}
