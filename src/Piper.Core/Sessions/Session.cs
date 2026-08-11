using Piper.Core.Http;

namespace Piper.Core.Sessions;

public enum SessionState
{
    Pending,
    SendingRequest,
    AwaitingResponse,
    Complete,
    Failed,
    Tunnel,
}

public enum TransportProtocol
{
    Http1_1,
    Http2,
    Http3,
}

/// <summary>One captured request/response exchange.</summary>
public sealed class Session
{
    private static int _counter;

    public Session()
    {
        Id = Interlocked.Increment(ref _counter);
        Started = DateTimeOffset.Now;
    }

    public int Id { get; }
    public DateTimeOffset Started { get; }
    public DateTimeOffset? Completed { get; set; }

    public SessionState State { get; set; } = SessionState.Pending;

    public HttpRequestData? Request { get; set; }
    public HttpResponseData? Response { get; set; }

    /// <summary>Populated when the exchange failed before a response was parsed.</summary>
    public string? Error { get; set; }

    /// <summary>True for CONNECT tunnels we passed through without decrypting.</summary>
    public bool IsTunnel { get; set; }

    public bool IsHttps { get; set; }

    /// <summary>Set when this session was produced by the Composer rather than captured.</summary>
    public bool IsComposed { get; set; }

    public string ClientEndpoint { get; set; } = string.Empty;
    public string? ServerEndpoint { get; set; }

    /// <summary>Short name (e.g. "chrome") of the OS process that owns the client TCP connection
    /// this session came from, resolved via <see cref="Proxy.ClientProcessLookup"/>. Empty when
    /// unresolved (composed sessions, lookup failure, non-loopback client, etc.).</summary>
    public string ProcessName { get; set; } = string.Empty;

    // --- Timings ---
    public TimeSpan? ConnectTime { get; set; }
    public TimeSpan? TimeToFirstByte { get; set; }
    public TimeSpan Duration => (Completed ?? DateTimeOffset.Now) - Started;

    // --- Convenience projections used by the grid and the search engine ---

    public string Method => Request?.Method ?? string.Empty;

    public string Url => Request?.Url?.ToString() ?? Request?.RequestTarget ?? string.Empty;

    public string Host => Request?.Url?.Host ?? Request?.Headers["Host"] ?? string.Empty;

    public string Path => Request?.Url?.AbsolutePath ?? string.Empty;

    public string Query => Request?.Url?.Query ?? string.Empty;

    public int StatusCode => Response?.StatusCode ?? 0;

    public string ContentType => Response?.ContentType ?? string.Empty;

    public long RequestSize => Request?.Body.LongLength ?? 0;

    public long ResponseSize => Response?.Body.LongLength ?? 0;

    /// <summary>The protocol version the browser actually used talking to Piper. Computed (not
    /// stored) from <see cref="Request"/>'s <c>HttpVersion</c> string, which is already populated
    /// per-leg by whichever adapter built that message (<c>HttpParser</c> for h1.1,
    /// <c>Http2MessageAdapter</c> for h2) -- same pattern as <see cref="Method"/>/<see cref="Host"/>.</summary>
    public TransportProtocol RequestProtocol => ParseProtocol(Request?.HttpVersion);

    /// <summary>The protocol version the real origin actually used talking to Piper. Can
    /// legitimately differ from <see cref="RequestProtocol"/> -- that divergence is the whole
    /// point of a proxy that translates between protocol versions rather than just relaying bytes.</summary>
    public TransportProtocol ResponseProtocol => ParseProtocol(Response?.HttpVersion);

    private static TransportProtocol ParseProtocol(string? httpVersion) => httpVersion switch
    {
        "HTTP/2" => TransportProtocol.Http2,
        "HTTP/3" => TransportProtocol.Http3,
        _ => TransportProtocol.Http1_1,
    };

    public string StatusText => State switch
    {
        SessionState.Failed => "ERR",
        SessionState.Tunnel => "CONNECT",
        _ when Response is not null => Response.StatusCode.ToString(),
        _ => "-",
    };

    /// <summary>Cached lowercase haystack for substring search. Built once, on demand.</summary>
    private string? _searchIndex;

    public string SearchIndex => _searchIndex ??= BuildSearchIndex();

    /// <summary>Invalidate the cached haystack after mutating the request or response.</summary>
    public void InvalidateSearchIndex() => _searchIndex = null;

    private string BuildSearchIndex()
    {
        var sb = new System.Text.StringBuilder(512);
        sb.Append(Method).Append(' ').Append(Url).Append(' ').Append(StatusText).Append(' ').Append(ContentType);

        if (Request is not null)
        {
            sb.Append(' ').Append(Request.Headers.ToRawString());
            AppendTextBody(sb, Request);
        }
        if (Response is not null)
        {
            sb.Append(' ').Append(Response.Headers.ToRawString());
            AppendTextBody(sb, Response);
        }
        return sb.ToString().ToLowerInvariant();
    }

    private static void AppendTextBody(System.Text.StringBuilder sb, HttpMessage message)
    {
        if (message.Body.Length == 0) return;
        if (!ContentCodec.LooksTextual(message.ContentType, message.Body)) return;
        try
        {
            var text = message.BodyAsText();
            // Cap per-message contribution so one huge payload cannot dominate memory.
            sb.Append(' ').Append(text.Length <= 64_000 ? text : text[..64_000]);
        }
        catch
        {
            // Undecodable body - it simply does not participate in text search.
        }
    }

    public override string ToString() => $"#{Id} {Method} {Url} -> {StatusText}";
}
