using System.Text;

namespace Piper.Core.Http;

public abstract class HttpMessage
{
    public string HttpVersion { get; set; } = "HTTP/1.1";
    public HeaderCollection Headers { get; set; } = new();

    /// <summary>Body exactly as it travelled on the wire, still content-encoded and de-chunked.</summary>
    public byte[] Body { get; set; } = [];

    public string? ContentType => Headers["Content-Type"];

    public string? ContentEncoding => Headers["Content-Encoding"];

    /// <summary>Body with Content-Encoding removed. Falls back to the raw body if decoding fails.</summary>
    public byte[] DecodedBody => ContentCodec.Decode(Body, ContentEncoding);

    /// <summary>Best-effort text rendering of <see cref="DecodedBody"/> using the charset from Content-Type.</summary>
    public string BodyAsText()
    {
        var bytes = DecodedBody;
        if (bytes.Length == 0) return string.Empty;
        return ContentCodec.CharsetFor(ContentType).GetString(bytes);
    }

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
}
