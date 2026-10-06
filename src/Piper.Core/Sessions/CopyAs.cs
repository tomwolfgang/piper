using System.Globalization;
using System.Text;
using Piper.Core.Http;

namespace Piper.Core.Sessions;

/// <summary>A command or snippet a captured request can be copied as.</summary>
public enum CopyAsTarget
{
    /// <summary>The JavaScript <c>fetch</c> function.</summary>
    JavaScriptFetch,
}

/// <summary>Something about the copied snippet that differs from the capture, or a part left out.</summary>
public enum CopyAsNote
{
    /// <summary>Only a prefix of the request body was captured, so no body is written.</summary>
    BodyNotCaptured,

    /// <summary>Decoding the body hit its size cap or a corrupt stream, so no body is written.</summary>
    BodyDecodeCut,

    /// <summary>The body is larger than <see cref="CopyAs.MaxInlineBodyBytes"/>, so no body is written.</summary>
    BodyTooLarge,

    /// <summary>The body is binary or unsafe to paste as text and is written as base64.</summary>
    BodyAsBase64,

    /// <summary>A header with a name or value that cannot be written safely was left out.</summary>
    HeadersSkipped,

    /// <summary>Repeated headers were merged because the target cannot repeat one.</summary>
    DuplicateHeadersMerged,

    /// <summary>A Host header that differs from the URL could not be carried to the target.</summary>
    HostHeaderDropped,

    /// <summary>An Upgrade header (a WebSocket handshake, say) was not copied, so the replay will not upgrade.</summary>
    UpgradeDropped,
}

/// <summary>The text to put on the clipboard, and what a person should be told about it.</summary>
public sealed record CopyAsResult(string Text, IReadOnlyList<CopyAsNote> Notes);

/// <summary>
/// Turns a captured request into a command another tool can replay, with one escaper per target.
/// </summary>
/// <remarks>
/// <para>
/// The result is pasted into a shell or an editor and run, so every captured value (the URL, each
/// header name and value, the body, even the method) is hostile input. Each target has its own
/// escaper that turns a value into one literal of that target's language and nothing else: no value
/// is ever concatenated into a command as text. A value that no escaper can make safe (a header
/// name that is not an RFC 9110 token, a value with a CR, LF or NUL) is left out and reported as a
/// <see cref="CopyAsNote"/> instead of being written. Cookies and Authorization are copied exactly as
/// captured, because copying them is the point of the command; nothing here logs a value.
/// </para>
/// <para>
/// The body written is the decoded one, with Content-Encoding, Content-Length and the other headers
/// the replaying tool manages itself left out, so the pasted command sends what the client meant
/// rather than a decoded body labelled as compressed. A body that is not plain UTF-8 text is written
/// as base64 and decoded by the snippet, never pasted raw. Comments in the output are fixed text and
/// numbers; they never repeat captured data.
/// </para>
/// </remarks>
public static class CopyAs
{
    /// <summary>The largest body written into a snippet; a bigger one is left out with a comment.</summary>
    public const int MaxInlineBodyBytes = 64 * 1024;

