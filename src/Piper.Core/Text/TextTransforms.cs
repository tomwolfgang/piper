using System.Buffers.Text;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Piper.Core.Text;

/// <summary>
/// The transforms the TextWizard offers, in the order Fiddler Classic lists them so the two tools read
/// the same way.
/// </summary>
public enum TextTransform
{
    ToBase64,
    ToBase64Url,
    FromBase64,
    UrlEncode,
    UrlDecode,
    HexEncode,
    HexDecode,
    ToCSharpByteArray,
    ToJsString,
    FromJsString,
    HtmlEncode,
    HtmlDecode,
    ToUtf7,
    FromUtf7,
    ToDeflatedSaml,
    FromDeflatedSaml,
    Md5,
    Sha1,
    Sha256,
    Sha384,
    Sha512,
}

/// <summary>
/// Converts a single piece of text between the encodings that show up in captured traffic. Text is treated
/// as UTF-8 throughout. Every decoder rejects malformed input by throwing rather than returning a guess:
/// <see cref="FormatException"/> for the encodings (<see cref="UriFormatException"/> derives from it),
/// <see cref="JsonException"/> for JS string literals, and <see cref="InvalidDataException"/> for corrupt
/// or oversized compressed data.
/// </summary>
public static class TextTransforms
{
    /// <summary>
    /// Ceiling on what an inflate may produce. SAML payloads are attacker-supplied and DEFLATE compresses
    /// repetitive data enormously, so a few hundred bytes of input must not be allowed to become gigabytes.
    /// </summary>
    private const int MaxInflatedBytes = 1024 * 1024;

    /// <summary>
    /// Escapes only what JSON requires, so a quote reads as a backslash-quote and an accented letter stays
    /// itself. The default encoder renders those as " and é — valid, but unreadable, and
    /// readability is the entire point of this tool. The relaxed encoder is only "unsafe" when its output is
    /// pasted straight into HTML or script; this output goes to a read-only box for the user to inspect.
    /// </summary>
    private static readonly JsonSerializerOptions JsStringOptions =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

#pragma warning disable SYSLIB0001 // UTF-7 is obsolete because it is dangerous, which is exactly why a
    // debugging proxy needs to read it: legacy mail gateways still emit it and UTF-7 is a classic XSS
    // filter-evasion vector. Decoding it here is inspection, not use.
    private static readonly Encoding Utf7 = new UTF7Encoding();
#pragma warning restore SYSLIB0001

    /// <summary>Applies <paramref name="transform"/> to <paramref name="input"/>.</summary>
    /// <exception cref="FormatException">The input is not valid for the chosen decoder.</exception>
    /// <exception cref="JsonException">The input is not a JS string literal.</exception>
    /// <exception cref="InvalidDataException">The compressed input is corrupt or expands past the cap.</exception>
    public static string Apply(TextTransform transform, string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        return transform switch
        {
            TextTransform.ToBase64 => Convert.ToBase64String(Encoding.UTF8.GetBytes(input)),
            TextTransform.ToBase64Url => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(input)),
            TextTransform.FromBase64 => Encoding.UTF8.GetString(FromBase64Lenient(input)),
            TextTransform.UrlEncode => Uri.EscapeDataString(input),
            // Fiddler, and every HTML form, treats "+" in a query string as a space.
            TextTransform.UrlDecode => Uri.UnescapeDataString(input.Replace('+', ' ')),
            TextTransform.HexEncode => Convert.ToHexString(Encoding.UTF8.GetBytes(input)),
            TextTransform.HexDecode => Encoding.UTF8.GetString(Convert.FromHexString(input)),
            TextTransform.ToCSharpByteArray => ToCSharpLiteral(Encoding.UTF8.GetBytes(input)),
            TextTransform.ToJsString => JsonSerializer.Serialize(input, JsStringOptions),
            TextTransform.FromJsString => JsonSerializer.Deserialize<string>(input) ?? string.Empty,
            TextTransform.HtmlEncode => WebUtility.HtmlEncode(input),
            TextTransform.HtmlDecode => WebUtility.HtmlDecode(input),
            // UTF-7 output is pure ASCII by construction, which is the point of the encoding.
            TextTransform.ToUtf7 => Encoding.ASCII.GetString(Utf7.GetBytes(input)),
            TextTransform.FromUtf7 => Utf7.GetString(Encoding.ASCII.GetBytes(input)),
            TextTransform.ToDeflatedSaml => Convert.ToBase64String(Deflate(Encoding.UTF8.GetBytes(input))),
            TextTransform.FromDeflatedSaml => Encoding.UTF8.GetString(Inflate(FromBase64Lenient(input))),
            TextTransform.Md5 => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(input))),
            TextTransform.Sha1 => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(input))),
            TextTransform.Sha256 => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))),
            TextTransform.Sha384 => Convert.ToHexString(SHA384.HashData(Encoding.UTF8.GetBytes(input))),
            TextTransform.Sha512 => Convert.ToHexString(SHA512.HashData(Encoding.UTF8.GetBytes(input))),
            _ => throw new ArgumentOutOfRangeException(nameof(transform)),
        };
    }

    /// <summary>
    /// Decodes base64 the way it actually turns up in traffic: either alphabet, padding optional, and
    /// wrapped across lines. Fiddler's "From Base64" is equally forgiving, and a stricter decoder would
    /// reject most real JWT segments and headers outright. Illegal characters are still an error.
    /// </summary>
    private static byte[] FromBase64Lenient(string input)
    {
        var buffer = new StringBuilder(input.Length);
        foreach (var c in input)
        {
            if (char.IsWhiteSpace(c)) continue;
            buffer.Append(c switch { '-' => '+', '_' => '/', _ => c });
        }

        // A base64 quantum is 4 characters; a remainder of 1 cannot be produced by any input.
        switch (buffer.Length % 4)
        {
            case 1: throw new FormatException("Not valid base64: the length leaves a single trailing character.");
            case 2: buffer.Append("=="); break;
            case 3: buffer.Append('='); break;
        }

        return Convert.FromBase64String(buffer.ToString());
    }

    private static string ToCSharpLiteral(byte[] bytes)
    {
        if (bytes.Length == 0) return "new byte[] { }";

        // StringBuilder rather than string.Join over a Select: at the 1 MiB input cap that would allocate
        // a million short-lived strings.
        var literal = new StringBuilder(bytes.Length * 6 + 16).Append("new byte[] { ");
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0) literal.Append(", ");
            literal.Append("0x").Append(bytes[i].ToString("X2"));
        }
        return literal.Append(" }").ToString();
    }

    /// <summary>Raw DEFLATE, which is what the SAML HTTP-Redirect binding uses (no zlib wrapper).</summary>
    private static byte[] Deflate(byte[] bytes)
    {
        using var target = new MemoryStream();
        using (var compressor = new DeflateStream(target, CompressionLevel.Optimal, leaveOpen: true))
            compressor.Write(bytes);
        return target.ToArray();
    }

    private static byte[] Inflate(byte[] bytes)
    {
        using var source = new MemoryStream(bytes, writable: false);
        using var decompressor = new DeflateStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();

        // Copied a block at a time so the cap is enforced as the data arrives; CopyTo would happily
        // materialise a decompression bomb first and let us notice afterwards.
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = decompressor.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (target.Length + read > MaxInflatedBytes)
                throw new InvalidDataException($"The compressed input expands past {MaxInflatedBytes / 1024} KiB.");
            target.Write(chunk, 0, read);
        }

        return target.ToArray();
    }
}
