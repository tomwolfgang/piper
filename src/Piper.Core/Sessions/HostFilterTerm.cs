namespace Piper.Core.Sessions;

/// <summary>
/// Turns the Filters tab's Hosts box (a free-typed, semicolon/comma/newline separated list of
/// host patterns) into a single term in the <see cref="SearchQuery"/> grammar.
/// Pure and side-effect-free on purpose -- unlike the WinForms panel that owns the actual text
/// box, this can be unit tested directly (see Piper.SmokeTests).
/// </summary>
public static class HostFilterTerm
{
    /// <summary>
    /// Splits <paramref name="hostsText"/> on ';', ',' and newlines, strips a leading wildcard
    /// marker from each pattern, and composes one <c>host:</c> (or negated <c>-host:</c> when
    /// <paramref name="hide"/> is true) term with the patterns OR'd together via '|'. Returns
    /// <see cref="string.Empty"/> when there is nothing usable to filter by.
    /// </summary>
    public static string Compose(string? hostsText, bool hide)
    {
        if (string.IsNullOrWhiteSpace(hostsText)) return string.Empty;

        var patterns = hostsText
            .Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(StripWildcard)
            .Where(p => p.Length > 0)
            .ToArray();

        if (patterns.Length == 0) return string.Empty;

        var joined = string.Join('|', patterns);
        return hide ? $"-host:{joined}" : $"host:{joined}";
    }

    /// <summary>
    /// Strips a leading "*." subdomain wildcard from a pattern.
    /// <see cref="SearchQuery"/>'s <c>host:</c> field is already a substring match, so
    /// "*.example.com" and "example.com" behave identically once the marker is gone -- but any
    /// literal '*' left behind (a lone "*" typed mid-edit, or "*example.com" without the dot)
    /// would never match a real hostname and silently filter every session out. Stripping every
    /// leading '*' regardless of whether a dot follows closes that trap.
    /// </summary>
    private static string StripWildcard(string pattern) => pattern.TrimStart('*', '.');
}
