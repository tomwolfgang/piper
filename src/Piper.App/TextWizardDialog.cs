using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;
using Piper.Core.Telemetry;
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

    /// <summary>Convenience settings must not create an unusably large form when the file is malformed.</summary>
    private const int MaxRestoredWindowDimension = 16_384;

    private static readonly Size MinimumWindowSize = new(700, 500);

    /// <summary>A token can hold a deeply nested or very wide JSON value; don't let it create an endless UI tree.</summary>
    private const int MaxJwtTreeNodes = 10_000;

    /// <summary>Fiddler-compatible transforms, followed by Piper's JWT inspector.</summary>
    private static readonly (string Label, TextTransform Transform)[] Choices =
    [
        (Strings.TextWizard.ToBase64, TextTransform.ToBase64),
        (Strings.TextWizard.ToBase64Url, TextTransform.ToBase64Url),
        (Strings.TextWizard.FromBase64, TextTransform.FromBase64),
        (Strings.TextWizard.InspectJwt, TextTransform.InspectJwt),
        (Strings.TextWizard.UrlEncode, TextTransform.UrlEncode),
        (Strings.TextWizard.UrlDecode, TextTransform.UrlDecode),
        (Strings.TextWizard.HexEncode, TextTransform.HexEncode),
        (Strings.TextWizard.HexDecode, TextTransform.HexDecode),
        (Strings.TextWizard.ToCSharpByteArray, TextTransform.ToCSharpByteArray),
        (Strings.TextWizard.ToJsString, TextTransform.ToJsString),
        (Strings.TextWizard.FromJsString, TextTransform.FromJsString),
        (Strings.TextWizard.HtmlEncode, TextTransform.HtmlEncode),
        (Strings.TextWizard.HtmlDecode, TextTransform.HtmlDecode),
        (Strings.TextWizard.ToUtf7, TextTransform.ToUtf7),
        (Strings.TextWizard.FromUtf7, TextTransform.FromUtf7),
        (Strings.TextWizard.ToDeflatedSaml, TextTransform.ToDeflatedSaml),
        (Strings.TextWizard.FromDeflatedSaml, TextTransform.FromDeflatedSaml),
        (Strings.TextWizard.ToMd5, TextTransform.Md5),
        (Strings.TextWizard.ToSha1, TextTransform.Sha1),
        (Strings.TextWizard.ToSha256, TextTransform.Sha256),
        (Strings.TextWizard.ToSha384, TextTransform.Sha384),
        (Strings.TextWizard.ToSha512, TextTransform.Sha512),
    ];

    private static TextWizardDialog? _open;

    private readonly SplitContainer _split;
    private readonly TextBox _input;
    private readonly ComboBox _transform;
    private readonly CheckBox _viewBytes;
    private readonly TextBox _output;
    private readonly TreeView _jwtOutput;
    private readonly ToolStripStatusLabel _status;

    private readonly ToolTip _tips = new();

    /// <summary>Drawn icons and the shared tooltip are owned here; a Button does not dispose its Image.</summary>
    private readonly List<Image> _icons = [];

    private string _result = string.Empty;
    private JwtInspection? _jwtInspection;

    /// <summary>
    /// Set while the dialog is choosing a transform for the user, so that a detected or restored choice is
    /// neither saved back over their own preference nor charged an extra transform run.
    /// </summary>
    private bool _selectingForUser;

    private TextWizardDialog()
    {
        Text = Strings.TextWizard.Caption;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = MinimumWindowSize;
        ClientSize = new Size(900, 660);
        if (LoadLastWindowSize() is { } savedSize) Size = savedSize;
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
            Text = Strings.TextWizard.Hint,
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
            AccessibleName = Strings.TextWizard.InputAccessibleName,
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
            AccessibleName = Strings.TextWizard.OutputAccessibleName,
        };

        _jwtOutput = new TreeView
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            HideSelection = false,
            Font = Palette.Mono,
            AccessibleName = Strings.TextWizard.JwtOutputAccessibleName,
            Visible = false,
        };

        _transform = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Palette.UiFont,
            AccessibleName = Strings.TextWizard.TransformAccessibleName,
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
            Text = Strings.TextWizard.ViewBytes,
            Font = Palette.UiFont,
            AutoSize = true,
            AccessibleName = Strings.TextWizard.ViewBytesAccessibleName,
            Margin = new Padding(16, 6, 0, 0),
        };
        _viewBytes.CheckedChanged += (_, _) => ShowResult();

        var saveToFile = Action(Strings.TextWizard.SaveButton, Strings.TextWizard.SaveTooltip, SaveIcon(), SaveOutput);
        // Through SetInput, never straight into _input.Text: MaxLength does not apply to an assignment, so
        // a direct write would skip the 1 MiB bound, and transforms expand their input - repeated chaining
        // of "To C# byte[]" would compound 1 MiB into hundreds.
        var chain = Action(Strings.TextWizard.ToInputButton, Strings.TextWizard.ToInputTooltip, UpArrowIcon(), () => SetInput(_result));
        var close = Action(Strings.TextWizard.CloseButton, Strings.TextWizard.CloseTooltip, null, Close);
        MakeSameSize(saveToFile, chain, close);

        var left = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = Padding.Empty };
        left.Controls.Add(new Label
        {
            Text = Strings.TextWizard.TransformLabel,
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
        _split.Panel2.Controls.Add(_jwtOutput);
        _split.Panel2.Controls.Add(bar);

        // A real status bar rather than a label: it carries the sizing grip, which is the affordance that
        // tells people the window resizes at all.
        _status = new ToolStripStatusLabel
        {
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Palette.TextDim,
            AccessibleName = Strings.TextWizard.StatusAccessibleName,
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
        Analytics.Track(AnalyticsEvents.FeatureUsed, (AnalyticsProperties.Feature, "textwizard"));
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
            ? Strings.TextWizard.Truncated(MaxInputLength / 1024 / 1024)
            : null;
        _selectingForUser = true;
        try
        {
            _input.Text = bounded;
            if (note is null && TextTransformDetector.Detect(bounded) is { } detected && IndexOf(detected) is { } index)
            {
                _transform.SelectedIndex = index;
                note = Strings.TextWizard.Detected(Choices[index].Label);
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
            SaveSettings(settings => settings.LastTransform = Choices[_transform.SelectedIndex].Transform.ToString());

        Run();
    }

    private void Run(bool keepStatus = false)
    {
        if (_selectingForUser) return;

        var index = _transform.SelectedIndex;
        if (index < 0 || index >= Choices.Length) return;

        try
        {
            var transform = Choices[index].Transform;
            _jwtInspection = transform == TextTransform.InspectJwt ? TextTransforms.InspectJwt(_input.Text) : null;
            _result = _jwtInspection is { } inspection
                ? FormatJwtInspection(inspection)
                : TextTransforms.Apply(transform, _input.Text);
            _status.ForeColor = Palette.TextDim;
            // At the limit is not the same as cut short: text of exactly this length was never truncated.
            if (_input.TextLength >= MaxInputLength && !keepStatus)
                _status.Text = Strings.TextWizard.AtLimit(MaxInputLength / 1024 / 1024);
            else if (!keepStatus)
                _status.Text = string.Empty;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidDataException)
        {
            // Malformed input is the normal case for a decoder, not a crash: say so and show nothing.
            _result = string.Empty;
            _status.ForeColor = Palette.StatusClientError;
            _status.Text = Strings.TextWizard.TransformFailed(_transform.Text, ex.Message);
        }

        ShowResult();
    }

    /// <summary>
    /// The text result is JSON even though it is normally presented as a tree. That gives To Input and Save
    /// an ordinary, useful value instead of the tree's display labels.
    /// </summary>
    private static string FormatJwtInspection(JwtInspection inspection) => inspection.ToJson();

    private static string JwtError(JwtValidationError error) => error switch
    {
        JwtValidationError.InvalidHeaderEncoding => Strings.TextWizard.JwtInvalidHeaderEncoding,
        JwtValidationError.InvalidHeaderJson => Strings.TextWizard.JwtInvalidHeaderJson,
        JwtValidationError.HeaderIsNotObject => Strings.TextWizard.JwtHeaderIsNotObject,
        JwtValidationError.MissingAlgorithm => Strings.TextWizard.JwtMissingAlgorithm,
        JwtValidationError.InvalidPayloadEncoding => Strings.TextWizard.JwtInvalidPayloadEncoding,
        JwtValidationError.InvalidPayloadJson => Strings.TextWizard.JwtInvalidPayloadJson,
        JwtValidationError.PayloadIsNotObject => Strings.TextWizard.JwtPayloadIsNotObject,
        JwtValidationError.InvalidSignatureEncoding => Strings.TextWizard.JwtInvalidSignatureEncoding,
        JwtValidationError.MissingSignature => Strings.TextWizard.JwtMissingSignature,
        JwtValidationError.UnexpectedSignature => Strings.TextWizard.JwtUnexpectedSignature,
        _ => throw new ArgumentOutOfRangeException(nameof(error)),
    };

    private void ShowResult()
    {
        var inspectingJwt = _jwtInspection is not null;
        _viewBytes.Visible = !inspectingJwt;
        _jwtOutput.Visible = inspectingJwt;
        _output.Visible = !inspectingJwt;

        if (inspectingJwt)
            ShowJwtInspection(_jwtInspection!);
        else
            _output.Text = _viewBytes.Checked ? HexDump(_result) : _result;

        Text = Strings.TextWizard.CaptionWithCounts(_input.TextLength, _result.Length);
    }

    private void ShowJwtInspection(JwtInspection inspection)
    {
        _jwtOutput.BeginUpdate();
        try
        {
            _jwtOutput.Nodes.Clear();
            if (inspection.Tokens.Count == 0)
            {
                _jwtOutput.Nodes.Add(Strings.TextWizard.JwtNoneFound);
                return;
            }

            var renderedNodes = 0;
            var treeTruncated = false;
            for (var index = 0; index < inspection.Tokens.Count; index++)
            {
                var token = inspection.Tokens[index];
                var parent = inspection.Tokens.Count == 1
                    ? null
                    : AddJwtNode(_jwtOutput.Nodes, Strings.TextWizard.JwtToken(index + 1), ref renderedNodes,
                        ref treeTruncated);
                if (treeTruncated) break;
                AddJwtPart(parent?.Nodes ?? _jwtOutput.Nodes, Strings.TextWizard.JwtHeader,
                    token.Header, token.HeaderError, isHeader: true, ref renderedNodes, ref treeTruncated);
                AddJwtPart(parent?.Nodes ?? _jwtOutput.Nodes, Strings.TextWizard.JwtPayload,
                    token.Payload, token.PayloadError, isHeader: false, ref renderedNodes, ref treeTruncated);
                if (treeTruncated) break;
            }

            if (inspection.IsTruncated)
                _jwtOutput.Nodes.Add(Strings.TextWizard.JwtMoreTokens(100));
            if (treeTruncated)
                _jwtOutput.Nodes.Add(Strings.TextWizard.JwtTreeTruncated);

            _jwtOutput.ExpandAll();
        }
        finally
        {
            _jwtOutput.EndUpdate();
        }
    }

    private static void AddJwtPart(TreeNodeCollection nodes, string name, string? json, JwtValidationError? error,
        bool isHeader, ref int renderedNodes, ref bool treeTruncated)
    {
        var label = error is { } validationError
            ? JwtPartInvalid(isHeader, JwtError(validationError))
            : name;
        if (json is null)
        {
            AddJwtNode(nodes, label, ref renderedNodes, ref treeTruncated);
            return;
        }

        using var document = JsonDocument.Parse(json);
        if (renderedNodes >= MaxJwtTreeNodes)
        {
            treeTruncated = true;
            return;
        }

        nodes.Add(CreateJsonNode(label, document.RootElement, ref renderedNodes, ref treeTruncated));
    }

    private static TreeNode? AddJwtNode(TreeNodeCollection nodes, string text, ref int renderedNodes,
        ref bool treeTruncated)
    {
        if (renderedNodes >= MaxJwtTreeNodes)
        {
            treeTruncated = true;
            return null;
        }

        renderedNodes++;
        return nodes.Add(text);
    }

    private static string JwtPartInvalid(bool isHeader, string reason) =>
        isHeader
            ? Strings.TextWizard.JwtHeaderInvalid(reason)
            : Strings.TextWizard.JwtPayloadInvalid(reason);

    private static TreeNode CreateJsonNode(string name, JsonElement value, ref int renderedNodes, ref bool treeTruncated)
    {
        var node = new TreeNode(FormatJsonValue(name, value));
        renderedNodes++;
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                {
                    if (renderedNodes >= MaxJwtTreeNodes)
                    {
                        treeTruncated = true;
                        break;
                    }
                    node.Nodes.Add(CreateJsonNode(property.Name, property.Value, ref renderedNodes, ref treeTruncated));
                }
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    if (renderedNodes >= MaxJwtTreeNodes)
                    {
                        treeTruncated = true;
                        break;
                    }
                    node.Nodes.Add(CreateJsonNode($"[{index++}]", item, ref renderedNodes, ref treeTruncated));
                }
                break;
        }

        return node;
    }

    private static string FormatJsonValue(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => $"{name}: {{ }}",
        JsonValueKind.Array => $"{name}: [ ]",
        JsonValueKind.String => $"{name}: {JsonSerializer.Serialize(value.GetString())}",
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => $"{name}: {value.GetRawText()}",
        JsonValueKind.Null => $"{name}: null",
        _ => $"{name}: {value.GetRawText()}",
    };

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

    private static Size? LoadLastWindowSize()
    {
        var settings = TextWizardSettingsStore.Load();
        if (settings?.LastWindowWidth is not { } width || settings.LastWindowHeight is not { } height) return null;

        var size = new Size(width, height);
        return IsValidWindowSize(size) ? size : null;
    }

    private void SaveWindowSize()
    {
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        if (!IsValidWindowSize(bounds.Size)) return;

        SaveSettings(settings =>
        {
            settings.LastWindowWidth = bounds.Width;
            settings.LastWindowHeight = bounds.Height;
        });
    }

    private static bool IsValidWindowSize(Size size) =>
        size.Width >= MinimumWindowSize.Width && size.Height >= MinimumWindowSize.Height
        && size.Width <= MaxRestoredWindowDimension && size.Height <= MaxRestoredWindowDimension;

    private static void SaveSettings(Action<TextWizardSettings> update)
    {
        var settings = TextWizardSettingsStore.Load() ?? new TextWizardSettings();
        update(settings);
        TextWizardSettingsStore.Save(settings);
    }

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
            dump.AppendLine(Strings.TextWizard.MoreBytesNotShown(all.Length - shown));

        return dump.ToString();
    }

    private void SaveOutput()
    {
        if (_result.Length == 0) return;

        using var dialog = new SaveFileDialog
        {
            Title = Strings.TextWizard.SaveCaption,
            Filter = Strings.TextWizard.SaveFilter,
            FileName = Strings.TextWizard.SaveFileName,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dialog.FileName, _result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Strings.TextWizard.SaveFailed(ex.Message),
                Strings.TextWizard.SaveCaption, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SaveWindowSize();
        base.OnFormClosed(e);
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
