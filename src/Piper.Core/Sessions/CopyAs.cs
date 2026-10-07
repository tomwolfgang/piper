using System.Globalization;
using System.Text;
using Piper.Core.Http;

namespace Piper.Core.Sessions;

/// <summary>A command or snippet a captured request can be copied as.</summary>
public enum CopyAsTarget
{
    /// <summary><c>curl</c> typed into bash or zsh.</summary>
    CurlBash,

    /// <summary><c>curl.exe</c> typed into the cmd.exe prompt.</summary>
    CurlCmd,

    /// <summary><c>curl.exe</c> typed into PowerShell, where <c>curl</c> alone is an alias for something else.</summary>
    CurlPowerShell,

    /// <summary><c>Invoke-WebRequest</c> in PowerShell.</summary>
    PowerShellWebRequest,

    /// <summary>The JavaScript <c>fetch</c> function.</summary>
    JavaScriptFetch,

    /// <summary>The Python <c>requests</c> package.</summary>
    PythonRequests,

    /// <summary><c>System.Net.Http.HttpClient</c>.</summary>
    CSharpHttpClient,
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

    /// <summary>Non-ASCII header text may be re-encoded by the shell it is pasted into.</summary>
    NonAsciiMayChange,

    /// <summary>The cmd.exe command is longer than the prompt accepts.</summary>
    CommandTooLong,

    /// <summary>The cmd.exe command passes the request through a temporary file (an exclamation mark or non-ASCII text).</summary>
    CmdValuesInFile,

    /// <summary>Windows PowerShell 5.1 does not send a Cookie header passed to Invoke-WebRequest.</summary>
    CookieNeedsPowerShell7,

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

    // cmd.exe stops reading a command at 8191 characters. Warn a little below it.
    private const int CmdLineWarningLength = 8000;

    // A body longer than this is not pasted as an argument to cmd.exe, which also has no way to put a
    // line break in one; it goes through a file instead.
    private const int CmdInlineBodyBytes = 2000;

