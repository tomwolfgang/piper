using System.Text;

namespace Piper.Core.Text;

/// <summary>
/// Guesses which decoder a piece of text wants, so the TextWizard can preselect it instead of making the
/// user identify the encoding themselves.
/// </summary>
/// <remarks>
/// This is a hint, never an authority: the user can always pick something else, and a wrong guess shows up
/// immediately as either visible nonsense or a decoder error on the status line. That tolerance is what lets
/// the checks stay cheap. Text arrives here straight from captured traffic, so the work is bounded twice
/// over - character classes are judged from a leading sample rather than the whole megabyte, and the checks
/// that need a real decode only run on input small enough to decode twice without being noticed.
/// </remarks>
public static class TextTransformDetector
{
    /// <summary>How much of the input the character-class checks look at.</summary>
    private const int SampleLength = 4096;

    /// <summary>Above this, no check that actually decodes is attempted.</summary>
    private const int DecodeAttemptLimit = 64 * 1024;

    /// <summary>The shortest run of hex digits worth reading as hex rather than as a word.</summary>
    private const int MinimumHexLength = 8;

    private const int MinimumBase64Length = 8;

    /// <summary>
    /// The decoder <paramref name="input"/> most likely wants, or null when nothing is recognisable and the
    /// caller should fall back to whatever the user chose last.
    /// </summary>
    public static TextTransform? Detect(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var trimmed = input.Trim();
        var sample = trimmed.Length > SampleLength ? trimmed[..SampleLength] : trimmed;

        // Ordered by how hard each shape is to mistake for another. A quoted literal, an HTML entity and a
        // percent escape are all but unambiguous; hex and base64 overlap with ordinary words, so they come
        // last and carry length floors.
        if (LooksLikeJsString(trimmed)) return TextTransform.FromJsString;
        if (LooksLikeHtml(sample)) return TextTransform.HtmlDecode;
        if (LooksLikeUrlEncoded(sample)) return TextTransform.UrlDecode;
        if (LooksLikeUtf7(sample)) return TextTransform.FromUtf7;
        if (LooksLikeHex(sample, trimmed.Length)) return TextTransform.HexDecode;

        if (!LooksLikeBase64(sample, trimmed.Length)) return null;

        // Both remaining candidates are base64; only a decode can separate them, so this is where the size
        // gate applies. Oversized input still gets the base64 guess, just without the confirmation.
        if (trimmed.Length > DecodeAttemptLimit) return TextTransform.FromBase64;
        if (DecodesAsDeflate(trimmed)) return TextTransform.FromDeflatedSaml;
        return DecodesToText(trimmed) ? TextTransform.FromBase64 : null;
    }

    /// <summary>A whole JSON string literal, quotes included.</summary>
    private static bool LooksLikeJsString(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"';

    private static bool LooksLikeHtml(string sample) =>
        sample.Contains("&lt;", StringComparison.OrdinalIgnoreCase)
        || sample.Contains("&gt;", StringComparison.OrdinalIgnoreCase)
        || sample.Contains("&amp;", StringComparison.OrdinalIgnoreCase)
        || sample.Contains("&quot;", StringComparison.OrdinalIgnoreCase)
        || sample.Contains("&#", StringComparison.Ordinal);

    /// <summary>A percent escape, which a plus alone is not: "a+b" is far more often just text.</summary>
    private static bool LooksLikeUrlEncoded(string sample)
    {
        for (var i = 0; i + 2 < sample.Length; i++)
            if (sample[i] == '%' && IsHex(sample[i + 1]) && IsHex(sample[i + 2])) return true;
        return false;
    }

    /// <summary>UTF-7's shifted runs, e.g. "+AOk-", which no other encoding here produces.</summary>
    private static bool LooksLikeUtf7(string sample)
    {
        var plus = sample.IndexOf('+');
        while (plus >= 0 && plus + 1 < sample.Length)
        {
            var end = sample.IndexOf('-', plus + 1);
            if (end > plus + 1)
            {
                var run = sample.AsSpan(plus + 1, end - plus - 1);
                var shifted = true;
                foreach (var c in run)
                    if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/')) { shifted = false; break; }
                if (shifted) return true;
            }
            plus = sample.IndexOf('+', plus + 1);
        }
        return false;
    }

    private static bool LooksLikeHex(string sample, int totalLength)
    {
        if (totalLength < MinimumHexLength || totalLength % 2 != 0) return false;
        foreach (var c in sample)
            if (!IsHex(c)) return false;
        return true;
    }

    private static bool LooksLikeBase64(string sample, int totalLength)
    {
        if (totalLength < MinimumBase64Length) return false;

        var seen = 0;
        foreach (var c in sample)
        {
            if (char.IsWhiteSpace(c)) continue;
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '-' or '_' or '=')) return false;
            seen++;
        }

        return seen >= MinimumBase64Length;
    }

    private static bool DecodesAsDeflate(string value)
    {
        try
        {
            return TextTransforms.Apply(TextTransform.FromDeflatedSaml, value).Length > 0;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when base64 yields something a person would recognise as text. Without this every random token -
    /// a session id, a nonce - would be offered as base64 and produce a boxful of replacement characters.
    /// </summary>
    private static bool DecodesToText(string value)
    {
        string decoded;
        try
        {
            decoded = TextTransforms.Apply(TextTransform.FromBase64, value);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length == 0) return false;

        var printable = 0;
        foreach (var c in decoded)
            if (!char.IsControl(c) && c != '�') printable++;
            else if (c is '\n' or '\r' or '\t') printable++;

        return printable * 10 >= decoded.Length * 9;
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
}
