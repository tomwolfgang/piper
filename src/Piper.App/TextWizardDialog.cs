using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Text;

namespace Piper.App;

/// <summary>
/// Fiddler's TextWizard: paste a value, pick a transform, read the result. Laid out the same way, so the
/// transform list and its wording carry over. Opened from the Tools menu or from the inspector context
/// menus, which is where the encoded values in captured traffic actually live.
/// </summary>
public sealed class TextWizardDialog : Form
{
    /// <summary>
    /// Text can arrive here straight from an origin response through the inspector menus, so the input is
    /// bounded. Every transform is O(n), so a megabyte stays well inside a UI-thread frame.
    /// </summary>
    private const int MaxInputLength = 1024 * 1024;

    /// <summary>Fiddler's transform list, in Fiddler's order and using Fiddler's names.</summary>
    private static readonly (string Label, TextTransform Transform)[] Choices =
    [
        ("To Base64", TextTransform.ToBase64),
        ("To Base64URL", TextTransform.ToBase64Url),
        ("From Base64", TextTransform.FromBase64),
        ("URLEncode", TextTransform.UrlEncode),
        ("URLDecode", TextTransform.UrlDecode),
        ("HexEncode", TextTransform.HexEncode),
        ("HexDecode", TextTransform.HexDecode),
        ("To C# byte[]", TextTransform.ToCSharpByteArray),
        ("To JS string", TextTransform.ToJsString),
        ("From JS string", TextTransform.FromJsString),
        ("HTML Encode", TextTransform.HtmlEncode),
        ("HTML Decode", TextTransform.HtmlDecode),
        ("To UTF-7", TextTransform.ToUtf7),
        ("From UTF-7", TextTransform.FromUtf7),
        ("To DeflatedSAML", TextTransform.ToDeflatedSaml),
        ("From DeflatedSAML", TextTransform.FromDeflatedSaml),
        ("To MD5", TextTransform.Md5),
        ("To SHA1", TextTransform.Sha1),
        ("To SHA256", TextTransform.Sha256),
        ("To SHA384", TextTransform.Sha384),
        ("To SHA512", TextTransform.Sha512),
    ];

    private static TextWizardDialog? _open;

    private readonly SplitContainer _split;
    private readonly TextBox _input;
    private readonly ComboBox _transform;
    private readonly CheckBox _viewBytes;
    private readonly TextBox _output;
    private readonly Label _status;

    private string _result = string.Empty;

