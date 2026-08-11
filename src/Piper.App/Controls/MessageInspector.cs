using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Be.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Piper.App.Theme;
using Piper.Core.Http;
using ShimmyMySherbet.WinForms.ZoomableImgBox;
using SixLabors.ImageSharp;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace Piper.App.Controls;

/// <summary>Tabbed view of one HTTP message: headers, raw bytes, decoded body and a hex dump.</summary>
public sealed class MessageInspector : UserControl
{
    private const int MaxRenderBytes = 4 * 1024 * 1024;

    private readonly TabControl _tabs;
    private readonly ListView _headersView;
    private readonly TextBox _headersSearch;
    private readonly Label _headersMatchCount;
    private readonly TextBox _rawView;
    private readonly TextBox _bodyView;
    private readonly HexBox _hexView;
    private readonly TextBox _hexSearch;
    private readonly CheckBox _hexSearchAsHex;
    private readonly Label _hexSearchStatus;
    private readonly TextBox _jsonSearch;
    private readonly Label _jsonMatchCount;
    private readonly TreeView _jsonTree;
    private readonly TextBox _summary;
    private readonly Label _size;
    private readonly Label _host;
    private readonly bool _showImageViewer;
    private readonly ZoomableImageBox? _imageView;
    private readonly Label? _imageStatus;
    private readonly WebView2? _videoView;
    private readonly Label? _videoStatus;

    private HttpMessage? _message;
    private HttpMessage? _renderedBody;
    private HttpMessage? _renderedRaw;
    private HttpMessage? _renderedHex;
    private HttpMessage? _renderedJson;
    private HttpMessage? _renderedImage;
    private HttpMessage? _renderedVideo;
    private string? _videoFilePath;
    private readonly List<TreeNode> _jsonMatches = [];
    private readonly List<(string Name, string Value)> _headers = [];
    private int _jsonMatchIndex = -1;

    public MessageInspector(string caption, bool showImageViewer = false)
    {
        _showImageViewer = showImageViewer;
        _summary = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = caption,
            Font = Palette.Mono,
            ReadOnly = true,
            ShortcutsEnabled = true,
            BorderStyle = BorderStyle.None,
        };

        _host = new Label
        {
            Dock = DockStyle.Right,
            Width = 170,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Palette.TextDim,
            Padding = new Padding(0, 0, 6, 0),
        };

