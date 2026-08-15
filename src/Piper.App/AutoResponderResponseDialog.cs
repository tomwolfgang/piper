using System.Text.Json.Nodes;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Http;

namespace Piper.App;

/// <summary>
/// Edits the response an AutoResponder rule serves, split into the status and headers above and the
/// body below.
/// </summary>
/// <remarks>
/// The body can be edited as text or, when it parses, as a JSON tree whose properties and values are
/// editable in place. That tree is the point of this dialog: stubbing an API response should not
/// mean copying a payload into another editor and pasting it back.
///
/// The text box stays the single source of truth. The tree writes through to it on every change, so
/// the two views can never drift apart and Save only ever reads one of them.
/// </remarks>
public sealed class AutoResponderResponseDialog : Form
{
    private readonly NumericUpDown _status;
    private readonly TextBox _reason;
    private readonly TextBox _headers;
    private readonly TextBox _body;
    private readonly DarkTabControl _bodyViews;
    private readonly TreeView _tree;
    private readonly TextBox _propertyName;
    private readonly TextBox _propertyValue;
    private readonly Label _jsonStatus;
    private readonly SplitContainer _split;

    private JsonNode? _root;
    private bool _applyingEdit;

    public AutoResponderResponseDialog(string ruleDescription, string rawResponse)
    {
        Text = "Edit Response";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(720, 560);
        ClientSize = new Size(900, 720);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;

        HttpWireFormat.TryParseResponse(System.Text.Encoding.Latin1.GetBytes(rawResponse), out var parsed, out _);

        // ----------------------------------------------------------------- status

        _reason = new TextBox { Dock = DockStyle.Fill, Font = Palette.Mono, Text = parsed.ReasonPhrase };

        _status = new NumericUpDown
        {
            Minimum = 100,
            Maximum = 599,
            Value = Math.Clamp(parsed.StatusCode, 100, 599),
            Width = 70,
            Dock = DockStyle.Left,
            Font = Palette.Mono,
        };

        // Changing the code fills in the matching phrase, which is what anyone typing 503 wants.
        _status.ValueChanged += (_, _) => _reason.Text = ReasonPhrases.ForOrClass((int)_status.Value);

        var statusRow = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(0, 2, 0, 2) };
        statusRow.Controls.Add(_reason);
        statusRow.Controls.Add(_status);
        statusRow.Controls.Add(new Label { Text = "Status", Dock = DockStyle.Left, Width = 70, Padding = new Padding(0, 5, 0, 0) });

