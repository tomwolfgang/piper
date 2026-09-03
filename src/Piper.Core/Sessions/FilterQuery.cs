namespace Piper.Core.Sessions;

/// <summary>
/// Composes the Filters tab's state into a single term in the <see cref="SearchQuery"/> grammar.
/// Pure and side-effect-free on purpose -- unlike the WinForms panel that owns the actual controls,
/// this can be unit tested directly (see Piper.SmokeTests). Both the applied query and the Filters
/// tab's glyph are derived from this one function, so neither can disagree with the checkbox.
/// </summary>
public static class FilterQuery
{
    /// <summary>
    /// Returns the query the session list and the store's admission filter should share, or
    /// <see cref="string.Empty"/> when <see cref="FilterSettings.UseFilters"/> is off. Filters
    /// being off deliberately wins over every host and status selection: the store discards
    /// non-matching completed sessions outright rather than hiding them, so a query left applied
    /// after the user disables filtering would silently drop captured traffic.
    /// </summary>
    public static string Compose(FilterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.UseFilters) return string.Empty;

        var terms = new List<string>();

        var hostsTerm = HostFilterTerm.Compose(EnabledHosts(settings), hide: settings.HostsMode == 1);
        if (hostsTerm.Length > 0) terms.Add(hostsTerm);

        if (settings.HideSuccess) terms.Add("-status:200..299");
        if (settings.HideNonSuccess) terms.Add("status:200..299");
        if (settings.HideRedirects) terms.Add("-status:300..303 -status:307");
        if (settings.HideAuthDemands) terms.Add("-status:401 -status:407");
        if (settings.HideNotModified) terms.Add("-status:304");

        return string.Join(' ', terms);
    }

    /// <summary>
    /// The host patterns that participate in the query, in the separated form
    /// <see cref="HostFilterTerm.Compose"/> accepts. Falls back to the legacy
    /// <see cref="FilterSettings.HostsText"/> field when a filterset predates the per-host
    /// checkboxes and so carries no <see cref="FilterSettings.Hosts"/> entries.
    /// </summary>
    private static string EnabledHosts(FilterSettings settings) =>
        settings.Hosts is { Count: > 0 } hosts
            ? string.Join(';', hosts.Where(host => host.Enabled).Select(host => host.Pattern))
            : settings.HostsText;
}