    private TextWizardDialog()
    {
        Text = "TextWizard";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(700, 500);
        ClientSize = new Size(900, 660);
        MinimizeBox = false;
        ShowInTaskbar = false;

        // AutoSize throughout: fixed pixel sizes do not survive display scaling, and a clipped button
        // loses the part of its hit box that the text was overflowing into.
        var hint = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10, 6, 10, 6),
            BackColor = Palette.SurfaceAlt,
            ForeColor = Palette.TextDim,
            Text = "Encodes and decodes text. Enter text above and choose a transform.",
        };

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

        _transform = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Palette.UiFont,
            AccessibleName = "TextWizard transform",
            Margin = new Padding(0, 3, 0, 0),
        };
        foreach (var choice in Choices) _transform.Items.Add(choice.Label);
        // Wide enough for the longest entry at whatever scale the display is running.
        _transform.Width = _transform.Items.Cast<string>()
            .Max(item => TextRenderer.MeasureText(item, Palette.UiFont).Width) + 40;
        _transform.SelectedIndex = 0;
        _transform.SelectedIndexChanged += (_, _) => Run();

        _viewBytes = new CheckBox
        {
            Text = "View bytes",
            Font = Palette.UiFont,
            AutoSize = true,
            AccessibleName = "TextWizard view bytes",
            Margin = new Padding(16, 6, 0, 0),
        };
        _viewBytes.CheckedChanged += (_, _) => ShowResult();

        var saveToFile = Action("Save Output...", SaveOutput);
        var chain = Action("Send output to input", () => _input.Text = _result);

        // The strip that sits between the two editors, matching Fiddler's arrangement.
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 6, 8, 6),
            WrapContents = false,
        };
        bar.Controls.Add(new Label
        {
            Text = "Transform:",
            Font = Palette.UiFont,
            AutoSize = true,
            Margin = new Padding(0, 7, 6, 0),
        });
        bar.Controls.Add(_transform);
        bar.Controls.Add(_viewBytes);
        bar.Controls.Add(saveToFile);
        bar.Controls.Add(chain);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        _split.Panel1.Controls.Add(_input);
        _split.Panel2.Controls.Add(_output);
        _split.Panel2.Controls.Add(bar);

        // A modeless form ignores a button's DialogResult, so Close has to be wired explicitly. Without
        // this the button and the Escape key both do nothing while the title-bar X still works.
        // Sized from PreferredSize and then pinned right: Dock and AutoSize fight each other, and the
        // loser is the button's height.
        var close = Action("Close", Close);
        close.Margin = Padding.Empty;
        close.AutoSize = false;
        close.Size = close.PreferredSize;
        close.Dock = DockStyle.Right;

        _status = new Label
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(2, 8, 8, 0),
            ForeColor = Palette.TextDim,
            AutoEllipsis = true,
            AccessibleName = "TextWizard status",
        };

        // Status and Close share one strip rather than each claiming a band of their own. PreferredSize
        // rather than Height: AutoSize has not been applied yet while the constructor is still running.
        var footer = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = close.Height + 16,
            Padding = new Padding(10, 8, 10, 8),
        };
        footer.Controls.Add(_status);
        footer.Controls.Add(close);

        Controls.Add(_split);
        Controls.Add(hint);
        Controls.Add(footer);
        CancelButton = close;
        Palette.Apply(this);
        Run();
    }

    private static Button Action(string text, Action onClick)
    {
        var button = new Button
        {
            Text = text,
            Font = Palette.UiFont,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(12, 4, 12, 4),
            Margin = new Padding(10, 2, 0, 0),
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>
    /// The splitter can only be positioned once the container has a real height; setting it in the
    /// constructor throws because the container is still its default 150x100 at that point.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        var wanted = _split.Height * 2 / 5;
        if (wanted > _split.Panel1MinSize && wanted < _split.Height - _split.Panel2MinSize)
            _split.SplitterDistance = wanted;
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
        var index = _transform.SelectedIndex;
        if (index < 0 || index >= Choices.Length) return;

        try
        {
            _result = TextTransforms.Apply(Choices[index].Transform, _input.Text);
            _status.ForeColor = Palette.TextDim;
            _status.Text = _input.TextLength >= MaxInputLength
                ? $"Input reached the {MaxInputLength / 1024 / 1024} MiB limit and was truncated."
                : string.Empty;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            // Malformed input is the normal case for a decoder, not a crash: say so and show nothing.
            _result = string.Empty;
            _status.ForeColor = Palette.StatusClientError;
            _status.Text = $"{_transform.Text}: {ex.Message}";
        }

        ShowResult();
    }

    private void ShowResult()
    {
        _output.Text = _viewBytes.Checked ? HexDump(_result) : _result;
        Text = $"TextWizard [{_input.TextLength} => {_result.Length} chars]";
    }

    /// <summary>Offset, hex and printable ASCII, the same shape as the Hex inspector's view.</summary>
    private static string HexDump(string value)
    {
        if (value.Length == 0) return string.Empty;

        var bytes = Encoding.UTF8.GetBytes(value);
        var dump = new StringBuilder(bytes.Length * 4);
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var line = bytes.AsSpan(offset, Math.Min(16, bytes.Length - offset));
            dump.Append(offset.ToString("X8")).Append("  ");
            for (var i = 0; i < 16; i++)
                dump.Append(i < line.Length ? line[i].ToString("X2") : "  ").Append(' ');
            dump.Append(' ');
            foreach (var b in line) dump.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            dump.AppendLine();
        }

        return dump.ToString();
    }

    private void SaveOutput()
    {
        if (_result.Length == 0) return;

        using var dialog = new SaveFileDialog
        {
            Title = "Save TextWizard output",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = "textwizard-output.txt",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dialog.FileName, _result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, $"Piper could not write that file:\r\n\r\n{ex.Message}",
                "Save TextWizard output", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
