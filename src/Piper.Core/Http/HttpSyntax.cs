namespace Piper.Core.Http;

/// <summary>A request line: method, request target and version.</summary>
public readonly record struct HttpRequestLine(string Method, string Target, string Version);

/// <summary>A status line. <see cref="Reason"/> is null when the line had none, so a caller that
/// wants a default can tell "absent" from "empty".</summary>
public readonly record struct HttpStatusLine(string Version, int StatusCode, string? Reason);

/// <summary>
/// The grammar of an HTTP/1.x message head, in one place. The proxy's stream parser, the SAZ
/// importer, the Composer and the AutoResponder's response files all read the same lines from peers
/// or files that are hostile, so they all get the same limits instead of four copies that drift.
/// </summary>
/// <remarks>
/// "Lenient" is for input a user supplied (a SAZ archive, a raw request typed into the Composer, a
/// response file): odd spacing, a made-up or missing version and trailing words are tolerated there
/// because rejecting them would make a file unusable for no gain. It never relaxes the parts that
/// feed the next hop: the method must be a token, the target and reason carry no control
/// characters, and a status code is exactly three digits (100-999 on an HTTP/1 response, see
/// <see cref="MaxHttp1StatusCode"/>; 100-599 on HTTP/2 and HTTP/3).
/// </remarks>
public static class HttpSyntax
{
    /// <summary>The most header lines any parser accepts in one block.</summary>
    public const int MaxHeaderCount = 200;

    /// <summary>
    /// The largest chunk a chunked body may announce (1 TiB). A hex size has to be bounded before it
    /// is turned into a number, or sixteen digits overflow a long; anything this large is not a real
    /// chunk. The body caps and the data actually arriving bound the rest.
    /// </summary>
    public const long MaxChunkSize = 1L << 40;

    /// <summary>
    /// Reads the size on a chunk-size line: hex digits, optionally padded with spaces or tabs, then
    /// any chunk extension after a ';'. Refuses an empty size, a sign, a "0x" prefix, any other
    /// character, and a size above <see cref="MaxChunkSize"/>. The only parser of chunk sizes:
    /// parsing them as an <c>int</c> read "FFFFFFFF" as -1.
    /// </summary>
    public static bool TryParseChunkSize(string line, out long size)
    {
        size = 0;
        var semicolon = line.IndexOf(';');
        var text = (semicolon >= 0 ? line.AsSpan(0, semicolon) : line.AsSpan()).Trim(" \t");
        if (text.IsEmpty) return false;

        long value = 0;
        foreach (var c in text)
        {
            var digit = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (digit < 0) return false;

            // value never exceeds MaxChunkSize here, so the shift cannot overflow.
            value = (value << 4) | (uint)digit;
            if (value > MaxChunkSize) return false;
        }

        size = value;
        return true;
    }

    /// <summary>The highest status an HTTP/1 response may carry. RFC 9110 15 defines 100-599, but real
    /// origins send others (LinkedIn answers 999), and a debugging proxy that refused such a reply
    /// would replace the one thing worth inspecting with its own 502. HTTP/1 responses (the proxy,
    /// SAZ archives, response files) therefore take any three digits from 100 to 999 and show them as
    /// sent; HTTP/2 and HTTP/3 keep the standard range, since their framing leaves no reason to.</summary>
    public const int MaxHttp1StatusCode = 999;

    /// <summary>A status code is exactly three ASCII digits, 100 to <paramref name="max"/> (599 unless
    /// the caller asks for the HTTP/1 range, <see cref="MaxHttp1StatusCode"/>).</summary>
    public static bool TryParseStatusCode(string? text, out int code, int max = 599)
    {
        code = 0;
        if (text is null || text.Length != 3) return false;

        foreach (var c in text)
            if (!char.IsAsciiDigit(c)) return false;

        code = (text[0] - '0') * 100 + (text[1] - '0') * 10 + (text[2] - '0');
        if (code >= 100 && code <= max) return true;

        code = 0;
        return false;
    }

