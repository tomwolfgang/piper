using System.Globalization;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Http3.Qpack;
using Piper.Core.Proxy;
using Piper.Core.Security;

// CA1416 flags System.Net.Quic as platform-specific (linux/macOS/windows). Every entry point here
// is gated behind IsSupported -> QuicConnection.IsSupported, which is the runtime check the
// platform analyser cannot see through, and h3 is opt-in and falls back to TCP when unavailable.
#pragma warning disable CA1416

namespace Piper.Core.Http3;

/// <summary>
/// Client-role HTTP/3 connection (origin-facing). QUIC itself comes from
/// <see cref="System.Net.Quic"/> -- msquic ships inside the .NET runtime, so this needs no NuGet
/// package and nothing extra installed on an end user's machine. What is implemented here is only
/// the HTTP/3 layer above it: the control stream, SETTINGS, framing and QPACK.
/// </summary>
/// <remarks>
/// Upstream only, by design. A browser pointed at a system HTTP proxy always tunnels through
/// <c>CONNECT</c> over TCP and disables QUIC for proxied traffic, so there is no such thing as a
/// browser speaking HTTP/3 *to* Piper. This exists so Piper can see what an origin actually
/// serves over QUIC, which is otherwise invisible.
/// <para>
/// Like <see cref="Http2ClientConnection"/>, phase 1 scope is one request per connection: no
/// stream reuse, no server push (we never send MAX_PUSH_ID, so a compliant origin cannot push).
/// The response is read in full before it is returned, so its size is bounded
/// (<see cref="DefaultMaxResponseBodyBytes"/>); relaying it as it arrives is epic E7.
/// </para>
/// <para>
/// Everything the origin sends is bounded. The field section is limited to the figure advertised
/// in SETTINGS, the body to a running total, ignorable frames in number and size, and the streams
/// the origin may open in number. The wait is an idle timeout that only real progress re-arms, so a
/// slow download that keeps moving is left alone and a stalled one is cut.
/// </para>
/// </remarks>
public sealed class Http3ClientConnection : IAsyncDisposable
{
    /// <summary>The field section size advertised in SETTINGS and enforced on every response (and
    /// trailer) section, measured the way RFC 9114 §4.2 measures it: decoded.</summary>
    public const long MaxFieldSectionSize = 128 * 1024;

    /// <summary>The most response body held in memory for one request. The body is copied once more
    /// when it is handed back, so the peak is about three times this.</summary>
    public const long DefaultMaxResponseBodyBytes = 64L * 1024 * 1024;

