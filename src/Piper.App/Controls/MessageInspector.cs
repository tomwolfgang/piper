using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Be.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Piper.App.Theme;
using Piper.Core.Http;
using Piper.Core.Sessions;
using ShimmyMySherbet.WinForms.ZoomableImgBox;

namespace Piper.App.Controls;

/// <summary>Tabbed view of one HTTP message: headers, raw bytes, decoded body and a hex dump.</summary>
public sealed class MessageInspector : UserControl
{
    private const int MaxRenderBytes = 4 * 1024 * 1024;

    private readonly DarkTabControl _tabs;
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
    private readonly bool _showImageViewer;
    private readonly bool _showWebForms;
    private readonly ListView? _webFormsView;
    private readonly Label? _webFormsStatus;
    private readonly ZoomableImageBox? _imageView;
    private readonly Label? _imageStatus;
    private readonly Button? _imageTryAnyway;
    private readonly Panel? _imageForcePanel;
    private readonly VideoView? _videoView;
    private readonly Label? _videoStatus;
    private readonly Button? _videoTryAnyway;
    private readonly Panel? _videoForcePanel;

    private HttpMessage? _message;
    private HttpMessage? _renderedBody;
    private HttpMessage? _renderedRaw;
    private HttpMessage? _renderedHex;
    private HttpMessage? _renderedJson;
    private HttpRequestData? _renderedWebForms;
    private readonly Func<WebView2, Task>? _startWebpView;
    private readonly Func<string?, string?>? _chooseImageSavePath;
    private WebpView? _webpView;
    private HttpMessage? _renderedImage;
    private HttpMessage? _renderedVideo;
    private HttpMessage? _droppedImage;
    private HttpMessage? _droppedVideo;
    // A node's tree path (child index at each level) is kept for a match, because a match in a
    // collapsed branch has no TreeNode until the path to it is expanded.
    private readonly List<int[]> _jsonMatches = [];
    private JsonTreeSource? _jsonSource;
    private CancellationTokenSource? _jsonBuild;
    private CancellationTokenSource? _jsonSearching;
    private static readonly object JsonPlaceholder = new();

    // Parses queue behind this, so a run of selections cannot have many multi-MB parses going at once.
    private static readonly SemaphoreSlim JsonBuildGate = new(2);
    private readonly List<(string Name, string Value)> _headers = [];
    private int _jsonMatchIndex = -1;
    private int _webFormsTabIndex = -1;

    /// <param name="startWebpView">Starts the WebP preview's WebView2; a test seam, null for the real one.</param>
    /// <param name="chooseImageSavePath">Replaces the Save image dialog: gets the requested format (null for any) and returns the file to write, or null to cancel. A test seam, null for the real dialog.</param>
    /// <param name="startVideoView">Starts the video preview's WebView2; a test seam, null for the real one.</param>
    public MessageInspector(string caption, bool showImageViewer = false, bool showWebForms = false, Func<WebView2, Task>? startWebpView = null,
        Func<string?, string?>? chooseImageSavePath = null, Func<WebView2, Task>? startVideoView = null)
    {
        _startWebpView = startWebpView;
        _chooseImageSavePath = chooseImageSavePath;
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
        _headersView.Columns.Add(Strings.Inspector.ColumnName, 210);
        _headersView.Columns.Add(Strings.Inspector.ColumnValue, 460);
        DarkListView.Attach(_headersView);
        DarkListView.AddFillerColumn(_headersView);
        _headersView.MouseDown += OnHeadersMouseDown;
        _headersView.KeyDown += (_, e) =>
        {
            if (!e.Control || e.KeyCode != Keys.C) return;
            var lines = _headersView.SelectedItems.Cast<ListViewItem>()
                .Select(i => $"{i.Text}: {i.SubItems[1].Text}");
            ClipboardText.TrySet(string.Join(Environment.NewLine, lines));
            e.Handled = true;
        };
        _headersView.ContextMenuStrip = BuildHeadersMenu();

        _headersSearch = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = Strings.Inspector.SearchHeaders,
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
            PlaceholderText = Strings.Inspector.FindTextOrBytes,
            Font = Palette.Mono,
        };
        _hexSearch.KeyDown += OnHexSearchKeyDown;
        _hexSearchAsHex = new CheckBox
        {
            Dock = DockStyle.Right,
            Width = 52,
            Text = Strings.Inspector.HexToggle,
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
        var previousHexMatch = new Button { Dock = DockStyle.Right, Width = 30, Text = Strings.Inspector.PreviousMatch, TabStop = false };
        previousHexMatch.Click += (_, _) => FindHex(-1);
        var nextHexMatch = new Button { Dock = DockStyle.Right, Width = 30, Text = Strings.Inspector.NextMatch, TabStop = false };
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
            PlaceholderText = Strings.Inspector.SearchJson,
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

        var previousMatch = new Button { Dock = DockStyle.Right, Width = 30, Text = Strings.Inspector.PreviousMatch, TabStop = false };
        previousMatch.Click += (_, _) => SelectJsonMatch(-1);
        var nextMatch = new Button { Dock = DockStyle.Right, Width = 30, Text = Strings.Inspector.NextMatch, TabStop = false };
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
        _jsonTree.BeforeExpand += (_, e) => { if (e.Node is { } node) ShowJsonChildren(node); };
        _jsonTree.ContextMenuStrip = BuildJsonMenu();

        _jsonTypeStatus = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Palette.TextDim,
            Padding = new Padding(6, 4, 0, 0),
        };
        _jsonTryAnyway = new Button
        {
            Text = Strings.Inspector.Force,
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
            _webFormsView.Columns.Add(Strings.Inspector.ColumnSource, 70);
            _webFormsView.Columns.Add(Strings.Inspector.ColumnName, 180);
            _webFormsView.Columns.Add(Strings.Inspector.ColumnValue, 420);
            _webFormsView.Columns.Add(Strings.Inspector.ColumnContentType, 160);
            DarkListView.Attach(_webFormsView);
            DarkListView.AddFillerColumn(_webFormsView);
            _webFormsView.ContextMenuStrip = BuildWebFormsMenu();
            _webFormsView.MouseDown += OnWebFormsMouseDown;
            _webFormsView.KeyDown += (_, e) =>
            {
                if (!e.Control || e.KeyCode != Keys.C) return;
                var lines = _webFormsView.SelectedItems.Cast<ListViewItem>()
                    .Select(item => $"{item.SubItems[1].Text}={item.SubItems[2].Text}");
                ClipboardText.TrySet(string.Join(Environment.NewLine, lines));
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
                Text = Strings.Inspector.Force,
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
                Text = Strings.Inspector.Force,
            };
            _videoTryAnyway.Click += (_, _) => TryRenderVideo();
            _videoForcePanel = BuildForcePanel(_videoTryAnyway);
            _videoView = new VideoView(startVideoView);
        }

