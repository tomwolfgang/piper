using System.IO.Compression;
using System.Text;
using Piper.Core.Http;

namespace Piper.Core.Sessions;

/// <summary>
/// The messages of the sessions being exported, read once on the caller's thread so that a capture
/// still running cannot change what is written while the archive is built elsewhere. Bodies are
/// shared, not copied: the proxy replaces a body array rather than writing into one.
/// </summary>
public sealed class SazExportSnapshot
{
    internal SazExportSnapshot(IReadOnlyList<(SazMessage Request, SazMessage? Response)> sessions) =>
        Sessions = sessions;

    internal IReadOnlyList<(SazMessage Request, SazMessage? Response)> Sessions { get; }

    /// <summary>The number of sessions the archive will hold.</summary>
    public int Count => Sessions.Count;
}

/// <summary>One message as it will be written: its headers already made consistent with <see cref="Body"/>.</summary>
internal sealed record SazMessage(string StartLine, HeaderCollection Headers, byte[] Body);

/// <summary>Writes Fiddler-compatible SAZ archives containing captured HTTP exchanges.</summary>
public static class SazExporter
{
    private static readonly Encoding HeaderEncoding = Encoding.Latin1;

    /// <summary>Bodies are written in slices of this size so that Cancel is noticed inside a large one.</summary>
    private const int BodySliceBytes = 1024 * 1024;

