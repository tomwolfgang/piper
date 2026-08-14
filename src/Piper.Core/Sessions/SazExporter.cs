using System.IO.Compression;
using System.Text;
using Piper.Core.Http;

namespace Piper.Core.Sessions;

/// <summary>Writes Fiddler-compatible SAZ archives containing captured HTTP exchanges.</summary>
public static class SazExporter
{
    private static readonly Encoding HeaderEncoding = Encoding.Latin1;

    /// <summary>
    /// Writes every session with a request to <paramref name="path"/>. Sessions without a
    /// response are retained as request-only archive entries, matching their captured state.
    /// </summary>
    /// <returns>The number of sessions written.</returns>
    public static int Export(string path, IEnumerable<Session> sessions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(sessions);

        var exportable = sessions.Where(session => session.Request is not null).ToArray();
        if (exportable.Length == 0)
            throw new ArgumentException("At least one session with a request is required.", nameof(sessions));

        // Create replaces a file after the SaveFileDialog's overwrite confirmation. ZipFile.Open
        // uses CreateNew for a new archive, which would reject that normal save workflow.
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);

        for (var index = 0; index < exportable.Length; index++)
        {
            var session = exportable[index];
            var entryNumber = index + 1;
            WriteEntry(archive, $"raw/{entryNumber}_c.txt", SerializeRequest(session.Request!));
            if (session.Response is { } response)
                WriteEntry(archive, $"raw/{entryNumber}_s.txt", SerializeResponse(response));
        }

        return exportable.Length;
    }

    private static byte[] SerializeRequest(HttpRequestData request)
    {
        // A SAZ reader cannot infer the original scheme from an origin-form request target.
        // Emit the full URL when Piper has resolved it, while retaining the original target as a
        // safe fallback for malformed or incomplete captures.
        var target = request.Url?.AbsoluteUri ?? request.RequestTarget;
        return SerializeMessage($"{request.Method} {target} {request.HttpVersion}", request.Headers, request.Body);
    }

    private static byte[] SerializeResponse(HttpResponseData response) =>
        SerializeMessage(response.StartLine, response.Headers, response.Body);

    private static byte[] SerializeMessage(string startLine, HeaderCollection originalHeaders, byte[] body)
    {
        // Piper stores de-chunked bodies. Make the archive internally consistent rather than
        // claiming a chunked transfer while writing plain body bytes.
        var headers = originalHeaders.Clone();
        var hadChunkedTransfer = headers.HasToken("Transfer-Encoding", "chunked");
        if (hadChunkedTransfer) headers.Remove("Transfer-Encoding");
        if (hadChunkedTransfer || headers.Contains("Content-Length"))
            headers.Set("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var head = HeaderEncoding.GetBytes(startLine + "\r\n" + headers.ToRawString() + "\r\n");
        if (body.Length == 0) return head;

        var message = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, message, 0, head.Length);
        Buffer.BlockCopy(body, 0, message, head.Length, body.Length);
        return message;
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] content)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }
}
