using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Piper.Core.Text;

/// <summary>The encodings and hashes the TextWizard offers, matching the set Fiddler Classic ships.</summary>
public enum TextTransform
{
    UrlEncode,
    UrlDecode,
    HtmlEncode,
    HtmlDecode,
    Base64Encode,
    Base64Decode,
    Base64UrlEncode,
    Base64UrlDecode,
    HexEncode,
    HexDecode,
    JsonStringEncode,
    JsonStringDecode,
    Md5,
    Sha1,
    Sha256,
    Sha512,
}

/// <summary>
/// Converts a single piece of text between the encodings that show up in captured traffic. Text is treated
/// as UTF-8 throughout. Every decoder rejects malformed input by throwing rather than returning a guess:
/// <see cref="FormatException"/> for the encodings (<see cref="UriFormatException"/> derives from it) and
/// <see cref="JsonException"/> for JSON string literals.
/// </summary>
public static class TextTransforms
{
    /// <summary>
    /// Escapes only what JSON requires, so a quote reads as a backslash-quote and an accented letter stays
    /// itself. The default encoder renders those as " and é — valid, but unreadable, and
    /// readability is the entire point of this tool. The relaxed encoder is only "unsafe" when its output is
    /// pasted straight into HTML or script; this output goes to a read-only box for the user to inspect.
    /// </summary>
    private static readonly JsonSerializerOptions JsonStringOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Applies <paramref name="transform"/> to <paramref name="input"/>.</summary>
    /// <exception cref="FormatException">The input is not valid for the chosen decoder.</exception>
    /// <exception cref="JsonException">The input is not a JSON string literal.</exception>
    public static string Apply(TextTransform transform, string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return transform switch
        {
            TextTransform.UrlEncode => Uri.EscapeDataString(input),
            // Fiddler, and every HTML form, treats "+" in a query string as a space.
            TextTransform.UrlDecode => Uri.UnescapeDataString(input.Replace('+', ' ')),
            TextTransform.HtmlEncode => WebUtility.HtmlEncode(input),
            TextTransform.HtmlDecode => WebUtility.HtmlDecode(input),
            TextTransform.Base64Encode => Convert.ToBase64String(Encoding.UTF8.GetBytes(input)),
            TextTransform.Base64Decode => Encoding.UTF8.GetString(Convert.FromBase64String(input)),
            TextTransform.Base64UrlEncode => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(input)),
            TextTransform.Base64UrlDecode => Encoding.UTF8.GetString(Base64Url.DecodeFromChars(input)),
            TextTransform.HexEncode => Convert.ToHexString(Encoding.UTF8.GetBytes(input)),
            TextTransform.HexDecode => Encoding.UTF8.GetString(Convert.FromHexString(input)),
            TextTransform.JsonStringEncode => JsonSerializer.Serialize(input, JsonStringOptions),
            TextTransform.JsonStringDecode => JsonSerializer.Deserialize<string>(input) ?? string.Empty,
            TextTransform.Md5 => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))),
            TextTransform.Sha1 => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(input))),
            TextTransform.Sha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))),
            TextTransform.Sha512 => Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(input))),
            _ => throw new ArgumentOutOfRangeException(nameof(transform)),
        };
    }
}
