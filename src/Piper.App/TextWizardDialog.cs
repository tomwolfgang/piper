using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;
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

    /// <summary>How much of the output the byte view will render. See <see cref="HexDump"/>.</summary>
    private const int MaxDumpBytes = 64 * 1024;

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
    private readonly ToolStripStatusLabel _status;

    private readonly ToolTip _tips = new();

    /// <summary>Drawn icons and the shared tooltip are owned here; a Button does not dispose its Image.</summary>
    private readonly List<Image> _icons = [];

    private string _result = string.Empty;

    /// <summary>
    /// Set while the dialog is choosing a transform for the user, so that a detected or restored choice is
    /// neither saved back over their own preference nor charged an extra transform run.
    /// </summary>
    private bool _selectingForUser;

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
        _transform.SelectedIndex = IndexOf(LoadLastTransform()) is { } restored ? restored : 0;
        _transform.SelectedIndexChanged += OnTransformChanged;

        _viewBytes = new CheckBox
        {
            Text = "View bytes",
            Font = Palette.UiFont,
            AutoSize = true,
            AccessibleName = "TextWizard view bytes",
            Margin = new Padding(16, 6, 0, 0),
        };
        _viewBytes.CheckedChanged += (_, _) => ShowResult();

        var saveToFile = Action("Save", "Save the output to a file", SaveIcon(), SaveOutput);
        // Through SetInput, never straight into _input.Text: MaxLength does not apply to an assignment, so
        // a direct write would skip the 1 MiB bound, and transforms expand their input - repeated chaining
        // of "To C# byte[]" would compound 1 MiB into hundreds.
        var chain = Action("To Input", "Send output to input", UpArrowIcon(), () => SetInput(_result));
        var close = Action("Close", "Close the TextWizard", null, Close);
        MakeSameSize(saveToFile, chain, close);

        var left = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        left.Controls.Add(new Label
        {
            Text = "Transform:",
            Font = Palette.UiFont,
            AutoSize = true,
            Margin = new Padding(0, 7, 6, 0),
        });
        left.Controls.Add(_transform);
        left.Controls.Add(_viewBytes);

        var right = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty, Anchor = AnchorStyles.Right };
        right.Controls.AddRange([saveToFile, chain, close]);

        // Two columns so the actions sit against the window's right edge and stay there as it is resized;
        // a FlowLayoutPanel alone cannot right-align part of its contents.
        var bar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(8, 6, 8, 6),
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        bar.Controls.Add(left, 0, 0);
        bar.Controls.Add(right, 1, 0);

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        _split.Panel1.Controls.Add(_input);
        _split.Panel2.Controls.Add(_output);
        _split.Panel2.Controls.Add(bar);

        // A real status bar rather than a label: it carries the sizing grip, which is the affordance that
        // tells people the window resizes at all.
        _status = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Palette.TextDim,
            AccessibleName = "TextWizard status",
        };
        var statusBar = new StatusStrip { Font = Palette.UiFont, SizingGrip = true };
        statusBar.Items.Add(_status);

        Controls.Add(_split);
        Controls.Add(hint);
        Controls.Add(statusBar);
        CancelButton = close;
        Palette.Apply(this);
        // After Apply, which paints every Label transparent: the hint reads as a band above the input
        // the way Fiddler's does, so it needs a surface of its own back.
        hint.BackColor = Palette.SurfaceAlt;
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
        var bounded = text.Length > MaxInputLength ? text[..MaxInputLength] : text;

        // Both the text and the transform are being set for the user, so the pair is applied as one change:
        // one transform run at the end rather than one per assignment, and no write over their saved choice.
        var note = bounded.Length < text.Length
            ? $"Input was truncated to {MaxInputLength / 1024 / 1024} MiB."
            : null;
        _selectingForUser = true;
        try
        {
            _input.Text = bounded;
            if (note is null && TextTransformDetector.Detect(bounded) is { } detected && IndexOf(detected) is { } index)
            {
                _transform.SelectedIndex = index;
                note = $"Detected {Choices[index].Label}.";
            }
        }
        finally
        {
            _selectingForUser = false;
        }

        _input.Select(0, 0);
        _input.Focus();

        // Only hold a status that was actually written here, or a stale error from the previous value
        // would sit beside a correct result.
        if (note is not null) _status.Text = note;
        Run(keepStatus: note is not null);
    }

    private void OnTransformChanged(object? sender, EventArgs e)
    {
        if (_selectingForUser) return;

        // Only a choice the user made themselves is worth remembering; a detected one belongs to the text
        // they happened to open, not to how they like to work.
        if (_transform.SelectedIndex >= 0 && _transform.SelectedIndex < Choices.Length)
            TextWizardSettingsStore.Save(new TextWizardSettings
            {
                LastTransform = Choices[_transform.SelectedIndex].Transform.ToString(),
            });

        Run();
    }

    private void Run(bool keepStatus = false)
    {
        if (_selectingForUser) return;

        var index = _transform.SelectedIndex;
        if (index < 0 || index >= Choices.Length) return;

        try
        {
            _result = TextTransforms.Apply(Choices[index].Transform, _input.Text);
            _status.ForeColor = Palette.TextDim;
            // At the limit is not the same as cut short: text of exactly this length was never truncated.
            if (_input.TextLength >= MaxInputLength && !keepStatus)
                _status.Text = $"Input is at the {MaxInputLength / 1024 / 1024} MiB limit.";
            else if (!keepStatus)
                _status.Text = string.Empty;
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

    private static int? IndexOf(TextTransform? transform)
    {
        if (transform is not { } wanted) return null;
        for (var i = 0; i < Choices.Length; i++)
            if (Choices[i].Transform == wanted) return i;
        return null;
    }

    private static TextTransform? LoadLastTransform() =>
        TextWizardSettingsStore.Load()?.LastTransform is { } name
        && Enum.TryParse<TextTransform>(name, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Offset, hex and printable ASCII, the same shape as the Hex inspector's view. Capped: the dump is
    /// roughly five times the size of what it describes, and a transform can already have multiplied a
    /// bounded input several times over, so an uncapped dump turns 1 MiB of input into a ten-megabyte
    /// string in a TextBox. Nobody reads past the first few hundred lines either - the Hex inspector is
    /// the tool for a whole binary payload.
    /// </summary>
    private static string HexDump(string value)
    {
        if (value.Length == 0) return string.Empty;

        var all = Encoding.UTF8.GetBytes(value);
        var shown = Math.Min(all.Length, MaxDumpBytes);
        var bytes = all.AsSpan(0, shown);
        var dump = new StringBuilder(shown * 5 + 80);
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var line = bytes.Slice(offset, Math.Min(16, bytes.Length - offset));
            dump.Append(offset.ToString("X8")).Append("  ");
            for (var i = 0; i < 16; i++)
                dump.Append(i < line.Length ? line[i].ToString("X2") : "  ").Append(' ');
            dump.Append(' ');
            foreach (var b in line) dump.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            dump.AppendLine();
        }

        if (shown < all.Length)
            dump.Append("... ").Append(all.Length - shown).AppendLine(" more bytes not shown.");

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

    private Button Action(string text, string tooltip, Image? icon, Action onClick)
    {
        if (icon is not null) _icons.Add(icon);
        var button = new Button
        {
            Text = text,
            Font = Palette.UiFont,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(4, 0, 4, 0),
            Margin = new Padding(6, 2, 0, 0),
            Image = icon,
            TextImageRelation = TextImageRelation.ImageBeforeText,
            ImageAlign = ContentAlignment.MiddleLeft,
            TextAlign = ContentAlignment.MiddleCenter,
            AccessibleName = tooltip,
        };
        _tips.SetToolTip(button, tooltip);
        button.Click += (_, _) => onClick();
        return button;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tips.Dispose();
            foreach (var icon in _icons) icon.Dispose();
            _icons.Clear();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Gives every action the width of the widest one. Measured rather than hard-coded, so the row stays
    /// even at any display scale.
    /// </summary>
    private static void MakeSameSize(params Button[] buttons)
    {
        var width = buttons.Max(b => b.PreferredSize.Width);
        var height = buttons.Max(b => b.PreferredSize.Height);
        foreach (var button in buttons)
        {
            button.AutoSize = false;
            button.Size = new Size(width, height);
        }
    }

    /// <summary>An up arrow, drawn rather than shipped as a resource, like the status-bar icons.</summary>
    private static Bitmap UpArrowIcon()
    {
        var image = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(image);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Palette.Text);
        graphics.FillPolygon(brush, [new Point(8, 3), new Point(13, 9), new Point(3, 9)]);
        graphics.FillRectangle(brush, 6, 9, 4, 4);
        return image;
    }

    /// <summary>A disk, for saving the output.</summary>
    private static Bitmap SaveIcon()
    {
        var image = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(image);
        using var pen = new Pen(Palette.Text);
        using var brush = new SolidBrush(Palette.Text);
        graphics.DrawRectangle(pen, 3, 3, 10, 10);
        graphics.FillRectangle(brush, 6, 3, 5, 4);
        graphics.FillRectangle(brush, 5, 9, 7, 4);
        return image;
    }
}
