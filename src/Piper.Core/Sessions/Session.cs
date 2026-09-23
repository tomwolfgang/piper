using Piper.Core.Http;

namespace Piper.Core.Sessions;

public enum SessionState
{
    Pending,
    SendingRequest,
    AwaitingResponse,

    /// <summary>The head has been relayed to the client and the body is still arriving.</summary>
    ReceivingBody,
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

    private int _state = (int)SessionState.Pending;

    /// <summary>
    /// Written by a proxy thread and read by the UI thread. Volatile, so that everything the proxy
    /// set before a transition -- the response, its body, the expected length -- is visible to a
    /// reader that has seen the new state.
    /// </summary>
    public SessionState State
    {
        get => (SessionState)Volatile.Read(ref _state);
        set => Volatile.Write(ref _state, (int)value);
    }

    private long _bytesReceived;

    /// <summary>Response body bytes relayed so far. Only a relayed body reports this.</summary>
    public long BytesReceived => Volatile.Read(ref _bytesReceived);

    /// <summary>Called by the relay on a proxy thread after each run of body bytes it forwards.</summary>
    public void ReportBytesReceived(long total) => Volatile.Write(ref _bytesReceived, total);

    /// <summary>The body length the origin announced for a relayed body; -1 when it did not.</summary>
    public long ExpectedResponseBytes { get; set; } = -1;

    /// <summary>
    /// How far through a relayed body of known length the session is, from 0 to 1; null when it is
    /// not receiving one, or the length was not announced.
    /// </summary>
    public double? ResponseProgress =>
        State == SessionState.ReceivingBody && ExpectedResponseBytes > 0
            ? Math.Clamp((double)BytesReceived / ExpectedResponseBytes, 0, 1)
            : null;

    public HttpRequestData? Request { get; set; }
    public HttpResponseData? Response { get; set; }

    /// <summary>Populated when the exchange failed before a response was parsed.</summary>
    public string? Error { get; set; }

    /// <summary>True for CONNECT tunnels we passed through without decrypting.</summary>
    public bool IsTunnel { get; set; }

    public bool IsHttps { get; set; }

    /// <summary>Set when this session was produced by the Composer rather than captured.</summary>
    public bool IsComposed { get; set; }

    /// <summary>True when Piper itself checked for a published update.</summary>
    public bool IsUpdateCheck { get; set; }

    /// <summary>
    /// The AutoResponder rule that answered this request, or null for ordinary traffic. Holds the
    /// rule rather than a flag because "which rule fired" is the first thing anyone asks when a
    /// response turns out to be faked.
    /// </summary>
    public string? AutoResponderRule { get; set; }

    public bool IsAutoResponded => AutoResponderRule is not null;

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

    public long RequestSize => Request?.BodyTotalLength ?? 0;

    /// <summary>What the response body weighed on the wire, which is what a size column means --
    /// not how much of it was kept when only a prefix of a large body is retained. While a body is
    /// still being relayed, and after one failed part way, that is the bytes relayed so far.</summary>
    public long ResponseSize => Math.Max(Response?.BodyTotalLength ?? 0, BytesReceived);

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
        SessionState.Failed => FailedStatusText,
        SessionState.Tunnel => TunnelStatusText,
        _ when Response is not null => Response.StatusCode.ToString(),
        _ => NoStatusText,
    };

    private const string FailedStatusText = "ERR";
    private const string TunnelStatusText = "CONNECT";
    private const string NoStatusText = "-";

    /// <summary>
    /// Every word <see cref="StatusText"/> can show in place of a status code, so the grid can size
    /// its Result column to the longest of them.
    /// </summary>
    public static IReadOnlyList<string> StatusWords { get; } = [FailedStatusText, TunnelStatusText, NoStatusText];

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