        _tabs = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        _tabs.TabPages.Add(NewPage(Strings.Inspector.TabHeaders, headersPanel));
        _tabs.TabPages.Add(NewPage(Strings.Inspector.TabBody, _bodyView));
        _tabs.TabPages.Add(NewPage(Strings.Inspector.TabRaw, _rawView));
        _tabs.TabPages.Add(NewPage(Strings.Inspector.TabHex, hexPanel));
        _tabs.TabPages.Add(NewPage(Strings.Inspector.TabJson, jsonPanel));
        if (_webFormsView is not null && _webFormsStatus is not null)
        {
            var webFormsPanel = new Panel { Dock = DockStyle.Fill };
            webFormsPanel.Controls.Add(_webFormsView);
            webFormsPanel.Controls.Add(_webFormsStatus);
            _webFormsTabIndex = _tabs.TabPages.Count;
            _tabs.TabPages.Add(NewPage(Strings.Inspector.TabWebForms, webFormsPanel));
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
            _tabs.TabPages.Add(NewPage(Strings.Inspector.TabImage, imagePanel));
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
            _tabs.TabPages.Add(NewPage(Strings.Inspector.TabVideo, videoPanel));
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
        var copyHeader = new ToolStripMenuItem(Strings.Inspector.CopyEntireHeader, null, (_, _) => CopySelectedHeader(valueOnly: false));
        var copyValue = new ToolStripMenuItem(Strings.Inspector.CopyValueOnly, null, (_, _) => CopySelectedHeader(valueOnly: true));
        var openInBrowser = new ToolStripMenuItem(Strings.Inspector.OpenWithDefaultBrowser, null,
            (_, _) => OpenInDefaultBrowser(SelectedHeader?.Value));
        var textWizard = new ToolStripMenuItem(Strings.Inspector.SendValueToTextWizard, null,
            (_, _) => SendToTextWizard(SelectedHeader?.Value));
        menu.Items.AddRange([copyHeader, copyValue, new ToolStripSeparator(), openInBrowser, textWizard]);
        menu.Opening += (_, _) =>
        {
            var header = SelectedHeader;
            var enabled = header is not null;
            copyHeader.Enabled = enabled;
            copyValue.Enabled = enabled;
            textWizard.Enabled = enabled;
            openInBrowser.Enabled = header is { } selected && TryGetBrowserUrl(selected.Value, out _);
        };
        return menu;
    }

