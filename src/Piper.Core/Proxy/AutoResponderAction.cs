using System.Text;
using Piper.Core.Http;

namespace Piper.Core.Proxy;

/// <summary>What the proxy should do with a request a rule has claimed.</summary>
public enum AutoResponderOutcome
{
    /// <summary>Send the request upstream as usual. Rule evaluation still stops.</summary>
    Passthrough,

    /// <summary>Answer locally with the rule's response.</summary>
    Respond,

    /// <summary>Fetch a different URL and return its answer under the original URL.</summary>
    Redirect,

    /// <summary>Close the connection without answering.</summary>
    Drop,

    /// <summary>Abort the connection so the client sees a reset rather than a clean close.</summary>
    Reset,
}

/// <summary>
/// One rule's action, compiled once when the rule set is applied.
/// </summary>
/// <remarks>
/// Fiddler's action vocabulary: <c>*404</c> and friends, <c>*redir:</c>, <c>*delay:</c>, <c>*drop</c>,
/// <c>*reset</c>, <c>*exit</c>, <c>*CORSPreflightAllow</c>, and a bare path meaning "serve this file".
/// A bare http(s) URL is Fiddler's transparent refetch: the client keeps its original URL and gets the
/// other address's content.
///
/// One extension: <c>*delay:</c> composes onto any other action (<c>*delay:500 *503</c>), because a slow
/// failure is the thing people most often need to reproduce and Fiddler makes you choose one or the other.
/// </remarks>
public sealed class AutoResponderAction
{
    private enum Kind { Passthrough, Status, File, Raw, Inline, Redirect, ClientRedirect, Drop, Reset, Cors }

    private readonly Kind _kind;
    private readonly string _argument;
    private readonly int _status;

    private AutoResponderAction(Kind kind, string argument = "", int status = 200, TimeSpan delay = default,
        string? warning = null)
    {
        _kind = kind;
        _argument = argument;
        _status = status;
        Delay = delay;
        Warning = warning;
    }

    /// <summary>Why this action cannot be honoured, or null. A warned action still parses, as a passthrough.</summary>
    public string? Warning { get; }

    /// <summary>How long to wait before acting, from any <c>*delay:</c> prefixes.</summary>
    public TimeSpan Delay { get; }

    public AutoResponderOutcome Outcome => _kind switch
    {
        Kind.Drop => AutoResponderOutcome.Drop,
        Kind.Reset => AutoResponderOutcome.Reset,
        Kind.Redirect => AutoResponderOutcome.Redirect,
        Kind.Passthrough => AutoResponderOutcome.Passthrough,
        _ => AutoResponderOutcome.Respond,
    };

    public static AutoResponderAction Parse(string? expression)
    {
        var text = expression?.Trim() ?? string.Empty;
        var delay = TimeSpan.Zero;

        // *delay: accumulates and then hands the rest of the string to the real action.
        while (TryTakeDelay(ref text, out var milliseconds))
            delay += TimeSpan.FromMilliseconds(milliseconds);

        if (text.Length == 0) return new AutoResponderAction(Kind.Passthrough, delay: delay);

        if (Is(text, "*drop")) return new AutoResponderAction(Kind.Drop, delay: delay);
        if (Is(text, "*reset")) return new AutoResponderAction(Kind.Reset, delay: delay);
        if (Is(text, "*exit")) return new AutoResponderAction(Kind.Passthrough, delay: delay);
        if (Is(text, "*inline")) return new AutoResponderAction(Kind.Inline, delay: delay);
        if (Is(text, "*CORSPreflightAllow")) return new AutoResponderAction(Kind.Cors, delay: delay);

        // Breakpoints do not exist in Piper. Parsed anyway so a rule set imported from Fiddler loads
        // instead of failing wholesale, with the reason shown against the rule.
        if (Is(text, "*bpu") || Is(text, "*bpafter"))
            return new AutoResponderAction(Kind.Passthrough, delay: delay,
                warning: $"{text} needs breakpoints, which Piper does not have yet - the request passes through");

        if (TryTakeArgument(text, "*redir:", out var redirect))
            return redirect.Length == 0
                ? Invalid("*redir: needs a URL", delay)
                : new AutoResponderAction(Kind.ClientRedirect, redirect, 307, delay);

        if (TryTakeArgument(text, "*raw:", out var raw) || TryTakeArgument(text, "*replay:", out raw))
            return raw.Length == 0
                ? Invalid("*raw: needs a file path", delay)
                : new AutoResponderAction(Kind.Raw, raw, delay: delay);

        if (TryTakeArgument(text, "*file:", out var file))
            return file.Length == 0
                ? Invalid("*file: needs a file path", delay)
                : new AutoResponderAction(Kind.File, file, delay: delay);

        if (text[0] == '*')
        {
            var code = text[1..].Trim();
            return int.TryParse(code, out var status) && status is >= 100 and <= 599
                ? new AutoResponderAction(Kind.Status, string.Empty, status, delay)
                : Invalid($"'{text}' is not an action Piper knows", delay);
        }

        // A bare URL is Fiddler's transparent refetch; anything else is a file to serve.
        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? new AutoResponderAction(Kind.Redirect, text, delay: delay)
            : new AutoResponderAction(Kind.File, text, delay: delay);
    }