    private const int MaxInterimResponses = 32;
    private const int MaxIgnoredFrames = 64;
    private const long MaxIgnoredFramePayload = 64 * 1024;
    private const int MaxInboundStreams = 32;
    private const int MaxGoAwayFrames = 16;
    private const long MaxControlFramePayload = 4096;
    private const long MaxAuxiliaryStreamBytes = 1024 * 1024;
    private const int RequestWriteChunk = 64 * 1024;
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);

    private readonly QuicConnection _connection;
    private QuicStream? _controlStream;

    // Cancelled to stop the inbound-stream handling when the connection is disposed.
    private readonly CancellationTokenSource _lifetime = new();
    // Cancelled when the connection can no longer carry the request; _abortReason says why.
    private readonly CancellationTokenSource _abort = new();
    private readonly Lock _gate = new();
    private Task? _inbound;
    private Exception? _abortReason;
    private long _requestStreamId = -1;
    private long _goAwayId = -1;
    private Http3PeerSettings? _peerSettings;
    private bool _controlStreamSeen;
    private bool _qpackEncoderStreamSeen;
    private bool _qpackDecoderStreamSeen;
    private TimeSpan _idleTimeout = TimeSpan.FromSeconds(15);
    private TimeSpan _maxResponseTime = TimeSpan.FromMinutes(15);

    private Http3ClientConnection(QuicConnection connection) => _connection = connection;

    public IPEndPoint? RemoteEndpoint => _connection.RemoteEndPoint as IPEndPoint;

    /// <summary>True when QUIC is usable at all on this machine. False means no msquic, and every
    /// h3 attempt should be skipped rather than repeatedly failing.</summary>
    public static bool IsSupported => QuicConnection.IsSupported;

    /// <summary>
    /// How long the request may go without the origin making progress: a complete header section, or
    /// body bytes. A pause, not a total budget -- a long download that keeps arriving never trips
    /// it. Set to <see cref="Timeout.InfiniteTimeSpan"/> to wait for ever.
    /// </summary>
    public TimeSpan IdleTimeout
    {
        get => _idleTimeout;
        set
        {
            if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(value), "The idle timeout must be positive.");
            _idleTimeout = value;
        }
    }

    /// <summary>
    /// The longest the request may take in all, however steadily it progresses -- a ceiling over
    /// <see cref="IdleTimeout"/>, which alone lets an origin that sends a few bytes per period hold the
    /// request for ever. Set to <see cref="Timeout.InfiniteTimeSpan"/> for no ceiling.
    /// </summary>
    public TimeSpan MaxResponseTime
    {
        get => _maxResponseTime;
        set
        {
            if (value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(value), "The response time limit must be positive.");
            _maxResponseTime = value;
        }
    }

    /// <summary>Body bytes that must arrive within one idle period for the origin to count as making
    /// progress. A few bytes now and then keep a connection alive without moving anything, so the
    /// idle timeout is only re-armed once a window has carried this much (about 70 bytes a second at
    /// the default). A complete header section re-arms it regardless.</summary>
    internal long MinProgressBytes { get; set; } = 1024;

    internal long MaxResponseBodyBytes { get; set; } = DefaultMaxResponseBodyBytes;

    /// <summary>The origin's SETTINGS, once its control stream has delivered them.</summary>
    public Http3PeerSettings? PeerSettings
    {
        get { lock (_gate) return _peerSettings; }
    }

    /// <summary>The stream id from the origin's GOAWAY, if it sent one.</summary>
    public long? GoAwayStreamId
    {
        get { lock (_gate) return _goAwayId >= 0 ? _goAwayId : null; }
    }

    public static async Task<Http3ClientConnection> ConnectAsync(
        string host, int port, ProxyOptions options, CancellationToken ct)
    {
        var remapping = options.HostRemapping.ResolveTarget(host);
        EndPoint remoteEndPoint = IPAddress.TryParse(remapping.Host, out var address)
            ? new IPEndPoint(address, port)
            : new DnsEndPoint(remapping.Host, port);

        string? rejectionDetail = null;
        QuicConnection connection;
        try
        {
            connection = await QuicConnection.ConnectAsync(new QuicClientConnectionOptions
            {
                RemoteEndPoint = remoteEndPoint,
                DefaultStreamErrorCode = (long)Http3ErrorCode.RequestCancelled,
                DefaultCloseErrorCode = (long)Http3ErrorCode.NoError,
                MaxInboundUnidirectionalStreams = 8, // control + QPACK encoder/decoder, plus slack
                MaxInboundBidirectionalStreams = 0,  // we never accept origin-initiated requests
                ClientAuthenticationOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = remapping.RewritesAuthority ? remapping.Host : host,
                    ApplicationProtocols = [new SslApplicationProtocol("h3")],
                    RemoteCertificateValidationCallback = (_, _, chain, errors) =>
                    {
                        if (!options.ValidateUpstreamCertificates || errors == SslPolicyErrors.None) return true;
                        rejectionDetail = CertificateRejectionDetail.Describe(errors, chain);
                        return false;
                    },
                },
            }, ct).ConfigureAwait(false);
        }
        catch (AuthenticationException ex) when (rejectionDetail is not null)
        {
            throw new AuthenticationException($"{ex.Message} ({rejectionDetail})", ex);
        }

        var client = new Http3ClientConnection(connection);
        try
        {
            await client.SendControlStreamAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        client._inbound = client.AcceptInboundStreamsAsync(client._lifetime.Token);
        return client;
    }

    /// <summary>
    /// Opens the outbound control stream and sends SETTINGS. RFC 9114 §6.2.1 requires each side to
    /// open exactly one control stream, whose first byte is the stream type, and to send SETTINGS
    /// as its first frame -- an origin will close the connection if this never arrives.
    /// </summary>
    private async Task SendControlStreamAsync(CancellationToken ct)
    {
        _controlStream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, ct).ConfigureAwait(false);

        var preamble = new List<byte>(32);
        VarInt.Write(preamble, Http3StreamType.Control);
        preamble.AddRange(Http3FrameWriter.Encode(Http3FrameType.Settings, Http3FrameWriter.EncodeSettings(
            // Zero capacity and zero blocked streams tell the origin's encoder it may not use the
            // dynamic table, which is what lets the QPACK decoder here stay static-table-only.
            (Http3SettingId.QpackMaxTableCapacity, 0),
            (Http3SettingId.QpackBlockedStreams, 0),
            (Http3SettingId.MaxFieldSectionSize, MaxFieldSectionSize))));

        await _controlStream.WriteAsync(preamble.ToArray(), ct).ConfigureAwait(false);
        await _controlStream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<HttpResponseData> SendRequestAsync(HttpRequestData request, CancellationToken ct)
    {
        // One token for everything after the handshake. It fires when the caller cancels, when the
        // connection can no longer carry the request (GOAWAY, a protocol violation), or when the
        // origin goes quiet for IdleTimeout; the catch below tells the three apart.
        using var deadline = new CancellationTokenSource();
        deadline.CancelAfter(MaxResponseTime);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _abort.Token, deadline.Token);
        var idle = IdleTimeout;
        var minProgress = MinProgressBytes;
        long windowBytes = 0;
        void Progress()
        {
            windowBytes = 0;
            operation.CancelAfter(idle);
        }
        void BodyProgress(int bytes)
        {
            windowBytes += bytes;
            if (windowBytes >= minProgress) Progress();
        }
        Progress();

        try
        {
            await using var stream = await _connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, operation.Token).ConfigureAwait(false);
            RegisterRequestStream(stream.Id);

            await WriteRequestAsync(stream, request, operation.Token, Progress).ConfigureAwait(false);
            return await ReadResponseAsync(stream, request.Method, operation.Token, Progress, BodyProgress).ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested && AbortReason is { } reason)
        {
            // Whatever the request tripped over (a cancelled wait, a closed connection) is the
            // symptom; the reason recorded when the connection was given up on is the cause.
            throw reason;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(deadline.IsCancellationRequested
                ? string.Create(CultureInfo.InvariantCulture, $"The HTTP/3 response took longer than {MaxResponseTime.TotalSeconds:0.##} s.")
                : string.Create(CultureInfo.InvariantCulture, $"The HTTP/3 origin made too little progress for {idle.TotalSeconds:0.##} s."));
        }
        catch (Http3ProtocolException ex)
        {
            // A connection error: tell the origin which, then let the caller see it.
            await CloseQuietlyAsync(ex.ErrorCode).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteRequestAsync(
        QuicStream stream, HttpRequestData request, CancellationToken ct, Action progress)
    {
        var fields = Http2MessageAdapter.ToHeaderFields(request); // h3 reuses h2's pseudo-header shape (RFC 9114 §4.1.1)
        var headerBlock = QpackEncoder.Encode(fields);

        await stream.WriteAsync(Http3FrameWriter.Encode(Http3FrameType.Headers, headerBlock),
            completeWrites: request.Body.Length == 0, ct).ConfigureAwait(false);
        progress();

        if (request.Body.Length == 0) return;

        // One DATA frame, written in pieces: each piece the origin's flow control accepts is progress,
        // so an upload is held to the idle timeout and not to one call that waits as long as it takes.
        await stream.WriteAsync(Http3FrameWriter.EncodeHeader(Http3FrameType.Data, request.Body.Length),
            completeWrites: false, ct).ConfigureAwait(false);
        for (var offset = 0; offset < request.Body.Length; offset += RequestWriteChunk)
        {
            var count = Math.Min(RequestWriteChunk, request.Body.Length - offset);
            await stream.WriteAsync(request.Body.AsMemory(offset, count),
                completeWrites: offset + count >= request.Body.Length, ct).ConfigureAwait(false);
            progress();
        }
    }

    /// <summary>
    /// Reads the response: <c>HEADERS(1xx)* HEADERS DATA* [HEADERS]</c> (RFC 9114 §4.1). Frames out of
    /// that order are connection errors; unknown frame types are skipped, within limits.
    /// </summary>
    private async Task<HttpResponseData> ReadResponseAsync(
        QuicStream stream, string method, CancellationToken ct, Action progress, Action<int> bodyProgress)
    {
        var reader = new Http3StreamReader(stream);
        var body = new MemoryStream();
        List<(string Name, string Value)>? fields = null;
        long? declared = null;
        var trailersSeen = false;
        var interim = 0;
        var ignored = 0;

        while (true)
        {
            var header = await reader.ReadFrameHeaderAsync(ct).ConfigureAwait(false);
            if (header is null) break; // origin finished the stream
            var (type, length) = header.Value;

            switch (type)
            {
                case (long)Http3FrameType.Headers:
                    if (trailersSeen)
                        throw new Http3ProtocolException(Http3ErrorCode.FrameUnexpected, "HEADERS after the trailer section.");

                    // The limit advertised in SETTINGS is defined on the decoded section, which is at least
                    // as large as the encoded one (a one-byte reference can stand for dozens of bytes). So
                    // a frame longer than the limit is certainly over it, and is refused on its length
                    // before being read; the decoder is the authority for the rest, and counts as it builds.
                    // Both are the same breach.
                    if (length > MaxFieldSectionSize)
                        throw new Http3ProtocolException(Http3ErrorCode.ExcessiveLoad,
                            $"HEADERS frame of {length} bytes exceeds the {MaxFieldSectionSize}-byte field section limit.");
                    var block = await reader.ReadPayloadAsync(length, MaxFieldSectionSize, ct).ConfigureAwait(false);
                    var decoded = QpackDecoder.Decode(block, MaxFieldSectionSize);
                    progress();

                    if (fields is not null)
                    {
                        // A trailer section: fields only. A pseudo-header in it -- a :status, say, which
                        // would be a second response, 1xx or not -- makes the message malformed (§4.1.2).
                        if (decoded.Any(f => f.Name.StartsWith(':')))
                            throw new HttpParseException("HTTP/3 trailer section carries a pseudo-header.");
                        trailersSeen = true; // carries nothing Piper records
                        break;
                    }

                    var status = decoded.FirstOrDefault(f => f.Name == ":status").Value;
                    if (!HttpSyntax.TryParseStatusCode(status, out var code))
                        throw new HttpParseException("HTTP/3 response carries no valid :status (three digits, 100 to 599).");

                    // 1xx are interim (RFC 9114 §4.1.2): keep reading for the real response.
                    if (code is >= 100 and < 200)
                    {
                        if (++interim > MaxInterimResponses)
                            throw new HttpParseException($"HTTP/3 origin sent more than {MaxInterimResponses} interim responses.");
                        break;
                    }

                    // A response that announces more than fits is refused on its headers, before a byte of it moves.
                    declared = DeclaredLength(decoded, method, code);
                    if (declared > MaxResponseBodyBytes) throw TooLarge();
                    fields = decoded;
                    break;

                case (long)Http3FrameType.Data:
                    if (fields is null)
                        throw new Http3ProtocolException(Http3ErrorCode.FrameUnexpected, "DATA before the response HEADERS.");
                    if (trailersSeen)
                        throw new Http3ProtocolException(Http3ErrorCode.FrameUnexpected, "DATA after the trailer section.");

                    // A frame that cannot fit is refused on its length, before any of it is read.
                    if (length > MaxResponseBodyBytes - body.Length)
                        throw TooLarge();
                    if (declared is { } announced && length > announced - body.Length)
                        throw new HttpParseException($"HTTP/3 body is longer than its Content-Length of {announced}.");

                    for (var remaining = length; remaining > 0;)
                    {
                        var copied = await reader.CopyPayloadAsync(body, remaining, ct).ConfigureAwait(false);
                        remaining -= copied;
                        bodyProgress(copied); // bytes arrived: counts once a window has carried enough
                    }
                    break;

                case (long)Http3FrameType.PushPromise:
                    // We never sent MAX_PUSH_ID, so there is no push ID this could be valid for.
                    throw new Http3ProtocolException(Http3ErrorCode.IdError, "The origin used server push, which was never enabled.");

                case (long)Http3FrameType.CancelPush: // control stream only (§7.2.3)
                case (long)Http3FrameType.Settings:
                case (long)Http3FrameType.GoAway:
                case (long)Http3FrameType.MaxPushId:
                    throw new Http3ProtocolException(Http3ErrorCode.FrameUnexpected, $"Control frame 0x{type:x} on a request stream.");

                default:
                    if (Http3FrameTypes.IsReservedFromHttp2(type))
                        throw new Http3ProtocolException(Http3ErrorCode.FrameUnexpected, $"Frame type 0x{type:x} is reserved from HTTP/2.");

                    // Unknown types must be ignored (§9), but ignoring is not unlimited: the frames
                    // are counted and sized, and skipping makes no progress, so a stream of them
                    // runs out the idle timeout like any other silence.
                    if (++ignored > MaxIgnoredFrames || length > MaxIgnoredFramePayload)
                        throw new Http3ProtocolException(Http3ErrorCode.ExcessiveLoad, "Too much ignorable data on a request stream.");
                    await reader.SkipPayloadAsync(length, ct).ConfigureAwait(false);
                    break;
            }
        }

        if (fields is null) throw new HttpParseException("HTTP/3 response ended before its headers arrived.");

        // A clean end of stream short of the announced length is not a complete message (§4.1.2).
        if (declared is { } expected && body.Length != expected)
            throw new HttpParseException($"HTTP/3 body ended after {body.Length} of the {expected} bytes its Content-Length announced.");

        var response = Http2MessageAdapter.ToResponse(fields);
        response.HttpVersion = "HTTP/3";
        response.Body = body.ToArray();
        return response;
    }

    // The body length the response announces, or null when none applies: HEAD, 204 and 304 announce a
    // length for a body that is not sent, and a Content-Length that is not a plain number is left
    // alone. Two plain numbers that disagree are ambiguous framing and refused.
    private static long? DeclaredLength(List<(string Name, string Value)> fields, string method, int status)
    {
        if (method.Equals("HEAD", StringComparison.OrdinalIgnoreCase) || status is 204 or 304) return null;

        long? declared = null;
        foreach (var (name, value) in fields)
        {
            if (name != "content-length") continue;
            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) continue;
            if (declared is not null && declared != parsed)
                throw new HttpParseException("HTTP/3 response carries conflicting Content-Length values.");
            declared = parsed;
        }
        return declared;
    }

    private Http3ResponseTooLargeException TooLarge() =>
        new($"The HTTP/3 response is larger than the {MaxResponseBodyBytes}-byte limit of the buffered path.");

    private void RegisterRequestStream(long streamId)
    {
        Exception? refused;
        lock (_gate)
        {
            _requestStreamId = streamId;
            refused = _goAwayId >= 0 && streamId >= _goAwayId ? NotProcessed(_goAwayId) : null;
        }
        if (refused is not null) throw refused;
    }

    private static Http3GoAwayException NotProcessed(long goAwayId) =>
        new(goAwayId, $"The HTTP/3 origin sent GOAWAY({goAwayId}) and will not process the request.");

    private Exception? AbortReason
    {
        get { lock (_gate) return _abortReason; }
    }

    // ------------------------------------------------------------------ inbound streams

    /// <summary>
    /// Takes the origin's unidirectional streams. The control stream is parsed (SETTINGS, GOAWAY);
    /// the QPACK streams carry nothing with a zero-capacity table and are drained within a bound,
    /// because leaving them unread would eventually apply back pressure; anything else is refused.
    /// Never throws: a violation aborts the connection and reaches the caller through the request.
    /// </summary>
    private async Task AcceptInboundStreamsAsync(CancellationToken ct)
    {
        var handlers = new List<Task>();
        try
        {
            for (var accepted = 0; ; accepted++)
            {
                var stream = await _connection.AcceptInboundStreamAsync(ct).ConfigureAwait(false);
                if (accepted >= MaxInboundStreams)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                    throw new Http3ProtocolException(Http3ErrorCode.ExcessiveLoad, "The origin opened too many streams.");
                }
                handlers.Add(HandleInboundStreamAsync(stream, ct));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuicException or ObjectDisposedException)
        {
            // The connection is closing, whether we or the origin closed it.
        }
        catch (Http3ProtocolException ex)
        {
            await AbortAsync(ex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await AbortAsync(new Http3ProtocolException(Http3ErrorCode.InternalError,
                $"Unexpected {ex.GetType().Name} accepting inbound streams.")).ConfigureAwait(false);
        }

        await Task.WhenAll(handlers).ConfigureAwait(false);
    }

    private async Task HandleInboundStreamAsync(QuicStream stream, CancellationToken ct)
    {
        try
        {
            await using (stream.ConfigureAwait(false))
            {
                var reader = new Http3StreamReader(stream);
                var type = await reader.ReadVarIntAsync(ct).ConfigureAwait(false);
                if (type is null) return; // closed before it said what it was

                switch (type.Value)
                {
                    case Http3StreamType.Control:
                        ClaimStream(ref _controlStreamSeen, "control");
                        try
                        {
                            await ReadControlStreamAsync(reader, ct).ConfigureAwait(false);
                        }
                        catch (QuicException) when (!ct.IsCancellationRequested)
                        {
                            // Reset or aborted by the origin while we are still using the connection:
                            // as much a closed control stream (§6.2.1) as a clean end of it.
                            throw new Http3ProtocolException(Http3ErrorCode.ClosedCriticalStream, "The origin reset its control stream.");
                        }
                        break;

                    case Http3StreamType.QpackEncoder:
                        ClaimStream(ref _qpackEncoderStreamSeen, "QPACK encoder");
                        await reader.DrainAsync(MaxAuxiliaryStreamBytes, ct).ConfigureAwait(false);
                        break;

                    case Http3StreamType.QpackDecoder:
                        ClaimStream(ref _qpackDecoderStreamSeen, "QPACK decoder");
                        await reader.DrainAsync(MaxAuxiliaryStreamBytes, ct).ConfigureAwait(false);
                        break;

                    case Http3StreamType.Push:
                        throw new Http3ProtocolException(Http3ErrorCode.IdError, "The origin opened a push stream, which was never enabled.");

                    default:
                        // An unknown type is to be ignored (§6.2.3); refusing to read it is the
                        // cheapest way, and says so to the origin.
                        stream.Abort(QuicAbortDirection.Read, (long)Http3ErrorCode.StreamCreationError);
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuicException or ObjectDisposedException)
        {
            // Cancelled with the connection, or the origin reset a stream we only drain. The
            // control stream is the exception, and ReadControlStreamAsync reports its own end.
        }
        catch (Http3ProtocolException ex)
        {
            await AbortAsync(ex).ConfigureAwait(false);
        }
        catch (HttpParseException ex)
        {
            await AbortAsync(new Http3ProtocolException(Http3ErrorCode.FrameError, ex.Message)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Nothing else is expected from reading a stream. If it happens anyway the connection is
            // given up on, so the request fails loudly instead of this task failing unobserved.
            await AbortAsync(new Http3ProtocolException(Http3ErrorCode.InternalError,
                $"Unexpected {ex.GetType().Name} on an inbound stream.")).ConfigureAwait(false);
        }
    }

    private void ClaimStream(ref bool seen, string kind)
    {
        lock (_gate)
        {
            if (!seen)
            {
                seen = true;
                return;
            }
        }
        throw new Http3ProtocolException(Http3ErrorCode.StreamCreationError, $"The origin opened a second {kind} stream.");
    }

    /// <summary>The origin's control stream (RFC 9114 §6.2.1, §7.2): SETTINGS first and once, GOAWAY
    /// thereafter, and it stays open as long as the connection does.</summary>
    private async Task ReadControlStreamAsync(Http3StreamReader reader, CancellationToken ct)
    {
        var first = true;
        var ignored = 0;
        var goAways = 0;

        while (true)
        {
            var header = await reader.ReadFrameHeaderAsync(ct).ConfigureAwait(false);
            if (header is null)
                throw new Http3ProtocolException(Http3ErrorCode.ClosedCriticalStream, "The origin closed its control stream.");
            var (type, length) = header.Value;

            if (first && type != (long)Http3FrameType.Settings)
                throw new Http3ProtocolException(Http3ErrorCode.MissingSettings, "The first frame on the control stream is not SETTINGS.");

            switch (type)
            {
                case (long)Http3FrameType.Settings:
                    if (!first)
                        throw new Http3ProtocolException(Http3ErrorCode.FrameUnexpected, "A second SETTINGS frame.");
                    var settings = Http3FrameWriter.ParseSettings(await ReadControlPayloadAsync(reader, length, ct).ConfigureAwait(false));
                    lock (_gate) _peerSettings = settings;
                    break;

                case (long)Http3FrameType.GoAway:
                    // The id may only fall, and it can fall only so many times: each is a lock and a
                    // wake-up for nothing.
                    if (++goAways > MaxGoAwayFrames)
                        throw new Http3ProtocolException(Http3ErrorCode.ExcessiveLoad, "Too many GOAWAY frames.");
                    var payload = await ReadControlPayloadAsync(reader, length, ct).ConfigureAwait(false);
                    var position = 0;
                    long id;
                    try { id = VarInt.Read(payload, ref position); }
                    catch (HttpParseException ex) { throw new Http3ProtocolException(Http3ErrorCode.FrameError, $"Malformed GOAWAY: {ex.Message}"); }
                    if (position != payload.Length)
                        throw new Http3ProtocolException(Http3ErrorCode.FrameError, "GOAWAY carries more than a stream id.");
                    OnGoAway(id);
                    break;

                case (long)Http3FrameType.Data:
                case (long)Http3FrameType.Headers:
                case (long)Http3FrameType.PushPromise:
                case (long)Http3FrameType.MaxPushId:
                    throw new Http3ProtocolException(Http3ErrorCode.FrameUnexpected, $"Frame type 0x{type:x} on the control stream.");

                case (long)Http3FrameType.CancelPush:
                    throw new Http3ProtocolException(Http3ErrorCode.IdError, "CANCEL_PUSH for a push that was never enabled.");

                default:
                    if (Http3FrameTypes.IsReservedFromHttp2(type))
                        throw new Http3ProtocolException(Http3ErrorCode.FrameUnexpected, $"Frame type 0x{type:x} is reserved from HTTP/2.");
                    if (++ignored > MaxIgnoredFrames || length > MaxIgnoredFramePayload)
                        throw new Http3ProtocolException(Http3ErrorCode.ExcessiveLoad, "Too much ignorable data on the control stream.");
                    await reader.SkipPayloadAsync(length, ct).ConfigureAwait(false);
                    break;
            }

            first = false;
        }
    }

    private static async Task<byte[]> ReadControlPayloadAsync(Http3StreamReader reader, long length, CancellationToken ct)
    {
        if (length > MaxControlFramePayload)
            throw new Http3ProtocolException(Http3ErrorCode.FrameError, $"A control frame of {length} bytes is longer than the {MaxControlFramePayload} allowed.");
        return await reader.ReadPayloadAsync(length, MaxControlFramePayload, ct).ConfigureAwait(false);
    }

    /// <summary>GOAWAY carries the first client-initiated bidirectional stream the origin did NOT
    /// and will not process (§5.2). Our one request is stream 0 unless something else was opened.</summary>
    private void OnGoAway(long id)
    {
        Http3GoAwayException? refused = null;
        lock (_gate)
        {
            if (id % 4 != 0)
                throw new Http3ProtocolException(Http3ErrorCode.IdError, $"GOAWAY({id}) is not a client-initiated bidirectional stream.");
            if (_goAwayId >= 0 && id > _goAwayId)
                throw new Http3ProtocolException(Http3ErrorCode.IdError, $"GOAWAY({id}) raises the limit set by GOAWAY({_goAwayId}).");

            _goAwayId = id;
            if (_requestStreamId >= 0 && _requestStreamId >= id) refused = NotProcessed(id);
        }

        if (refused is not null) SignalAbort(refused);
    }

    // ------------------------------------------------------------------ failure and teardown

    private void SignalAbort(Exception reason)
    {
        lock (_gate) _abortReason ??= reason;
        try { _abort.Cancel(); }
        catch (ObjectDisposedException) { /* disposed: nothing is waiting on it any more */ }
    }

    private async Task AbortAsync(Http3ProtocolException reason)
    {
        SignalAbort(reason);
        await CloseQuietlyAsync(reason.ErrorCode).ConfigureAwait(false);
    }

    private async Task CloseQuietlyAsync(Http3ErrorCode code)
    {
        using var grace = new CancellationTokenSource(CloseGrace);
        try
        {
            await _connection.CloseAsync((long)code, grace.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or QuicException or ObjectDisposedException or InvalidOperationException)
        {
            // Already closing, or the origin is gone: the error has been recorded, and that is
            // what the caller reads.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        if (_controlStream is not null) await _controlStream.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);

        // Closing the connection ends the inbound loop; give it a moment, and abandon it rather than
        // wait on an origin that will not let go.
        // Waiting with WhenAny never rethrows the task's own fault: teardown must not replace the
        // failure the caller is about to see. The handlers already turn what can go wrong into the
        // connection's abort reason; a fault that still escapes them is a teardown detail, only observed.
        if (_inbound is not null)
        {
            using var grace = new CancellationTokenSource();
            var finished = await Task.WhenAny(_inbound, Task.Delay(CloseGrace, grace.Token)).ConfigureAwait(false);
            await grace.CancelAsync().ConfigureAwait(false);
            if (finished == _inbound && _inbound.IsFaulted) _ = _inbound.Exception; // observed, so it is not reported later
        }

        // Safe even for an abandoned task: both sources are already cancelled or only ever cancelled,
        // and SignalAbort tolerates a disposed one.
        _lifetime.Dispose();
        _abort.Dispose();
    }
}
