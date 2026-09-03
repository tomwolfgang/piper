using System.Diagnostics.CodeAnalysis;
using System.Text;

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
        var patterns = Split(hostsText)
            .Select(StripWildcard)
            .Where(p => p.Length > 0)
            .ToArray();

        if (patterns.Length == 0) return string.Empty;

        var joined = string.Join('|', patterns);
        return hide ? $"-host:{joined}" : $"host:{joined}";
    }

    /// <summary>
    /// Whether a host observed on the wire may be turned into a filter pattern. A Host header is
    /// attacker-controlled and reaches <see cref="Session.Host"/> verbatim whenever the request
    /// line has no parseable URL (<see cref="Http.HttpParser.ResolveUrl"/> returns null), so
    /// anything that is not plain hostname material must never be composed into a query or
    /// persisted as a pattern: whitespace ends a value and injects a whole extra term, '|' adds
    /// alternatives, a leading '/' or '"' switches the value into regex or quoted mode, and
    /// ';', ',' and newlines split one pattern into several. Letters and digits are accepted in any
    /// script so hiding an internationalised host still works, and the length is capped in bytes --
    /// 253, the longest legal DNS name -- to keep an oversized header out of the settings file.
    /// </summary>
    public static bool IsFilterableHost([NotNullWhen(true)] string? host) =>
        host is { Length: > 0 }
        && Encoding.UTF8.GetByteCount(host) <= 253
        && host.All(c => char.IsLetterOrDigit(c) || c is '-' or '.' or '_' or ':' or '[' or ']' or '%');

    /// <summary>Splits user-entered host patterns without changing their display text.</summary>
    public static IReadOnlyList<string> Split(string? hostsText) =>
        string.IsNullOrWhiteSpace(hostsText)
            ? []
            : hostsText.Split([';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Strips a leading "*." subdomain wildcard from a pattern.
    /// <see cref="SearchQuery"/>'s <c>host:</c> field is already a substring match, so
    /// "*.example.com" and "example.com" behave identically once the marker is gone -- but any
    /// literal '*' left behind (a lone "*" typed mid-edit, or "*example.com" without the dot)
    /// would never match a real hostname and silently filter every session out. Stripping every
    /// leading '*' regardless of whether a dot follows closes that trap.
    /// </summary>
    public static string StripWildcard(string pattern) => pattern.TrimStart('*', '.');
}
