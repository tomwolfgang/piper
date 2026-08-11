using System.Text;

namespace Piper.Core.Http;

/// <summary>Extracts the user-facing fields from an HTTP request's query string and HTML form body.</summary>
public static class WebFormParser
{
    /// <summary>A parsed request field. Binary multipart content is kept out of <see cref="Value"/>.</summary>
    public sealed record Field(
        string Source,
        string Name,
        string Value,
        string? ContentType = null,
        byte[]? BinaryData = null,
        string? FileName = null)
    {
        public bool HasBinaryData => BinaryData is not null;
    }

    /// <summary>
    /// Reads query parameters plus <c>application/x-www-form-urlencoded</c> and
    /// <c>multipart/form-data</c> request bodies. Unknown body formats deliberately return no
    /// fields rather than guessing at a binary protocol.
    /// </summary>
    public static IReadOnlyList<Field> Parse(HttpRequestData request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fields = new List<Field>();
        AddUrlEncoded(fields, QueryFrom(request), "Query");

        var contentType = request.ContentType ?? string.Empty;
        if (contentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            AddUrlEncoded(fields, ContentCodec.CharsetFor(contentType).GetString(request.DecodedBody), "Form");
        }
        else if (contentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase)
                 && TryGetParameter(contentType, "boundary", out var boundary))
        {
            AddMultipart(fields, request.DecodedBody, boundary);
        }

        return fields;
    }

    private static string QueryFrom(HttpRequestData request)
    {
        if (request.Url is { } url) return url.Query;

        var target = request.RequestTarget;
        var start = target.IndexOf('?');
        if (start < 0) return string.Empty;
        var end = target.IndexOf('#', start);
        return target[start..(end < 0 ? target.Length : end)];
    }

    private static void AddUrlEncoded(List<Field> fields, string value, string source)
    {
        if (value.StartsWith('?')) value = value[1..];
        if (value.Length == 0) return;

        foreach (var pair in value.Split('&', StringSplitOptions.None))
        {
            if (pair.Length == 0) continue;
            var separator = pair.IndexOf('=');
            var name = separator < 0 ? pair : pair[..separator];
            var fieldValue = separator < 0 ? string.Empty : pair[(separator + 1)..];
            fields.Add(new Field(source, Decode(name), Decode(fieldValue)));
        }
    }

    private static void AddMultipart(List<Field> fields, byte[] body, string boundary)
    {
        var marker = Encoding.ASCII.GetBytes("--" + boundary);
        var position = FindBoundary(body, marker, 0);
        while (position >= 0)
        {
            var partStart = position + marker.Length;
            if (StartsWith(body, partStart, "--"u8)) break;
            if (StartsWith(body, partStart, "\r\n"u8)) partStart += 2;
            else if (StartsWith(body, partStart, "\n"u8)) partStart++;

            var nextBoundary = FindBoundary(body, marker, partStart);
            if (nextBoundary < 0) break;

            var partEnd = nextBoundary;
            if (partEnd >= 2 && body[partEnd - 2] == '\r' && body[partEnd - 1] == '\n') partEnd -= 2;
            else if (partEnd >= 1 && body[partEnd - 1] == '\n') partEnd--;

            AddMultipartPart(fields, body.AsSpan(partStart, partEnd - partStart));
            position = nextBoundary;
        }
    }

    private static void AddMultipartPart(List<Field> fields, ReadOnlySpan<byte> part)
    {
        var split = IndexOf(part, "\r\n\r\n"u8);
        var separatorLength = 4;
        if (split < 0)
        {
            split = IndexOf(part, "\n\n"u8);
            separatorLength = 2;
        }
        if (split < 0) return;

        var headers = HeaderCollection.Parse(Encoding.Latin1.GetString(part[..split]));
        var disposition = headers["Content-Disposition"];
        if (disposition is null || !TryGetParameter(disposition, "name", out var name)) return;

        var data = part[(split + separatorLength)..].ToArray();
        var contentType = headers["Content-Type"];
        var hasFileName = TryGetParameter(disposition, "filename", out var fileName);
        var isBinary = hasFileName || !IsTextual(contentType, data);
        if (isBinary)
        {
            var description = hasFileName
                ? $"[binary file: {fileName}; {data.Length:N0} bytes]"
                : $"[binary data: {data.Length:N0} bytes]";
            fields.Add(new Field("Form", name, description, contentType, data, hasFileName ? fileName : null));
            return;
        }

        fields.Add(new Field("Form", name, ContentCodec.CharsetFor(contentType).GetString(data), contentType));
    }

    private static bool StartsWith(byte[] bytes, int offset, ReadOnlySpan<byte> value) =>
        offset >= 0 && offset <= bytes.Length - value.Length && bytes.AsSpan(offset, value.Length).SequenceEqual(value);

    private static bool IsTextual(string? contentType, byte[] data)
    {
        if (!ContentCodec.LooksTextual(contentType, data)) return false;

        try
        {
            var charset = ContentCodec.CharsetFor(contentType);
            var strict = Encoding.GetEncoding(charset.WebName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            _ = strict.GetString(data);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int FindBoundary(byte[] bytes, ReadOnlySpan<byte> marker, int start)
    {
        for (var position = IndexOf(bytes, marker, start); position >= 0; position = IndexOf(bytes, marker, position + marker.Length))
        {
            var beginsLine = position == 0
                || (position >= 2 && bytes[position - 2] == '\r' && bytes[position - 1] == '\n')
                || (position >= 1 && bytes[position - 1] == '\n');
            var endsBoundary = StartsWith(bytes, position + marker.Length, "--"u8)
                || StartsWith(bytes, position + marker.Length, "\r\n"u8)
                || StartsWith(bytes, position + marker.Length, "\n"u8);
            if (beginsLine && endsBoundary) return position;
        }
        return -1;
    }

    private static int IndexOf(byte[] bytes, ReadOnlySpan<byte> value, int start)
    {
        start = Math.Max(0, start);
        if (start > bytes.Length) return -1;
        var index = IndexOf(bytes.AsSpan(start), value);
        return index < 0 ? -1 : index + start;
    }

    private static int IndexOf(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> value)
    {
        if (value.Length == 0 || bytes.Length < value.Length) return -1;
        for (var index = 0; index <= bytes.Length - value.Length; index++)
            if (bytes.Slice(index, value.Length).SequenceEqual(value)) return index;
        return -1;
    }

    private static bool TryGetParameter(string headerValue, string parameter, out string value)
    {
        value = string.Empty;
        foreach (var segment in headerValue.Split(';').Skip(1))
        {
            var separator = segment.IndexOf('=');
            if (separator < 0) continue;
            if (!segment[..separator].Trim().Equals(parameter, StringComparison.OrdinalIgnoreCase)) continue;

            value = segment[(separator + 1)..].Trim().Trim('"');
            return value.Length > 0;
        }
        return false;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}
