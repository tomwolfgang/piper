using System.Globalization;
using System.Text;
using Piper.Core.Sessions;

namespace Piper.App.Controls;

/// <summary>How a failed client handshake is coloured: amber when the client declined or left, red when it
/// is not a decision the client made.</summary>
public enum TlsFailureTone
{
    Refusal,
    Fault,
}

/// <summary>
/// What the grid and the inspector say about a failed client TLS handshake, as pure functions of
/// <see cref="TlsFailureInfo"/>, so they are checked without WinForms. The wording is the observed fact only: it
/// never says "pinning" (a client that pins and one that does not trust Piper's root look the same from
/// here), and it says what Piper cannot tell. The host, process and SNI come from the client; they are
/// shown as values, never as part of a template, with control and direction characters replaced and
/// the length clipped.
/// </summary>
public static class TlsFailureText
{
    /// <summary>The longest host, app or SNI shown; a DNS name is at most 253.</summary>
    internal const int MaxShown = 120;

    /// <summary>Where the inspector breaks a long sentence into rows, which do not wrap.</summary>
    internal const int PanelWidth = 64;

    /// <summary>The Result cell: not <c>ERR</c>, which stays for every other failure.</summary>
    public static string Result => Strings.TlsFailure.Result;

    /// <summary>Amber for the kinds where the client refused or left (nothing broke in Piper); red for the rest.</summary>
    public static TlsFailureTone Tone(TlsFailureInfo failure)
    {
        var declined = failure.Kind is TlsFailureKind.Rejected or TlsFailureKind.ClosedInHandshake
            or TlsFailureKind.ClosedAfterHandshake or TlsFailureKind.ClosedEarly;
        return declined && !failure.IsOverflow ? TlsFailureTone.Refusal : TlsFailureTone.Fault;
    }

    /// <summary>The reason alone, without a count. A rejection names the alert when one was read.</summary>
    public static string Reason(TlsFailureInfo failure) =>
        Strings.TlsFailure.Reason(failure.Kind, Clean(failure.Alert, 32) is { Length: > 0 } alert ? alert : null);

    /// <summary>The Path cell: the reason, and how many attempts it stands for once there is more than one.</summary>
    public static string PathLabel(TlsFailureInfo failure)
    {
        var attempts = Math.Max(1, failure.Attempts);
        return failure.IsOverflow
            ? Strings.TlsFailure.Overflow(attempts)
            : Strings.TlsFailure.PathLabel(Reason(failure), attempts);
    }

    /// <summary>The inspector's summary line.</summary>
    public static string Summary(TlsFailureInfo failure) => Strings.Inspector.ResponseTlsFailed(PathLabel(failure));

    /// <summary>
    /// The inspector's explanation as name and value rows (a long sentence continues on rows with no name).
    /// The facts come from <paramref name="failure"/>; the host and app, which it does not hold, are given.
    /// </summary>
    public static IReadOnlyList<(string Name, string Value)> Panel(TlsFailureInfo failure, string? host, string? app)
    {
        var rows = new List<(string Name, string Value)>();
        var hostText = Clean(host, MaxShown);
        var sni = Clean(failure.Sni, MaxShown);

        if (Clean(app, MaxShown) is { Length: > 0 } appText) rows.Add((Strings.TlsFailure.PanelApp, appText));
        if (hostText.Length > 0) rows.Add((Strings.TlsFailure.PanelHost, hostText));
        if (sni.Length > 0 && !string.Equals(sni, hostText, StringComparison.OrdinalIgnoreCase))
            rows.Add((Strings.TlsFailure.PanelSni, sni));
        if (failure.Attempts > 1)
        {
            rows.Add((Strings.TlsFailure.PanelAttempts, Strings.TlsFailure.AttemptsValue(failure.Attempts)));
            if (failure.LastSeen != default)
                rows.Add((Strings.TlsFailure.PanelLastAttempt, Strings.TlsFailure.LastAttemptValue(failure.LastSeen.ToLocalTime())));
        }

        if (!failure.IsOverflow) rows.Add((Strings.TlsFailure.PanelPhase, Strings.TlsFailure.Phase(failure.Phase)));
        if (Clean(failure.Alert, 32) is { Length: > 0 } alert) rows.Add((Strings.TlsFailure.PanelAlert, alert));
        if (failure.SecStatus is { } code) rows.Add((Strings.TlsFailure.PanelSecurityStatus, Strings.TlsFailure.SecurityStatusValue(code)));

        AddWrapped(rows, Strings.TlsFailure.PanelObserved, Strings.TlsFailure.Observed(failure.Kind, failure.IsOverflow));
        AddWrapped(rows, Strings.TlsFailure.PanelUnknown, Strings.TlsFailure.Unknown(failure.Kind, failure.IsOverflow));
        AddWrapped(rows, Strings.TlsFailure.PanelNext, Strings.TlsFailure.Next(failure.Kind, failure.IsOverflow));
        return rows;
    }

    private static void AddWrapped(List<(string Name, string Value)> rows, string name, string text)
    {
        var first = true;
        foreach (var line in Wrap(text, PanelWidth))
        {
            rows.Add((first ? name : string.Empty, line));
            first = false;
        }
    }

    /// <summary>Breaks text at spaces into lines of at most <paramref name="width"/> characters; a word longer than that is cut.</summary>
    internal static List<string> Wrap(string text, int width)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var rest = word;
            while (rest.Length > width)
            {
                if (line.Length > 0) { lines.Add(line.ToString()); line.Clear(); }
                lines.Add(rest[..width]);
                rest = rest[width..];
            }

            if (line.Length > 0 && line.Length + 1 + rest.Length > width) { lines.Add(line.ToString()); line.Clear(); }
            if (line.Length > 0) line.Append(' ');
            line.Append(rest);
        }
        if (line.Length > 0 || lines.Count == 0) lines.Add(line.ToString());
        return lines;
    }

    /// <summary>
    /// Text from the client made safe to show: control, line-break and direction-changing characters become
    /// <c>?</c>, and what is longer than <paramref name="max"/> is cut and ends in <c>...</c>.
    /// </summary>
    internal static string Clean(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var clipped = text.Length > max;
        var length = clipped ? max : text.Length;
        // Cutting between the halves of a surrogate pair would leave half a character.
        if (clipped && char.IsHighSurrogate(text[length - 1])) length--;

        var builder = new StringBuilder(length + 3);
        for (var i = 0; i < length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[++i]);
                continue;
            }
            builder.Append(IsUnsafe(c) || char.IsSurrogate(c) ? '?' : c);
        }
        if (clipped) builder.Append("...");
        return builder.ToString();
    }

    private static bool IsUnsafe(char c) =>
        char.IsControl(c)
        || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
}
