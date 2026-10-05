using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Piper.Core.Http;

namespace Piper.Core.Sessions;

/// <summary>
/// Imports Fiddler SAZ archives, whose wire captures live at <c>raw/N_[cs].txt</c>. RAZ files are
/// the same layout under another extension and go through this same reader.
/// </summary>
/// <remarks>
/// An archive is a file someone sent us. Nothing it claims is believed: the sizes in its central
/// directory only ever reject an entry, never size a buffer, and every byte read is counted against
/// <see cref="SazImportLimits"/>. A bad entry is skipped with a warning; an archive that breaks a
/// limit as a whole is rejected with a <see cref="SazImportFailure"/> and imports nothing.
/// </remarks>
public static partial class SazImporter
{
    /// <summary>Warnings kept per import. A hostile archive can make one per entry.</summary>
    public const int MaxWarnings = 50;

    private static readonly Encoding HeaderEncoding = Encoding.Latin1;

    /// <summary>Digits allowed in a session number: nine always fit an <see cref="int"/>.</summary>
    private const int MaxSessionNumberDigits = 9;

    /// <summary>
    /// Headroom on the central-directory budget for what is read besides the directory itself: the
    /// end-of-central-directory record, which the runtime searches for in the last 64 KiB of the
    /// file, in blocks, plus the zip64 records beside it. Twice the search window, so a real
    /// archive never trips it.
    /// </summary>
    private const long EndRecordSearchAllowance = 128 * 1024;