    private const int Base64LineLength = 4000;

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
            CopyAsTarget.CurlBash => RenderCurlBash(model),
            CopyAsTarget.CurlCmd => RenderCurlCmd(model),
            CopyAsTarget.CurlPowerShell => RenderCurlPowerShell(model),
            CopyAsTarget.PowerShellWebRequest => RenderPowerShellWebRequest(model),
            CopyAsTarget.JavaScriptFetch => RenderJavaScript(model),
            CopyAsTarget.PythonRequests => RenderPython(model),
            CopyAsTarget.CSharpHttpClient => RenderCSharp(model),
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
        var isCurl = target is CopyAsTarget.CurlBash or CopyAsTarget.CurlCmd or CopyAsTarget.CurlPowerShell;
        var curlConfigShell = target is CopyAsTarget.CurlCmd or CopyAsTarget.CurlPowerShell;

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
                if (!isCurl)
                {
                    model.Note(CopyAsNote.HostHeaderDropped, Comments.HostHeaderDropped);
                    continue;
                }
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
            else if (name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase) && target == CopyAsTarget.CSharpHttpClient)
            {
                // The generated handler negotiates and removes compression itself.
                continue;
            }

            if (curlConfigShell && HasInvisibleCharacter(value.Text))
            {
                model.Note(CopyAsNote.HeadersSkipped, Comments.HeadersSkipped);
                continue;
            }

            // PowerShell 7 was not probed: Windows PowerShell 5.1 sends the bytes of such a value as they
            // are, another PowerShell may not.
            if ((curlConfigShell || target == CopyAsTarget.PowerShellWebRequest) && value.Text.Any(c => c > '~'))
                model.Note(CopyAsNote.NonAsciiMayChange, Comments.NonAsciiMayChange);

            if (target == CopyAsTarget.PowerShellWebRequest && name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
                model.Note(CopyAsNote.CookieNeedsPowerShell7, Comments.CookieNeedsPowerShell7);

            model.Headers.Add((name, value));
        }

        // RFC 9113 8.2.3: a client may split the Cookie header into one field per crumb on HTTP/2, and a
        // Cookie header is one header with "; " between crumbs on HTTP/1.1, so it is always joined. No
        // tool joins repeated Cookie headers, and fetch joins repeated headers with ", ", which corrupts it.
        // Targets that take a header dictionary cannot repeat any other header either.
        MergeDuplicates(model, allHeaders: target is CopyAsTarget.PowerShellWebRequest or CopyAsTarget.PythonRequests);

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
        var target = model.Target;
        var asciiOnly = target is CopyAsTarget.CurlCmd or CopyAsTarget.CurlPowerShell;
        var singleLine = target == CopyAsTarget.CurlCmd;
        var limit = target == CopyAsTarget.CurlCmd ? CmdInlineBodyBytes : int.MaxValue;

        if (decoded.Bytes.Length <= limit && TryInlineText(decoded.Bytes, request.ContentType, asciiOnly, singleLine, out var text))
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

    private static bool HasInvisibleCharacter(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune == Rune.ReplacementChar) return true;
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control when rune.Value != '\t':
                case UnicodeCategory.Format:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                    return true;
            }
        }
        return false;
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
        public const string NonAsciiMayChange = "Non-ASCII header text may be re-encoded by the shell it is pasted into, or by the runtime that sends it.";
        public const string BodyDecodeCut = "The request body is not included: decoding it stopped at a size limit or a corrupt stream, so only part of it is known.";
        public const string BodyAsBase64 = "The request body is binary or not safe to paste as text, so it is written as base64 and decoded here.";
        public const string UpgradeDropped = "The Upgrade and Connection headers were not copied, so this request will not switch protocols (a WebSocket handshake, for example).";
        public const string CookieNeedsPowerShell7 = "Windows PowerShell 5.1 does not send a Cookie header given this way; PowerShell 7 does.";
        public const string CommandTooLong = "This command is longer than the 8191 characters cmd.exe accepts; use the PowerShell or bash variant.";

        public const string CmdValuesInFile = "This request has values cmd.exe cannot carry safely (an exclamation mark, or text that is not plain ASCII), so the request, headers and credentials included, is passed to curl in a temporary file in your temp folder as plain text. It is deleted when curl finishes, and stays there if the paste is interrupted: delete the piper-* files in that folder then.";

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

    // ---------------------------------------------------------------- curl, bash

    private static string RenderCurlBash(Model m)
    {
        // Under Git for Windows bash runs a native Windows curl.exe, and one built with an ANSI main gets
        // its command line converted to the ANSI code page, where Windows maps some characters above
        // ASCII to ASCII ones (U+FF02 becomes a double quote, which closes the quoted argument and lets
        // the rest of a header become curl options). Bytes above ASCII therefore never go on the command
        // line: the request goes to curl in a config file, whose bytes curl reads as they are.
        var headerBytes = m.Headers.Select(h => (byte[])[.. Encoding.ASCII.GetBytes(HeaderPrefix(h.Name, h.Value.Bytes.Length)), .. h.Value.Bytes]).ToList();
        if (m.HasBody && !m.HasHeader("Content-Type")) headerBytes.Add(Encoding.ASCII.GetBytes("Content-Type:"));
        var bodyBytes = m.Kind == BodyKind.Text ? Encoding.UTF8.GetBytes(m.BodyText) : [];
        if (headerBytes.Any(HasNonAscii) || HasNonAscii(bodyBytes) || HasNonAscii(Encoding.UTF8.GetBytes(m.Url + m.Method)))
            return RenderCurlBashConfig(m, headerBytes, bodyBytes);

        var args = new List<string> { "curl " + BashQuote(m.Url), "--globoff" };
        AddCurlMethod(m, args, BashQuote);
        if (m.HasHeader("Accept-Encoding")) args.Add("--compressed");
        foreach (var (name, value) in m.Headers)
            args.Add("--header " + BashQuote([.. Encoding.ASCII.GetBytes(HeaderPrefix(name, value.Bytes.Length)), .. value.Bytes]));
        if (m.HasBody && !m.HasHeader("Content-Type")) args.Add("--header 'Content-Type:'");

        var prefix = string.Empty;
        if (m.Kind == BodyKind.Binary)
        {
            prefix = "printf %s " + BashQuote(Base64(m.Body)) + " | base64 -d | ";
            args.Add("--data-binary @-");
        }
        else if (m.Kind == BodyKind.Text && bodyBytes.Contains((byte)'/'))
        {
            // Git for Windows bash rewrites a word such as /x or next=/home into a Windows path before a
            // native curl.exe sees it, and a body is the one argument whose first characters are not
            // fixed (a header starts with its name, the URL with its scheme). printf is a builtin, so the
            // text goes to curl on standard input instead of on its command line.
            prefix = "printf %s " + BashQuote(bodyBytes) + " | ";
            args.Add("--data-binary @-");
        }
        else if (m.Kind == BodyKind.Text)
        {
            args.Add("--data-raw " + BashQuote(bodyBytes));
        }

        return CommentBlock(m, "# ", "\n") + prefix + string.Join(" \\\n  ", args);
    }

    private static bool HasNonAscii(byte[] bytes) => bytes.Any(b => b > 0x7e);

    private static string RenderCurlBashConfig(Model m, List<byte[]> headers, byte[] body)
    {
        // Each character of this string stands for one byte (Latin-1), so the header bytes stay as they
        // were captured; CurlConfigString only touches ASCII characters.
        var latin1 = Encoding.Latin1;
        var config = new StringBuilder();
        config.Append("url = ").Append(CurlConfigString(m.Url)).Append('\n');
        config.Append("globoff\n");
        if (m.IsHead) config.Append("head\n");
        else if (m.Method != "GET" || m.HasBody) config.Append("request = ").Append(CurlConfigString(m.Method)).Append('\n');
        if (m.HasHeader("Accept-Encoding")) config.Append("compressed\n");
        foreach (var header in headers) config.Append("header = ").Append(CurlConfigString(latin1.GetString(header))).Append('\n');
        if (m.Kind == BodyKind.Text) config.Append("data-raw = ").Append(CurlConfigString(latin1.GetString(body))).Append('\n');
        var word = BashQuote(latin1.GetBytes(config.ToString()));

        // Without a binary body the config is piped to curl. A binary body needs curl's standard input
        // for itself, so the config goes to a private temporary file. A subshell removes it when it ends,
        // however it ends: Ctrl+C on a hung curl would otherwise skip a trailing rm and leave the
        // credentials in the file behind.
        string text;
        if (m.Kind == BodyKind.Binary)
        {
            // Git for Windows runs a native curl.exe. Newer Git Bash versions do not always convert a
            // variable-expanded /tmp path for curl's --config argument, so convert it explicitly when
            // cygpath is available. Ordinary Unix shells keep the path mktemp returned.
            text = "( trap 'rm -f \"$piperConfig\"' EXIT; trap 'exit 130' INT TERM; piperConfig=$(mktemp \"${TMPDIR:-/tmp}/piper-XXXXXXXXXX\") && printf %s " + word + " > \"$piperConfig\" && "
                + "piperCurlConfig=$piperConfig; command -v cygpath >/dev/null 2>&1 && piperCurlConfig=$(cygpath -w \"$piperConfig\"); "
                + "printf %s " + BashQuote(Base64(m.Body)) + " | base64 -d | curl --config \"$piperCurlConfig\" --data-binary @- )";
        }
        else
        {
            text = "printf %s " + word + " | curl --config -";
        }

        return CommentBlock(m, "# ", "\n") + text;
    }

    // curl sends "Name;" for a header with an empty value; "Name:" would remove the header instead.
    private static string HeaderPrefix(string name, int valueLength) =>
        valueLength == 0 ? name + ";" : name + ": ";

    private static void AddCurlMethod(Model m, List<string> args, Func<string, string> quote)
    {
        if (m.IsHead) args.Add("--head");
        else if (m.Method != "GET" || m.HasBody) args.Add("--request " + quote(m.Method));
    }

    /// <summary>
    /// One bash word that is exactly <paramref name="bytes"/>: printable ASCII and line feeds sit inside
    /// single quotes, a single quote is <c>\'</c>, and every other byte is an ANSI-C <c>$'\xHH'</c>
    /// escape, so nothing in the input is ever interpreted and no byte depends on the locale.
    /// </summary>
    internal static string BashQuote(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0) return "''";

        var sb = new StringBuilder(bytes.Length + 8);
        var open = '\0'; // the quote currently open: ' for a plain run, $ for an ANSI-C run
        void Close()
        {
            if (open != '\0') sb.Append('\'');
            open = '\0';
        }

        foreach (var b in bytes)
        {
            if (b is >= 0x20 and <= 0x7e && b != '\'' || b == '\n')
            {
                if (open != '\'') { Close(); sb.Append('\''); open = '\''; }
                sb.Append((char)b);
            }
            else if (b == '\'')
            {
                Close();
                sb.Append("\\'");
            }
            else
            {
                if (open != '$') { Close(); sb.Append("$'"); open = '$'; }
                sb.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
        }
        Close();
        return sb.ToString();
    }

    internal static string BashQuote(string text) => BashQuote(Encoding.UTF8.GetBytes(text));

    // ---------------------------------------------------------------- curl, cmd.exe

    // A character cmd.exe or a curl.exe built for the ANSI code page cannot be trusted with on a command
    // line: an exclamation mark (cmd /v:on expands it, see CmdArg), and anything outside printable ASCII,
    // which Windows can convert with a best-fit mapping to a different character (U+FF02 becomes a
    // double quote in an ANSI argv, so it could close the quoted argument CmdArg wrote).
    private static bool NeedsCmdFile(string text)
    {
        foreach (var c in text)
            if (c is < ' ' or > '~' or '!') return true;
        return false;
    }

    private static string RenderCurlCmd(Model m)
    {
        const string NewLine = "\r\n";

        // Any value cmd.exe cannot carry safely (see NeedsCmdFile) is never put on the command line. When
        // there is one, the whole request (URL, method, every header, the text body) goes to curl in a
        // config file, written as base64 and decoded by certutil like a binary body. curl reads that file
        // as bytes, so nothing is re-read through the console, cmd.exe or an ANSI code page, and nothing
        // sensitive stays on the command line. The command is then pure printable ASCII.
        var headers = m.Headers.Select(h => HeaderPrefix(h.Name, h.Value.Bytes.Length) + h.Value.Text).ToList();
        if (m.HasBody && !m.HasHeader("Content-Type")) headers.Add("Content-Type:");
        var sendsMethod = !m.IsHead && (m.Method != "GET" || m.HasBody);
        var useFile = NeedsCmdFile(m.Url) || (sendsMethod && NeedsCmdFile(m.Method)) || headers.Any(NeedsCmdFile)
            || (m.Kind == BodyKind.Text && NeedsCmdFile(m.BodyText));

        // A name nobody can guess, so another user of a shared temporary directory cannot create the
        // file first and have curl read their config instead.
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var before = new StringBuilder();
        var deleted = new List<string>();
        void WriteFile(string stem, string extension, byte[] bytes)
        {
            var b64 = stem + ".b64";
            var file = stem + extension;
            var encoded = Base64(bytes);
            for (var i = 0; i < encoded.Length; i += Base64LineLength)
            {
                var chunk = encoded.AsSpan(i, Math.Min(Base64LineLength, encoded.Length - i));
                before.Append(i == 0 ? ">" : ">>").Append(" \"").Append(b64).Append("\" echo ").Append(chunk).Append(NewLine);
            }
            before.Append("certutil -f -decode \"").Append(b64).Append("\" \"").Append(file).Append("\" > nul").Append(NewLine);

            // The base64 copy is not needed once decoded, so it does not wait for curl to finish.
            before.Append("del /q \"").Append(b64).Append('"').Append(NewLine);
            deleted.Add(file);
        }

        var args = new List<string> { "curl.exe" };
        if (useFile)
        {
            var config = new StringBuilder();
            config.Append("url = ").Append(CurlConfigString(m.Url)).Append('\n');
            config.Append("globoff\n");
            if (m.IsHead) config.Append("head\n");
            else if (sendsMethod) config.Append("request = ").Append(CurlConfigString(m.Method)).Append('\n');
            if (m.HasHeader("Accept-Encoding")) config.Append("compressed\n");
            foreach (var header in headers) config.Append("header = ").Append(CurlConfigString(header)).Append('\n');
            if (m.Kind == BodyKind.Text) config.Append("data-raw = ").Append(CurlConfigString(m.BodyText)).Append('\n');

            var stem = "%TEMP%\\piper-args-" + token;
            WriteFile(stem, ".cfg", Encoding.UTF8.GetBytes(config.ToString()));
            args[0] += " --config \"" + stem + ".cfg\"";
            m.Note(CopyAsNote.CmdValuesInFile, Comments.CmdValuesInFile);
        }
        else
        {
            args[0] += " " + CmdArg(m.Url);
            args.Add("--globoff");
            AddCurlMethod(m, args, CmdArg);
            if (m.HasHeader("Accept-Encoding")) args.Add("--compressed");
            foreach (var header in headers) args.Add("--header " + CmdArg(header));
            if (m.Kind == BodyKind.Text) args.Add("--data-raw " + CmdArg(m.BodyText));
        }

        if (m.Kind == BodyKind.Binary)
        {
            // cmd.exe has no way to hold a line break or arbitrary bytes in an argument, so the body is
            // written to a temporary file as base64 and decoded by certutil, which ships with Windows.
            var stem = "%TEMP%\\piper-body-" + token;
            WriteFile(stem, ".bin", m.Body);
            args.Add("--data-binary \"@" + stem + ".bin\"");
        }

        if (string.Join(" ", args).Length > CmdLineWarningLength)
            m.Note(CopyAsNote.CommandTooLong, Comments.CommandTooLong);

        // The files go whether or not curl succeeded; only an interrupted paste can leave them behind.
        var after = deleted.Count == 0 ? string.Empty : " & del /q " + string.Join(" ", deleted.Select(f => "\"" + f + "\""));
        return CommentBlock(m, "REM ", NewLine) + before + string.Join(" ^" + NewLine + "  ", args) + after;
    }

    /// <summary>
    /// One argument for a program started from the cmd.exe prompt that parses its command line the way
    /// the Microsoft C runtime does (curl.exe does). The text is first quoted for that parser: wrapped
    /// in double quotes, with <c>"</c> as <c>\"</c> and backslashes doubled where they precede a quote.
    /// Then every character cmd.exe itself treats specially, the quotes included, is escaped with a
    /// caret, so cmd.exe never sees a quoted region at all and hands the program exactly the quoted
    /// text. A <c>%</c> is the exception: cmd.exe expands <c>%name%</c> and <c>%name:x=y%</c> before it
    /// reads a caret (so <c>^%FOO:x=^%</c> still leaks FOO), and the only thing that stops it is a
    /// name that begins with a caret. Every <c>%</c> is therefore followed by a caret, which no variable
    /// name starts with. For the interactive prompt and <c>cmd /c</c>; a batch file would need <c>%%</c>. The text must
    /// not hold a line break, which cmd.exe cannot carry in an argument.
    /// </summary>
    /// <remarks>
    /// The text must not hold an exclamation mark either, and the method throws if it does. With delayed
    /// expansion on (<c>cmd /v:on</c>, or the DelayedExpansion registry value, both off by default) cmd.exe
    /// replaces <c>!NAME!</c> with the variable's value after it has read the carets, and a second pass
    /// removes carets from any line that still has a <c>!</c>. <c>^!</c> is right only with it off and
    /// <c>^^^!</c> only with it on, so there is no spelling that is right for both, and a leak would send
    /// an environment variable to the request's host. The caller moves such a value out of the command
    /// line instead (see <c>RenderCurlCmd</c>). The same limit applies to a <c>%TEMP%</c> whose path holds
    /// an exclamation mark, which the temporary files of the snippet are named from.
    /// <para>
    /// Nor may it hold a character outside printable ASCII (a tab, a control character, or anything above
    /// <c>~</c>). A program built with an ANSI <c>main</c> (Git for Windows' curl.exe is one) gets its
    /// command line converted to the ANSI code page, where Windows maps some characters to ASCII ones
    /// (U+FF02 becomes a double quote, U+FF3C and U+00A5 a backslash, on some code pages), and that would
    /// undo the quoting written here. Such a value goes through the config file as well.
    /// </para>
    /// </remarks>
    internal static string CmdArg(string text)
    {
        if (NeedsCmdFile(text)) throw new ArgumentException("An exclamation mark or a character outside printable ASCII cannot be quoted for cmd.exe.", nameof(text));

        var quoted = new StringBuilder(text.Length + 2).Append('"');
        for (var i = 0; i < text.Length; i++)
        {
            var backslashes = 0;
            while (i < text.Length && text[i] == '\\') { backslashes++; i++; }

            if (i == text.Length)
            {
                quoted.Append('\\', backslashes * 2);
                break;
            }

            if (text[i] == '"') quoted.Append('\\', backslashes * 2 + 1).Append('"');
            else quoted.Append('\\', backslashes).Append(text[i]);
        }
        quoted.Append('"');

        var text2 = quoted.ToString();
        var sb = new StringBuilder(text2.Length + 8);
        for (var i = 0; i < text2.Length; i++)
        {
            var c = text2[i];
            if (c == '%')
            {
                // cmd.exe expands %name%, %name:old=new% and %name:~0,3% before it reads a caret, so a
                // caret in front of the percent sign does not stop it (and a caret is accepted inside
                // the substitution text): "^%FOO:x=^%" expands to the value of FOO. What stops it is a
                // variable name that starts with a caret, which no variable has, so every percent sign
                // is followed by a caret. That caret then escapes whatever comes next, which is
                // harmless for an ordinary character, and when the next character is one that is
                // caret-escaped below its own caret already follows the percent sign.
                sb.Append('%');
                if (i + 1 >= text2.Length || !IsCmdMetacharacter(text2[i + 1]) || text2[i + 1] == '%') sb.Append('^');
                continue;
            }

            if (IsCmdMetacharacter(c)) sb.Append('^');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // Every character cmd.exe treats specially outside quotes; the percent sign is dealt with apart, and
    // the exclamation mark is never quoted (see CmdArg).
    private static bool IsCmdMetacharacter(char c) => "()%^\"<>&|".Contains(c);

    // ---------------------------------------------------------------- curl, PowerShell

    private static string RenderCurlPowerShell(Model m)
    {
        // The arguments go in a curl config read from stdin rather than on the command line. How
        // PowerShell hands double quotes to a native program differs between Windows PowerShell 5.1
        // and PowerShell 7, so any quoting of them would be right in only one. A literal here-string
        // is not interpreted at all, and curl's own config escapes are the same everywhere.
        var config = new StringBuilder();
        config.Append("url = ").Append(CurlConfigString(m.Url)).Append('\n');
        config.Append("globoff\n");
        if (m.IsHead) config.Append("head\n");
        else if (m.Method != "GET" || m.HasBody) config.Append("request = ").Append(CurlConfigString(m.Method)).Append('\n');
        if (m.HasHeader("Accept-Encoding")) config.Append("compressed\n");
        foreach (var (name, value) in m.Headers)
            config.Append("header = ").Append(CurlConfigString(HeaderPrefix(name, value.Bytes.Length) + value.Text)).Append('\n');
        if (m.HasBody && !m.HasHeader("Content-Type")) config.Append("header = \"Content-Type:\"\n");
        if (m.Kind == BodyKind.Text) config.Append("data-raw = ").Append(CurlConfigString(m.BodyText)).Append('\n');

        var sb = new StringBuilder(CommentBlock(m, "# ", "\n"));
        var binary = m.Kind == BodyKind.Binary;
        var tail = binary ? " | curl.exe --config - --data-binary \"@$piperBody\"" : " | curl.exe --config -";
        if (binary)
        {
            // The file holds the request body, so it goes in a piper- name that the README's cleanup advice
            // covers, and the Remove-Item is a finally: Ctrl+C during curl stops the pipeline, and a
            // statement after it would never run.
            sb.Append("$piperBody = Join-Path ([IO.Path]::GetTempPath()) ('piper-body-' + [IO.Path]::GetRandomFileName())\n");
            sb.Append("try {\n");
            sb.Append("    [IO.File]::WriteAllBytes($piperBody, [Convert]::FromBase64String('").Append(Base64(m.Body)).Append("'))\n");
        }

        // Every config line starts with a keyword, so none can be the "'@" that ends a here-string.
        sb.Append("@'\n").Append(config).Append("'@").Append(tail);
        if (binary) sb.Append("\n} finally {\n    Remove-Item -LiteralPath $piperBody -ErrorAction SilentlyContinue\n}");
        return sb.ToString();
    }

    /// <summary>One curl config value in double quotes, with the escapes curl's config parser defines.</summary>
    internal static string CurlConfigString(string text)
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
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    // ---------------------------------------------------------------- PowerShell Invoke-WebRequest

    private static string RenderPowerShellWebRequest(Model m)
    {
        var sb = new StringBuilder(CommentBlock(m, "# ", "\n"));
        sb.Append("$params = @{\n");
        sb.Append("    Uri = ").Append(PsString(m.Url)).Append('\n');
        sb.Append("    Method = ").Append(PsString(m.Method)).Append('\n');
        sb.Append("    UseBasicParsing = $true\n");

        // A redirect is not followed: a server that answers with one could otherwise send the copied
        // Cookie and other credentials on to a host the capture never contacted.
        sb.Append("    MaximumRedirection = 0\n");

        var other = new List<(string Name, HeaderValue Value)>();
        foreach (var header in m.Headers)
        {
            if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                sb.Append("    ContentType = ").Append(PsString(WireText(header.Value))).Append('\n');
            else if (header.Name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                sb.Append("    UserAgent = ").Append(PsString(WireText(header.Value))).Append('\n');
            else
                other.Add(header);
        }

        if (other.Count > 0)
        {
            sb.Append("    Headers = @{\n");
            foreach (var (name, value) in other)
                sb.Append("        ").Append(PsString(name)).Append(" = ").Append(PsString(WireText(value))).Append('\n');
            sb.Append("    }\n");
        }

        if (m.Kind == BodyKind.Binary)
            sb.Append("    Body = [Convert]::FromBase64String(").Append(PsString(Base64(m.Body))).Append(")\n");
        else if (m.Kind == BodyKind.Text)
        {
            var literal = PsString(m.BodyText);
            sb.Append("    Body = ").Append(m.BodyText.All(c => c <= '\u007f')
                ? literal
                : "[Text.Encoding]::UTF8.GetBytes(" + literal + ")").Append('\n');
        }

        sb.Append("}\nInvoke-WebRequest @params");
        return sb.ToString();
    }

    // The most operands of one + chain PsString writes; the stack of Windows PowerShell 5.1 gave out
    // between 3000 and 4000, and the chain is a part of an expression that may sit deeper still.
    private const int MaxPsOperands = 100;

    /// <summary>
    /// A PowerShell expression that is exactly <paramref name="text"/>. Printable characters sit in a
    /// single-quoted literal with <c>'</c> doubled; everything else, line breaks, control and format
    /// characters, and the typographic quotes PowerShell reads as single quotes, is a <c>[char]</c>
    /// joined on, so no character can end the literal or be reinterpreted. Past
    /// <see cref="MaxPsOperands"/> operands the whole value is base64 of its UTF-16 text instead (no
    /// caller passes an unpaired surrogate: header values and bodies are checked for one first).
    /// </summary>
    internal static string PsString(string text)
    {
        var parts = new List<string>();
        var literal = new StringBuilder();

        void Flush()
        {
            if (literal.Length == 0) return;
            parts.Add("'" + literal + "'");
            literal.Clear();
        }

        foreach (var c in text)
        {
            if (c == '\'') literal.Append("''");
            else if (IsPlainPowerShellChar(c)) literal.Append(c);
            else
            {
                Flush();
                parts.Add("[char]0x" + ((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
        }
        Flush();

        if (parts.Count == 0) return "''";

        // Windows PowerShell 5.1 parses a chain of + operands recursively on a fixed stack: a few
        // thousand of them (a pasted multi-line body) end the process with a stack overflow. A value
        // that is mostly characters to escape is therefore carried flat, as one base64 literal.
        if (parts.Count > MaxPsOperands)
            return "[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.Unicode.GetBytes(text)) + "'))";

        // The left operand decides the type of a +, so an expression that starts with a character must
        // be turned into a string first.
        if (parts[0].StartsWith('[')) parts[0] = "[string]" + parts[0];
        return parts.Count == 1 ? parts[0] : "(" + string.Join(" + ", parts) + ")";
    }

    private static bool IsPlainPowerShellChar(char c)
    {
        if (c is >= ' ' and <= '~') return true;
        if (c is '‘' or '’' or '‚' or '‛') return false;
        if (char.IsSurrogate(c)) return false;
        return char.GetUnicodeCategory(c) is not (UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned);
    }

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

    // ---------------------------------------------------------------- Python requests

    private static string RenderPython(Model m)
    {
        var sb = new StringBuilder(CommentBlock(m, "# ", "\n"));
        sb.Append("import requests\n");
        if (m.Kind == BodyKind.Binary) sb.Append("import base64\n");
        sb.Append("\nresponse = requests.request(\n");
        sb.Append("    ").Append(PyString(m.Method)).Append(",\n");
        sb.Append("    ").Append(PyString(m.Url)).Append(",\n");

        // A redirect is not followed: a server that answers with one could otherwise send the copied
        // Cookie and other credentials on to a host the capture never contacted.
        sb.Append("    allow_redirects=False,\n");
        if (m.Headers.Count > 0)
        {
            sb.Append("    headers={\n");
            foreach (var (name, value) in m.Headers)
                sb.Append("        ").Append(PyString(name)).Append(": ")
                  .Append(value.Bytes.All(b => b < 0x80) ? PyString(value.Text) : PyBytes(value.Bytes)).Append(",\n");
            sb.Append("    },\n");
        }

        if (m.Kind == BodyKind.Binary)
            sb.Append("    data=base64.b64decode(").Append(PyString(Base64(m.Body))).Append("),\n");
        else if (m.Kind == BodyKind.Text)
            sb.Append("    data=").Append(PyString(m.BodyText)).Append(".encode(\"utf-8\"),\n");

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>
    /// A Python bytes literal for exactly <paramref name="bytes"/>. A header value that is not ASCII is
    /// written this way because requests would otherwise encode the text as Latin-1 (or refuse it).
    /// </summary>
    internal static string PyBytes(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length + 3).Append("b\"");
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'\\': sb.Append("\\\\"); break;
                case (byte)'"': sb.Append("\\\""); break;
                case >= (byte)' ' and <= (byte)'~': sb.Append((char)b); break;
                default: sb.Append("\\x").Append(b.ToString("x2", CultureInfo.InvariantCulture)); break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>A Python double-quoted literal; only printable ASCII appears unescaped.</summary>
    internal static string PyString(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('"');
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case >= ' ' and <= '~': sb.Append((char)rune.Value); break;
                case <= 0xffff: sb.Append("\\u").Append(rune.Value.ToString("x4", CultureInfo.InvariantCulture)); break;
                default: sb.Append("\\U").Append(rune.Value.ToString("x8", CultureInfo.InvariantCulture)); break;
            }
        }
        return sb.Append('"').ToString();
    }

    // ---------------------------------------------------------------- C# HttpClient

    private static string RenderCSharp(Model m)
    {
        var sb = new StringBuilder(CommentBlock(m, "// ", "\n"));
        // Convert is in System, which only a project with implicit usings imports on its own.
        if (m.Kind == BodyKind.Binary) sb.Append("using System;\n");
        sb.Append("using System.Net;\nusing System.Net.Http;\nusing System.Text;\n\n");
        sb.Append("using var client = new HttpClient(new SocketsHttpHandler\n{\n");
        sb.Append("    AutomaticDecompression = DecompressionMethods.All,\n    UseCookies = false,\n");

        // A redirect is not followed: a server that answers with one could otherwise send the copied
        // Cookie and other credentials on to a host the capture never contacted.
        sb.Append("    AllowAutoRedirect = false,\n");

        // Header values are written one character per wire byte; the default encoding refuses them.
        if (m.Headers.Any(h => h.Value.Bytes.Any(b => b >= 0x80)))
            sb.Append("    RequestHeaderEncodingSelector = (_, _) => Encoding.Latin1,\n");
        sb.Append("});\n");
        sb.Append("using var request = new HttpRequestMessage(new HttpMethod(").Append(CSharpString(m.Method))
          .Append("), ").Append(CSharpString(m.Url)).Append(");\n");

        var contentHeaders = m.Headers.Where(h => IsContentHeader(h.Name)).ToList();
        if (m.Kind == BodyKind.Binary)
            sb.Append("request.Content = new ByteArrayContent(Convert.FromBase64String(").Append(CSharpString(Base64(m.Body))).Append("));\n");
        else if (m.Kind == BodyKind.Text)
            sb.Append("request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(").Append(CSharpString(m.BodyText)).Append("));\n");
        else if (contentHeaders.Count > 0)
            sb.Append("request.Content = new ByteArrayContent([]);\n");

        foreach (var (name, value) in m.Headers)
        {
            var owner = IsContentHeader(name) ? "request.Content.Headers" : "request.Headers";
            sb.Append(owner).Append(".TryAddWithoutValidation(").Append(CSharpString(name)).Append(", ")
              .Append(CSharpString(WireText(value))).Append(");\n");
        }

        sb.Append("using var response = await client.SendAsync(request);");
        return sb.ToString();
    }

    // HttpClient keeps the entity headers on the content and everything else on the request.
    private static bool IsContentHeader(string name) =>
        name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Allow", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Expires", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase);

    /// <summary>A C# regular string literal; only printable ASCII appears unescaped.</summary>
    internal static string CSharpString(string text)
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
