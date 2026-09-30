using System.Text;

namespace Piper.Core.Http;

public abstract class HttpMessage
{
    public string HttpVersion { get; set; } = "HTTP/1.1";
    public HeaderCollection Headers { get; set; } = new();

    /// <summary>Body exactly as it travelled on the wire, still content-encoded and de-chunked.
    /// When <see cref="IsBodyComplete"/> is false this holds only the start of it.</summary>
    /// <remarks>Replace the array to change the body; never write into it. Search remembers each
    /// body's match by array identity, so an array changed in place would keep a stale result.</remarks>
    public byte[] Body { get; set; } = [];

    private long? _bodyTotalLength;

    /// <summary>
    /// How many body bytes actually crossed the wire, which is not always how many were kept.
    /// </summary>
    /// <remarks>
    /// Set explicitly only when a body was relayed rather than buffered; otherwise it is simply the
    /// length of <see cref="Body"/>. Everything reporting a size must read this rather than
    /// <c>Body.Length</c>, or a relayed body is reported at the size of the fragment retained --
    /// mitmproxy's HAR export has that bug, where a streamed body is indistinguishable from an
    /// empty one.
    /// </remarks>
    public long BodyTotalLength
    {
        get => _bodyTotalLength ?? Body.LongLength;
        set => _bodyTotalLength = value;
    }

    /// <summary>True when <see cref="Body"/> is the whole body rather than a retained prefix.</summary>
    public bool IsBodyComplete => Body.LongLength >= BodyTotalLength;

    /// <summary>
    /// Drops the retained bytes while keeping the length they weighed, so a released body is
    /// reported as the size it was rather than as an empty one.
    /// </summary>
    public void ReleaseBody()
    {
        _bodyTotalLength = BodyTotalLength;
        Body = [];
    }

    /// <summary>
    /// Keeps only the first <paramref name="limit"/> bytes of a body that was read whole, as a
    /// relayed body is kept, so a buffered download is not retained in full either.
    /// </summary>
    public void KeepPrefix(long limit)
    {
        if (Body.LongLength <= limit) return;
        _bodyTotalLength = BodyTotalLength;
        Body = Body[..(int)Math.Max(0, limit)];
    }

    public string? ContentType => Headers["Content-Type"];

    public string? ContentEncoding => Headers["Content-Encoding"];

    /// <summary>
    /// <see cref="Body"/> with Content-Encoding removed, capped at <see cref="ContentCodec.MaxDecodedBytes"/>,
    /// and whether the cap (or a corrupt stream) cut it short. Decoded once per body array and remembered,
    /// so reading it again costs nothing.
    /// </summary>
    internal DecodedContent Decoded => ContentCodec.DecodeCached(Body, ContentEncoding);

    /// <summary>
    /// True when <see cref="DecodedBody"/> is known to be incomplete or unreliable because a layer of the
    /// Content-Encoding was cut (see <see cref="DecodedContent.Truncated"/> for the exact meaning). A
    /// retained prefix of a larger body is a separate condition: check <see cref="IsBodyComplete"/> too.
    /// </summary>
    public bool IsDecodedBodyTruncated => Decoded.Truncated;

    /// <summary>Body with Content-Encoding removed. Falls back to the raw body if decoding fails.</summary>
    /// <remarks>
    /// A body that decodes to at most 8 MiB is remembered per message, so reading this again costs a copy
    /// rather than a decompression; larger ones (up to the 64 MiB cap) are decoded afresh each time. The
    /// copy is what lets a caller edit the result without corrupting what the next reader sees. A body
    /// with no Content-Encoding is <see cref="Body"/> itself, with the same "read it, never write into
    /// it" rule.
    /// </remarks>
    public byte[] DecodedBody
    {
        get
        {
            var body = Body;
            var decoded = ContentCodec.DecodeCached(body, ContentEncoding).Bytes;
            return ReferenceEquals(decoded, body) ? body : (byte[])decoded.Clone();
        }
    }

    /// <summary>Best-effort text rendering of <see cref="DecodedBody"/> using the charset from Content-Type.</summary>
    public string BodyAsText() => TextOf(Body, ContentType, ContentEncoding);

    /// <summary>Renders an already decoded body without repeating content decompression.</summary>
    public string BodyAsText(byte[] decodedBody) => DecodedText(decodedBody, ContentType);

    /// <summary>
    /// What <see cref="BodyAsText()"/> gives, for a body and headers already read off the message,
    /// so a caller that must describe exactly those values decodes them the same way the viewer does.
    /// </summary>
    public static string TextOf(byte[] body, string? contentType, string? contentEncoding) =>
        DecodedText(ContentCodec.Decode(body, contentEncoding), contentType);