        _headers = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = Palette.Mono,
            Text = parsed.Headers.ToRawString().TrimEnd('\r', '\n'),
        };

        var headerPane = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 0) };
        headerPane.Controls.Add(_headers);
        headerPane.Controls.Add(statusRow);
        headerPane.Controls.Add(new Label
        {
            Text = "Status and headers",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Palette.TextDim,
        });

        // ------------------------------------------------------------------- body

        _body = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            AcceptsReturn = true,
            AcceptsTab = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = Palette.Mono,
            Text = parsed.BodyAsText(),
        };

        _tree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, Font = Palette.Mono, BorderStyle = BorderStyle.None };
        _tree.AfterSelect += (_, _) => LoadSelectedNode();

        _propertyName = new TextBox { Dock = DockStyle.Fill, Font = Palette.Mono, Enabled = false };
        _propertyValue = new TextBox { Dock = DockStyle.Fill, Font = Palette.Mono, Enabled = false };
        _propertyName.Leave += (_, _) => ApplyName();
        _propertyValue.Leave += (_, _) => ApplyValue();
        _propertyName.KeyDown += (_, e) => CommitOnEnter(e, ApplyName);
        _propertyValue.KeyDown += (_, e) => CommitOnEnter(e, ApplyValue);

        _jsonStatus = new Label { Dock = DockStyle.Bottom, Height = 20, ForeColor = Palette.TextDim };

        var editRow = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(0, 2, 0, 2) };
        editRow.Controls.Add(EditorRow("Value", _propertyValue));
        editRow.Controls.Add(EditorRow("Property", _propertyName));

        var jsonPage = new Panel { Dock = DockStyle.Fill };
        jsonPage.Controls.Add(_tree);
        jsonPage.Controls.Add(editRow);
        jsonPage.Controls.Add(_jsonStatus);

        _bodyViews = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        var textPage = new TabPage("Text");
        textPage.Controls.Add(_body);
        var treePage = new TabPage("JSON");
        treePage.Controls.Add(jsonPage);
        _bodyViews.TabPages.Add(textPage);
        _bodyViews.TabPages.Add(treePage);
        _bodyViews.SelectedIndexChanged += (_, _) => { if (_bodyViews.SelectedIndex == 1) RebuildTree(); };

        var bodyPane = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 2, 0, 0) };
        bodyPane.Controls.Add(_bodyViews);
        bodyPane.Controls.Add(new Label
        {
            Text = "Body",
            Dock = DockStyle.Top,
            Height = 22,
            ForeColor = Palette.TextDim,
        });

        // Minimum sizes and the splitter position are all applied in OnShown: every one of them
        // re-validates against the control's current height, and a SplitContainer is only 100px tall
        // until it has been laid out.
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 4,
        };
        _split.Panel1.Controls.Add(headerPane);
        _split.Panel2.Controls.Add(bodyPane);

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 0) };
        body.Controls.Add(_split);

        // ----------------------------------------------------------------- chrome

        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(12, 9, 8, 0),
            Text = ruleDescription,
            AutoEllipsis = true,
        };

        var save = new Button { Text = "Save", Size = new Size(100, 34) };
        save.Click += (_, _) => Commit();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Size = new Size(100, 34) };

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 68, Padding = new Padding(12, 12, 12, 10) };
        footer.Paint += DrawFooterBorder;
        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 216,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
        };
        actions.Controls.Add(cancel);
        actions.Controls.Add(save);
        footer.Controls.Add(actions);

        Controls.Add(body);
        Controls.Add(heading);
        Controls.Add(footer);
        CancelButton = cancel;
        Palette.Apply(this);
    }

    /// <summary>The edited response, validated and re-framed. Only set once Save succeeds.</summary>
    public byte[] RawResponse { get; private set; } = [];

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        if (_split.Height < 400) return; // too small to be worth constraining

        _split.Panel1MinSize = 120;
        _split.Panel2MinSize = 200;
        _split.SplitterDistance = 190;
    }

    // --------------------------------------------------------------- JSON editing

    private void RebuildTree()
    {
        _tree.BeginUpdate();
        try
        {
            _tree.Nodes.Clear();

            if (JsonEditing.TryParse(_body.Text, out _root, out var error))
            {
                _jsonStatus.Text = "Select a property to edit its name or value.";
                var root = new TreeNode(JsonEditing.Describe("root", _root)) { Tag = new Slot(null, null, -1) };
                Populate(root, _root);
                _tree.Nodes.Add(root);
                root.Expand();
            }
            else
            {
                _jsonStatus.Text = $"Not JSON: {error}  -  edit it on the Text tab.";
            }
        }
        finally
        {
            _tree.EndUpdate();
        }

        // Sets the two boxes from whatever is now selected. Only reached on a tab switch, so it can
        // never disable a box the user is typing in - which would bounce focus to the top of the form.
        LoadSelectedNode();
    }

    /// <summary>
    /// Where a value lives, rather than the value itself.
    /// </summary>
    /// <remarks>
    /// Holding the container and key means a property whose value is null is still addressable -
    /// a null has no node to point at, so a tree keyed on nodes cannot edit one back into a value.
    /// </remarks>
    private sealed record Slot(JsonNode? Container, string? Name, int Index)
    {
        public string Label => Name ?? (Container is null ? "root" : $"[{Index}]");

        public JsonNode? Value => Container switch
        {
            JsonObject o when Name is not null => o[Name],
            JsonArray a when Index >= 0 && Index < a.Count => a[Index],
            _ => null,
        };
    }

    private void Populate(TreeNode parent, JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, child) in obj)
                {
                    var item = new TreeNode(JsonEditing.Describe(name, child)) { Tag = new Slot(obj, name, -1) };
                    parent.Nodes.Add(item);
                    Populate(item, child);
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var item = new TreeNode(JsonEditing.Describe($"[{i}]", array[i])) { Tag = new Slot(array, null, i) };
                    parent.Nodes.Add(item);
                    Populate(item, array[i]);
                }
                break;
        }
    }

    private void LoadSelectedNode()
    {
        _applyingEdit = true;
        try
        {
            var slot = _tree.SelectedNode?.Tag as Slot;
            var isProperty = slot is { Container: JsonObject, Name: not null };

            _propertyName.Enabled = isProperty;
            _propertyValue.Enabled = slot is not null && JsonEditing.IsLeaf(slot.Value);

            _propertyName.Text = isProperty ? slot!.Name! : string.Empty;
            _propertyValue.Text = _propertyValue.Enabled ? JsonEditing.EditableText(slot!.Value) : string.Empty;
        }
        finally
        {
            _applyingEdit = false;
        }
    }

    private void ApplyName()
    {
        if (_applyingEdit || !_propertyName.Enabled) return;
        if (_tree.SelectedNode is not { Tag: Slot { Container: JsonObject parent, Name: { } oldName } slot } selected) return;

        var newName = _propertyName.Text;
        if (oldName == newName) return;

        if (!JsonEditing.TryRenameProperty(parent, oldName, newName, out var error))
        {
            _jsonStatus.Text = error;
            LoadSelectedNode();
            return;
        }

        // Retag and relabel this node only. Rebuilding the tree would collapse every branch the
        // user had opened and, by disabling these boxes, throw focus back to the top of the dialog.
        // Only this node's slot changes. The rename rebuilds the parent object but re-adds the very
        // same child instances, so every descendant's container is still the object it was.
        var moved = slot with { Name = newName };
        selected.Tag = moved;
        selected.Text = JsonEditing.Describe(newName, moved.Value);
        SyncBodyText($"Renamed to '{newName}'.");
    }

    private void ApplyValue()
    {
        if (_applyingEdit || !_propertyValue.Enabled) return;
        if (_tree.SelectedNode is not { Tag: Slot slot } selected) return;

        var current = slot.Value;
        if (JsonEditing.EditableText(current) == _propertyValue.Text) return;

        var replacement = JsonEditing.ValueFrom(current, _propertyValue.Text);
        switch (slot.Container)
        {
            case JsonObject parent when slot.Name is not null:
                parent[slot.Name] = replacement;
                break;

            case JsonArray parent when slot.Index >= 0 && slot.Index < parent.Count:
                parent[slot.Index] = replacement;
                break;

            default:
                _jsonStatus.Text = "The whole document can only be replaced from the Text tab.";
                return;
        }

        selected.Text = JsonEditing.Describe(slot.Label, replacement);
        SyncBodyText($"{slot.Label} updated.");
    }

    /// <summary>Pushes the edited tree into the body text, which is what Save reads.</summary>
    private void SyncBodyText(string status)
    {
        _body.Text = JsonEditing.Serialize(_root);
        _jsonStatus.Text = status;
    }

    private void CommitOnEnter(KeyEventArgs e, Action apply)
    {
        if (e.KeyCode != Keys.Enter) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        apply();
    }

    // -------------------------------------------------------------------- saving

    private void Commit()
    {
        // Whichever tab is showing, the text box holds the body: the tree writes through to it.
        var reason = _reason.Text.Trim();
        var head = $"HTTP/1.1 {(int)_status.Value} {reason}".TrimEnd();
        var headers = _headers.Text.Trim('\r', '\n');
        var raw = headers.Length > 0
            ? $"{head}\r\n{headers}\r\n\r\n{_body.Text}"
            : $"{head}\r\n\r\n{_body.Text}";

        if (!HttpWireFormat.TryParseEditedResponse(raw, out var bytes, out var error))
        {
            MessageBox.Show(this,
                $"That is not a complete HTTP response: {error}.\r\n\r\n"
                + "Check the header lines - each should read \"Name: value\".",
                "Edit Response", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        RawResponse = bytes;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static Panel EditorRow(string label, Control editor)
    {
        var row = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(0, 2, 0, 2) };
        row.Controls.Add(editor);
        row.Controls.Add(new Label { Text = label, Dock = DockStyle.Left, Width = 70, Padding = new Padding(0, 5, 0, 0) });
        return row;
    }

    private static void DrawFooterBorder(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(Palette.Border);
        e.Graphics.DrawLine(pen, 0, 0, e.ClipRectangle.Width, 0);
    }
}
