using System.IO.Compression;
using System.Text;

namespace Piper.Core.Http;

/// <summary>Content-Encoding and charset handling for display purposes.</summary>
public static class ContentCodec
{
    /// <summary>
    /// Strips Content-Encoding so the body can be shown as text. Encodings are applied
    /// right-to-left per RFC 9110. Returns the input unchanged if anything fails - a
    /// debugger must never lose the original bytes to a decode error.
    /// </summary>
    public static byte[] Decode(byte[] body, string? contentEncoding)
    {
        if (body.Length == 0 || string.IsNullOrWhiteSpace(contentEncoding)) return body;

        var encodings = contentEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = body;
        for (var i = encodings.Length - 1; i >= 0; i--)
        {
            if (!TryDecodeOne(current, encodings[i], out var decoded)) return body;
            current = decoded;
        }
        return current;
    }

    public static bool IsKnownEncoding(string encoding) => encoding.ToLowerInvariant() switch
    {
        "gzip" or "x-gzip" or "deflate" or "br" or "identity" or "none" => true,
        _ => false,
    };

    private static bool TryDecodeOne(byte[] input, string encoding, out byte[] output)
    {
        output = input;
        try
        {
            switch (encoding.ToLowerInvariant())
            {
                case "identity":
                case "none":
                case "":
                    return true;
                case "gzip":
                case "x-gzip":
                    output = Run(input, s => new GZipStream(s, CompressionMode.Decompress));
                    return true;
                case "br":
                    output = Run(input, s => new BrotliStream(s, CompressionMode.Decompress));
                    return true;
                case "deflate":
                    // Servers disagree on whether "deflate" means zlib (RFC 1950) or raw
                    // (RFC 1951). Try zlib first, then fall back to raw.
                    try { output = Run(input, s => new ZLibStream(s, CompressionMode.Decompress)); }
                    catch { output = Run(input, s => new DeflateStream(s, CompressionMode.Decompress)); }
                    return true;
                default:
                    return false; // unknown (e.g. zstd) - leave the body alone
            }
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Run(byte[] input, Func<Stream, Stream> wrap)
    {
        using var source = new MemoryStream(input, writable: false);
        using var decompressor = wrap(source);
        using var target = new MemoryStream(input.Length * 4);
        decompressor.CopyTo(target);
        return target.ToArray();
    }

    /// <summary>Resolves the charset from a Content-Type value, defaulting to UTF-8.</summary>
    public static Encoding CharsetFor(string? contentType)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            var idx = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var value = contentType[(idx + 8)..].Trim().Trim('"');
                var end = value.IndexOf(';');
                if (end >= 0) value = value[..end];
                value = value.Trim().Trim('"');
                try
                {
                    if (value.Length > 0) return Encoding.GetEncoding(value);
                }
                catch (ArgumentException)
                {
                    // Unrecognised charset label - fall through to UTF-8.
                }
            }
        }
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    /// <summary>Heuristic: is this payload safe to show in a text view?</summary>
    public static bool LooksTextual(string? contentType, byte[] body)
    {
        if (!string.IsNullOrEmpty(contentType))
        {
            var ct = contentType.ToLowerInvariant();
            if (ct.StartsWith("text/")) return true;
            if (ct.Contains("json") || ct.Contains("xml") || ct.Contains("javascript")
                || ct.Contains("x-www-form-urlencoded") || ct.Contains("graphql")) return true;
            if (ct.StartsWith("image/") || ct.StartsWith("video/") || ct.StartsWith("audio/")
                || ct.Contains("octet-stream") || ct.Contains("font")) return false;
        }

        // Sniff: treat as binary if there are NUL bytes in the first block.
        var probe = Math.Min(body.Length, 1024);
        for (var i = 0; i < probe; i++)
            if (body[i] == 0) return false;
        return true;
    }
}