    private static string DecodedText(byte[] decodedBody, string? contentType) => decodedBody.Length == 0
        ? string.Empty
        : ContentCodec.CharsetFor(contentType).GetString(decodedBody);

    public abstract string StartLine { get; }

    /// <summary>Start line + headers + blank line, as text.</summary>
    public string HeadAsText() => StartLine + "\r\n" + Headers.ToRawString() + "\r\n";
}

public sealed class HttpRequestData : HttpMessage
{
    public string Method { get; set; } = "GET";

    /// <summary>Request target exactly as it appeared: origin-form, absolute-form or authority-form.</summary>
    public string RequestTarget { get; set; } = "/";

    /// <summary>Fully-qualified URL, reconstructed from Host when the target is origin-form.</summary>
    public Uri? Url { get; set; }

    public override string StartLine => $"{Method} {RequestTarget} {HttpVersion}";

    public HttpRequestData Clone() => new()
    {
        Method = Method,
        RequestTarget = RequestTarget,
        HttpVersion = HttpVersion,
        Url = Url,
        Headers = Headers.Clone(),
        Body = (byte[])Body.Clone(),
        BodyTotalLength = BodyTotalLength,
    };

    /// <summary>Serialises in origin-form, which is what an upstream origin server expects.</summary>
    public byte[] ToOriginFormBytes()
    {
        var target = Url is not null ? Url.PathAndQuery : RequestTarget;
        var sb = new StringBuilder();
        sb.Append(Method).Append(' ').Append(target).Append(' ').Append(HttpVersion).Append("\r\n");
        sb.Append(Headers.ToRawString());
        sb.Append("\r\n");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        if (Body.Length == 0) return head;
        var full = new byte[head.Length + Body.Length];
        Buffer.BlockCopy(head, 0, full, 0, head.Length);
        Buffer.BlockCopy(Body, 0, full, head.Length, Body.Length);
        return full;
    }
}

public sealed class HttpResponseData : HttpMessage
{
    public int StatusCode { get; set; } = 200;
    public string ReasonPhrase { get; set; } = "OK";

    public override string StartLine => $"{HttpVersion} {StatusCode} {ReasonPhrase}";

    public HttpResponseData Clone() => new()
    {
        StatusCode = StatusCode,
        ReasonPhrase = ReasonPhrase,
        HttpVersion = HttpVersion,
        Headers = Headers.Clone(),
        Body = (byte[])Body.Clone(),
        BodyTotalLength = BodyTotalLength,
    };

    public byte[] ToBytes()
    {
        var sb = new StringBuilder();
        sb.Append(StartLine).Append("\r\n");
        sb.Append(Headers.ToRawString());
        sb.Append("\r\n");
        var head = Encoding.Latin1.GetBytes(sb.ToString());
        if (Body.Length == 0) return head;
        var full = new byte[head.Length + Body.Length];
        Buffer.BlockCopy(head, 0, full, 0, head.Length);
        Buffer.BlockCopy(Body, 0, full, head.Length, Body.Length);
        return full;
    }

    public static HttpResponseData Simple(int status, string reason, string body, string contentType = "text/plain; charset=utf-8")
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var r = new HttpResponseData { StatusCode = status, ReasonPhrase = reason, Body = bytes };
        r.Headers.Set("Content-Type", contentType);
        r.Headers.Set("Content-Length", bytes.Length.ToString());
        r.Headers.Set("Connection", "close");
        return r;
    }

    /// <summary>
    /// A locally-generated response whose downstream framing is decided later - by
    /// <c>ProxyServer.BuildInboundResponse</c> on HTTP/1.1, or by the framer on HTTP/2.
    /// </summary>
    /// <remarks>
    /// Deliberately sets no Connection header, unlike <see cref="Simple"/>, which exists for terminal
    /// errors on a connection that is about to close. A response served from a rule has to be able to
    /// keep the connection alive, or every faked request would look artificially slow.
    /// </remarks>
    public static HttpResponseData Canned(int status, byte[] body, string? contentType = null, string? reason = null)
    {
        ArgumentNullException.ThrowIfNull(body);

        var response = new HttpResponseData
        {
            StatusCode = status,
            ReasonPhrase = reason ?? ReasonPhrases.ForOrClass(status),
            Body = body,
        };

        if (contentType is not null) response.Headers.Set("Content-Type", contentType);
        response.Headers.Set("Content-Length", body.Length.ToString());
        return response;
    }
}