    /// <summary>
    /// Media types whose bytes are already compressed. Deflating them again costs CPU and saves
    /// nothing, so their entries are stored.
    /// </summary>
    private static readonly HashSet<string> CompressedMediaTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg", "image/png", "image/gif", "image/webp", "image/avif", "image/heic", "image/heif",
        "audio/mpeg", "audio/aac", "audio/ogg", "audio/opus", "audio/webm", "audio/mp4", "audio/flac",
        "application/zip", "application/gzip", "application/x-gzip", "application/zstd",
        "application/x-7z-compressed", "application/vnd.rar", "application/x-rar-compressed",
        "application/x-bzip2", "application/x-xz", "font/woff", "font/woff2",
    };

    /// <summary>
    /// Writes every session with a request to <paramref name="path"/>. Sessions without a
    /// response are retained as request-only archive entries, matching their captured state.
    /// </summary>
    /// <returns>The number of sessions written.</returns>
    public static int Export(string path, IEnumerable<Session> sessions) => Export(path, Snapshot(sessions));

    /// <summary>
    /// Reads the request and response of every session that has a request. Call it where the
    /// sessions are owned (the UI thread), then pass the result to <see cref="Export(string, SazExportSnapshot, IProgress{int}?, CancellationToken)"/>
    /// on any thread.
    /// </summary>
    public static SazExportSnapshot Snapshot(IEnumerable<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var captured = new List<(SazMessage, SazMessage?)>();
        foreach (var session in sessions)
        {
            // Each property is read once: the proxy can still assign a response while this runs.
            if (session.Request is not { } request) continue;
            var response = session.Response;
            captured.Add((CaptureRequest(request), response is null ? null : CaptureResponse(response)));
        }

        if (captured.Count == 0)
            throw new ArgumentException("At least one session with a request is required.", nameof(sessions));
        return new SazExportSnapshot(captured);
    }

    /// <summary>
    /// Writes <paramref name="snapshot"/> to <paramref name="path"/>. The archive is built in a
    /// temporary file next to the target and moved over it only once complete, so a failure or a
    /// cancellation leaves an existing file untouched and deletes the temporary one.
    /// </summary>
    /// <param name="progress">Receives the number of sessions written so far, at most about 100 times.</param>
    /// <returns>The number of sessions written.</returns>
    public static int Export(
        string path, SazExportSnapshot snapshot, IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        Export(path, snapshot, progress, cancellationToken, wrapTarget: null);

    /// <param name="wrapTarget">Test seam: wraps the temporary file's stream, for injecting a write failure.</param>
    internal static int Export(
        string path, SazExportSnapshot snapshot, IProgress<int>? progress, CancellationToken cancellationToken,
        Func<Stream, Stream>? wrapTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);

        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)
            ?? throw new ArgumentException("The path must name a file.", nameof(path));

        // A read-only or locked target would only fail at the final move. Find out before spending
        // the whole export on it; opening for write without writing leaves the file as it is.
        if (File.Exists(target))
            using (new FileStream(target, FileMode.Open, FileAccess.Write, FileShare.None)) { }

        // Same directory, so the final move is a rename on one volume rather than a copy.
        var temp = Path.Combine(directory, $"{Path.GetFileName(target)}.{Path.GetRandomFileName()}.tmp");
        var created = false;
        var replaced = false;
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                using (var archive = new ZipArchive(wrapTarget?.Invoke(file) ?? file, ZipArchiveMode.Create, leaveOpen: true))
                    WriteSessions(archive, snapshot, progress, cancellationToken);
                file.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, target, overwrite: true);
            replaced = true;
        }
        finally
        {
            if (created && !replaced) TryDelete(temp);
        }

        return snapshot.Count;
    }

    private static void WriteSessions(
        ZipArchive archive, SazExportSnapshot snapshot, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        var sessions = snapshot.Sessions;
        var reportedPercent = -1;
        for (var index = 0; index < sessions.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (request, response) = sessions[index];
            var entryNumber = index + 1;
            WriteEntry(archive, $"raw/{entryNumber}_c.txt", request, cancellationToken);
            if (response is not null)
                WriteEntry(archive, $"raw/{entryNumber}_s.txt", response, cancellationToken);

            var percent = (int)(entryNumber * 100L / sessions.Count);
            if (percent == reportedPercent) continue;
            reportedPercent = percent;
            progress?.Report(entryNumber);
        }
    }

    /// <summary>
    /// Marks a message whose body was only partly kept. A reader that does not know the header
    /// still gets a self-consistent message, and one that reads it is not misled into treating a
    /// fragment as the whole body -- mitmproxy's HAR export writes streamed bodies as zero bytes,
    /// indistinguishable from empty, which is the mistake being avoided here.
    /// </summary>
    private const string TruncationHeader = "X-Piper-Body-Truncated";

    private static SazMessage CaptureRequest(HttpRequestData request)
    {
        // A SAZ reader cannot infer the original scheme from an origin-form request target.
        // Emit the full URL when Piper has resolved it, while retaining the original target as a
        // safe fallback for malformed or incomplete captures.
        var target = request.Url?.AbsoluteUri ?? request.RequestTarget;
        return Capture($"{request.Method} {target} {request.HttpVersion}", request.Headers, request.Body, request.BodyTotalLength);
    }

    private static SazMessage CaptureResponse(HttpResponseData response) =>
        Capture(response.StartLine, response.Headers, response.Body, response.BodyTotalLength);

    private static SazMessage Capture(string startLine, HeaderCollection originalHeaders, byte[] body, long totalLength)
    {
        // Piper stores de-chunked bodies. Make the archive internally consistent rather than
        // claiming a chunked transfer while writing plain body bytes.
        var headers = originalHeaders.Clone();
        var hadChunkedTransfer = headers.HasToken("Transfer-Encoding", "chunked");
        if (hadChunkedTransfer) headers.Remove("Transfer-Encoding");
        if (hadChunkedTransfer || headers.Contains("Content-Length"))
            headers.Set("Content-Length", body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (body.LongLength < totalLength)
            headers.Set(TruncationHeader, totalLength.ToString(System.Globalization.CultureInfo.InvariantCulture));

        return new SazMessage(startLine, headers, body);
    }

    private static byte[] SerializeHead(SazMessage message) =>
        HeaderEncoding.GetBytes(message.StartLine + "\r\n" + message.Headers.ToRawString() + "\r\n");

    private static void WriteEntry(ZipArchive archive, string name, SazMessage message, CancellationToken cancellationToken)
    {
        var level = IsAlreadyCompressed(message.Headers) ? CompressionLevel.NoCompression : CompressionLevel.Fastest;
        var entry = archive.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(SerializeHead(message));
        var body = message.Body;
        for (var offset = 0; offset < body.Length; offset += BodySliceBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.Write(body, offset, Math.Min(BodySliceBytes, body.Length - offset));
        }
    }

    /// <summary>True when the body carries a Content-Encoding or is a media type that is compressed by nature.</summary>
    private static bool IsAlreadyCompressed(HeaderCollection headers)
    {
        var encoding = headers["Content-Encoding"]?.Trim();
        if (!string.IsNullOrEmpty(encoding) && !encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
            return true;

        var contentType = headers["Content-Type"];
        if (contentType is null) return false;
        var separator = contentType.IndexOf(';');
        var mediaType = (separator < 0 ? contentType : contentType[..separator]).Trim();
        return mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) || CompressedMediaTypes.Contains(mediaType);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The export has already failed and that failure is what the caller reports. A temporary
            // file that cannot be removed (an antivirus scan holding it, say) keeps its .tmp suffix
            // next to the target; there is nothing further to try.
        }
    }
}