        _size = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            Visible = false,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Palette.TextDim,
            Padding = new Padding(6, 0, 0, 0),
        };

        var summaryLine = new Panel { Dock = DockStyle.Top, Height = 28, Padding = new Padding(6, 5, 0, 3) };
        summaryLine.Controls.Add(_summary);
        summaryLine.Controls.Add(_host);

        var summaryBar = new Panel { Dock = DockStyle.Top, Height = 52, Padding = new Padding(0, 0, 6, 0) };
        summaryBar.Controls.Add(_size);
        summaryBar.Controls.Add(summaryLine);

        _headersView = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            GridLines = false,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            Font = Palette.Mono,
        };
        _headersView.Columns.Add("Name", 210);
        _headersView.Columns.Add("Value", 460);
        DarkListView.Attach(_headersView);
        DarkListView.AddFillerColumn(_headersView);
        _headersView.MouseDown += OnHeadersMouseDown;
        _headersView.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.C) return;
            var lines = _headersView.SelectedItems.Cast<ListViewItem>()
                .Select(i => $"{i.Text}: {i.SubItems[1].Text}");
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            e.Handled = true;
        };
        _headersView.ContextMenuStrip = BuildHeadersMenu();

        _headersSearch = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Search headers",
            Font = Palette.Mono,
        };
        _headersSearch.TextChanged += (_, _) => RenderHeaders();

        _headersMatchCount = new Label
        {
            Dock = DockStyle.Right,
            Width = 90,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Palette.TextDim,
            Padding = new Padding(0, 3, 5, 0),
        };
        var headersSearchRow = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(2) };
        headersSearchRow.Controls.Add(_headersSearch);
        headersSearchRow.Controls.Add(_headersMatchCount);

        var headersPanel = new Panel { Dock = DockStyle.Fill };
        headersPanel.Controls.Add(_headersView);
        headersPanel.Controls.Add(headersSearchRow);

        _rawView = MakeTextView();
        _bodyView = MakeTextView();
        _hexView = new HexBox
        {
            Dock = DockStyle.Fill,
            Font = Palette.Mono,
            ReadOnly = true,
            BytesPerLine = 16,
            UseFixedBytesPerLine = true,
            GroupSize = 8,
            GroupSeparatorVisible = true,
            LineInfoVisible = true,
            ColumnInfoVisible = true,
            StringViewVisible = true,
            VScrollBarVisible = true,
            BorderStyle = BorderStyle.None,
            InfoForeColor = Palette.TextDim,
            SelectionBackColor = Palette.Selection,
            SelectionForeColor = Palette.Text,
        };
        _hexView.ContextMenuStrip = BuildHexMenu();

        _hexSearch = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Find text or bytes",
            Font = Palette.Mono,
        };
        _hexSearch.KeyDown += OnHexSearchKeyDown;
        _hexSearchAsHex = new CheckBox
        {
            Dock = DockStyle.Right,
            Width = 52,
            Text = "Hex",
            TextAlign = ContentAlignment.MiddleCenter,
        };
        _hexSearchStatus = new Label
        {
            Dock = DockStyle.Right,
            Width = 110,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Palette.TextDim,
            Padding = new Padding(0, 3, 5, 0),
        };
        var previousHexMatch = new Button { Dock = DockStyle.Right, Width = 30, Text = "\u25c0", TabStop = false };
        previousHexMatch.Click += (_, _) => FindHex(-1);
        var nextHexMatch = new Button { Dock = DockStyle.Right, Width = 30, Text = "\u25b6", TabStop = false };
        nextHexMatch.Click += (_, _) => FindHex(1);
        var hexSearchRow = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(2) };
        hexSearchRow.Controls.Add(_hexSearch);
        hexSearchRow.Controls.Add(_hexSearchAsHex);
        hexSearchRow.Controls.Add(_hexSearchStatus);
        hexSearchRow.Controls.Add(nextHexMatch);
        hexSearchRow.Controls.Add(previousHexMatch);
        var hexPanel = new Panel { Dock = DockStyle.Fill };
        hexPanel.Controls.Add(_hexView);
        hexPanel.Controls.Add(hexSearchRow);

        _jsonSearch = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Search JSON",
            Font = Palette.Mono,
        };
        _jsonSearch.TextChanged += (_, _) => FindJsonMatches();
        _jsonSearch.KeyDown += OnJsonSearchKeyDown;

        _jsonMatchCount = new Label
        {
            Dock = DockStyle.Right,
            Width = 82,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Palette.TextDim,
            Padding = new Padding(0, 3, 5, 0),
        };

        var previousMatch = new Button { Dock = DockStyle.Right, Width = 30, Text = "\u25c0", TabStop = false };
        previousMatch.Click += (_, _) => SelectJsonMatch(-1);
        var nextMatch = new Button { Dock = DockStyle.Right, Width = 30, Text = "\u25b6", TabStop = false };
        nextMatch.Click += (_, _) => SelectJsonMatch(1);

        var jsonSearchRow = new Panel { Dock = DockStyle.Bottom, Height = 28, Padding = new Padding(2) };
        jsonSearchRow.Controls.Add(_jsonSearch);
        jsonSearchRow.Controls.Add(_jsonMatchCount);
        jsonSearchRow.Controls.Add(nextMatch);
        jsonSearchRow.Controls.Add(previousMatch);

        _jsonTree = new TreeView
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            HideSelection = false,
            Font = Palette.Mono,
        };
        _jsonTree.NodeMouseClick += OnJsonNodeMouseClick;
        _jsonTree.ContextMenuStrip = BuildJsonMenu();

        var jsonPanel = new Panel { Dock = DockStyle.Fill };
        jsonPanel.Controls.Add(_jsonTree);
        jsonPanel.Controls.Add(jsonSearchRow);

        if (_showImageViewer)
        {
            _imageStatus = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                ForeColor = Palette.TextDim,
                Padding = new Padding(6, 4, 0, 0),
            };
            _imageView = new ZoomableImageBox
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Surface,
                DragPanning = true,
                Zoom = 1,
            };
            _imageView.ContextMenuStrip = BuildImageMenu();

            _videoStatus = new Label
            {
                Dock = DockStyle.Top,
                Height = 42,
                ForeColor = Palette.TextDim,
                Padding = new Padding(6, 4, 0, 0),
            };
            _videoView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Palette.Surface };
        }

        _tabs = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        _tabs.TabPages.Add(NewPage("Headers", headersPanel));
        _tabs.TabPages.Add(NewPage("Body", _bodyView));
        _tabs.TabPages.Add(NewPage("Raw", _rawView));
        _tabs.TabPages.Add(NewPage("Hex", hexPanel));
        _tabs.TabPages.Add(NewPage("JSON", jsonPanel));
        if (_imageView is not null && _imageStatus is not null)
        {
            var imagePanel = new Panel { Dock = DockStyle.Fill };
            imagePanel.Controls.Add(_imageView);
            imagePanel.Controls.Add(_imageStatus);
            _tabs.TabPages.Add(NewPage("Image", imagePanel));
        }
        if (_videoView is not null && _videoStatus is not null)
        {
            var videoPanel = new Panel { Dock = DockStyle.Fill };
            videoPanel.Controls.Add(_videoView);
            videoPanel.Controls.Add(_videoStatus);
            _tabs.TabPages.Add(NewPage("Video", videoPanel));
        }
        _tabs.SelectedIndexChanged += (_, _) => RenderSelectedTab();

        Controls.Add(_tabs);
        Controls.Add(summaryBar);
    }

    private static TextBox MakeTextView() => new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Both,
        WordWrap = false,
        Font = Palette.Mono,
        BorderStyle = BorderStyle.None,
    };

    private static TabPage NewPage(string title, Control content)
    {
        var page = new TabPage(title);
        page.Controls.Add(content);
        return page;
    }

    private ContextMenuStrip BuildHeadersMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var copyHeader = new ToolStripMenuItem("Copy entire header", null, (_, _) => CopySelectedHeader(valueOnly: false));
        var copyValue = new ToolStripMenuItem("Copy value only", null, (_, _) => CopySelectedHeader(valueOnly: true));
        menu.Items.AddRange([copyHeader, copyValue]);
        menu.Opening += (_, _) =>
        {
            var enabled = SelectedHeader is not null;
            copyHeader.Enabled = enabled;
            copyValue.Enabled = enabled;
        };
        return menu;
    }

    private ContextMenuStrip BuildJsonMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var copyPair = new ToolStripMenuItem("Copy key-value", null, (_, _) => CopySelectedJson(valueOnly: false));
        var copyValue = new ToolStripMenuItem("Copy value only", null, (_, _) => CopySelectedJson(valueOnly: true));
        menu.Items.AddRange([copyPair, copyValue]);
        menu.Opening += (_, _) =>
        {
            var enabled = _jsonTree.SelectedNode?.Tag is JsonNodeValue;
            copyPair.Enabled = enabled;
            copyValue.Enabled = enabled;
        };
        return menu;
    }

    private ContextMenuStrip BuildHexMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var copy = new ToolStripMenuItem("Copy selection", null, (_, _) => _hexView.Copy());
        var copyHex = new ToolStripMenuItem("Copy selection as hex", null, (_, _) => _hexView.CopyHex());
        var selectAll = new ToolStripMenuItem("Select all", null, (_, _) => _hexView.SelectAll());
        var find = new ToolStripMenuItem("Find", null, (_, _) => _hexSearch.Focus());
        menu.Items.AddRange([copy, copyHex, new ToolStripSeparator(), selectAll, new ToolStripSeparator(), find]);
        menu.Opening += (_, _) =>
        {
            var canCopy = _hexView.CanCopy();
            copy.Enabled = canCopy;
            copyHex.Enabled = canCopy;
            selectAll.Enabled = _hexView.CanSelectAll();
        };
        return menu;
    }

    private ContextMenuStrip BuildImageMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        menu.Items.Add("Zoom in", null, (_, _) => ChangeImageZoom(1.25f));
        menu.Items.Add("Zoom out", null, (_, _) => ChangeImageZoom(0.8f));
        menu.Items.Add("Actual size", null, (_, _) => ResetImageView());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Save as PNG...", null, (_, _) => SaveCurrentImage("png"));
        menu.Items.Add("Save as JPEG...", null, (_, _) => SaveCurrentImage("jpg"));
        menu.Items.Add("Save as WebP...", null, (_, _) => SaveCurrentImage("webp"));
        menu.Items.Add("Save image as...", null, (_, _) => SaveCurrentImage(null));
        return menu;
    }

    private void ChangeImageZoom(float multiplier)
    {
        if (_imageView is null || _imageView.Image is null) return;
        _imageView.Zoom = Math.Clamp(_imageView.Zoom * multiplier, 0.05f, 32f);
    }

    private void ResetImageView()
    {
        if (_imageView is null || _imageView.Image is null) return;
        _imageView.Zoom = 1;
        _imageView.PanX = 0;
        _imageView.PanY = 0;
    }

    private void SaveCurrentImage(string? requestedFormat)
    {
        if (_message is null || _imageView?.Image is null) return;

        using var dialog = new SaveFileDialog
        {
            Title = "Save image",
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|WebP image (*.webp)|*.webp",
            FilterIndex = requestedFormat switch
            {
                "jpg" => 2,
                "webp" => 3,
                _ => 1,
            },
            FileName = $"response.{requestedFormat ?? "png"}",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        var format = Path.GetExtension(dialog.FileName).TrimStart('.').ToLowerInvariant();
        if (format is not ("png" or "jpg" or "jpeg" or "webp")) format = requestedFormat ?? "png";

        try
        {
            using var image = ImageSharpImage.Load(_message.DecodedBody);
            switch (format)
            {
                case "jpg" or "jpeg": image.SaveAsJpeg(dialog.FileName); break;
                case "webp": image.SaveAsWebp(dialog.FileName); break;
                default: image.SaveAsPng(dialog.FileName); break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the image: {ex.Message}",
                "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private (string Name, string Value)? SelectedHeader => _headersView.SelectedItems.Count == 1
        ? (_headersView.SelectedItems[0].Text, _headersView.SelectedItems[0].SubItems[1].Text)
        : null;

    private void OnHeadersMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || _headersView.GetItemAt(e.X, e.Y) is not { } item) return;
        _headersView.SelectedIndices.Clear();
        item.Selected = true;
        item.Focused = true;
    }

    private void CopySelectedHeader(bool valueOnly)
    {
        if (SelectedHeader is not { } header) return;
        Clipboard.SetText(valueOnly ? header.Value : $"{header.Name}: {header.Value}");
    }

    private void RenderHeaders()
    {
        var query = _headersSearch.Text.Trim();
        var visible = query.Length == 0
            ? _headers
            : _headers.Where(h => h.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || h.Value.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        _headersView.BeginUpdate();
        try
        {
            _headersView.Items.Clear();
            foreach (var (name, value) in visible)
                _headersView.Items.Add(new ListViewItem([name, value]));
        }
        finally
        {
            _headersView.EndUpdate();
        }

        _headersMatchCount.Text = query.Length == 0 ? string.Empty : $"{visible.Count}/{_headers.Count}";
    }

    private void OnJsonNodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button == MouseButtons.Right) _jsonTree.SelectedNode = e.Node;
    }

    private void CopySelectedJson(bool valueOnly)
    {
        if (_jsonTree.SelectedNode?.Tag is not JsonNodeValue value) return;
        Clipboard.SetText(valueOnly ? value.RawValue : _jsonTree.SelectedNode.Text);
    }

    public void SetMessage(HttpMessage? message, string summary, string host = "")
    {
        // Session-list refreshes deliberately preserve their selection, but WinForms still
        // raises SelectedIndexChanged while doing so. Re-rendering a multi-MB body on every
        // refresh both wastes time and visibly resets the editor's paint/scroll state.
        if (ReferenceEquals(_message, message))
        {
            _summary.Text = summary;
            _host.Text = host;
            UpdateSummaryMetadata(message);
            return;
        }

        _message = message;
        _summary.Text = summary;
        _host.Text = host;
        UpdateSummaryMetadata(message);

        _headers.Clear();

        if (message is null)
        {
            RenderHeaders();
            ClearDeferredViews();
            return;
        }

        foreach (var header in message.Headers)
            _headers.Add((header.Name, header.Value));
        RenderHeaders();

        // Bodies can be large and decoding/formatting them used to happen four times for each
        // selection, whether or not their tabs were ever viewed. Clear stale content now and
        // render only the tab the user opens.
        ClearDeferredViews();
        RenderSelectedTab();
    }

    private void ClearDeferredViews()
    {
        _rawView.Clear();
        _bodyView.Clear();
        _hexView.ByteProvider = new DynamicByteProvider(Array.Empty<byte>());
        _hexSearchStatus.Text = string.Empty;
        if (_imageView?.Image is { } image) image.Dispose();
        if (_imageView is not null) _imageView.Image = null;
        if (_imageStatus is not null) _imageStatus.Text = string.Empty;
        ClearVideo();
        _jsonTree.Nodes.Clear();
        _jsonMatchCount.Text = string.Empty;
        _renderedBody = _renderedRaw = _renderedHex = _renderedJson = _renderedImage = _renderedVideo = null;
        _jsonMatches.Clear();
        _jsonMatchIndex = -1;
    }

    private void RenderSelectedTab()
    {
        if (_message is null) return;

        switch (_tabs.SelectedIndex)
        {
            case 1 when !ReferenceEquals(_renderedBody, _message):
                _bodyView.Text = RenderBody(_message);
                _renderedBody = _message;
                break;
            case 2 when !ReferenceEquals(_renderedRaw, _message):
                _rawView.Text = RenderRaw(_message);
                _renderedRaw = _message;
                break;
            case 3 when !ReferenceEquals(_renderedHex, _message):
                _hexView.ByteProvider = new DynamicByteProvider(_message.Body);
                _renderedHex = _message;
                break;
            case 4 when !ReferenceEquals(_renderedJson, _message):
                RenderJson(_message);
                _renderedJson = _message;
                break;
            case 5 when _showImageViewer && !ReferenceEquals(_renderedImage, _message):
                RenderImage(_message);
                _renderedImage = _message;
                break;
            case 6 when _showImageViewer && !ReferenceEquals(_renderedVideo, _message):
                RenderVideo(_message);
                _renderedVideo = _message;
                break;
        }
    }

    private void UpdateSummaryMetadata(HttpMessage? message)
    {
        _size.Visible = _showImageViewer && message is not null;
        if (!_size.Visible || message is null)
        {
            _size.Text = string.Empty;
            return;
        }

        long contentSize;
        try { contentSize = message.DecodedBody.LongLength; }
        catch { contentSize = message.Body.LongLength; }
        _size.Text = $"Download {FormatSize(message.Body.LongLength)}  ·  decoded content {FormatSize(contentSize)}";
    }

    private static string FormatSize(long size) => size switch
    {
        < 1024 => $"{size} B",
        < 1024 * 1024 => $"{size / 1024.0:N1} KB",
        _ => $"{size / (1024.0 * 1024):N2} MB",
    };

    private void RenderImage(HttpMessage message)
    {
        if (_imageView is null || _imageStatus is null) return;

        if (message.ContentType is not { } contentType
            || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            _imageStatus.Text = "Response does not have an image content type.";
            return;
        }

        try
        {
            using var decoded = ImageSharpImage.Load(message.DecodedBody);
            using var png = new MemoryStream();
            decoded.SaveAsPng(png);
            png.Position = 0;
            using var source = System.Drawing.Image.FromStream(png);
            if (_imageView.Image is { } oldImage) oldImage.Dispose();
            _imageView.Image = new System.Drawing.Bitmap(source);
            _imageStatus.Text = $"{_imageView.Image.Width:N0} x {_imageView.Image.Height:N0} px\r\n{message.ContentType}";
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or NotSupportedException or InvalidImageContentException)
        {
            _imageStatus.Text = $"Could not display image: {ex.Message}";
        }
    }

    private async void RenderVideo(HttpMessage message)
    {
        if (_videoView is null || _videoStatus is null) return;

        if (message.ContentType is not { } contentType
            || !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            _videoStatus.Text = "Response does not have a video content type.";
            return;
        }

        ClearVideo();
        var extension = VideoExtensionFor(contentType);
        var folder = Path.Combine(Path.GetTempPath(), "Piper", "video-inspector");
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}{extension}");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, message.DecodedBody);
            _videoFilePath = path;
            _videoStatus.Text = $"{contentType}\r\nLoading the embedded media player...";

            await _videoView.EnsureCoreWebView2Async();
            if (!ReferenceEquals(_message, message) || _videoFilePath != path) return;

            _videoView.Source = new Uri(path);
            _videoStatus.Text = contentType;
        }
        catch (Exception ex)
        {
            if (_videoFilePath == path) _videoFilePath = null;
            TryDeleteFile(path);
            _videoStatus.Text = $"Could not load the video player: {ex.Message}";
        }
    }

    private void ClearVideo()
    {
        if (_videoView?.CoreWebView2 is not null) _videoView.CoreWebView2.Navigate("about:blank");
        if (_videoFilePath is { } path) TryDeleteFile(path);
        _videoFilePath = null;
        if (_videoStatus is not null) _videoStatus.Text = string.Empty;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string VideoExtensionFor(string contentType) => contentType.Split(';')[0].Trim().ToLowerInvariant() switch
    {
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        "video/ogg" => ".ogv",
        "video/quicktime" => ".mov",
        "video/x-msvideo" => ".avi",
        _ => ".video",
    };

    private static string RenderRaw(HttpMessage message)
    {
        var sb = new StringBuilder();
        sb.Append(message.HeadAsText());

        if (message.Body.Length == 0) return sb.ToString();

        if (ContentCodec.LooksTextual(message.ContentType, message.DecodedBody))
        {
            var text = message.BodyAsText();
            sb.Append(text.Length > MaxRenderBytes ? text[..MaxRenderBytes] + "\r\n\r\n[truncated]" : text);
        }
        else
        {
            sb.Append($"[{message.Body.Length:N0} bytes of binary content - see the Hex tab]");
        }

        return sb.ToString();
    }

    private static string RenderBody(HttpMessage message)
    {
        if (message.Body.Length == 0) return "(no body)";

        var decoded = message.DecodedBody;
        if (!ContentCodec.LooksTextual(message.ContentType, decoded))
            return $"[{message.Body.Length:N0} bytes of {message.ContentType ?? "binary"} content]\r\n\r\nSee the Hex tab.";

        string text;
        try { text = message.BodyAsText(); }
        catch (Exception ex) { return $"[could not decode body: {ex.Message}]"; }

        var contentType = message.ContentType ?? string.Empty;
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && TryPrettyJson(text, out var pretty))
            text = pretty;

        return text.Length > MaxRenderBytes ? text[..MaxRenderBytes] + "\r\n\r\n[truncated]" : text;
    }

    private static bool TryPrettyJson(string text, out string pretty)
    {
        pretty = text;
        try
        {
            using var document = JsonDocument.Parse(text);
            pretty = JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
            return true;
        }
        catch (JsonException)
        {
            return false; // not valid JSON despite the content type - show it verbatim
        }
    }

    private void RenderJson(HttpMessage message)
    {
        _jsonTree.BeginUpdate();
        try
        {
            _jsonTree.Nodes.Clear();

            if (message.Body.Length == 0)
            {
                _jsonTree.Nodes.Add("(no body)");
                return;
            }

            if (!ContentCodec.LooksTextual(message.ContentType, message.DecodedBody))
            {
                _jsonTree.Nodes.Add($"[{message.Body.Length:N0} bytes of {message.ContentType ?? "binary"} content]");
                return;
            }

            try
            {
                using var document = JsonDocument.Parse(message.BodyAsText());
                var root = CreateJsonNode("root", document.RootElement);
                _jsonTree.Nodes.Add(root);
                // Keep the top-level shape visible, while avoiding an overwhelming expansion
                // of every nested object and array in large responses.
                root.Expand();
            }
            catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
            {
                _jsonTree.Nodes.Add($"[not valid JSON: {ex.Message}]");
            }
        }
        finally
        {
            _jsonTree.EndUpdate();
        }

        FindJsonMatches();
    }

    private static TreeNode CreateJsonNode(string name, JsonElement value)
    {
        var node = new TreeNode(FormatJsonValue(name, value))
        {
            Tag = new JsonNodeValue(name, value.GetRawText()),
        };

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in value.EnumerateObject())
                    node.Nodes.Add(CreateJsonNode(property.Name, property.Value));
                break;
            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in value.EnumerateArray())
                    node.Nodes.Add(CreateJsonNode($"[{index++}]", item));
                break;
        }

        return node;
    }

    private static string FormatJsonValue(string name, JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => $"{name}: {{ }}",
        JsonValueKind.Array => $"{name}: [ ]",
        JsonValueKind.String => $"{name}: {JsonSerializer.Serialize(value.GetString())}",
        JsonValueKind.Number => $"{name}: {value.GetRawText()}",
        JsonValueKind.True or JsonValueKind.False => $"{name}: {value.GetRawText()}",
        JsonValueKind.Null => $"{name}: null",
        _ => $"{name}: {value.GetRawText()}",
    };

    private void OnJsonSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        SelectJsonMatch(e.Shift ? -1 : 1);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void OnHexSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.Enter) return;
        FindHex(e.Shift ? -1 : 1);
        e.Handled = true;
        e.SuppressKeyPress = true;
    }

    private void FindHex(int direction)
    {
        var input = _hexSearch.Text.Trim();
        if (input.Length == 0 || _hexView.ByteProvider is null) return;

        var options = new FindOptions
        {
            MatchCase = false,
            FindDirection = direction < 0 ? Direction.Backward : Direction.Forward,
            IsValid = true,
        };

        if (_hexSearchAsHex.Checked)
        {
            if (!TryParseHex(input, out var bytes))
            {
                _hexSearchStatus.Text = "invalid hex";
                return;
            }
            options.Type = FindType.Hex;
            options.Hex = bytes;
        }
        else
        {
            options.Type = FindType.Text;
            options.Text = input;
        }

        var position = _hexView.Find(options);
        _hexSearchStatus.Text = position < 0 ? "not found" : $"found 0x{position:X}";
    }

    private static bool TryParseHex(string input, out byte[] bytes)
    {
        var compact = string.Concat(input.Where(c => !char.IsWhiteSpace(c) && c is not '-' and not ':'));
        if (compact.Length == 0 || compact.Length % 2 != 0)
        {
            bytes = [];
            return false;
        }

        bytes = new byte[compact.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            if (!byte.TryParse(compact.AsSpan(i * 2, 2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out bytes[i]))
                return false;
        return true;
    }

    private void FindJsonMatches()
    {
        _jsonMatches.Clear();
        _jsonMatchIndex = -1;

        var query = _jsonSearch.Text.Trim();
        if (query.Length == 0 || _jsonTree.Nodes.Count == 0)
        {
            _jsonMatchCount.Text = string.Empty;
            return;
        }

        foreach (TreeNode root in _jsonTree.Nodes)
            FindJsonMatches(root, query);

        _jsonMatchCount.Text = _jsonMatches.Count == 1 ? "1 match" : $"{_jsonMatches.Count} matches";
        if (_jsonMatches.Count > 0) SelectJsonMatch(1);
    }

    private void FindJsonMatches(TreeNode node, string query)
    {
        if (node.Text.Contains(query, StringComparison.OrdinalIgnoreCase)) _jsonMatches.Add(node);
        foreach (TreeNode child in node.Nodes) FindJsonMatches(child, query);
    }

    private void SelectJsonMatch(int direction)
    {
        if (_jsonMatches.Count == 0) return;

        _jsonMatchIndex = (_jsonMatchIndex + direction + _jsonMatches.Count) % _jsonMatches.Count;
        var match = _jsonMatches[_jsonMatchIndex];
        for (var parent = match.Parent; parent is not null; parent = parent.Parent) parent.Expand();
        _jsonTree.SelectedNode = match;
        match.EnsureVisible();
        _jsonMatchCount.Text = $"{_jsonMatchIndex + 1}/{_jsonMatches.Count}";
    }

    private sealed record JsonNodeValue(string Key, string RawValue);

    /// <summary>Jumps to the tab most useful for this payload.</summary>
    public void SelectBestTab()
    {
        if (_message is null || _message.Body.Length == 0)
        {
            _tabs.SelectedIndex = 0;
            return;
        }
        _tabs.SelectedIndex = ContentCodec.LooksTextual(_message.ContentType, _message.DecodedBody) ? 1 : 3;
    }
}
