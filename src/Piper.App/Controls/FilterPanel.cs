using System.Text.Json;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;

namespace Piper.App.Controls;

/// <summary>
/// A staged filter editor with a Hosts allow/deny list and Response Status Code toggles.
/// Changes remain in the panel until the user explicitly runs the filterset from Actions.
/// </summary>
public sealed class FilterPanel : UserControl
{
    private readonly CheckBox _useFilters;
    private readonly ComboBox _hostsMode;
    private readonly TextBox _hostEntry;
    private readonly CheckedListBox _hostsList;
    private readonly CheckBox _hideSuccess;
    private readonly CheckBox _hideNonSuccess;
    private readonly CheckBox _hideRedirects;
    private readonly CheckBox _hideAuthDemands;
    private readonly CheckBox _hideNotModified;
    private bool _applyingSettings;

    /// <summary>Raised only when an Actions command applies or clears the current filterset.</summary>
    public event EventHandler<string>? FilterChanged;

    /// <summary>Raised after the staged controls change, so their settings can be persisted.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>Captures the current controls in the same format used by filterset files.</summary>
    public FilterSettings Settings
    {
        get
        {
            var hosts = HostEntries().ToArray();
            return new FilterSettings
            {
                UseFilters = _useFilters.Checked,
                HostsMode = _hostsMode.SelectedIndex,
                // Keep the legacy text form populated so filtersets still work in earlier Piper
                // versions. The Hosts collection below preserves each checkbox state.
                HostsText = string.Join("; ", hosts.Select(host => host.Pattern)),
                Hosts = hosts.ToList(),
                HideSuccess = _hideSuccess.Checked,
                HideNonSuccess = _hideNonSuccess.Checked,
                HideRedirects = _hideRedirects.Checked,
                HideAuthDemands = _hideAuthDemands.Checked,
                HideNotModified = _hideNotModified.Checked,
            };
        }
    }

    public FilterPanel()
    {
        _useFilters = new CheckBox
        {
            Dock = DockStyle.Fill,
            Text = "Use Filters when run",
            AutoSize = false,
            Padding = new Padding(6, 10, 0, 0),
            Font = new Font(Palette.UiFont, FontStyle.Bold),
        };
        _useFilters.CheckedChanged += (_, _) => OnCriteriaChanged();

        // ---------------------------------------------------------------- Hosts

        _hostsMode = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Palette.UiFont,
        };
        _hostsMode.Items.AddRange(["Show only the following Hosts", "Hide the following Hosts"]);
        _hostsMode.SelectedIndex = 0;
        _hostsMode.SelectedIndexChanged += (_, _) => OnCriteriaChanged();

