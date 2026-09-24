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
            Text = Strings.Filters.UseFilters,
            AutoSize = false,
            Padding = new Padding(6, 10, 0, 0),
            Font = Palette.UiFontBold,
        };
        // The global switch is live in both directions, matching Fiddler: unchecking stops
        // filtering at once (leaving a filterset applied after the user disables it keeps
        // SessionStore discarding non-matching completed sessions, which is unrecoverable), and
        // checking runs the staged criteria the same way Actions > Run Filterset now does. The
        // criteria themselves stay staged, so a half-typed host pattern is never applied on its own.
        // MainForm's FilterChanged handler persists Settings, so this must not also raise
        // SettingsChanged or every toggle would save twice.
        _useFilters.CheckedChanged += (_, _) =>
        {
            if (!_applyingSettings) ApplyFilterset();
        };

        // ---------------------------------------------------------------- Hosts

        _hostsMode = new ComboBox
        {
            Dock = DockStyle.Top,
            DropDownStyle = ComboBoxStyle.DropDownList,
            Font = Palette.UiFont,
        };
        _hostsMode.Items.AddRange([Strings.Filters.ShowOnlyTheseHosts, Strings.Filters.HideTheseHosts]);
        _hostsMode.SelectedIndex = 0;
        _hostsMode.SelectedIndexChanged += (_, _) => OnCriteriaChanged();

        _hostEntry = new TextBox
        {
            Dock = DockStyle.Fill,
            Font = Palette.Mono,
            PlaceholderText = Strings.Filters.HostPlaceholder,
        };
        _hostEntry.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            AddHosts();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };

        var addHost = new Button { Dock = DockStyle.Right, Text = Strings.Filters.AddHost, Width = 70 };
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

        var removeHost = new Button { Dock = DockStyle.Right, Text = Strings.Filters.RemoveSelectedHost, Width = 130 };
        removeHost.Click += (_, _) => RemoveSelectedHost();
        var removeHostRow = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 2, 0, 2) };
        removeHostRow.Controls.Add(removeHost);

        var hostsGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = Strings.Filters.HostsGroup,
            Padding = new Padding(8, 4, 8, 8),
            Font = Palette.UiFont,
        };
        hostsGroup.Controls.Add(_hostsList);
        hostsGroup.Controls.Add(removeHostRow);
        hostsGroup.Controls.Add(addHostRow);
        hostsGroup.Controls.Add(_hostsMode);

        // ------------------------------------------------------ Response Status Code

        _hideSuccess = NewCheck(Strings.Filters.HideSuccess);
        _hideNonSuccess = NewCheck(Strings.Filters.HideNonSuccess);
        _hideRedirects = NewCheck(Strings.Filters.HideRedirects);
        _hideAuthDemands = NewCheck(Strings.Filters.HideAuthDemands);
        _hideNotModified = NewCheck(Strings.Filters.HideNotModified);
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
            Text = Strings.Filters.StatusGroup,
            Height = 210,
            Padding = new Padding(8, 4, 8, 8),
            Font = Palette.UiFont,
        };
        statusGroup.Controls.Add(statusStack);

        // -------------------------------------------------------------- Actions

        var actions = new ToolStripDropDownButton(Strings.Filters.Actions) { DisplayStyle = ToolStripItemDisplayStyle.Text };
        actions.DropDownItems.Add(Strings.Filters.RunFiltersetNow, null, (_, _) => RunFiltersetNow());
        actions.DropDownItems.Add(Strings.Filters.ShowAllSessions, null, (_, _) => ShowAllSessions());
        actions.DropDownItems.Add(new ToolStripSeparator());
        actions.DropDownItems.Add(Strings.Filters.LoadFilterset, null, (_, _) => LoadFilterset());
        actions.DropDownItems.Add(Strings.Filters.SaveFilterset, null, (_, _) => SaveFilterset());
        actions.DropDownItems.Add(new ToolStripSeparator());
        actions.DropDownItems.Add(Strings.Filters.HelpMenu, null, (_, _) => ShowHelp());

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
        SetUseFilters(true);
    }

    /// <summary>
    /// Moves the switch to <paramref name="useFilters"/> and applies the filterset exactly once.
    /// Assigning the checkbox raises CheckedChanged, which applies on its own, so an unconditional
    /// apply here would refilter the whole grid and rewrite the settings file twice per command.
    /// A no-op assignment raises nothing, so that case still has to apply explicitly.
    /// </summary>
    private void SetUseFilters(bool useFilters)
    {
        if (_useFilters.Checked == useFilters) ApplyFilterset();
        else _useFilters.Checked = useFilters;
    }

    private void ShowAllSessions()
    {
        // Preserve every host and status choice for a later run; only the applied query is
        // cleared. This makes it safe to temporarily inspect the full capture list.
        SetUseFilters(false);
    }

    private void LoadFilterset()
    {
        using var dialog = new OpenFileDialog
        {
            Title = Strings.Filters.LoadCaption,
            Filter = Strings.Filters.FilterSetFilter,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            var json = File.ReadAllText(dialog.FileName);
            var settings = JsonSerializer.Deserialize<FilterSettings>(json);
            if (settings is null) return;

            // A filterset carries its own enabled state, so applying it is part of loading it.
            // Staging only would leave the previous query on SessionStore.CompletedSessionFilter:
            // the panel would show the loaded criteria while capture kept dropping traffic by the
            // old ones, and a filterset saved with Use Filters off would not stop the filtering it
            // is meant to describe. Loading from Actions is an explicit user action, like the
            // switch itself, so it applies rather than staging.
            ApplySettings(settings);
            ApplyFilterset();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Strings.Filters.LoadFailed(ex.Message),
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void SaveFilterset()
    {
        using var dialog = new SaveFileDialog
        {
            Title = Strings.Filters.SaveCaption,
            Filter = Strings.Filters.FilterSetFilter,
            FileName = Strings.Filters.SaveFileName,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Strings.Filters.SaveFailed(ex.Message),
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowHelp() => MessageBox.Show(this, Strings.Filters.HelpBody,
        Strings.Filters.HelpCaption, MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>
    /// Applies the staged filterset, or clears the applied query when Use Filters is off.
    /// Reached from the Actions commands, from the Use Filters switch, and once at startup.
    /// </summary>
    private void ApplyFilterset() => FilterChanged?.Invoke(this, FilterQuery.Compose(Settings));

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
            // A hand-edited or truncated file can carry null entries and blank patterns, the same
            // hazard FilterSettings.HideHost guards against, so skip them rather than dereference.
            foreach (var host in hosts.Where(host => !string.IsNullOrWhiteSpace(host?.Pattern)))
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

        // Refuse what the composed query would silently drop, and leave it in the box to fix:
        // otherwise a show-only list of nothing but such entries restricts nothing once it runs.
        var rejected = patterns.Where(pattern => !HostFilterTerm.IsUsablePattern(pattern)).ToArray();
        var existing = HostEntries().Select(host => host.Pattern).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in patterns.Except(rejected))
        {
            if (existing.Add(pattern)) _hostsList.Items.Add(pattern, isChecked: true);
        }
        _hostEntry.Text = string.Join("; ", rejected);
        OnCriteriaChanged();

        if (rejected.Length > 0)
            MessageBox.Show(this, Strings.Filters.HostPatternsRejected(string.Join(", ", rejected)),
                Strings.App.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
}
