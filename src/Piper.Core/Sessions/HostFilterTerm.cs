using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Piper.Core.Sessions;

/// <summary>
/// Turns the Filters tab's Hosts box (a free-typed, semicolon/comma/whitespace separated list of
/// host patterns) into a single term in the <see cref="SearchQuery"/> grammar.
/// Pure and side-effect-free on purpose -- unlike the WinForms panel that owns the actual text
/// box, this can be unit tested directly (see Piper.SmokeTests).
/// </summary>
public static class HostFilterTerm
{
    /// <summary>
    /// Splits <paramref name="hostsText"/> on ';', ',', whitespace and newlines, strips a leading
    /// wildcard marker from each pattern, and composes one <c>domain:</c> (or negated
    /// <c>-domain:</c> when <paramref name="hide"/> is true) term with the patterns OR'd together
    /// via '|'. Returns <see cref="string.Empty"/> when there is nothing usable to filter by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>domain:</c> rather than <c>host:</c>, because <c>host:</c> is a substring search: hiding
    /// <c>x.com</c> used to hide <c>netflix.com</c> and <c>dropbox.com</c> with it, and while a
    /// filterset runs those sessions are discarded at admission, not merely hidden.
    /// </para>
    /// <para>
    /// A pattern carrying anything that is not hostname material is left out rather than composed.
    /// Entries arrive from a pasted list or a filterset file, and a quote, a slash, a '|' or a
    /// whitespace character the splitter did not catch would otherwise end the value early or change
    /// its meaning, turning the rest into terms of their own in the admission query.
    /// </para>
    /// </remarks>
    public static string Compose(string? hostsText, bool hide)
    {
        var patterns = Split(hostsText)
            .Where(IsUsablePattern)
            .Select(StripWildcard)
            .ToArray();

        if (patterns.Length == 0) return string.Empty;

        var joined = string.Join('|', patterns);
        return hide ? $"-domain:{joined}" : $"domain:{joined}";
    }

    /// <summary>
    /// Whether a host list entry filters <paramref name="host"/>: the same rule the composed
    /// <c>domain:</c> term applies (<see cref="SearchQuery.MatchesHostPattern"/>), so the list and
    /// the query never disagree.
    /// </summary>
    public static bool Covers(string pattern, string host) => SearchQuery.MatchesHostPattern(host, pattern);

    /// <summary>
    /// How many entries of <paramref name="hostsText"/> <see cref="Compose"/> leaves out. Dropping
    /// them is silent otherwise, and when it drops every entry a show-only list stops restricting
    /// anything: the whole hosts term disappears and all traffic is admitted.
    /// </summary>
    public static int CountIgnored(string? hostsText) =>
        Split(hostsText).Count(pattern => !IsUsablePattern(pattern));

    /// <summary>
    /// Whether <see cref="Compose"/> keeps a host list entry. The one drop rule: the Filters tab's
    /// Add box and <see cref="CountIgnored"/> ask this too, so neither can drift from the query.
    /// </summary>
    public static bool IsUsablePattern(string pattern) => IsFilterableHost(StripWildcard(pattern));

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

    /// <summary>
    /// Splits user-entered host patterns without changing their display text. Any whitespace
    /// separates, by the same <see cref="char.IsWhiteSpace(char)"/> test the query tokenizer ends a
    /// value on: "a.com b.com" composed as one pattern ended the value at the space and turned
    /// "b.com" into a bare term of its own, which then discarded almost all traffic at admission,
    /// and a no-break or ideographic space did the same.
    /// </summary>
    public static IReadOnlyList<string> Split(string? hostsText)
    {
        if (string.IsNullOrWhiteSpace(hostsText)) return [];

        var patterns = new List<string>();
        var start = 0;
        for (var i = 0; i <= hostsText.Length; i++)
        {
            if (i < hostsText.Length && hostsText[i] is not (';' or ',') && !char.IsWhiteSpace(hostsText[i])) continue;
            if (i > start) patterns.Add(hostsText[start..i]);
            start = i + 1;
        }

        return patterns;
    }

    /// <summary>
    /// Strips a leading "*." subdomain wildcard from a pattern.
    /// <see cref="SearchQuery"/>'s <c>domain:</c> field already matches subdomains, so
    /// "*.example.com" and "example.com" behave identically once the marker is gone -- but any
    /// literal '*' left behind (a lone "*" typed mid-edit, or "*example.com" without the dot)
    /// would never match a real hostname and silently filter every session out. Stripping every
    /// leading '*' regardless of whether a dot follows closes that trap.
    /// </summary>
    public static string StripWildcard(string pattern) => pattern.TrimStart('*', '.');
}