    // Headers that belong to one connection or to the replaying tool rather than to the request.
    private static readonly HashSet<string> ManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade", "HTTP2-Settings",
    };

    /// <summary>
    /// Builds the snippet for <paramref name="target"/>, or null when the request cannot be expressed:
    /// no absolute http or https URL, or a method that is not a token.
    /// </summary>
    public static CopyAsResult? Build(HttpRequestData request, CopyAsTarget target)
    {
        ArgumentNullException.ThrowIfNull(request);

        var model = Prepare(request, target);
        if (model is null) return null;

        var text = target switch
        {
            CopyAsTarget.JavaScriptFetch => RenderJavaScript(model),
            _ => throw new ArgumentOutOfRangeException(nameof(target)),
        };
        return new CopyAsResult(text, model.Notes);
    }

    // ---------------------------------------------------------------- model

    private enum BodyKind { None, Text, Binary, Omitted }

    private readonly record struct HeaderValue(string Text, byte[] Bytes);

    private sealed class Model(CopyAsTarget target, string method, string url)
    {
        public CopyAsTarget Target { get; } = target;
        public string Method { get; } = method;
        public string Url { get; } = url;
        public List<(string Name, HeaderValue Value)> Headers { get; } = [];
        public BodyKind Kind { get; set; }
        public byte[] Body { get; set; } = [];
        public string BodyText { get; set; } = string.Empty;
        public List<string> Comments { get; } = [];
        public List<CopyAsNote> Notes { get; } = [];

        public bool HasBody => Kind is BodyKind.Text or BodyKind.Binary;
        public bool IsHead => Method.Equals("HEAD", StringComparison.Ordinal);
        public bool HasHeader(string name) => Headers.Exists(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        public void Note(CopyAsNote note, string? comment)
        {
            // Said once however many headers triggered it.
            if (Notes.Contains(note)) return;
            Notes.Add(note);
            if (comment is not null) Comments.Add(comment);
        }
    }

    private static Model? Prepare(HttpRequestData request, CopyAsTarget target)
    {
        if (request.Url is not { IsAbsoluteUri: true } uri || uri.Scheme is not ("http" or "https")) return null;
        if (!IsToken(request.Method)) return null;

        var url = uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped);
        foreach (var c in url)
            if (c is < ' ' or > '~') return null;

        var model = new Model(target, request.Method, url);

        var bodyWasDecoded = PrepareBody(request, model);

        foreach (var header in request.Headers)
        {
            var name = header.Name;

            // An HTTP/2 pseudo-header (:authority and friends) is the transport's, not a header to send.
            if (name.StartsWith(':')) continue;

            if (!IsToken(name) || !TryNormalise(header.Value ?? string.Empty, out var value))
            {
                model.Note(CopyAsNote.HeadersSkipped, Comments.HeadersSkipped);
                continue;
            }

            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                // A Host that merely repeats the URL is the tool's job. One that differs is a deliberate
                // override: curl can send it, the other targets cannot.
                if (SameAuthority(value.Text, uri)) continue;
                model.Note(CopyAsNote.HostHeaderDropped, Comments.HostHeaderDropped);
                continue;
            }
            else if (ManagedHeaders.Contains(name))
            {
                if (name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase))
                    model.Note(CopyAsNote.UpgradeDropped, Comments.UpgradeDropped);
                continue;
            }
            else if (name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) && bodyWasDecoded)
            {
                continue;
            }

            model.Headers.Add((name, value));
        }

        // RFC 9113 8.2.3: a client may split the Cookie header into one field per crumb on HTTP/2, and a
        // Cookie header is one header with "; " between crumbs on HTTP/1.1, so it is always joined. No
        // tool joins repeated Cookie headers, and fetch joins repeated headers with ", ", which corrupts it.
        // Targets that take a header dictionary cannot repeat any other header either.
        MergeDuplicates(model, allHeaders: false);

        return model;
    }

    /// <summary>Fills in the body; returns true when it holds the body with Content-Encoding removed.</summary>
    private static bool PrepareBody(HttpRequestData request, Model model)
    {
        if (request.BodyTotalLength <= 0 && request.Body.Length == 0) return false;

        if (!request.IsBodyComplete)
        {
            model.Kind = BodyKind.Omitted;
            model.Note(CopyAsNote.BodyNotCaptured, Comments.BodyNotCaptured(request.Body.LongLength, request.BodyTotalLength));
            return false;
        }

        var decoded = request.Decoded;
        var wasDecoded = !ReferenceEquals(decoded.Bytes, request.Body);
        if (wasDecoded && decoded.Truncated)
        {
            model.Kind = BodyKind.Omitted;
            model.Note(CopyAsNote.BodyDecodeCut, Comments.BodyDecodeCut);
            return false;
        }

        if (decoded.Bytes.Length > MaxInlineBodyBytes)
        {
            model.Kind = BodyKind.Omitted;
            model.Note(CopyAsNote.BodyTooLarge, Comments.BodyTooLarge(decoded.Bytes.LongLength, MaxInlineBodyBytes));
            return false;
        }

        model.Body = decoded.Bytes;

        if (TryInlineText(decoded.Bytes, request.ContentType, asciiOnly: false, singleLine: false, out var text))
        {
            model.Kind = BodyKind.Text;
            model.BodyText = text;
            return wasDecoded;
        }

        model.Kind = BodyKind.Binary;
        model.Note(CopyAsNote.BodyAsBase64, Comments.BodyAsBase64);
        return wasDecoded;
    }

    /// <summary>
    /// Joins repeated headers into the first of them: Cookie with "; ", always; any other header with
    /// ", " only when <paramref name="allHeaders"/> (the target cannot repeat a header), and then says so.
    /// </summary>
    private static void MergeDuplicates(Model model, bool allHeaders)
    {
        var merged = new List<(string Name, HeaderValue Value)>();
        var changed = false;
        var mergedOther = false;
        foreach (var (name, value) in model.Headers)
        {
            var isCookie = name.Equals("Cookie", StringComparison.OrdinalIgnoreCase);
            var index = isCookie || allHeaders ? merged.FindIndex(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) : -1;
            if (index < 0)
            {
                merged.Add((name, value));
                continue;
            }

            var separator = isCookie ? "; " : ", ";
            var existing = merged[index];
            merged[index] = (existing.Name, new HeaderValue(existing.Value.Text + separator + value.Text,
                [.. existing.Value.Bytes, .. Encoding.ASCII.GetBytes(separator), .. value.Bytes]));
            changed = true;
            mergedOther |= !isCookie;
        }

        if (!changed) return;
        model.Headers.Clear();
        model.Headers.AddRange(merged);
        if (mergedOther) model.Note(CopyAsNote.DuplicateHeadersMerged, Comments.DuplicateHeadersMerged);
    }

    /// <summary>RFC 9110 token: what a method or a header name may be.</summary>
    internal static bool IsToken(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 256) return false;
        foreach (var c in text)
        {
            var ok = c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9'
                || "!#$%&'*+-.^_`|~".Contains(c);
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// A header value as the text a person means and as the bytes that crossed the wire. Headers are
    /// read as Latin-1, so a UTF-8 value arrives as several characters per letter; when the bytes are
    /// valid UTF-8 the text is what they decode to. Fails for a value that cannot be written on one
    /// line (CR, LF, NUL and the other control characters but tab).
    /// </summary>
    private static bool TryNormalise(string raw, out HeaderValue value)
    {
        value = default;
        foreach (var c in raw)
            if (c is < ' ' and not '\t' || c == '\u007f') return false;
        if (!ValidSurrogates(raw)) return false;

        var bytes = raw.All(c => c <= 'ÿ') ? Encoding.Latin1.GetBytes(raw) : Encoding.UTF8.GetBytes(raw);
        var text = raw;
        if (raw.Any(c => c > '\u007f'))
        {
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (ArgumentException) { /* not UTF-8: keep the characters as read */ }
        }

        value = new HeaderValue(text, bytes);
        return true;
    }

    private static bool ValidSurrogates(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) i++;
            else if (char.IsSurrogate(text[i])) return false;
        }
        return true;
    }

    private static bool SameAuthority(string host, Uri uri)
    {
        host = host.Trim();
        if (host.Equals(uri.Authority, StringComparison.OrdinalIgnoreCase)) return true;
        return uri.IsDefaultPort
            && host.Equals(uri.Authority + ":" + uri.Port.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the body is text that can be pasted as a string literal and arrive unchanged: UTF-8
    /// (or ASCII) with no control characters beyond tab, CR and LF. Anything else, a NUL, an escape
    /// sequence, an unusual charset, bytes that are not valid UTF-8, goes through base64.
    /// </summary>
    private static bool TryInlineText(byte[] bytes, string? contentType, bool asciiOnly, bool singleLine, out string text)
    {
        text = string.Empty;
        if (ContentCodec.CharsetFor(contentType).CodePage is not (65001 or 20127)) return false;

        foreach (var b in bytes)
        {
            if (b == 0 || b == 0x7f) return false;
            if (b < 0x20 && b is not (9 or 10 or 13)) return false;
            if (singleLine && b is 10 or 13) return false;
            if (asciiOnly && b >= 0x80) return false;
        }

        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (ArgumentException) { return false; }
        return true;
    }

    // ---------------------------------------------------------------- comments

    // Fixed text and numbers only: a comment never repeats anything that was captured.
    private static class Comments
    {
        public const string HeadersSkipped = "Headers with a name or value that cannot be written safely were left out.";
        public const string HostHeaderDropped = "The captured Host header differs from the URL and was not copied.";
        public const string DuplicateHeadersMerged = "Repeated headers were merged into one value because this tool cannot repeat a header.";
        public const string BodyDecodeCut = "The request body is not included: decoding it stopped at a size limit or a corrupt stream, so only part of it is known.";
        public const string BodyAsBase64 = "The request body is binary or not safe to paste as text, so it is written as base64 and decoded here.";
        public const string UpgradeDropped = "The Upgrade and Connection headers were not copied, so this request will not switch protocols (a WebSocket handshake, for example).";

        public static string BodyNotCaptured(long kept, long total) =>
            string.Create(CultureInfo.InvariantCulture, $"The request body is not included: only the first {kept} of {total} bytes were captured.");

        public static string BodyTooLarge(long size, int limit) =>
            string.Create(CultureInfo.InvariantCulture, $"The request body is not included: it is {size} bytes, more than the {limit} bytes copied.");
    }

    private static string CommentBlock(Model model, string prefix, string newline)
    {
        if (model.Comments.Count == 0) return string.Empty;
        var sb = new StringBuilder();
        foreach (var comment in model.Comments) sb.Append(prefix).Append(comment).Append(newline);
        return sb.ToString();
    }

    // One character per byte that crossed the wire: what fetch and HttpClient's Latin-1 encoder send.
    private static string WireText(HeaderValue value) => Encoding.Latin1.GetString(value.Bytes);

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes);

    // ---------------------------------------------------------------- JavaScript fetch

    private static string RenderJavaScript(Model m)
    {
        var sb = new StringBuilder(CommentBlock(m, "// ", "\n"));
        sb.Append("await fetch(").Append(JsString(m.Url)).Append(", {\n");
        if (m.Method != "GET") sb.Append("  method: ").Append(JsString(m.Method)).Append(",\n");

        // A redirect is not followed: a server that answers with one could otherwise send the copied
        // Cookie and other credentials on to a host the capture never contacted.
        sb.Append("  redirect: \"manual\",\n");
        if (m.Headers.Count > 0)
        {
            // An array of pairs keeps the order and the repeats a plain object would lose. A value is a
            // ByteString: one character per byte that crossed the wire, which is all fetch can send.
            sb.Append("  headers: [\n");
            foreach (var (name, value) in m.Headers)
                sb.Append("    [").Append(JsString(name)).Append(", ").Append(JsString(WireText(value))).Append("],\n");
            sb.Append("  ],\n");
        }

        if (m.Kind == BodyKind.Binary)
            sb.Append("  body: Uint8Array.from(atob(").Append(JsString(Base64(m.Body))).Append("), c => c.charCodeAt(0)),\n");
        else if (m.Kind == BodyKind.Text)
            sb.Append("  body: ").Append(JsString(m.BodyText)).Append(",\n");

        sb.Append("});");
        return sb.ToString();
    }

    /// <summary>A JavaScript double-quoted literal; only printable ASCII appears unescaped.</summary>
    internal static string JsString(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case >= ' ' and <= '~': sb.Append(c); break;
                default: sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture)); break;
            }
        }
        return sb.Append('"').ToString();
    }
}