    private ContextMenuStrip BuildJsonMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var copyPair = new ToolStripMenuItem(Strings.Inspector.CopyKeyValue, null, (_, _) => CopySelectedJson(valueOnly: false));
        var copyValue = new ToolStripMenuItem(Strings.Inspector.CopyValueOnly, null, (_, _) => CopySelectedJson(valueOnly: true));
        var openInBrowser = new ToolStripMenuItem(Strings.Inspector.OpenWithDefaultBrowser, null,
            (_, _) => OpenInDefaultBrowser(SelectedJsonValue?.StringValue));
        var textWizard = new ToolStripMenuItem(Strings.Inspector.SendValueToTextWizard, null,
            (_, _) => SendToTextWizard(SelectedJsonValue is { } selected ? selected.StringValue ?? selected.RawValue : null));
        menu.Items.AddRange([copyPair, copyValue, new ToolStripSeparator(), openInBrowser, textWizard]);
        menu.Opening += (_, _) =>
        {
            var value = SelectedJsonValue;
            var enabled = value is not null;
            copyPair.Enabled = enabled;
            copyValue.Enabled = enabled;
            textWizard.Enabled = enabled;
            openInBrowser.Enabled = value is { StringValue: { } stringValue } && TryGetBrowserUrl(stringValue, out _);
        };
        return menu;
    }

    private ContextMenuStrip BuildWebFormsMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var copyNameValue = new ToolStripMenuItem(Strings.Inspector.CopyNameValue, null, (_, _) => CopySelectedWebForm(valueOnly: false));
        var copyValue = new ToolStripMenuItem(Strings.Inspector.CopyValue, null, (_, _) => CopySelectedWebForm(valueOnly: true));
        var save = new ToolStripMenuItem(Strings.Inspector.SaveBinaryData, null, (_, _) => SaveSelectedWebFormBinary());
        var viewHex = new ToolStripMenuItem(Strings.Inspector.ViewInHex, null, (_, _) => ViewSelectedWebFormBinaryAsHex());
        var textWizard = new ToolStripMenuItem(Strings.Inspector.SendValueToTextWizard, null,
            (_, _) => SendToTextWizard(SelectedWebFormField?.Value));
        menu.Items.AddRange([copyNameValue, copyValue, new ToolStripSeparator(), save, viewHex, textWizard]);
        menu.Opening += (_, _) =>
        {
            var field = SelectedWebFormField;
            copyNameValue.Enabled = field is not null;
            copyValue.Enabled = field is not null;
            textWizard.Enabled = field is not null;
            save.Enabled = field?.HasBinaryData == true;
            viewHex.Enabled = field?.HasBinaryData == true;
        };
        return menu;
    }

    private ContextMenuStrip BuildHexMenu()
    {
        var menu = new ContextMenuStrip { Font = Palette.UiFont };
        var copy = new ToolStripMenuItem(Strings.Inspector.CopySelection, null, (_, _) => _hexView.Copy());
        var copyHex = new ToolStripMenuItem(Strings.Inspector.CopySelectionAsHex, null, (_, _) => _hexView.CopyHex());
        var selectAll = new ToolStripMenuItem(Strings.Inspector.SelectAll, null, (_, _) => _hexView.SelectAll());
        var find = new ToolStripMenuItem(Strings.Inspector.Find, null, (_, _) => _hexSearch.Focus());
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
        menu.Items.Add(Strings.Inspector.ZoomIn, null, (_, _) => ChangeImageZoom(1.25f));
        menu.Items.Add(Strings.Inspector.ZoomOut, null, (_, _) => ChangeImageZoom(0.8f));
        menu.Items.Add(Strings.Inspector.ActualSize, null, (_, _) => ResetImageView());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Strings.Inspector.SaveAsPng, null, (_, _) => SaveCurrentImage("png")).Tag = ConvertTag;
        menu.Items.Add(Strings.Inspector.SaveAsJpeg, null, (_, _) => SaveCurrentImage("jpg")).Tag = ConvertTag;
        menu.Items.Add(Strings.Inspector.SaveImageAs, null, (_, _) => SaveCurrentImage(null)).Tag = ConvertTag;
        menu.Opening += (_, _) => EnableConversions(menu.Items);
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
        var zoomIn = new ToolStripButton(Strings.Inspector.ZoomInButton)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = Strings.Inspector.ZoomIn,
        };
        zoomIn.Click += (_, _) => ChangeImageZoom(1.25f);

        var zoomOut = new ToolStripButton(Strings.Inspector.ZoomOutButton)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = Strings.Inspector.ZoomOut,
        };
        zoomOut.Click += (_, _) => ChangeImageZoom(0.8f);

        var actualSize = new ToolStripButton(Strings.Inspector.ActualSizeButton)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = Strings.Inspector.ActualSizeTooltip,
        };
        actualSize.Click += (_, _) => ResetImageView();

        var save = new ToolStripDropDownButton(Strings.Inspector.SaveImageMenu)
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ToolTipText = Strings.Inspector.SaveImageTooltip,
        };
        save.DropDownItems.Add(Strings.Inspector.SaveAsPng, null, (_, _) => SaveCurrentImage("png")).Tag = ConvertTag;
        save.DropDownItems.Add(Strings.Inspector.SaveAsJpeg, null, (_, _) => SaveCurrentImage("jpg")).Tag = ConvertTag;
        save.DropDownItems.Add(Strings.Inspector.SaveImageAs, null, (_, _) => SaveCurrentImage(null)).Tag = ConvertTag;
        save.DropDownOpening += (_, _) => EnableConversions(save.DropDownItems);

        var toolbar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Font = Palette.UiFont,
        };
        toolbar.Items.AddRange([zoomIn, zoomOut, actualSize, new ToolStripSeparator(), save]);
        return toolbar;
    }

    // The save items re-encode with GDI+, so they are off while a WebP (which has no bitmap) is shown.
    private static readonly object ConvertTag = new();

    private void EnableConversions(ToolStripItemCollection items)
    {
        foreach (ToolStripItem item in items)
            if (ReferenceEquals(item.Tag, ConvertTag)) item.Enabled = _imageView?.Image is not null;
    }

    private void ChangeImageZoom(float multiplier)
    {
        if (_webpView is { Visible: true } webp) { webp.ChangeZoom(multiplier); return; }

        if (_imageView is null || _imageView.Image is null) return;
        _imageView.Zoom = Math.Clamp(_imageView.Zoom * multiplier, 0.05f, 32f);
    }

    private void ResetImageView()
    {
        if (_webpView is { Visible: true } webp) { webp.ChangeZoom(0); return; }

        if (_imageView is null || _imageView.Image is null) return;
        _imageView.Zoom = 1;
        _imageView.PanX = 0;
        _imageView.PanY = 0;
    }

    private async void SaveCurrentImage(string? requestedFormat)
    {
        if (_message is null || _imageView?.Image is null) return;

        // The image on screen is the dropped response when one was dropped on the tab, not the selected
        // session's. It is taken now: the dialog is modal, but selecting another session while it is open
        // must not change what the user asked to save.
        var source = _droppedImage ?? _message;
        var path = ChooseImageSavePath(requestedFormat);
        if (path is null) return;

        var format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        if (format is not ("png" or "jpg" or "jpeg")) format = requestedFormat ?? "png";

        try
        {
            var body = source.DecodedBody;

            // The guard, decoding and re-encoding take up to seconds for a large image, so they run off
            // the UI thread and into a buffer: a timeout or failure never leaves a half-written file behind.
            // GDI+ cannot be cancelled, so a timeout stops the wait and the worker's late result is dropped.
            using var timeout = new CancellationTokenSource(ImageWorkTimeout);
            var (encoded, verdict) = await ImageDecoding.RunUntilAbandonedAsync(() => GuardAndConvertImage(body, format, timeout.Token), timeout.Token);
            if (encoded is null)
            {
                MessageBox.Show(this, RefusalText(verdict) ?? Strings.Inspector.ImageWebpNotConverted, Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            await File.WriteAllBytesAsync(path, encoded);
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed)
                MessageBox.Show(this, Strings.Inspector.ImageTimedOut, Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception ex)
        {
            if (!IsDisposed)
                MessageBox.Show(this, Strings.Inspector.SaveImageFailed(ex.Message),
                    Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private string? ChooseImageSavePath(string? requestedFormat)
    {
        if (_chooseImageSavePath is { } choose) return choose(requestedFormat);

        using var dialog = new SaveFileDialog
        {
            Title = Strings.Inspector.SaveImageCaption,
            Filter = Strings.Inspector.SaveImageFilter,
            FilterIndex = requestedFormat == "jpg" ? 2 : 1,
            FileName = Strings.Inspector.SaveImageFileName(requestedFormat ?? "png"),
        };
        return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
    }

    // The encoded bytes are null when the guard refused the body, and for a WebP, which GDI+ cannot encode.
    private static (byte[]? Encoded, ImageGuardVerdict Verdict) GuardAndConvertImage(
        byte[] body, string format, CancellationToken cancellationToken)
    {
        var verdict = ImageGuard.Check(body);
        return verdict == ImageGuardVerdict.Allowed && !ImageDecoding.IsWebp(body)
            ? (ConvertImage(body, format, cancellationToken), verdict)
            : (null, verdict);
    }

    // GDI+ saves PNG and JPEG. The token is checked between the decode and the encode, not inside either.
    private static byte[] ConvertImage(byte[] body, string format, CancellationToken cancellationToken)
    {
        using var bitmap = ImageDecoding.DecodeToBitmap(body, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var output = new MemoryStream();
        bitmap.Save(output, format is "jpg" or "jpeg" ? ImageFormat.Jpeg : ImageFormat.Png);
        return output.ToArray();
    }

    /// <summary>The fixed text for a body the guard will not hand to the decoder, or null when allowed.</summary>
    private static string? RefusalText(ImageGuardVerdict verdict) => verdict switch
    {
        ImageGuardVerdict.Allowed => null,
        ImageGuardVerdict.Unrecognised => Strings.Inspector.ImageDecodeFailed,
        ImageGuardVerdict.Tiff or ImageGuardVerdict.EmbeddedProfile => Strings.Inspector.ImageBlocked,
        _ => Strings.Inspector.ImageRefused(ImageGuard.MaxPixels / (1024 * 1024), ImageGuard.MaxBytes / (1024 * 1024)),
    };

    // The most a decode or conversion may take before the inspector gives up on it.
    private static readonly TimeSpan ImageWorkTimeout = TimeSpan.FromSeconds(30);

    // Cancelled when another message is selected, the view is cleared or the control is disposed.
    private CancellationTokenSource? _imageDecoding;

    private void CancelImageDecoding()
    {
        var decoding = _imageDecoding;
        _imageDecoding = null;
        decoding?.Cancel();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelImageDecoding();
            CancelJsonWork();
        }

        base.Dispose(disposing);
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
        ClipboardText.TrySet(valueOnly ? header.Value : $"{header.Name}: {header.Value}");
    }

    private static void OpenInDefaultBrowser(string? value)
    {
        if (!TryGetBrowserUrl(value, out var url)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void SendToTextWizard(string? value)
    {
        if (value is null) return;
        TextWizardDialog.Open(FindForm(), value);
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

        _headersMatchCount.Text = query.Length == 0 ? string.Empty : Strings.Inspector.HeaderMatchCount(visible.Count, _headers.Count);
    }

    private void OnJsonNodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
    {
        if (e.Button == MouseButtons.Right) _jsonTree.SelectedNode = e.Node;
    }

    private JsonNodeRef? SelectedJsonValue => _jsonTree.SelectedNode?.Tag as JsonNodeRef;

    private void CopySelectedJson(bool valueOnly)
    {
        if (SelectedJsonValue is not { } value) return;
        // Not the node's text: a long value is clipped there.
        if ((valueOnly ? value.RawValue : value.PairText) is { } text) ClipboardText.TrySet(text);
    }

    public void SetMessage(HttpMessage? message, string summary)
    {
        // Session-list refreshes deliberately preserve their selection, but WinForms still
        // raises SelectedIndexChanged while doing so. Re-rendering a multi-MB body on every
        // refresh both wastes time and visibly resets the editor's paint/scroll state.
        if (ReferenceEquals(_message, message))
        {
            _summary.Text = summary;
            UpdateSummaryMetadata(message);
            return;
        }

        _message = message;
        _summary.Text = summary;
        UpdateSummaryMetadata(message);

        _headers.Clear();

        if (message is null)
        {
            RenderHeaders();
            ClearDeferredViews();
            if (_showImageViewer) _tabs.SelectWithoutFocus(0);
            return;
        }

        RenderMessage(message, newMessage: true);
    }

    /// <summary>The message on display, or null.</summary>
    public HttpMessage? Message => _message;

    /// <summary>
    /// Re-renders the message already on display. A relayed body is filled into the message whose
    /// head was shown while it was arriving, so <see cref="SetMessage"/> would see the same object
    /// and keep the empty body. The user's tab is left where it is.
    /// </summary>
    public void Reload(HttpMessage message, string summary)
    {
        _message = message;
        _summary.Text = summary;
        UpdateSummaryMetadata(message);
        _headers.Clear();
        RenderMessage(message, newMessage: false);
    }

    /// <summary>Changes only the summary line, for figures that move while a message arrives.</summary>
    public void SetSummary(string summary)
    {
        if (_summary.Text != summary) _summary.Text = summary;
    }

    private void RenderMessage(HttpMessage message, bool newMessage)
    {
        foreach (var header in message.Headers)
            _headers.Add((header.Name, header.Value));
        RenderHeaders();

        // Bodies can be large and decoding/formatting them used to happen four times for each
        // selection, whether or not their tabs were ever viewed. Clear stale content now and
        // render only the most useful tab for a newly selected response. A reload never keeps the
        // JSON tree: it is the same message object, so it would compare equal to its own stale view.
        var preserveJsonView = newMessage && CanPreserveJsonView(message);
        ClearDeferredViews(preserveJsonView);
        // Carry the rendered marker forward to the newly selected message. This prevents the
        // normal lazy renderer from rebuilding the identical tree (and collapsing it again).
        if (preserveJsonView) _renderedJson = message;
        if (newMessage && _showImageViewer) SelectBestTab();
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
        CancelImageDecoding();
        if (_imageView?.Image is { } image) image.Dispose();
        if (_imageView is not null) _imageView.Image = null;
        HideWebp();
        if (_imageStatus is not null) _imageStatus.Text = string.Empty;
        if (_imageForcePanel is not null) _imageForcePanel.Visible = false;
        ClearVideo();
        _droppedImage = _droppedVideo = null;
        _renderedBody = _renderedRaw = _renderedHex = _renderedImage = _renderedVideo = null;
        _renderedWebForms = null;
        if (preserveJsonView) return;

        CancelJsonWork();
        _jsonTree.Nodes.Clear();
        _jsonMatchCount.Text = string.Empty;
        _jsonTypeNotice.Visible = false;
        _jsonForcePanel.Visible = false;
        _jsonTypeStatus.Text = string.Empty;
        _renderedJson = null;
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
        ClipboardText.TrySet(valueOnly ? field.Value : $"{field.Name}={field.Value}");
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
            Title = Strings.Inspector.SaveFormFieldCaption,
            FileName = FileNameFor(field),
            Filter = Strings.Common.AllFilesFilter,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllBytes(dialog.FileName, data);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, Strings.Inspector.SaveFormFieldFailedCaption, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ViewSelectedWebFormBinaryAsHex()
    {
        var field = SelectedWebFormField;
        if (field?.BinaryData is not { } data) return;

        using var dialog = new Form
        {
            Text = Strings.Inspector.HexViewerCaption(field.Name),
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
        return string.IsNullOrWhiteSpace(name) ? Strings.Inspector.FormFieldFallbackFileName : name;
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
                ? Strings.Inspector.NoWebFormFields
                : Strings.Inspector.WebFormFieldCount(fields.Count);
        }
        catch (Exception ex)
        {
            _webFormsStatus.Text = Strings.Inspector.WebFormParseFailed(ex.Message);
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
        _size.Text = Strings.Inspector.TransferSummary(FormatSize(message.Body.LongLength), FormatSize(contentSize));
    }

    private static string FormatSize(long size) => size switch
    {
        < 1024 => Strings.Units.Bytes(size),
        < 1024 * 1024 => Strings.Units.Kilobytes(size / 1024.0),
        _ => Strings.Units.Megabytes(size / (1024.0 * 1024)),
    };

    private async void RenderImage(HttpMessage message, bool force = false)
    {
        if (_imageView is null || _imageStatus is null) return;

        if (!force && (message.ContentType is not { } contentType
            || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
        {
            _imageStatus.Text = Strings.Inspector.NotAnImageContentType;
            if (_imageForcePanel is not null) _imageForcePanel.Visible = true;
            return;
        }

        if (_imageForcePanel is not null) _imageForcePanel.Visible = false;
        CancelImageDecoding();
        HideWebp();
        var decoding = new CancellationTokenSource(ImageWorkTimeout);
        try
        {
            var body = message.DecodedBody;
            _imageDecoding = decoding;
            _imageStatus.Text = Strings.Inspector.ImageDecoding;

            // The guard scans the whole body (about 100 ms for a hostile 32 MiB JPEG), so it shares the
            // decoder's thread, timeout and cancellation rather than running here. GDI+ cannot be
            // cancelled: a timeout or a new selection stops the wait, and a late bitmap is disposed.
            var (bitmap, verdict) = await ImageDecoding.GuardAndDecodeAsync(body, decoding.Token);
            if (!ReferenceEquals(_imageDecoding, decoding) || IsDisposed)
            {
                bitmap?.Dispose();
                return;
            }

            if (bitmap is null)
            {
                // Nothing of an earlier image stays. Allowed but not decoded means a WebP, which GDI+ cannot read.
                if (_imageView.Image is { } previous) previous.Dispose();
                _imageView.Image = null;
                if (verdict != ImageGuardVerdict.Allowed) _imageStatus.Text = RefusalText(verdict);
                else await ShowWebpAsync(body, message, decoding);
                return;
            }

            if (_imageView.Image is { } oldImage) oldImage.Dispose();
            _imageView.Image = bitmap;
            var label = message.ContentType ?? Strings.Inspector.DetectedImage;
            _imageStatus.Text = Strings.Inspector.ImageDimensions(bitmap.Width, bitmap.Height, label);
        }
        catch (OperationCanceledException)
        {
            // Another message was selected (nothing to say), or the decode ran out of time.
            if (ReferenceEquals(_imageDecoding, decoding) && !IsDisposed) _imageStatus.Text = Strings.Inspector.ImageTimedOut;
        }
        catch (Exception ex) when (ImageDecoding.IsInvalidImage(ex))
        {
            // Servers sometimes label a response as image/* while returning an error page or
            // other non-image bytes. This is an inspection failure, not an application error.
            if (ReferenceEquals(_imageDecoding, decoding) && !IsDisposed) _imageStatus.Text = Strings.Inspector.ImageDecodeFailed;
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_imageDecoding, decoding) && !IsDisposed) _imageStatus.Text = Strings.Inspector.ImageFailed(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_imageDecoding, decoding)) _imageDecoding = null;
            decoding.Dispose();
        }
    }

    // View-only: WebView2 shows a WebP that ImageGuard allowed; see WebpView for the lock-down.
    private async Task ShowWebpAsync(byte[] body, HttpMessage message, CancellationTokenSource decoding)
    {
        if (_imageView?.Parent is not { } parent || _imageStatus is null) return;

        if (_webpView is null)
        {
            _webpView = new WebpView(_startWebpView);
            parent.Controls.Add(_webpView);
            parent.Controls.SetChildIndex(_webpView, 0);
        }

        var result = await _webpView.ShowAsync(body);
        if (result == WebpShowResult.Superseded || !ReferenceEquals(_imageDecoding, decoding) || IsDisposed) return;

        _imageView.Visible = result != WebpShowResult.Shown;
        _imageStatus.Text = result switch
        {
            WebpShowResult.Shown => Strings.Inspector.ImageWebpShown(message.ContentType ?? Strings.Inspector.DetectedImage),
            WebpShowResult.RuntimeUnavailable => Strings.Inspector.ImageWebpNeedsRuntime,
            _ => Strings.Inspector.ImageDecodeFailed,
        };
    }

    private void HideWebp()
    {
        _webpView?.Clear();
        if (_imageView is not null) _imageView.Visible = true;
    }

    private async void RenderVideo(HttpMessage message, bool force = false)
    {
        if (_videoView is null || _videoStatus is null) return;

        var contentType = message.ContentType;
        if (!force && (contentType is null
            || !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))
        {
            _videoStatus.Text = Strings.Inspector.NotAVideoContentType;
            if (_videoForcePanel is not null) _videoForcePanel.Visible = true;
            return;
        }

        if (_videoForcePanel is not null) _videoForcePanel.Visible = false;
        ClearVideo();
        _videoStatus.Text = Strings.Inspector.VideoLoading(contentType);
        try
        {
            // View-only: the body is served from memory to a locked-down WebView2, never written out; see VideoView.
            var result = await _videoView.ShowAsync(message.DecodedBody, contentType);
            if (result == VideoShowResult.Superseded || IsDisposed) return;

            _videoStatus.Text = result switch
            {
                VideoShowResult.Shown => contentType ?? Strings.Inspector.VideoLoadedNoContentType,
                VideoShowResult.RuntimeUnavailable => Strings.Inspector.VideoNeedsRuntime,
                _ => Strings.Inspector.VideoNotLoaded,
            };
        }
        catch (Exception ex)
        {
            if (!IsDisposed) _videoStatus.Text = Strings.Inspector.VideoFailed(ex.Message);
        }
    }

    private void ClearVideo()
    {
        _videoView?.Clear();
        if (_videoStatus is not null) _videoStatus.Text = string.Empty;
        if (_videoForcePanel is not null) _videoForcePanel.Visible = false;
    }

    private static string RenderRaw(HttpMessage message)
    {
        var sb = new StringBuilder();
        sb.Append(message.HeadAsText());

        if (message.Body.Length == 0) return sb.ToString();

        var decodedBody = message.DecodedBody;
        if (ContentCodec.LooksTextual(message.ContentType, decodedBody))
        {
            var text = message.BodyAsText(decodedBody);
            sb.Append(text.Length > MaxRenderBytes ? text[..MaxRenderBytes] + Strings.Inspector.Truncated : text);
        }
        else
        {
            sb.Append(Strings.Inspector.BinaryContent(message.Body.Length));
        }

        return sb.ToString();
    }

    private static string RenderBody(HttpMessage message)
    {
        // A body that was relayed but not kept is not an empty one, and must not read as one.
        if (message.Body.Length == 0)
            return message.BodyTotalLength > 0
                ? Strings.Inspector.BodyReleased(message.BodyTotalLength)
                : Strings.Inspector.NoBody;

        var decoded = message.DecodedBody;
        if (!ContentCodec.LooksTextual(message.ContentType, decoded))
            return Strings.Inspector.BinaryBody(message.BodyTotalLength, message.ContentType)
                   + PartialCaptureNote(message);

        string text;
        try { text = message.BodyAsText(decoded); }
        catch (Exception ex) { return Strings.Inspector.BodyDecodeFailed(ex.Message); }

        var contentType = message.ContentType ?? string.Empty;
        if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase) && TryPrettyJson(text, out var pretty))
            text = pretty;

        if (text.Length > MaxRenderBytes) text = text[..MaxRenderBytes] + Strings.Inspector.Truncated;
        return text + PartialCaptureNote(message);
    }

    /// <summary>
    /// Says so when the displayed bytes are only the start of the body. Without this the inspector
    /// shows a fragment indistinguishable from a complete small response, which is worse than
    /// showing nothing -- someone would draw conclusions from a body that was never all there.
    /// </summary>
    private static string PartialCaptureNote(HttpMessage message) =>
        message.IsBodyComplete
            ? string.Empty
            : Strings.Inspector.BodyNotFullyCaptured(message.Body.LongLength, message.BodyTotalLength);

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
        CancelJsonWork();
        _jsonTree.BeginUpdate();
        try
        {
            _jsonTree.Nodes.Clear();
            _jsonTypeNotice.Visible = false;
            _jsonForcePanel.Visible = false;
            _jsonTypeStatus.Text = string.Empty;

            if (message.Body.Length == 0)
            {
                _jsonTree.Nodes.Add(Strings.Inspector.NoBody);
                return;
            }

            if (!force && !IsJsonContentType(message.ContentType))
            {
                _jsonTypeStatus.Text = Strings.Inspector.NotAJsonContentType;
                _jsonForcePanel.Visible = true;
                _jsonTypeNotice.Visible = true;
                _jsonTree.Nodes.Add(Strings.Inspector.JsonSkipped);
                return;
            }

            _jsonTree.Nodes.Add(Strings.Inspector.JsonBuilding);
        }
        finally
        {
            _jsonTree.EndUpdate();
        }

        // Decoding, parsing and searching a body the sender chose the size of happen off the UI thread.
        // The result is shown only if this is still the tree wanted when it arrives.
        var build = _jsonBuild = new CancellationTokenSource();
        _ = ShowJsonAsync(message, force, build);
    }

    private async Task ShowJsonAsync(HttpMessage message, bool force, CancellationTokenSource build)
    {
        JsonTreeSource? source = null;
        string? failure = null;
        try
        {
            await JsonBuildGate.WaitAsync(build.Token);
            try
            {
                (source, failure) = await Task.Run(() => BuildJsonTree(message, force, build.Token), build.Token);
            }
            finally
            {
                JsonBuildGate.Release();
            }
        }
        catch (OperationCanceledException) when (build.IsCancellationRequested)
        {
            build.Dispose();
            return; // another message was selected, or the view was cleared
        }
        catch (Exception ex)
        {
            failure = Strings.Inspector.NotValidJson(ex.Message);
        }

        build.Dispose();
        if (!ReferenceEquals(_jsonBuild, build) || IsDisposed)
        {
            source?.Retire();
            return;
        }

        _jsonBuild = null;
        _jsonTree.BeginUpdate();
        try
        {
            _jsonTree.Nodes.Clear();
            if (source?.Root() is { } root)
            {
                _jsonSource = source;
                var node = CreateJsonNode(source, root);
                _jsonTree.Nodes.Add(node);
                // Keep the top-level shape visible, while avoiding an overwhelming expansion
                // of every nested object and array in large responses.
                ShowJsonChildren(node);
                node.Expand();
            }
            else
            {
                _jsonTree.Nodes.Add(failure ?? Strings.Inspector.NotValidJson(string.Empty));
            }
        }
        finally
        {
            _jsonTree.EndUpdate();
        }

        FindJsonMatches();
    }

    // The worker half: a tree to show, or the text that says why there is none.
    private static (JsonTreeSource? Source, string? Failure) BuildJsonTree(HttpMessage message, bool force, CancellationToken cancellationToken)
    {
        try
        {
            var decodedBody = message.DecodedBody;
            cancellationToken.ThrowIfCancellationRequested();
            if (!force && !ContentCodec.LooksTextual(message.ContentType, decodedBody))
                return (null, Strings.Inspector.BinaryJsonBody(message.Body.Length, message.ContentType));

            var limits = JsonTreeLimits.Default;
            if (decodedBody.Length > limits.MaxChars)
                return (null, Strings.Inspector.JsonTooLarge(limits.MaxChars / (1024 * 1024)));

            var source = JsonTreeSource.Parse(message.BodyAsText(decodedBody), limits);
            if (cancellationToken.IsCancellationRequested)
            {
                source.Retire();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return (source, null);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            return (null, Strings.Inspector.NotValidJson(ex.Message));
        }
    }

    // Stops the build and search of the tree on show and lets go of its document.
    private void CancelJsonWork()
    {
        var build = _jsonBuild;
        _jsonBuild = null;
        build?.Cancel();
        var search = _jsonSearching;
        _jsonSearching = null;
        search?.Cancel();
        _jsonMatches.Clear();
        _jsonMatchIndex = -1;
        _jsonSource?.Retire();
        _jsonSource = null;
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

    // A node that can expand starts with one placeholder child, so the tree draws its expander;
    // ShowJsonChildren swaps in the real children the first time it opens.
    private static TreeNode CreateJsonNode(JsonTreeSource source, JsonTreeChild child)
    {
        var node = new TreeNode(child.Text) { Tag = new JsonNodeRef(source, child.Name, child.Value) };
        if (child.HasChildren) node.Nodes.Add(new TreeNode { Tag = JsonPlaceholder });
        return node;
    }

    private static void ShowJsonChildren(TreeNode node)
    {
        if (node.Nodes is not [{ Tag: var placeholder }] || !ReferenceEquals(placeholder, JsonPlaceholder)
            || node.Tag is not JsonNodeRef parent
            || parent.Source.Children(parent.Value) is not { } level) return;

        var children = new TreeNode[level.Children.Count + (level.Omitted > 0 ? 1 : 0)];
        for (var i = 0; i < level.Children.Count; i++) children[i] = CreateJsonNode(parent.Source, level.Children[i]);
        if (level.Omitted > 0) children[^1] = new TreeNode(Strings.Inspector.JsonOmitted(level.Omitted));
        node.Nodes.Clear();
        node.Nodes.AddRange(children);
    }

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
                _hexSearchStatus.Text = Strings.Inspector.InvalidHex;
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
        _hexSearchStatus.Text = position < 0 ? Strings.Inspector.NotFound : Strings.Inspector.FoundAt(position);
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

    // Looks through the whole document, not only the nodes made so far, on a worker thread: each
    // keystroke replaces the search before it. Only what the tree can show is searched.
    private void FindJsonMatches()
    {
        var previous = _jsonSearching;
        _jsonSearching = null;
        previous?.Cancel();
        _jsonMatches.Clear();
        _jsonMatchIndex = -1;

        var query = _jsonSearch.Text.Trim();
        if (query.Length == 0 || _jsonSource is not { } source)
        {
            _jsonMatchCount.Text = string.Empty;
            return;
        }

        var search = _jsonSearching = new CancellationTokenSource();
        _ = SearchJsonAsync(source, query, search);
    }

    private async Task SearchJsonAsync(JsonTreeSource source, string query, CancellationTokenSource search)
    {
        try
        {
            var matches = await Task.Run(() => source.Search(query, search.Token), search.Token);
            if (search.IsCancellationRequested || matches is null || IsDisposed) return;

            _jsonMatches.AddRange(matches);
            // The singular is the catalogue's _one form now, so the count is all this has to pass.
            _jsonMatchCount.Text = Strings.Inspector.MatchCount(_jsonMatches.Count);
            if (_jsonMatches.Count > 0) SelectJsonMatch(1);
        }
        catch (OperationCanceledException) when (search.IsCancellationRequested)
        {
            // A newer search, another message or the view closing took over.
        }
        finally
        {
            if (ReferenceEquals(_jsonSearching, search)) _jsonSearching = null;
            search.Dispose();
        }
    }

    private void SelectJsonMatch(int direction)
    {
        if (_jsonMatches.Count == 0) return;

        _jsonMatchIndex = (_jsonMatchIndex + direction + _jsonMatches.Count) % _jsonMatches.Count;
        if (JsonNodeAt(_jsonMatches[_jsonMatchIndex]) is not { } match) return;
        _jsonTree.SelectedNode = match;
        match.EnsureVisible();
        _jsonMatchCount.Text = Strings.Inspector.MatchPosition(_jsonMatchIndex + 1, _jsonMatches.Count);
    }

    // Opens the way down the path, making the nodes on it. A node the tree cannot show (past its caps)
    // ends the walk, and the deepest node that is shown stands in for the match.
    private TreeNode? JsonNodeAt(int[] path)
    {
        if (_jsonTree.Nodes is not [{ Tag: JsonNodeRef } node, ..]) return null;

        foreach (var index in path)
        {
            ShowJsonChildren(node);
            node.Expand();
            if (index >= node.Nodes.Count || node.Nodes[index].Tag is not JsonNodeRef) break;
            node = node.Nodes[index];
        }

        return node;
    }

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
            _tabs.SelectWithoutFocus(0);
            return;
        }

        var contentType = _message.ContentType ?? string.Empty;
        if (_showImageViewer && contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            _tabs.SelectWithoutFocus(5);
            return;
        }

        if (_showImageViewer && contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            _tabs.SelectWithoutFocus(6);
            return;
        }

        if (IsJsonContentType(contentType))
        {
            _tabs.SelectWithoutFocus(4);
            return;
        }

        // Response inspection should start at headers unless its content advertises one of
        // the specialised viewers above. The body/raw/hex views remain available explicitly.
        _tabs.SelectWithoutFocus(0);
    }

    private static bool IsJsonContentType(string? contentType) => contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
}
