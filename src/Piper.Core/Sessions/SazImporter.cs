using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Piper.Core.Http;

namespace Piper.Core.Sessions;

/// <summary>Imports Fiddler SAZ archives, whose wire captures live at <c>raw/N_[cs].txt</c>.</summary>
public static partial class SazImporter
{
    private static readonly Encoding HeaderEncoding = Encoding.Latin1;

    public static SazImportResult Import(string path)
    {
        var sessions = new List<Session>();
        var warnings = new List<string>();

        try
        {
            using var archive = ZipFile.OpenRead(path);
            var requests = archive.Entries
                .Select(entry => (Entry: entry, Match: ClientEntryName().Match(entry.FullName)))
                .Where(item => item.Match.Success)
                .OrderBy(item => int.Parse(item.Match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));

            foreach (var (requestEntry, match) in requests)
            {
                var id = match.Groups[1].Value;
                try
                {
                    var request = ParseRequest(ReadEntry(requestEntry));
                    var responseEntry = archive.GetEntry($"raw/{id}_s.txt");
                    var response = responseEntry is null ? null : ParseResponse(ReadEntry(responseEntry));
                    var now = DateTimeOffset.Now;
                    sessions.Add(new Session
                    {
                        Request = request,
                        Response = response,
                        IsHttps = request.Url?.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) == true,
                        State = response is null ? SessionState.Failed : SessionState.Complete,
                        Error = response is null ? "This SAZ session has no captured response." : null,
                        ClientEndpoint = "SAZ import",
                        ProcessName = "Fiddler SAZ",
                        Completed = now,
                    });
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or HttpParseException)
                {
                    warnings.Add($"Session {id}: {ex.Message}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            warnings.Add(ex.Message);
        }

        return new SazImportResult(sessions, warnings);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        using var input = entry.Open();
        using var output = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
        input.CopyTo(output);
        return output.ToArray();
    }

    private static HttpRequestData ParseRequest(byte[] raw)
    {
        var (head, body) = SplitWireMessage(raw);
        var lines = head.Replace("\r\n", "\n").Split('\n');
        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new HttpParseException("Malformed request line.");

        var request = new HttpRequestData
        {
            Method = parts[0],
            RequestTarget = parts[1],
            HttpVersion = parts.Length > 2 ? parts[2] : "HTTP/1.0",
            Headers = HeaderCollection.Parse(string.Join('\n', lines.Skip(1))),
            Body = body,
        };
        request.Url = HttpParser.ResolveUrl(request, assumeHttps: true);
        return request;
    }

    private static HttpResponseData ParseResponse(byte[] raw)
    {
        var (head, body) = SplitWireMessage(raw);
        var lines = head.Replace("\r\n", "\n").Split('\n');
        var parts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !int.TryParse(parts[1], out var status))
            throw new HttpParseException("Malformed response line.");

        return new HttpResponseData
        {
            HttpVersion = parts[0],
            StatusCode = status,
            ReasonPhrase = parts.Length > 2 ? parts[2] : string.Empty,
            Headers = HeaderCollection.Parse(string.Join('\n', lines.Skip(1))),
            Body = body,
        };
    }

    private static (string Head, byte[] Body) SplitWireMessage(byte[] raw)
    {
        var separator = FindHeaderEnd(raw, out var bodyStart);
        if (separator < 0) throw new HttpParseException("Missing header terminator.");
        return (HeaderEncoding.GetString(raw, 0, separator), raw[bodyStart..]);
    }

    private static int FindHeaderEnd(byte[] bytes, out int bodyStart)
    {
        for (var index = 0; index < bytes.Length - 3; index++)
        {
            if (bytes[index] != (byte)'\r' || bytes[index + 1] != (byte)'\n'
                || bytes[index + 2] != (byte)'\r' || bytes[index + 3] != (byte)'\n') continue;
            bodyStart = index + 4;
            return index;
        }

        for (var index = 0; index < bytes.Length - 1; index++)
        {
            if (bytes[index] != (byte)'\n' || bytes[index + 1] != (byte)'\n') continue;
            bodyStart = index + 2;
            return index;
        }

        bodyStart = -1;
        return -1;
    }

    [GeneratedRegex(@"^raw/(\d+)_c\.txt$", RegexOptions.CultureInvariant)]
    private static partial Regex ClientEntryName();
}

/// <summary>SAZ import output. Invalid individual sessions are reported as warnings and skipped.</summary>
public sealed record SazImportResult(IReadOnlyList<Session> Sessions, IReadOnlyList<string> Warnings);