    /// <summary>The URL a <see cref="AutoResponderOutcome.Redirect"/> action should fetch instead.</summary>
    public Uri? ResolveTarget(AutoResponderMatchResult match, Uri? original)
    {
        var target = match.Expand(_argument);
        if (Uri.TryCreate(target, UriKind.Absolute, out var absolute)) return absolute;
        return original is not null && Uri.TryCreate(original, target, out var relative) ? relative : null;
    }

    /// <summary>
    /// Builds the response this action serves. Every failure becomes a diagnosable 502 naming the rule
    /// and what it tried: a mistyped path has to be visible in the inspector, not a silent passthrough.
    /// </summary>
    public async Task<HttpResponseData> BuildResponseAsync(
        AutoResponderRule rule, AutoResponderMatchResult match, HttpRequestData request,
        long maxBodyBytes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(request);

        switch (_kind)
        {
            case Kind.Status:
                return HttpResponseData.Canned(_status, []);

            case Kind.ClientRedirect:
            {
                var response = HttpResponseData.Canned(_status, []);
                response.Headers.Set("Location", match.Expand(_argument));
                return response;
            }

            case Kind.Inline:
            {
                var body = Encoding.UTF8.GetBytes(match.Expand(rule.Body ?? string.Empty));
                return HttpResponseData.Canned(200, body, rule.ContentType ?? "text/plain; charset=utf-8");
            }

            case Kind.Cors:
                return BuildCorsPreflight(request);

            case Kind.File:
            case Kind.Raw:
                return await ServeFileAsync(rule, match, maxBodyBytes, ct).ConfigureAwait(false);

            default:
                return HttpResponseData.Canned(200, []);
        }
    }

    private async Task<HttpResponseData> ServeFileAsync(
        AutoResponderRule rule, AutoResponderMatchResult match, long maxBodyBytes, CancellationToken ct)
    {
        var path = match.Expand(_argument);
        try
        {
            // Deliberately re-read on every request and never cached: editing the file and refreshing
            // the page is the whole workflow this action exists for.
            var full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            if (!info.Exists) return Failure(rule, $"no file at {full}");
            if (info.Length > maxBodyBytes) return Failure(rule, $"{full} is larger than the {maxBodyBytes:N0} byte limit");

            var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            if (_kind == Kind.File) return HttpResponseData.Canned(200, bytes, MimeTypes.ForFile(full));

            if (!HttpWireFormat.TryParseResponse(bytes, out var response, out var error))
                return Failure(rule, $"{full} is not a raw HTTP response: {error}");

            // Framing belonged to the exchange this was captured from and would be a lie now. The body
            // and its Content-Encoding stay together, so the inspector decodes it exactly as before.
            foreach (var header in new[] { "Transfer-Encoding", "Connection", "Keep-Alive", "Trailer" })
                response.Headers.Remove(header);

            response.Headers.Set("Content-Length", response.Body.Length.ToString());
            return response;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            return Failure(rule, $"could not read {path}: {ex.Message}");
        }
    }

    /// <summary>
    /// Echoes the browser's own preflight back at it. A wildcard origin is rejected outright when
    /// credentials are involved, which is exactly the case anyone reaching for this is debugging.
    /// </summary>
    private static HttpResponseData BuildCorsPreflight(HttpRequestData request)
    {
        var response = HttpResponseData.Canned(200, []);
        response.Headers.Set("Access-Control-Allow-Origin", request.Headers["Origin"] ?? "*");
        response.Headers.Set("Access-Control-Allow-Methods",
            request.Headers["Access-Control-Request-Method"] ?? "GET, POST, PUT, PATCH, DELETE, OPTIONS, HEAD");
        if (request.Headers["Access-Control-Request-Headers"] is { } requested)
            response.Headers.Set("Access-Control-Allow-Headers", requested);
        response.Headers.Set("Access-Control-Allow-Credentials", "true");
        response.Headers.Set("Access-Control-Max-Age", "600");
        return response;
    }

    private static HttpResponseData Failure(AutoResponderRule rule, string detail) =>
        HttpResponseData.Canned(502,
            Encoding.UTF8.GetBytes($"Piper's AutoResponder could not answer this request.\r\n\r\n"
                                   + $"Rule:   {rule.Match}\r\nAction: {rule.Action}\r\nReason: {detail}\r\n"),
            "text/plain; charset=utf-8");

    private static AutoResponderAction Invalid(string warning, TimeSpan delay) =>
        new(Kind.Passthrough, delay: delay, warning: warning);

    private static bool Is(string text, string keyword) => text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    private static bool TryTakeArgument(string text, string prefix, out string argument)
    {
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            argument = string.Empty;
            return false;
        }

        argument = text[prefix.Length..].Trim();
        return true;
    }

    private static bool TryTakeDelay(ref string text, out int milliseconds)
    {
        milliseconds = 0;
        if (!text.StartsWith("*delay:", StringComparison.OrdinalIgnoreCase)) return false;

        var rest = text["*delay:".Length..];
        var end = 0;
        while (end < rest.Length && char.IsAsciiDigit(rest[end])) end++;
        if (end == 0 || !int.TryParse(rest[..end], out milliseconds)) return false;

        text = rest[end..].TrimStart(' ', '\t', ';');
        return true;
    }

    /// <summary>Actions offered by the panel's dropdown and help.</summary>
    public static readonly string[] Templates =
    [
        "*200", "*301", "*404", "*500", "*503",
        "*redir:https://", "*delay:1000", "*inline", "*raw:", "*drop", "*reset", "*exit",
        "*CORSPreflightAllow",
    ];
}
