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

        var hint = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Padding = new Padding(10, 6, 0, 0),
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

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            Padding = new Padding(10, 4, 0, 0),
            ForeColor = Palette.TextDim,
            AccessibleName = "TextWizard status",
        };

        _transform = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Palette.UiFont,
            Width = 190,
            AccessibleName = "TextWizard transform",
        };
        foreach (var choice in Choices) _transform.Items.Add(choice.Label);
        _transform.SelectedIndex = 0;
        _transform.SelectedIndexChanged += (_, _) => Run();

        _viewBytes = new CheckBox
        {
            Text = "View bytes",
            Font = Palette.UiFont,
            AutoSize = true,
            AccessibleName = "TextWizard view bytes",
        };
        _viewBytes.CheckedChanged += (_, _) => ShowResult();

        var saveToFile = new Button { Text = "Save Output...", Size = new Size(120, 28), Font = Palette.UiFont };
        saveToFile.Click += (_, _) => SaveOutput();
        var chain = new Button { Text = "Send output to input", Size = new Size(150, 28), Font = Palette.UiFont };
        chain.Click += (_, _) => _input.Text = _result;

        // The strip that sits between the two editors, matching Fiddler's arrangement.
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 44,
            Padding = new Padding(8, 8, 8, 4),
            WrapContents = false,
            AutoScroll = false,
        };
        bar.Controls.Add(new Label
        {
            Text = "Transform:",
            Font = Palette.UiFont,
            AutoSize = true,
            Padding = new Padding(0, 6, 4, 0),
        });
        bar.Controls.Add(_transform);
        bar.Controls.Add(new Panel { Width = 12, Height = 1 });
        bar.Controls.Add(_viewBytes);
        bar.Controls.Add(new Panel { Width = 12, Height = 1 });
        bar.Controls.Add(saveToFile);
        bar.Controls.Add(chain);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        split.Panel1.Controls.Add(_input);
        split.Panel2.Controls.Add(_output);
        split.Panel2.Controls.Add(bar);

        var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel, Size = new Size(100, 30) };
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            Padding = new Padding(10, 6, 10, 6),
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actions.Controls.Add(close);

        Controls.Add(split);
        Controls.Add(hint);
        Controls.Add(_status);
        Controls.Add(actions);
        CancelButton = close;
        Palette.Apply(this);
        Run();
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