        _hostEntry = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = Palette.Mono,
            PlaceholderText = "Host pattern, e.g. *.curseforge.com",
        };
        _hostEntry.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            AddHosts();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };

        var addHost = new Button { Dock = DockStyle.Right, Text = "Add", Width = 70 };
        addHost.Click += (_, _) => AddHosts();
        var addHostRow = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 2, 0, 2) };
        addHostRow.Controls.Add(_hostEntry);
        addHostRow.Controls.Add(addHost);

        _hostsList = new CheckedListBox
        {
            Dock = DockStyle.Fill,
            CheckOnClick = true,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Palette.Mono,
            IntegralHeight = false,
        };
        // ItemCheck is raised before the checked state changes. Schedule persistence for after
        // the native control commits the new state, without applying the filterset.
        _hostsList.ItemCheck += (_, _) =>
        {
            if (!_applyingSettings && IsHandleCreated)
                BeginInvoke((MethodInvoker)OnCriteriaChanged);
        };
        _hostsList.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Delete) return;
            RemoveSelectedHost();
            e.Handled = true;
        };

        var removeHost = new Button { Dock = DockStyle.Right, Text = "Remove selected", Width = 130 };
        removeHost.Click += (_, _) => RemoveSelectedHost();
        var removeHostRow = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 2, 0, 2) };
        removeHostRow.Controls.Add(removeHost);

        var hostsGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "Hosts",
            Padding = new Padding(8, 4, 8, 8),
            Font = Palette.UiFont,
        };
        hostsGroup.Controls.Add(_hostsList);
        hostsGroup.Controls.Add(removeHostRow);
        hostsGroup.Controls.Add(addHostRow);
        hostsGroup.Controls.Add(_hostsMode);

        // ------------------------------------------------------ Response Status Code

        _hideSuccess = NewCheck("Hide success (2xx)");
        _hideNonSuccess = NewCheck("Hide non-2xx");
        _hideRedirects = NewCheck("Hide redirects (300-303, 307)");
        _hideAuthDemands = NewCheck("Hide Authentication demands (401, 407)");
        _hideNotModified = NewCheck("Hide Not Modified (304)");
        foreach (var check in new[] { _hideSuccess, _hideNonSuccess, _hideRedirects, _hideAuthDemands, _hideNotModified })
            check.CheckedChanged += (_, _) => OnCriteriaChanged();

        var statusStack = new Panel { Dock = DockStyle.Fill };
        statusStack.Controls.Add(_hideNotModified);
        statusStack.Controls.Add(_hideAuthDemands);
        statusStack.Controls.Add(_hideRedirects);
        statusStack.Controls.Add(_hideNonSuccess);
        statusStack.Controls.Add(_hideSuccess);

        var statusGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            Text = "Response Status Code",
            Height = 210,
            Padding = new Padding(8, 4, 8, 8),
            Font = Palette.UiFont,
        };
        statusGroup.Controls.Add(statusStack);

        // -------------------------------------------------------------- Actions

        var actions = new ToolStripDropDownButton("Actions") { DisplayStyle = ToolStripItemDisplayStyle.Text };
        actions.DropDownItems.Add("Run Filterset now", null, (_, _) => RunFiltersetNow());
        actions.DropDownItems.Add("Show all sessions", null, (_, _) => ShowAllSessions());
        actions.DropDownItems.Add(new ToolStripSeparator());
        actions.DropDownItems.Add("Load Filterset...", null, (_, _) => LoadFilterset());
        actions.DropDownItems.Add("Save Filterset...", null, (_, _) => SaveFilterset());
        actions.DropDownItems.Add(new ToolStripSeparator());
        actions.DropDownItems.Add("Help", null, (_, _) => ShowHelp());

        var actionsBar = new ToolStrip
        {
            Dock = DockStyle.Right,
            AutoSize = false,
            Width = 86,
            GripStyle = ToolStripGripStyle.Hidden,
            Font = Palette.UiFont,
        };
        actionsBar.Items.Add(actions);

        var filterHeader = new Panel { Dock = DockStyle.Top, Height = 38 };
        filterHeader.Controls.Add(_useFilters);
        filterHeader.Controls.Add(actionsBar);

        // Hosts fills all available space so its checkbox list grows with the Filters tab.
        Controls.Add(hostsGroup);
        Controls.Add(statusGroup);
        Controls.Add(filterHeader);
    }

    private static CheckBox NewCheck(string text) => new()
    {
        Dock = DockStyle.Top,
        Text = text,
        AutoSize = false,
        Height = 32,
        Padding = new Padding(4, 7, 0, 0),
    };

    // ------------------------------------------------------------------ actions

    private void RunFiltersetNow()
    {
        // Adding hosts should make the expected path simple: Actions > Run activates the staged
        // filterset even when the user has not separately ticked the global checkbox.
        _useFilters.Checked = true;
        ApplyFilterset();
    }

    private void ShowAllSessions()
    {
        // Preserve every host and status choice for a later run; only the applied query is
        // cleared. This makes it safe to temporarily inspect the full capture list.
        _useFilters.Checked = false;
        ApplyFilterset();
    }

    private void LoadFilterset()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Load Filterset",
            Filter = "Filterset (*.json)|*.json|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var settings = JsonSerializer.Deserialize<FilterSettings>(json);
            if (settings is not null) ApplySettings(settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not load the filterset: {ex.Message}",
                "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveFilterset()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Save Filterset",
            Filter = "Filterset (*.json)|*.json|All files (*.*)|*.*",
            FileName = "Filterset.json",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save the filterset: {ex.Message}",
                "Piper", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowHelp() => MessageBox.Show(this,
        "Add one or more host patterns, then use their checkboxes to choose which ones apply. "
        + "Changes stay staged until Actions > Run Filterset now. Actions > Show all sessions "
        + "removes the applied filter without discarding the filterset.\r\n\r\n"
        + "Right-clicking a session and choosing \"Hide this host\" adds it here, so the choice "
        + "is remembered. Like the rest of the filterset it only applies once \"Use Filters\" is "
        + "on; untick or remove the entry to undo it.\r\n\r\n"
        + "Filters compose into the same query grammar as the session grid's own filter box, "
        + "so running a filterset overwrites anything typed there by hand.\r\n\r\n"
        + "Only Hosts and Response Status Code filters are implemented.",
        "Filters", MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>Applies the currently staged filterset; this is intentionally Actions-only.</summary>
    private void ApplyFilterset() =>
        FilterChanged?.Invoke(this, _useFilters.Checked ? ComposeQuery() : string.Empty);

    /// <summary>Applies restored settings at startup without changing their enabled state.</summary>
    public void ApplyCurrentFilterset() => ApplyFilterset();

    /// <summary>Updates every filter control without applying it to the session grid.</summary>
    public void ApplySettings(FilterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _applyingSettings = true;
        try
        {
            _hostsMode.SelectedIndex = settings.HostsMode is 0 or 1 ? settings.HostsMode : 0;
            _hostsList.Items.Clear();
            var savedHosts = settings.Hosts ?? [];
            var hosts = savedHosts.Count > 0
                ? savedHosts
                : HostFilterTerm.Split(settings.HostsText).Select(pattern => new HostFilterEntry { Pattern = pattern }).ToList();
            foreach (var host in hosts.Where(host => !string.IsNullOrWhiteSpace(host.Pattern)))
                _hostsList.Items.Add(host.Pattern.Trim(), host.Enabled);

            _hideSuccess.Checked = settings.HideSuccess;
            _hideNonSuccess.Checked = settings.HideNonSuccess;
            _hideRedirects.Checked = settings.HideRedirects;
            _hideAuthDemands.Checked = settings.HideAuthDemands;
            _hideNotModified.Checked = settings.HideNotModified;
            _useFilters.Checked = settings.UseFilters;
        }
        finally
        {
            _applyingSettings = false;
        }

        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Records an edit without changing the capture-list filter.</summary>
    private void OnCriteriaChanged()
    {
        if (!_applyingSettings) SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddHosts()
    {
        var patterns = HostFilterTerm.Split(_hostEntry.Text);
        if (patterns.Count == 0) return;

        var existing = HostEntries().Select(host => host.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns)
        {
            if (existing.Add(pattern)) _hostsList.Items.Add(pattern, isChecked: true);
        }
        _hostEntry.Clear();
        OnCriteriaChanged();
    }

    private void RemoveSelectedHost()
    {
        if (_hostsList.SelectedIndex < 0) return;
        _hostsList.Items.RemoveAt(_hostsList.SelectedIndex);
        OnCriteriaChanged();
    }

    // --------------------------------------------------------------- composition

    private IEnumerable<HostFilterEntry> HostEntries()
    {
        for (var index = 0; index < _hostsList.Items.Count; index++)
        {
            if (_hostsList.Items[index] is not string pattern) continue;
            yield return new HostFilterEntry
            {
                Pattern = pattern,
                Enabled = _hostsList.GetItemChecked(index),
            };
        }
    }

    private string ComposeQuery()
    {
        var terms = new List<string>();

        var hostsTerm = ComposeHostsTerm();
        if (hostsTerm.Length > 0) terms.Add(hostsTerm);

        if (_hideSuccess.Checked) terms.Add("-status:200..299");
        if (_hideNonSuccess.Checked) terms.Add("status:200..299");
        if (_hideRedirects.Checked) terms.Add("-status:300..303 -status:307");
        if (_hideAuthDemands.Checked) terms.Add("-status:401 -status:407");
        if (_hideNotModified.Checked) terms.Add("-status:304");

        return string.Join(' ', terms);
    }

    private string ComposeHostsTerm() => HostFilterTerm.Compose(
        string.Join(';', HostEntries().Where(host => host.Enabled).Select(host => host.Pattern)),
        hide: _hostsMode.SelectedIndex == 1);
}
