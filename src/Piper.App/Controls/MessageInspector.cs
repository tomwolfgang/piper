using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Be.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Piper.App.Theme;
using Piper.Core.Http;
using Piper.Core.Sessions;
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
    private readonly Panel _jsonTypeNotice;
    private readonly Label _jsonTypeStatus;
    private readonly Button _jsonTryAnyway;
    private readonly Panel _jsonForcePanel;
    private readonly TextBox _summary;
    private readonly Label _size;
    private readonly Label _host;
    private readonly bool _showImageViewer;
    private readonly bool _showWebForms;
    private readonly ListView? _webFormsView;
    private readonly Label? _webFormsStatus;
    private readonly ZoomableImageBox? _imageView;
    private readonly Label? _imageStatus;
    private readonly Button? _imageTryAnyway;
    private readonly Panel? _imageForcePanel;
    private readonly WebView2? _videoView;
    private readonly Label? _videoStatus;
    private readonly Button? _videoTryAnyway;
    private readonly Panel? _videoForcePanel;

    private HttpMessage? _message;
    private HttpMessage? _renderedBody;
    private HttpMessage? _renderedRaw;
    private HttpMessage? _renderedHex;
    private HttpMessage? _renderedJson;
    private HttpRequestData? _renderedWebForms;
    private HttpMessage? _renderedImage;
    private HttpMessage? _renderedVideo;
    private HttpMessage? _droppedImage;
    private HttpMessage? _droppedVideo;
    private string? _videoFilePath;
    private readonly List<TreeNode> _jsonMatches = [];
    private readonly List<(string Name, string Value)> _headers = [];
    private int _jsonMatchIndex = -1;
    private int _webFormsTabIndex = -1;

    public MessageInspector(string caption, bool showImageViewer = false, bool showWebForms = false)
    {
        _showImageViewer = showImageViewer;
        _showWebForms = showWebForms;
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

        _jsonTypeStatus = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Palette.TextDim,
            Padding = new Padding(6, 4, 0, 0),
        };
        _jsonTryAnyway = new Button
        {
            Text = "Force",
        };
        _jsonTryAnyway.Click += (_, _) => TryRenderJson();
        _jsonForcePanel = BuildForcePanel(_jsonTryAnyway);
        _jsonTypeNotice = new Panel { Dock = DockStyle.Top, Height = 34, Visible = false };
        _jsonTypeNotice.Controls.Add(_jsonTypeStatus);
        _jsonTypeNotice.Controls.Add(_jsonForcePanel);

        var jsonPanel = new Panel { Dock = DockStyle.Fill };
        jsonPanel.Controls.Add(_jsonTree);
        jsonPanel.Controls.Add(jsonSearchRow);
        jsonPanel.Controls.Add(_jsonTypeNotice);

        if (_showWebForms)
        {
            _webFormsView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = false,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                Font = Palette.Mono,
            };
            _webFormsView.Columns.Add("Source", 70);
            _webFormsView.Columns.Add("Name", 180);
            _webFormsView.Columns.Add("Value", 420);
            _webFormsView.Columns.Add("Content-Type", 160);
            DarkListView.Attach(_webFormsView);
            DarkListView.AddFillerColumn(_webFormsView);
            _webFormsView.ContextMenuStrip = BuildWebFormsMenu();
            _webFormsView.MouseDown += OnWebFormsMouseDown;
            _webFormsView.KeyDown += (_, e) =>
            {
                if (!e.Control || e.KeyCode != Keys.C) return;
                var lines = _webFormsView.SelectedItems.Cast<ListViewItem>()
                    .Select(item => $"{item.SubItems[1].Text}={item.SubItems[2].Text}");
                Clipboard.SetText(string.Join(Environment.NewLine, lines));
                e.Handled = true;
            };

            _webFormsStatus = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                ForeColor = Palette.TextDim,
                Padding = new Padding(6, 4, 6, 0),
            };
        }

        if (_showImageViewer)
        {
            _imageStatus = new Label
            {
                Dock = DockStyle.Fill,
                // Keep both the dimensions and the (sometimes long) MIME type fully visible.
                // A fixed height is intentional here: AutoSize lets the docked image viewer
                // claim the remaining layout space before a second line is measured.
                Height = 64,
                ForeColor = Palette.TextDim,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(6, 4, 6, 4),
            };
            _imageTryAnyway = new Button
            {
                Text = "Force",
            };
            _imageTryAnyway.Click += (_, _) => TryRenderImage();
            _imageForcePanel = BuildForcePanel(_imageTryAnyway);
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
                Dock = DockStyle.Fill,
                Height = 42,
                ForeColor = Palette.TextDim,
                Padding = new Padding(6, 4, 0, 0),
            };
            _videoTryAnyway = new Button
            {
                Text = "Force",
            };
            _videoTryAnyway.Click += (_, _) => TryRenderVideo();
            _videoForcePanel = BuildForcePanel(_videoTryAnyway);
            _videoView = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Palette.Surface };
        }

        _tabs = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        _tabs.TabPages.Add(NewPage("Headers", headersPanel));
        _tabs.TabPages.Add(NewPage("Body", _bodyView));
        _tabs.TabPages.Add(NewPage("Raw", _rawView));
        _tabs.TabPages.Add(NewPage("Hex", hexPanel));
        _tabs.TabPages.Add(NewPage("JSON", jsonPanel));
        if (_webFormsView is not null && _webFormsStatus is not null)
        {
            var webFormsPanel = new Panel { Dock = DockStyle.Fill };
            webFormsPanel.Controls.Add(_webFormsView);
            webFormsPanel.Controls.Add(_webFormsStatus);
            _webFormsTabIndex = _tabs.TabPages.Count;
            _tabs.TabPages.Add(NewPage("WebForms", webFormsPanel));
        }
        if (_imageView is not null && _imageStatus is not null)
        {
            var imagePanel = new Panel { Dock = DockStyle.Fill };
            var imageStatusPanel = new Panel { Dock = DockStyle.Top, Height = 64 };
            imageStatusPanel.Controls.Add(_imageStatus);
            imageStatusPanel.Controls.Add(_imageForcePanel!);
            imagePanel.Controls.Add(_imageView);
            imagePanel.Controls.Add(imageStatusPanel);
            imagePanel.Controls.Add(BuildImageToolbar());
            EnableMediaDrop(imagePanel, imageTabIndex: 5);
            _tabs.TabPages.Add(NewPage("Image", imagePanel));
        }
        if (_videoView is not null && _videoStatus is not null)
        {
            var videoPanel = new Panel { Dock = DockStyle.Fill };
            var videoStatusPanel = new Panel { Dock = DockStyle.Top, Height = 42 };
            videoStatusPanel.Controls.Add(_videoStatus);
            videoStatusPanel.Controls.Add(_videoForcePanel!);
            videoPanel.Controls.Add(_videoView);
            videoPanel.Controls.Add(videoStatusPanel);
            EnableMediaDrop(videoPanel, imageTabIndex: 6);
            _tabs.TabPages.Add(NewPage("Video", videoPanel));
        }
        _tabs.SelectedIndexChanged += (_, _) => RenderSelectedTab();
        if (_showImageViewer)
        {
            _tabs.AllowDrop = true;
            _tabs.DragEnter += OnMediaTabDragEnter;
            _tabs.DragOver += OnMediaTabDragEnter;
            _tabs.DragDrop += OnMediaTabDragDrop;
        }

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

    private static Panel BuildForcePanel(Button button)
    {
        button.Dock = DockStyle.Top;
        button.Height = 30;

        var panel = new Panel
        {
            Dock = DockStyle.Right,
            Width = 82,
            Padding = new Padding(0, 2, 4, 2),
            Visible = false,
        };
        panel.Controls.Add(button);
        return panel;
    }

    private ContextMenuStrip BuildHeadersMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var copyHeader = new ToolStripMenuItem("Copy entire header", null, (_, _) => CopySelectedHeader(valueOnly: false));
        var copyValue = new ToolStripMenuItem("Copy value only", null, (_, _) => CopySelectedHeader(valueOnly: true));
        var openInBrowser = new ToolStripMenuItem("Open with default browser", null,
            (_, _) => OpenInDefaultBrowser(SelectedHeader?.Value));
        menu.Items.AddRange([copyHeader, copyValue, new ToolStripSeparator(), openInBrowser]);
        menu.Opening += (_, _) =>
        {
            var header = SelectedHeader;
            var enabled = header is not null;
            copyHeader.Enabled = enabled;
            copyValue.Enabled = enabled;
            openInBrowser.Enabled = header is { } selected && TryGetBrowserUrl(selected.Value, out _);
        };
        return menu;
    }

    private ContextMenuStrip BuildJsonMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var copyPair = new ToolStripMenuItem("Copy key-value", null, (_, _) => CopySelectedJson(valueOnly: false));
        var copyValue = new ToolStripMenuItem("Copy value only", null, (_, _) => CopySelectedJson(valueOnly: true));
        var openInBrowser = new ToolStripMenuItem("Open with default browser", null,
            (_, _) => OpenInDefaultBrowser(SelectedJsonValue?.StringValue));
        menu.Items.AddRange([copyPair, copyValue, new ToolStripSeparator(), openInBrowser]);
        menu.Opening += (_, _) =>
        {
            var value = SelectedJsonValue;
            var enabled = value is not null;
            copyPair.Enabled = enabled;
            copyValue.Enabled = enabled;
            openInBrowser.Enabled = value is { StringValue: { } stringValue } && TryGetBrowserUrl(stringValue, out _);
        };
        return menu;
    }

    private ContextMenuStrip BuildWebFormsMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var copyNameValue = new ToolStripMenuItem("Copy Name-Value", null, (_, _) => CopySelectedWebForm(valueOnly: false));
        var copyValue = new ToolStripMenuItem("Copy Value", null, (_, _) => CopySelectedWebForm(valueOnly: true));
        var save = new ToolStripMenuItem("Save binary data...", null, (_, _) => SaveSelectedWebFormBinary());
        var viewHex = new ToolStripMenuItem("View in Hex...", null, (_, _) => ViewSelectedWebFormBinaryAsHex());
        menu.Items.AddRange([copyNameValue, copyValue, new ToolStripSeparator(), save, viewHex]);
        menu.Opening += (_, _) =>
        {
            var field = SelectedWebFormField;
            copyNameValue.Enabled = field is not null;
            copyValue.Enabled = field is not null;
            save.Enabled = field?.HasBinaryData == true;
            viewHex.Enabled = field?.HasBinaryData == true;
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

    private void EnableMediaDrop(Control control, int imageTabIndex)
    {
        control.AllowDrop = true;
        control.DragEnter += (_, e) => SetMediaDropEffect(e, imageTabIndex);
        control.DragOver += (_, e) => SetMediaDropEffect(e, imageTabIndex);
        control.DragDrop += (_, e) => LoadDroppedMedia(e, imageTabIndex);
        foreach (Control child in control.Controls) EnableMediaDrop(child, imageTabIndex);
    }

    private void OnMediaTabDragEnter(object? sender, DragEventArgs e)
    {
        var point = _tabs.PointToClient(new System.Drawing.Point(e.X, e.Y));
        var target = Enumerable.Range(5, Math.Min(2, _tabs.TabCount - 5))
            .FirstOrDefault(index => _tabs.GetTabRect(index).Contains(point), -1);
        SetMediaDropEffect(e, target);
        if (e.Effect == DragDropEffects.Copy) _tabs.SelectedIndex = target;
    }

    private void OnMediaTabDragDrop(object? sender, DragEventArgs e)
    {
        var point = _tabs.PointToClient(new System.Drawing.Point(e.X, e.Y));
        var target = Enumerable.Range(5, Math.Min(2, _tabs.TabCount - 5))
            .FirstOrDefault(index => _tabs.GetTabRect(index).Contains(point), -1);
        LoadDroppedMedia(e, target);
    }

    private static HttpMessage? DroppedResponse(IDataObject? data) =>
        data?.GetData(typeof(Session)) is Session { Response: { } response } ? response : null;

    private void SetMediaDropEffect(DragEventArgs e, int targetTabIndex)
    {
        e.Effect = targetTabIndex is 5 or 6 && DroppedResponse(e.Data) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void LoadDroppedMedia(DragEventArgs e, int targetTabIndex)
    {
        if (targetTabIndex is not (5 or 6) || DroppedResponse(e.Data) is not { } response) return;

        if (targetTabIndex == 5)
        {
            _droppedImage = response;
            _renderedImage = null;
        }
        else
        {
            _droppedVideo = response;
            _renderedVideo = null;
        }

        _tabs.SelectedIndex = targetTabIndex;
        RenderSelectedTab();
    }

    /// <summary>Creates the visible counterpart to the image view's right-click actions.</summary>
    private ToolStrip BuildImageToolbar()
    {
        var zoomIn = new ToolStripButton("Zoom +")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Zoom in",
        };
        zoomIn.Click += (_, _) => ChangeImageZoom(1.25f);

        var zoomOut = new ToolStripButton("Zoom -")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Zoom out",
        };
        zoomOut.Click += (_, _) => ChangeImageZoom(0.8f);

        var actualSize = new ToolStripButton("100%")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Actual size and reset pan",
        };
        actualSize.Click += (_, _) => ResetImageView();

        var save = new ToolStripDropDownButton("Save")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = "Save or convert this image",
        };
        save.DropDownItems.Add("Save as PNG...", null, (_, _) => SaveCurrentImage("png"));
        save.DropDownItems.Add("Save as JPEG...", null, (_, _) => SaveCurrentImage("jpg"));
        save.DropDownItems.Add("Save as WebP...", null, (_, _) => SaveCurrentImage("webp"));
        save.DropDownItems.Add("Save image as...", null, (_, _) => SaveCurrentImage(null));

        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Font = Palette.UiFont,
        };
        toolbar.Items.AddRange([zoomIn, zoomOut, actualSize, new ToolStripSeparator(), save]);
        return toolbar;
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

    private static void OpenInDefaultBrowser(string? value)
    {
        if (!TryGetBrowserUrl(value, out var url)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private static bool TryGetBrowserUrl(string? value, out string url)
    {
        url = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;

        url = uri.AbsoluteUri;
        return true;
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

    private JsonNodeValue? SelectedJsonValue => _jsonTree.SelectedNode?.Tag as JsonNodeValue;

    private void CopySelectedJson(bool valueOnly)
    {
        if (SelectedJsonValue is not { } value) return;
        Clipboard.SetText(valueOnly ? value.RawValue : _jsonTree.SelectedNode?.Text ?? string.Empty);
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
            if (_showImageViewer) _tabs.SelectedIndex = 0;
            return;
        }

        foreach (var header in message.Headers)
            _headers.Add((header.Name, header.Value));
        RenderHeaders();

        // Bodies can be large and decoding/formatting them used to happen four times for each
        // selection, whether or not their tabs were ever viewed. Clear stale content now and
        // render only the most useful tab for a newly selected response.
        var preserveJsonView = CanPreserveJsonView(message);
        ClearDeferredViews(preserveJsonView);
        // Carry the rendered marker forward to the newly selected message. This prevents the
        // normal lazy renderer from rebuilding the identical tree (and collapsing it again).
        if (preserveJsonView) _renderedJson = message;
        if (_showImageViewer) SelectBestTab();
        RenderSelectedTab();
    }

    /// <summary>True when the currently rendered JSON tree already represents <paramref name="message"/>.</summary>
    private bool CanPreserveJsonView(HttpMessage message)
    {
        if (_renderedJson is null
            || !IsJsonContentType(message.ContentType)
            || !IsJsonContentType(_renderedJson.ContentType)) return false;

        try
        {
            // Compare decoded bytes rather than wire bytes: gzip/br compression differences do
            // not change the JSON the user is inspecting.
            return message.DecodedBody.AsSpan().SequenceEqual(_renderedJson.DecodedBody);
        }
        catch
        {
            // A malformed encoded body should fall back to the normal safe re-render path.
            return false;
        }
    }

    private void ClearDeferredViews(bool preserveJsonView = false)
    {
        _rawView.Clear();
        _bodyView.Clear();
        _hexView.ByteProvider = new DynamicByteProvider(Array.Empty<byte>());
        _hexSearchStatus.Text = string.Empty;
        if (_webFormsView is not null) _webFormsView.Items.Clear();
        if (_webFormsStatus is not null) _webFormsStatus.Text = string.Empty;
        if (_imageView?.Image is { } image) image.Dispose();
        if (_imageView is not null) _imageView.Image = null;
        if (_imageStatus is not null) _imageStatus.Text = string.Empty;
        if (_imageForcePanel is not null) _imageForcePanel.Visible = false;
        ClearVideo();
        _droppedImage = _droppedVideo = null;
        _renderedBody = _renderedRaw = _renderedHex = _renderedImage = _renderedVideo = null;
        _renderedWebForms = null;
        if (preserveJsonView) return;

        _jsonTree.Nodes.Clear();
        _jsonMatchCount.Text = string.Empty;
        _jsonTypeNotice.Visible = false;
        _jsonForcePanel.Visible = false;
        _jsonTypeStatus.Text = string.Empty;
        _renderedJson = null;
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
            case var index when index == _webFormsTabIndex
                                && _message is HttpRequestData request
                                && !ReferenceEquals(_renderedWebForms, request):
                RenderWebForms(request);
                _renderedWebForms = request;
                break;
            case 5 when _showImageViewer && !ReferenceEquals(_renderedImage, _message):
                var image = _droppedImage ?? _message;
                if (!ReferenceEquals(_renderedImage, image))
                {
                    RenderImage(image, force: _droppedImage is not null);
                    _renderedImage = image;
                }
                break;
            case 6 when _showImageViewer && !ReferenceEquals(_renderedVideo, _message):
                var video = _droppedVideo ?? _message;
                if (!ReferenceEquals(_renderedVideo, video))
                {
                    RenderVideo(video, force: _droppedVideo is not null);
                    _renderedVideo = video;
                }
                break;
        }
    }

    private WebFormParser.Field? SelectedWebFormField =>
        _webFormsView?.SelectedItems.Count > 0
            ? _webFormsView.SelectedItems[0].Tag as WebFormParser.Field
            : null;

    private void CopySelectedWebForm(bool valueOnly)
    {
        if (SelectedWebFormField is not { } field) return;
        Clipboard.SetText(valueOnly ? field.Value : $"{field.Name}={field.Value}");
    }

    private void OnWebFormsMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right || _webFormsView?.GetItemAt(e.X, e.Y) is not { } item) return;
        _webFormsView.SelectedIndices.Clear();
        item.Selected = true;
        item.Focused = true;
    }

    private void SaveSelectedWebFormBinary()
    {
        var field = SelectedWebFormField;
        if (field?.BinaryData is not { } data) return;

        using var dialog = new SaveFileDialog
        {
            Title = "Save form field contents",
            FileName = FileNameFor(field),
            Filter = "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, data);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not save form field", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ViewSelectedWebFormBinaryAsHex()
    {
        var field = SelectedWebFormField;
        if (field?.BinaryData is not { } data) return;

        using var dialog = new Form
        {
            Text = $"Hex - {field.Name}",
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new System.Drawing.Size(900, 600),
            MinimumSize = new System.Drawing.Size(500, 350),
        };
        var hex = new HexBox
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
            ByteProvider = new DynamicByteProvider(data),
        };
        dialog.Controls.Add(hex);
        Palette.Apply(dialog);
        hex.BackColor = Palette.Surface;
        hex.ForeColor = Palette.Text;
        dialog.ShowDialog(this);
    }

    private static string FileNameFor(WebFormParser.Field field)
    {
        var name = field.FileName ?? field.Name;
        foreach (var character in Path.GetInvalidFileNameChars()) name = name.Replace(character, '_');
        return string.IsNullOrWhiteSpace(name) ? "form-field.bin" : name;
    }

    private void RenderWebForms(HttpRequestData request)
    {
        if (_webFormsView is null || _webFormsStatus is null) return;

        _webFormsView.BeginUpdate();
        try
        {
            _webFormsView.Items.Clear();
            var fields = WebFormParser.Parse(request);
            foreach (var field in fields)
            {
                var item = new ListViewItem([field.Source, field.Name, field.Value, field.ContentType ?? string.Empty])
                {
                    Tag = field,
                };
                _webFormsView.Items.Add(item);
            }

            _webFormsStatus.Text = fields.Count == 0
                ? "No query-string or supported HTML form fields in this request."
                : $"{fields.Count:N0} request parameter(s)  ·  Query string, URL-encoded forms, and multipart forms are shown.";
        }
        catch (Exception ex)
        {
            _webFormsStatus.Text = $"Could not parse request parameters: {ex.Message}";
        }
        finally
        {
            _webFormsView.EndUpdate();
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

    private void RenderImage(HttpMessage message, bool force = false)
    {
        if (_imageView is null || _imageStatus is null) return;

        if (!force && (message.ContentType is not { } contentType
            || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            _imageStatus.Text = "Response does not have an image content type.";
            if (_imageForcePanel is not null) _imageForcePanel.Visible = true;
            return;
        }

        if (_imageForcePanel is not null) _imageForcePanel.Visible = false;
        try
        {
            using var decoded = ImageSharpImage.Load(message.DecodedBody);
            using var png = new MemoryStream();
            decoded.SaveAsPng(png);
            png.Position = 0;
            using var source = System.Drawing.Image.FromStream(png);
            if (_imageView.Image is { } oldImage) oldImage.Dispose();
            _imageView.Image = new System.Drawing.Bitmap(source);
            var label = message.ContentType ?? "Detected image (no Content-Type)";
            _imageStatus.Text = $"{_imageView.Image.Width:N0} x {_imageView.Image.Height:N0} px\r\n{label}";
        }
        catch (UnknownImageFormatException)
        {
            // Servers sometimes label a response as image/* while returning an error page or
            // other non-image bytes. This is an inspection failure, not an application error.
            _imageStatus.Text = "Could not display this response as an image: unsupported or invalid image data.";
        }
        catch (Exception ex)
        {
            _imageStatus.Text = $"Could not display this response as an image: {ex.Message}";
        }
    }

    private async void RenderVideo(HttpMessage message, bool force = false)
    {
        if (_videoView is null || _videoStatus is null) return;

        var contentType = message.ContentType;
        if (!force && (contentType is null
            || !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
        {
            _videoStatus.Text = "Response does not have a video content type.";
            if (_videoForcePanel is not null) _videoForcePanel.Visible = true;
            return;
        }

        if (_videoForcePanel is not null) _videoForcePanel.Visible = false;
        ClearVideo();
        var extension = VideoExtensionFor(contentType, message.DecodedBody);
        var folder = Path.Combine(Path.GetTempPath(), "Piper", "video-inspector");
        var path = Path.Combine(folder, $"{Guid.NewGuid():N}{extension}");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(path, message.DecodedBody);
            _videoFilePath = path;
            _videoStatus.Text = $"{contentType ?? "Detected video (no Content-Type)"}\r\nLoading the embedded media player...";

            await _videoView.EnsureCoreWebView2Async();
            if (_videoFilePath != path
                || (!ReferenceEquals(_message, message) && !ReferenceEquals(_droppedVideo, message))) return;

            _videoView.Source = new Uri(path);
            _videoStatus.Text = contentType ?? "Video loaded from response bytes (no Content-Type)";
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
        if (_videoForcePanel is not null) _videoForcePanel.Visible = false;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string VideoExtensionFor(string? contentType, byte[] bytes)
    {
        var declared = contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "video/mp4" => ".mp4",
            "video/webm" => ".webm",
            "video/ogg" => ".ogv",
            "video/quicktime" => ".mov",
            "video/x-msvideo" => ".avi",
            _ => null,
        };
        if (declared is not null) return declared;
        if (bytes.Length >= 12 && bytes.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return ".mp4";
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("OggS"u8)) return ".ogv";
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })) return ".webm";
        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8)
            && bytes.AsSpan(8, 4).SequenceEqual("AVI "u8)) return ".avi";
        return ".video";
    }

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

    private void RenderJson(HttpMessage message, bool force = false)
    {
        _jsonTree.BeginUpdate();
        try
        {
            _jsonTree.Nodes.Clear();
            _jsonTypeNotice.Visible = false;
            _jsonForcePanel.Visible = false;
            _jsonTypeStatus.Text = string.Empty;

            if (message.Body.Length == 0)
            {
                _jsonTree.Nodes.Add("(no body)");
                return;
            }

            if (!force && !IsJsonContentType(message.ContentType))
            {
                _jsonTypeStatus.Text = "Response does not have a JSON content type.";
                _jsonForcePanel.Visible = true;
                _jsonTypeNotice.Visible = true;
                _jsonTree.Nodes.Add("[JSON parsing skipped because the content type does not match]");
                return;
            }

            if (!force && !ContentCodec.LooksTextual(message.ContentType, message.DecodedBody))
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

    private void TryRenderJson()
    {
        if (_message is null) return;
        RenderJson(_message, force: true);
        _renderedJson = _message;
    }

    private void TryRenderImage()
    {
        if (_message is null) return;
        RenderImage(_message, force: true);
        _renderedImage = _message;
    }

    private void TryRenderVideo()
    {
        if (_message is null) return;
        RenderVideo(_message, force: true);
        _renderedVideo = _message;
    }

    private static TreeNode CreateJsonNode(string name, JsonElement value)
    {
        var node = new TreeNode(FormatJsonValue(name, value))
        {
            Tag = new JsonNodeValue(name, value.GetRawText(),
                value.ValueKind == JsonValueKind.String ? value.GetString() : null),
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

    private sealed record JsonNodeValue(string Key, string RawValue, string? StringValue);

    /// <summary>Routes the standard Find shortcut to the search field for the active inspector tab.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.F))
        {
            var searchBox = _tabs.SelectedIndex switch
            {
                0 => _headersSearch,
                3 => _hexSearch,
                4 => _jsonSearch,
                _ => null,
            };

            if (searchBox is not null)
            {
                searchBox.Focus();
                searchBox.SelectAll();
                return true;
            }
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    /// <summary>Jumps to the tab most useful for this payload.</summary>
    public void SelectBestTab()
    {
        if (_message is null || _message.Body.Length == 0)
        {
            _tabs.SelectedIndex = 0;
            return;
        }

        var contentType = _message.ContentType ?? string.Empty;
        if (_showImageViewer && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            _tabs.SelectedIndex = 5;
            return;
        }

        if (_showImageViewer && contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            _tabs.SelectedIndex = 6;
            return;
        }

        if (IsJsonContentType(contentType))
        {
            _tabs.SelectedIndex = 4;
            return;
        }

        // Response inspection should start at headers unless its content advertises one of
        // the specialised viewers above. The body/raw/hex views remain available explicitly.
        _tabs.SelectedIndex = 0;
    }

    private static bool IsJsonContentType(string? contentType) => contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
}
