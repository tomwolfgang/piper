using System.Text.Json;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;

namespace Piper.App.Controls;

/// <summary>
/// A focused filter panel with a Hosts allow/deny list and Response Status Code toggles,
/// composed into the same query grammar the session grid's own filter box understands
/// (see <see cref="Piper.Core.Sessions.SearchQuery"/>). Other filter categories are out of
/// scope and not implemented here.
/// </summary>
/// <remarks>
/// Every control re-composes and re-applies the query immediately while "Use Filters" is
/// checked -- editing the Hosts box or a status checkbox takes effect right away, no separate
/// "Run Filterset now" click required. "Run Filterset now" and loading a filterset still force
/// an apply (and turn "Use Filters" on) as an explicit affordance.
/// </remarks>
public sealed class FilterPanel : UserControl
{
    private readonly CheckBox _useFilters;
    private readonly ComboBox _hostsMode;
    private readonly TextBox _hostsText;
    private readonly CheckBox _hideSuccess;
    private readonly CheckBox _hideNonSuccess;
    private readonly CheckBox _hideRedirects;
    private readonly CheckBox _hideAuthDemands;
    private readonly CheckBox _hideNotModified;
    private bool _applyingSettings;

    public event EventHandler<string>? FilterChanged;

    /// <summary>Captures the current controls in the same format used by filterset files.</summary>
    public FilterSettings Settings => new()
    {
        UseFilters = _useFilters.Checked,
        HostsMode = _hostsMode.SelectedIndex,
        HostsText = _hostsText.Text,
        HideSuccess = _hideSuccess.Checked,
        HideNonSuccess = _hideNonSuccess.Checked,
        HideRedirects = _hideRedirects.Checked,
        HideAuthDemands = _hideAuthDemands.Checked,
        HideNotModified = _hideNotModified.Checked,
    };

    public FilterPanel()
    {
        _useFilters = new CheckBox
        {
            Dock = DockStyle.Top,
            Text = "Use Filters",
            AutoSize = false,
            Height = 38,
            Padding = new Padding(6, 10, 0, 0),
            Font = new Font(Palette.UiFont, FontStyle.Bold),
        };
        _useFilters.CheckedChanged += (_, _) => ApplyIfEnabled();

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

        _hostsText = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Font = Palette.Mono,
            PlaceholderText = "localhost; *.curseforge.com; *.example.net",
        };
        _hostsText.TextChanged += (_, _) => OnCriteriaChanged();

        var hostsGroup = new GroupBox
        {
            Dock = DockStyle.Top,
            Text = "Hosts",
            Height = 180,
            Padding = new Padding(8, 4, 8, 8),
            Font = Palette.UiFont,
        };
        // Fill added before Top so the combo ends up above the textbox (same stacking trick
        // used throughout this app: Dock=Fill children are added first, then the Dock=Top
        // children that should sit above them).
        hostsGroup.Controls.Add(_hostsText);
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
        // Added in reverse so the visual, top-to-bottom order is: success, non-2xx, redirects,
        // auth demands, not-modified (same trick as above -- last add ends up on top for
        // same-Dock siblings).
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
        actions.DropDownItems.Add(new ToolStripSeparator());
        actions.DropDownItems.Add("Load Filterset...", null, (_, _) => LoadFilterset());
        actions.DropDownItems.Add("Save Filterset...", null, (_, _) => SaveFilterset());
        actions.DropDownItems.Add(new ToolStripSeparator());
        actions.DropDownItems.Add("Help", null, (_, _) => ShowHelp());

        var actionsBar = new ToolStrip
        {
            Dock = DockStyle.Top,
            GripStyle = ToolStripGripStyle.Hidden,
            Font = Palette.UiFont,
        };
        actionsBar.Items.Add(actions);

        // ------------------------------------------------------------- assembly

        // Reverse order again: the last control added ends up visually topmost among the
        // Dock=Top siblings, giving (top to bottom) Use Filters, Hosts, Response Status Code,
        // Actions.
        Controls.Add(actionsBar);
        Controls.Add(statusGroup);
        Controls.Add(hostsGroup);
        Controls.Add(_useFilters);
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
        // Always recomposes and applies, regardless of the checkbox -- but flipping it on too
        // is more intuitive than leaving it unchecked while a filter is visibly active on the grid.
        var wasChecked = _useFilters.Checked;
        _useFilters.Checked = true;
        if (wasChecked) ApplyIfEnabled(); // CheckedChanged only fires on an actual change
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
        "Filters compose into the same query grammar as the session grid's own filter box, "
        + "and apply by writing into that same filter -- so applying a filterset overwrites "
        + "anything typed there by hand.\r\n\r\n"
        + "Only Hosts and Response Status Code filters are implemented.",
        "Filters", MessageBoxButtons.OK, MessageBoxIcon.Information);

    /// <summary>Re-composes from current control state and applies it, but only while "Use
    /// Filters" is checked -- this is what makes every control (Hosts text, mode, status
    /// checkboxes) apply live instead of requiring a separate "Run Filterset now".</summary>
    private void ApplyIfEnabled()
    {
        if (_applyingSettings) return;
        FilterChanged?.Invoke(this, _useFilters.Checked ? ComposeQuery() : string.Empty);
    }

    /// <summary>Updates every filter control and applies the resulting enabled/disabled state once.</summary>
    public void ApplySettings(FilterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        _applyingSettings = true;
        try
        {
            _hostsMode.SelectedIndex = settings.HostsMode is 0 or 1 ? settings.HostsMode : 0;
            _hostsText.Text = settings.HostsText ?? string.Empty;
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

        ApplyIfEnabled();
    }

    /// <summary>Handles an edit to the Hosts box/mode or a status checkbox. If "Use Filters"
    /// isn't checked yet, editing these controls would otherwise do nothing visible at all --
    /// <see cref="ApplyIfEnabled"/> only ever applies while it's checked, so typing a host
    /// pattern before ticking that box silently had no effect. Turning it on the moment there's
    /// real criteria to apply removes that trap; unchecking it by hand still fully disables
    /// filtering without losing what was typed.</summary>
    private void OnCriteriaChanged()
    {
        if (!_useFilters.Checked && ComposeQuery().Length > 0)
        {
            _useFilters.Checked = true; // CheckedChanged applies it
            return;
        }
        ApplyIfEnabled();
    }

    // --------------------------------------------------------------- composition

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

    private string ComposeHostsTerm() => HostFilterTerm.Compose(_hostsText.Text, hide: _hostsMode.SelectedIndex == 1);

}
