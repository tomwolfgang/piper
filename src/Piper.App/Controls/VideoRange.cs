namespace Piper.App.Controls;

/// <summary>How a request for a held video body is answered.</summary>
/// <param name="Status">200 (the whole body), 206 (a slice) or 416.</param>
/// <param name="Start">The first byte to send (0 unless 206).</param>
/// <param name="Length">The number of bytes to send (0 for 416).</param>
/// <param name="ContentRange">The Content-Range value for a 206 or 416, otherwise null.</param>
internal readonly record struct RangeAnswer(int Status, long Start, long Length, string? ContentRange);

/// <summary>
/// The decisions behind serving a captured video from memory, free of WebView2 so they are checked on their
/// own. The Range header comes from the browser's media stack, whose requests are driven by an attacker's file,
/// and the MIME type is chosen here and never copied from a header.
/// </summary>
internal static class VideoRange
{
    /// <summary>
    /// Answers a Range header for a body of <paramref name="length"/> bytes (RFC 9110 section 14). No header, or a
    /// unit other than bytes, gets the whole body (200). One range gets 206 with its Content-Range; a last byte past
    /// the end is cut to it, a suffix longer than the body is the whole body. A first byte at or past the end, an
    /// empty suffix, a last byte before the first, or text that is not a range is 416 with <c>bytes */length</c>.
    /// Several ranges are not supported: the whole body is sent, which the RFC allows and a browser handles.
    /// </summary>
    internal static RangeAnswer Resolve(string? header, long length)
    {
        var whole = new RangeAnswer(200, 0, length, null);
        var unsatisfiable = new RangeAnswer(416, 0, 0, $"bytes */{length}");
        const string unit = "bytes=";
        var value = (header ?? string.Empty).AsSpan().Trim();
        if (value.IsEmpty || !value.StartsWith(unit, StringComparison.OrdinalIgnoreCase)) return whole;

        var specs = value[unit.Length..];
        if (specs.Contains(',')) return whole;

        var dash = specs.IndexOf('-');
        if (dash < 0) return unsatisfiable;

        var first = specs[..dash].Trim();
        var last = specs[(dash + 1)..].Trim();
        long start, end;
        if (first.IsEmpty)
        {
            // A suffix: the last N bytes.
            if (!TryParse(last, out var count) || count == 0 || length == 0) return unsatisfiable;
            start = length - Math.Min(count, length);
            end = length - 1;
        }
        else
        {
            if (!TryParse(first, out start) || start >= length) return unsatisfiable;
            if (last.IsEmpty) end = length - 1;
            else if (!TryParse(last, out end) || end < start) return unsatisfiable;
            else end = Math.Min(end, length - 1);
        }

        return new RangeAnswer(206, start, end - start + 1, $"bytes {start}-{end}/{length}");
    }

    /// <summary>
    /// The MIME type to serve: the declared one if it is a video type the engine knows, otherwise what the first
    /// bytes say, otherwise a generic one. Always one of these fixed strings, so no header text reaches the response.
    /// </summary>
    internal static string MimeFor(string? contentType, byte[] bytes)
    {
        var declared = contentType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "video/mp4" => "video/mp4",
            "video/webm" => "video/webm",
            "video/ogg" => "video/ogg",
            "video/quicktime" => "video/quicktime",
            "video/x-msvideo" => "video/x-msvideo",
            _ => null,
        };
        if (declared is not null) return declared;
        if (bytes.Length >= 12 && bytes.AsSpan(4, 4).SequenceEqual("ftyp"u8)) return "video/mp4";
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual("OggS"u8)) return "video/ogg";
        if (bytes.Length >= 4 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 })) return "video/webm";
        if (bytes.Length >= 12 && bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) && bytes.AsSpan(8, 4).SequenceEqual("AVI "u8)) return "video/x-msvideo";
        return "application/octet-stream";
    }

    // Decimal digits only, no sign or space; a number too big for a long saturates, so it still compares as huge.
    private static bool TryParse(ReadOnlySpan<char> digits, out long value)
    {
        value = 0;
        if (digits.IsEmpty) return false;
        foreach (var c in digits)
        {
            if (c is < '0' or > '9') return false;
            var digit = c - '0';
            value = value > (long.MaxValue - digit) / 10 ? long.MaxValue : value * 10 + digit;
        }

        return true;
    }
}
