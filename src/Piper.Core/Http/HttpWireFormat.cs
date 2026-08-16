using System.Text;

namespace Piper.Core.Http;

/// <summary>
/// Reads and writes messages in the raw form they travel on the wire - the same bytes a SAZ archive
/// stores, so a response captured once can be saved to a file and served back later.
/// </summary>
public static class HttpWireFormat
{
    /// <summary>The exact bytes of a response, head and body, ready to be written to a file.</summary>
    public static byte[] Serialize(HttpResponseData response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.ToBytes();
    }

    /// <summary>
    /// Parses a complete raw response. Returns false with a reason rather than throwing: the input is
    /// a file the user pointed a rule at, so a bad one has to be reportable.
    /// </summary>
    public static bool TryParseResponse(byte[] raw, out HttpResponseData response, out string error)
    {
        response = new HttpResponseData();
        error = string.Empty;

        if (raw is null || raw.Length == 0)
        {
            error = "the file is empty";
            return false;
        }

        var (headEnd, bodyStart) = FindHeaderEnd(raw);
        // Latin1 keeps every byte of the head recoverable; header values are not required to be UTF-8.
        var head = Encoding.Latin1.GetString(raw, 0, headEnd);
        var lines = head.Split(["\r\n", "\n"], StringSplitOptions.None);
        if (lines.Length == 0 || lines[0].Length == 0)
        {
            error = "no status line";
            return false;
        }

        var statusLine = lines[0].Split(' ', 3);
        if (statusLine.Length < 2 || !statusLine[0].StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(statusLine[1], out var status))
        {
            error = $"'{lines[0]}' is not an HTTP status line";
            return false;
        }

        response.HttpVersion = statusLine[0];
        response.StatusCode = status;
        response.ReasonPhrase = statusLine.Length > 2 ? statusLine[2] : ReasonPhrases.ForOrClass(status);
        response.Headers = lines.Length > 1
            ? HeaderCollection.Parse(string.Join("\r\n", lines[1..]))
            : new HeaderCollection();
        response.Body = raw[bodyStart..];
        return true;
    }

    /// <summary>
    /// Renders a response as the text an editor should show: head, blank line, body.
    /// </summary>
    /// <remarks>
    /// The body is decoded first and Content-Encoding dropped with it - editing gzipped bytes as
    /// text helps nobody, and leaving the header on would describe the edited body incorrectly.
    /// Passing null produces a usable starting point rather than an empty box.
    /// </remarks>
    public static string ToEditableText(HttpResponseData? response)
    {
        response ??= HttpResponseData.Canned(200, Encoding.UTF8.GetBytes("Replace me.\r\n"), "text/plain; charset=utf-8");

        var decoded = response.DecodedBody;
        var editable = new HttpResponseData
        {
            HttpVersion = "HTTP/1.1",
            StatusCode = response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
            Headers = response.Headers.Clone(),
            Body = decoded,
        };

        editable.Headers.Remove("Content-Encoding");
        editable.Headers.Remove("Transfer-Encoding");
        editable.Headers.Set("Content-Length", decoded.Length.ToString());

        return editable.HeadAsText() + editable.BodyAsText(decoded);
    }

    /// <summary>
    /// Turns edited text back into response bytes, or explains why it is not a response.
    /// </summary>
    /// <remarks>
    /// Line endings are normalised to CRLF first: an editor will happily hand back lone newlines,
    /// and a bare LF on the blank line would fold the body into the header block. Content-Length is
    /// restated so an edited body is framed correctly however the user left the header.
    /// </remarks>
    public static bool TryParseEditedResponse(string? text, out byte[] raw, out string error)
    {
        raw = [];

        // Latin1 round-trips every byte value a text editor can hold, matching how raw messages read.
        var bytes = Encoding.Latin1.GetBytes((text ?? string.Empty).ReplaceLineEndings("\r\n"));
        if (!TryParseResponse(bytes, out var parsed, out error)) return false;

        parsed.Headers.Set("Content-Length", parsed.Body.Length.ToString());
        raw = parsed.ToBytes();
        return true;
    }

    /// <summary>Locates the blank line ending the head, tolerating LF-only files a text editor may produce.</summary>
    private static (int HeadEnd, int BodyStart) FindHeaderEnd(byte[] raw)
    {
        for (var i = 0; i + 3 < raw.Length; i++)
            if (raw[i] == '\r' && raw[i + 1] == '\n' && raw[i + 2] == '\r' && raw[i + 3] == '\n')
                return (i, i + 4);

        for (var i = 0; i + 1 < raw.Length; i++)
            if (raw[i] == '\n' && raw[i + 1] == '\n')
                return (i, i + 2);

        return (raw.Length, raw.Length);
    }
}
