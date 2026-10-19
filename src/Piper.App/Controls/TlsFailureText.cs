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
/// What the grid says about a failed client TLS handshake, as pure functions of <see cref="TlsFailureInfo"/>,
/// so they are checked without WinForms. The wording is the observed fact only: it never says "pinning" (a
/// client that pins and one that does not trust Piper's root look the same from here). Text that comes from
/// the client is shown only after <see cref="Clean"/>, and as a value, never as part of a template.
/// </summary>
public static class TlsFailureText
{
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