    /// <summary>
    /// Parses a request line. Words are separated by spaces (runs of them are tolerated). The version
    /// may be left out, in which case <paramref name="defaultVersion"/> is used.
    /// </summary>
    public static bool TryParseRequestLine(
        string line, bool lenient, string defaultVersion, out HttpRequestLine result, out string error)
    {
        result = default;
        // Trailing blanks are not part of the version (a client may well send "HTTP/1.1 ").
        var parts = line.TrimEnd(' ', '\t').Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            error = $"Malformed request line: '{Truncate(line)}'";
            return false;
        }

        if (!IsToken(parts[0]))
        {
            error = $"Malformed request line: the method is not a token: '{Truncate(line)}'";
            return false;
        }

        if (HasControlCharacter(parts[1]))
        {
            error = "Malformed request line: the request target contains a control character.";
            return false;
        }

        var version = parts.Length > 2 ? parts[2] : defaultVersion;
        if (lenient && parts.Length > 2)
        {
            // Whatever follows the first word is not the version: a typed "HTTP/1.1 extra" is "HTTP/1.1".
            var space = version.IndexOfAny([' ', '\t']);
            if (space >= 0) version = version[..space];
            if (HasControlCharacter(version))
            {
                error = "Malformed request line: the version contains a control character.";
                return false;
            }
        }
        else if (!lenient && parts.Length > 2 && !IsHttp1Version(version))
        {
            error = $"Malformed request line: bad version '{Truncate(version)}'.";
            return false;
        }

        result = new HttpRequestLine(parts[0], parts[1], version);
        error = string.Empty;
        return true;
    }

    /// <summary>Parses a status line: version, a status code (see <see cref="TryParseStatusCode"/>)
    /// and an optional reason phrase.</summary>
    public static bool TryParseStatusLine(string line, bool lenient, out HttpStatusLine result, out string error)
    {
        result = default;
        var parts = lenient
            ? line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries)
            : line.Split(' ', 3);

        if (parts.Length < 2)
        {
            error = $"Malformed status line: '{Truncate(line)}'";
            return false;
        }

        var versionOk = lenient
            ? parts[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
            : IsHttp1Version(parts[0]);
        if (!versionOk)
        {
            error = $"Malformed status line: bad version '{Truncate(parts[0])}'.";
            return false;
        }

        if (!TryParseStatusCode(parts[1], out var status, MaxHttp1StatusCode))
        {
            error = $"Malformed status line: '{Truncate(parts[1])}' is not a status code from 100 to {MaxHttp1StatusCode}.";
            return false;
        }

        var reason = parts.Length > 2 ? parts[2] : null;
        if (reason is not null && HasControlCharacter(reason))
        {
            error = "Malformed status line: the reason phrase contains a control character.";
            return false;
        }

        result = new HttpStatusLine(parts[0], status, reason);
        error = string.Empty;
        return true;
    }

    /// <summary>"HTTP/" followed by a digit, a dot and a digit (RFC 9112 2.3).</summary>
    private static bool IsHttp1Version(string version) =>
        version.Length == 8
        && version.StartsWith("HTTP/", StringComparison.Ordinal)
        && char.IsAsciiDigit(version[5]) && version[6] == '.' && char.IsAsciiDigit(version[7]);

    /// <summary>A method (or header name) is a token: visible ASCII without delimiters (RFC 9110 5.6.2).</summary>
    internal static bool IsToken(string value)
    {
        if (value.Length == 0) return false;

        foreach (var c in value)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>NUL and the other C0 controls (HTAB excepted) and DEL: bytes that, copied into a line
    /// sent onward, would let one peer's text end a line for the next hop.</summary>
    private static bool HasControlCharacter(string value)
    {
        foreach (var c in value)
            if ((c < 0x20 && c != '\t') || c == 0x7F) return true;
        return false;
    }

    private static string Truncate(string value) => HttpParser.Truncate(value);
}