    public static SazImportResult Import(string path, SazImportLimits? limits = null)
    {
        limits ??= SazImportLimits.Default;
        var sessions = new List<Session>();
        var warnings = new WarningLog();

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var directoryBudget = new ReadBudgetStream(file, limits.MaxCentralDirectoryBytes + EndRecordSearchAllowance);
            using var archive = new ZipArchive(directoryBudget, ZipArchiveMode.Read);

            // Newer runtimes read the central directory on first use of Entries, not in the
            // constructor, so it is forced here, inside the budget, before the budget is lifted.
            var entryCount = archive.Entries.Count;
            directoryBudget.Disarm();

            if (entryCount > limits.MaxEntries)
                throw new ArchiveLimitException(SazImportFailure.TooManyEntries, $"The archive has {entryCount:N0} entries.");

            var remainingBytes = limits.MaxTotalBytes;
            foreach (var (requestEntry, id) in ClientEntries(archive, warnings))
            {
                HttpRequestData request;
                try
                {
                    request = ParseRequest(ReadEntry(requestEntry, limits, ref remainingBytes), limits);
                    if (ClipBody(request, limits.MaxBodyBytes) is { } keptOfRequest)
                        warnings.Add($"Session {id}: request body of {keptOfRequest:N0} bytes was cut to the first {limits.MaxBodyBytes:N0}.");
                }
                catch (Exception ex) when (IsBadEntry(ex))
                {
                    warnings.Add($"Session {id}: {Describe(ex)}");
                    continue;
                }

                // A response entry can exist but be empty or corrupt (e.g. a "request-only"
                // capture with no response ever recorded). That must not discard an otherwise
                // valid request, so a bad response degrades to "no response" instead of failing
                // the whole session.
                HttpResponseData? response = null;
                var responseEntry = archive.GetEntry($"raw/{id}_s.txt");
                if (responseEntry is not null)
                {
                    try
                    {
                        response = ParseResponse(ReadEntry(responseEntry, limits, ref remainingBytes), limits);
                        if (ClipBody(response, limits.MaxBodyBytes) is { } keptOfResponse)
                            warnings.Add($"Session {id}: response body of {keptOfResponse:N0} bytes was cut to the first {limits.MaxBodyBytes:N0}.");
                    }
                    catch (Exception ex) when (IsBadEntry(ex))
                    {
                        warnings.Add($"Session {id}: response not captured ({Describe(ex)})");
                    }
                }

                sessions.Add(new Session
                {
                    Request = request,
                    Response = response,
                    IsHttps = request.Url?.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) == true,
                    State = response is null ? SessionState.Failed : SessionState.Complete,
                    Error = response is null ? "This SAZ session has no captured response." : null,
                    ClientEndpoint = "SAZ import",
                    ProcessName = "Fiddler SAZ",
                    IsImported = true,
                    Completed = DateTimeOffset.Now,
                });
            }
        }
        catch (ArchiveLimitException ex)
        {
            warnings.Add(ex.Message);
            return new SazImportResult([], warnings.ToList(), ex.Failure);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or NotSupportedException or ArgumentException)
        {
            // Not a zip, a truncated one, or a file that cannot be opened: nothing can be imported.
            // ArgumentException is here for the path (a name the file system rejects); nothing that
            // parses an entry can raise it, which is why it is not in IsBadEntry. File-system messages
            // embed the full local path, so the warning, which reaches the log, names only the type;
            // the message travels separately in FailureDetail for the dialog.
            warnings.Add($"The archive could not be read ({ex.GetType().Name}).");
            return new SazImportResult([], warnings.ToList(), SazImportFailure.Unreadable, ex.Message);
        }

        return new SazImportResult(sessions, warnings.ToList());
    }

    /// <summary>Client-side entries (<c>raw/N_c.txt</c>) in session order, skipping names that are not a number.</summary>
    private static List<(ZipArchiveEntry Entry, string Id)> ClientEntries(ZipArchive archive, WarningLog warnings)
    {
        var found = new List<(ZipArchiveEntry Entry, string Id, int Number)>();
        foreach (var entry in archive.Entries)
        {
            var match = ClientEntryName().Match(entry.FullName);
            if (!match.Success) continue;

            var digits = match.Groups[1].ValueSpan;
            if (!IsSessionNumber(digits) || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                warnings.Add($"Skipped entry \"{Printable(entry.FullName)}\": the session number must be 1 to {MaxSessionNumberDigits} digits.");
                continue;
            }

            found.Add((entry, match.Groups[1].Value, number));
        }

        // Stable, so two entries that share a number keep their archive order.
        return found.OrderBy(item => item.Number).Select(item => (item.Entry, item.Id)).ToList();
    }

    /// <summary>
    /// ASCII digits only, and few enough to fit an <see cref="int"/>. <c>\d</c> would also let in
    /// every other script's digits, which <see cref="int.Parse(string)"/> then refuses by throwing.
    /// </summary>
    private static bool IsSessionNumber(ReadOnlySpan<char> text)
    {
        if (text.Length is 0 or > MaxSessionNumberDigits) return false;
        foreach (var c in text)
            if (c is < '0' or > '9') return false;
        return true;
    }

    /// <summary>An entry name safe to put in a log line: printable ASCII, and short.</summary>
    private static string Printable(string name)
    {
        var text = new StringBuilder(Math.Min(name.Length, 48));
        foreach (var c in name.Take(48)) text.Append(c is >= ' ' and <= '~' ? c : '?');
        if (name.Length > 48) text.Append("...");
        return text.ToString();
    }

    /// <summary>
    /// What a single unusable entry looks like. Anything else is not the entry's fault and propagates.
    /// ArgumentException is deliberately absent: reading an entry and parsing its head use only
    /// <c>TryParse</c>, <c>Uri.TryCreate</c> and range-checked slicing, none of which throw it.
    /// </summary>
    private static bool IsBadEntry(Exception ex) =>
        ex is InvalidDataException or FormatException or HttpParseException or IOException or NotSupportedException;

    /// <summary>An exception as a warning. A file-system message names the full local path, so only its type is kept.</summary>
    private static string Describe(Exception ex) => ex is IOException ? ex.GetType().Name : ex.Message;

    /// <summary>
    /// Reads one entry, trusting neither its declared length nor how far it inflates: the output is
    /// counted as it is produced and reading stops the moment the entry passes its own cap or the
    /// archive runs out of its total.
    /// </summary>
    private static byte[] ReadEntry(ZipArchiveEntry entry, SazImportLimits limits, ref long remainingBytes)
    {
        // The declared length is only allowed to reject: a claim past the cap is refused up front,
        // and it never decides how much memory is set aside.
        if (entry.Length > limits.MaxEntryBytes)
            throw new InvalidDataException($"The entry claims {entry.Length:N0} bytes, over the {limits.MaxEntryBytes:N0} byte limit.");

        using var input = entry.Open();
        using var output = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long entryBytes = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            entryBytes += read;
            remainingBytes -= read;
            if (remainingBytes < 0)
                throw new ArchiveLimitException(SazImportFailure.TooLarge,
                    $"The archive holds more than {limits.MaxTotalBytes:N0} bytes once expanded.");
            if (entryBytes > limits.MaxEntryBytes)
                throw new InvalidDataException($"The entry expands past the {limits.MaxEntryBytes:N0} byte limit.");
            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    /// <summary>Keeps the first <paramref name="limit"/> bytes of a body; returns the length it had if it was cut.</summary>
    private static long? ClipBody(HttpMessage message, long limit)
    {
        if (limit <= 0 || message.Body.LongLength <= limit) return null;
        var original = message.Body.LongLength;
        message.KeepPrefix(limit);
        return original;
    }

    private static HttpRequestData ParseRequest(byte[] raw, SazImportLimits limits)
    {
        var (head, body) = SplitWireMessage(raw, limits);
        var lines = head.Replace("\r\n", "\n").Split('\n');
        if (!HttpSyntax.TryParseRequestLine(lines[0], lenient: true, "HTTP/1.0", out var start, out var startError))
            throw new HttpParseException(startError);

        var request = new HttpRequestData
        {
            Method = start.Method,
            RequestTarget = start.Target,
            HttpVersion = start.Version,
            Headers = HeaderCollection.Parse(string.Join('\n', lines.Skip(1))),
            Body = body,
        };
        request.Url = HttpParser.ResolveUrl(request, assumeHttps: true);
        return request;
    }

    private static HttpResponseData ParseResponse(byte[] raw, SazImportLimits limits)
    {
        var (head, body) = SplitWireMessage(raw, limits);
        var lines = head.Replace("\r\n", "\n").Split('\n');
        if (!HttpSyntax.TryParseStatusLine(lines[0], lenient: true, out var start, out var startError))
            throw new HttpParseException(startError);

        return new HttpResponseData
        {
            HttpVersion = start.Version,
            StatusCode = start.StatusCode,
            ReasonPhrase = start.Reason ?? string.Empty,
            Headers = HeaderCollection.Parse(string.Join('\n', lines.Skip(1))),
            Body = body,
        };
    }

    private static (string Head, byte[] Body) SplitWireMessage(byte[] raw, SazImportLimits limits)
    {
        var separator = FindHeaderEnd(raw, limits.MaxHeadBytes, out var bodyStart);
        if (separator < 0) throw new HttpParseException($"Missing header terminator within the first {limits.MaxHeadBytes:N0} bytes.");
        return (HeaderEncoding.GetString(raw, 0, separator), raw[bodyStart..]);
    }

    /// <summary>
    /// Finds the blank line that ends the head, looking no further than <paramref name="maxHeadBytes"/>
    /// in: a head is not megabytes long, and header folding is quadratic in the size of one.
    /// </summary>
    private static int FindHeaderEnd(byte[] bytes, int maxHeadBytes, out int bodyStart)
    {
        var window = (int)Math.Min(bytes.Length, (long)maxHeadBytes + 4);

        for (var index = 0; index < window - 3; index++)
        {
            if (bytes[index] != (byte)'\r' || bytes[index + 1] != (byte)'\n'
                || bytes[index + 2] != (byte)'\r' || bytes[index + 3] != (byte)'\n') continue;
            bodyStart = index + 4;
            return index;
        }

        for (var index = 0; index < window - 1; index++)
        {
            if (bytes[index] != (byte)'\n' || bytes[index + 1] != (byte)'\n') continue;
            bodyStart = index + 2;
            return index;
        }

        bodyStart = -1;
        return -1;
    }

    [GeneratedRegex(@"^raw/([^/]+)_c\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientEntryName();

    /// <summary>Thrown when the archive as a whole, rather than one entry, breaks a limit.</summary>
    private sealed class ArchiveLimitException(SazImportFailure failure, string message) : Exception(message)
    {
        public SazImportFailure Failure { get; } = failure;
    }

    /// <summary>Keeps the first <see cref="MaxWarnings"/> messages and counts the rest.</summary>
    private sealed class WarningLog
    {
        private readonly List<string> _kept = [];
        private int _suppressed;

        public void Add(string warning)
        {
            if (_kept.Count < MaxWarnings) _kept.Add(warning);
            else _suppressed++;
        }

        public List<string> ToList()
        {
            var all = new List<string>(_kept);
            if (_suppressed > 0) all.Add($"{_suppressed:N0} further warnings were not listed.");
            return all;
        }
    }

    /// <summary>
    /// Passes reads through until <see cref="Disarm"/>, throwing once more than a budget has been read.
    /// Wrapped around the archive while its central directory is read, because the runtime builds an
    /// object per entry the directory contains before anything can ask how many there are.
    /// </summary>
    private sealed class ReadBudgetStream(Stream inner, long budget) : Stream
    {
        private long _remaining = budget;
        private bool _armed = true;

        public void Disarm() => _armed = false;

        private void Charge(int bytes)
        {
            if (!_armed) return;
            _remaining -= bytes;
            if (_remaining < 0)
                throw new ArchiveLimitException(SazImportFailure.TooManyEntries, "The archive's central directory is too large.");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Charge(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = inner.Read(buffer);
            Charge(read);
            return read;
        }

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// What one import is allowed to cost. The defaults suit a real capture; tests shrink them.
/// </summary>
public sealed record SazImportLimits
{
    public static SazImportLimits Default { get; } = new();

    /// <summary>Entries in the archive. Two per session plus whatever else Fiddler stores.</summary>
    public int MaxEntries { get; init; } = 200_000;

    /// <summary>Bytes one entry may inflate to, however small it says it is.</summary>
    public long MaxEntryBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Bytes the whole archive may inflate to. The same as the session store's body budget.</summary>
    public long MaxTotalBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Bytes of central directory read before the archive is given up on.</summary>
    public long MaxCentralDirectoryBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Bytes of a request or response head. Nothing legitimate is near this.</summary>
    public int MaxHeadBytes { get; init; } = 64 * 1024;

    /// <summary>
    /// How much of each body is kept, as a captured body is; the rest is dropped and the message
    /// reports the length it had. 0 keeps every body.
    /// </summary>
    public long MaxBodyBytes { get; init; } = 32L * 1024 * 1024;
}

/// <summary>Why a whole archive was refused. Individual bad entries are warnings, not failures.</summary>
public enum SazImportFailure
{
    None,

    /// <summary>Not a readable zip: missing, truncated, not an archive, or refused by the file system.</summary>
    Unreadable,

    /// <summary>More entries than <see cref="SazImportLimits.MaxEntries"/>, or a central directory too large to be honest.</summary>
    TooManyEntries,

    /// <summary>Expands to more than <see cref="SazImportLimits.MaxTotalBytes"/>.</summary>
    TooLarge,
}

/// <summary>
/// SAZ import output. Invalid individual sessions are reported as warnings and skipped; an archive
/// that breaks a limit as a whole is refused (<see cref="Failure"/>) and yields no sessions.
/// </summary>
/// <remarks>
/// <see cref="FailureDetail"/> is the underlying error text for a person to read in a dialog. It can
/// name a local path, so it is kept out of <see cref="Warnings"/>, which are logged.
/// </remarks>
public sealed record SazImportResult(
    IReadOnlyList<Session> Sessions, IReadOnlyList<string> Warnings, SazImportFailure Failure = SazImportFailure.None,
    string? FailureDetail = null);
