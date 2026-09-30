using System.Buffers;
using System.Diagnostics;
using Piper.Core.Http;
using Piper.Core.Http2.Hpack;

namespace Piper.Core.Http2;

/// <summary>
/// Client-role HTTP/2 connection (origin-facing). Phase 1 scope: one request per connection
/// instance -- each downstream stream opens its own fresh upstream connection (mirroring the
/// existing Composer/<c>RequestExecutor</c> pattern), so unlike the server role there is no
/// concurrent multiplexing, no outbox <c>Channel</c>, and no background writer. Sending and
/// receiving still interleave on this single stream ID, because a request body larger than the
/// peer's initial flow-control window can only keep going once a WINDOW_UPDATE arrives -- which
/// means reading frames while still sending is required for correctness, not just nice-to-have.
/// </summary>
/// <remarks>
/// The origin is hostile input, so this role enforces what <see cref="Http2Connection"/> enforces on
/// a client: a cap on a header block and on the list it expands to, contiguous CONTINUATION frames,
/// a valid flow-control window, and a bounded wait. A connection error is reported to the origin
/// with GOAWAY and to the caller as <see cref="Http2ProtocolException"/>.
/// </remarks>
/// <param name="stream">The connection to the origin, already past TLS/ALPN.</param>
/// <param name="idleTimeout">
/// How long the origin may go without making progress (response HEADERS, non-empty DATA, or
/// granting window an upload was waiting for) before the exchange fails. Null or infinite waits
/// for ever, as this class did before it had one.
/// </param>
public sealed class Http2ClientConnection(Stream stream, TimeSpan? idleTimeout = null)
{
    private static readonly byte[] PrefaceBytes = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();

    // Client-initiated stream IDs are odd (RFC 9113 §5.1.1); only one is ever opened here.
    private const int StreamId = 1;

    // RFC 9113 §6.9.1: a flow-control window can never exceed 2^31-1.
    private const long MaxWindow = int.MaxValue;

    // Interim (1xx) responses an origin may send before the final one. A handful is real (103 Early
    // Hints); an endless run only keeps the request open.
    private const int MaxInterimResponses = 32;

    private readonly Http2Settings _localSettings = Http2Settings.Advertised();
    private readonly Http2Settings _peerSettings = new();
    private readonly HpackDecoder _hpackDecoder = new(Http2Settings.Advertised().HeaderTableSize);

    // What one header block may take on the wire and expand to: the limit this side advertised,
    // applied exactly as the server role applies it to a client.
    private int MaxHeaderBlockSize => _localSettings.MaxHeaderListSize ?? 65_536;

    private readonly TimeSpan? _idleTimeout =
        idleTimeout is { } t && t > TimeSpan.Zero && t != Timeout.InfiniteTimeSpan ? t : null;

    // When the origin last did something that counts (see MarkProgress).
    private long _lastProgress = Stopwatch.GetTimestamp();

    private long _peerConnectionWindow = 65_535;
    private long _peerStreamWindow = 65_535;

    /// <summary>
    /// Bytes received but not yet acknowledged back to the origin with a WINDOW_UPDATE, tracked
    /// separately for the connection and for our one stream.
    /// </summary>
    /// <remarks>
    /// RFC 9113 §6.9.2: SETTINGS_INITIAL_WINDOW_SIZE sizes only *stream* windows. The
    /// connection-level flow-control window always starts at 65,535 and can be raised solely by
    /// WINDOW_UPDATE, no matter what a peer advertises in SETTINGS. So a receiver that never
    /// sends WINDOW_UPDATE silently stalls every response larger than 64 KB: the origin sends
    /// exactly 65,535 bytes, then waits forever for credit that never arrives. Small pages work,
    /// real ones hang -- which is precisely how this presented.
    /// </remarks>
    private long _connectionBytesToAck;
    private long _streamBytesToAck;

    /// <summary>Acknowledge once roughly half the initial 65,535-byte connection window is
    /// consumed, so credit is replenished well before the sender runs out.</summary>
    private const int WindowUpdateThreshold = 32 * 1024;

    // The header block being assembled, from a HEADERS frame to the CONTINUATION that ends it.
    private readonly ArrayBufferWriter<byte> _headerBlockFragment = new();
    private bool _inHeaderBlock;
    private int _interimResponses;
    private int? _goAwayLastStreamId;
    private List<(string Name, string Value)>? _responseFields;
    private bool _sawEndStreamOnHeaders;
    private bool _responseComplete;

    // The payload of the last DATA frame read, not yet handed to the body's reader. Frames are read
    // only when the reader asks for more, so this never holds more than one.
    private ReadOnlyMemory<byte> _pendingData;

    /// <summary>
    /// Sends the request and returns once the response head has arrived, leaving the body to be
    /// read from <see cref="ResponseBody"/> as the origin sends it.
    /// </summary>
    public async Task<HttpResponseData> SendRequestHeadAsync(HttpRequestData request, CancellationToken ct)
    {
        MarkProgress();
        await stream.WriteAsync(PrefaceBytes, ct).ConfigureAwait(false);
        await Http2FrameWriter.WriteAsync(stream, Http2FrameType.Settings, Http2FrameFlags.None, 0, _localSettings.ToPayload(), ct).ConfigureAwait(false);

        var fields = Http2MessageAdapter.ToHeaderFields(request);
        var block = HpackEncoder.Encode(fields);
        var hasBody = request.Body.Length > 0;

        // The peer's real MAX_FRAME_SIZE isn't known yet (their SETTINGS hasn't necessarily
        // arrived) -- the RFC default our Http2Settings starts with is always safe to assume.
        await Http2FrameWriter.WriteHeadersAsync(stream, StreamId, block, endStream: !hasBody, _peerSettings.MaxFrameSize, ct).ConfigureAwait(false);

        if (hasBody && !_responseComplete)
            await SendBodyAsync(request.Body, ct).ConfigureAwait(false);

        while (_responseFields is null)
        {
            if (_responseComplete) throw new HttpParseException("HTTP/2 response ended before its headers completed.");
            await ReadAndProcessFrameAsync(ct).ConfigureAwait(false);
        }

        return Http2MessageAdapter.ToResponse(_responseFields);
    }

    /// <summary>
    /// The response body, read frame by frame on demand, ending at END_STREAM. Only one DATA frame
    /// is read ahead of the reader, so an origin sending faster than the body is consumed is held
    /// back by TCP rather than buffered here.
    /// </summary>
    public Stream ResponseBody => new BodyStream(this);

    private async ValueTask<int> ReadBodyAsync(Memory<byte> destination, CancellationToken ct)
    {
        // The wait for the origin starts now. Time the caller spent not reading is not the origin's
        // idleness -- a slow consumer is what holds the origin back, through flow control.
        if (_pendingData.IsEmpty) MarkProgress();

        while (_pendingData.IsEmpty)
        {
            if (_responseComplete) return 0;
            await ReadAndProcessFrameAsync(ct).ConfigureAwait(false);
        }

        var take = Math.Min(destination.Length, _pendingData.Length);
        _pendingData[..take].CopyTo(destination);
        _pendingData = _pendingData[take..];
        return take;
    }

    private sealed class BodyStream(Http2ClientConnection connection) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            connection.ReadBodyAsync(buffer, ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            connection.ReadBodyAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Sends the request body respecting the peer's flow-control window, reading and
    /// processing incoming frames whenever the window is exhausted (which is also how a
    /// same-stream early response -- e.g. a 4xx rejecting the upload outright -- gets noticed
    /// and short-circuits the rest of the send).</summary>
    private async Task SendBodyAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        // Stops as well once a response DATA frame is waiting to be read: taking another frame
        // would overwrite it. The origin has already answered, so the rest of the upload is moot.
        while (offset < body.Length && !_responseComplete && _pendingData.IsEmpty)
        {
            var available = (int)Math.Max(0, Math.Min(
                Math.Min(_peerStreamWindow, _peerConnectionWindow),
                Math.Min(body.Length - offset, _peerSettings.MaxFrameSize)));

            if (available <= 0)
            {
                await ReadAndProcessFrameAsync(ct).ConfigureAwait(false);
                continue;
            }

            _peerStreamWindow -= available;
            _peerConnectionWindow -= available;
            var isLast = offset + available >= body.Length;
            await Http2FrameWriter.WriteAsync(stream, Http2FrameType.Data,
                isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None, StreamId,
                body.AsMemory(offset, available), ct).ConfigureAwait(false);
            offset += available;
            MarkProgress(); // the origin granted this window, so it is listening
        }
    }

    private async Task ReadAndProcessFrameAsync(CancellationToken ct)
    {
        try
        {
            await ProcessNextFrameAsync(ct).ConfigureAwait(false);
        }
        catch (Http2ProtocolException ex)
        {
            await TrySendGoAwayAsync(ex.ErrorCode).ConfigureAwait(false);
            throw;
        }
    }

    private async Task ProcessNextFrameAsync(CancellationToken ct)
    {
        var frame = await ReadFrameAsync(ct).ConfigureAwait(false);

        // RFC 9113 §4.3: a header block is one uninterrupted run of HEADERS then CONTINUATION frames
        // on one stream. The HPACK table is shared, so a block that is skipped or split desyncs it.
        if (_inHeaderBlock && (frame.Type != Http2FrameType.Continuation || frame.StreamId != StreamId))
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "Header block interrupted before END_HEADERS.");

        switch (frame.Type)
        {
            case Http2FrameType.Settings:
                if (!frame.HasFlag(Http2FrameFlags.Ack))
                {
                    if (frame.Payload.Length > 0) ApplyPeerSettings(frame.Payload.Span);
                    await Http2FrameWriter.WriteAsync(stream, Http2FrameType.Settings, Http2FrameFlags.Ack, 0, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
                }
                break;

            case Http2FrameType.Ping:
                if (!frame.HasFlag(Http2FrameFlags.Ack))
                    await Http2FrameWriter.WriteAsync(stream, Http2FrameType.Ping, Http2FrameFlags.Ack, 0, frame.Payload.ToArray(), ct).ConfigureAwait(false);
                break;

            case Http2FrameType.WindowUpdate:
                ApplyWindowUpdate(frame);
                break;

            case Http2FrameType.RstStream:
                if (frame.StreamId == StreamId)
                    throw new IOException("Origin reset the HTTP/2 stream before completing the response.");
                break;

            case Http2FrameType.GoAway:
                HandleGoAway(frame);
                break;

            case Http2FrameType.Headers:
            case Http2FrameType.Continuation:
                HandleResponseHeaders(frame);
                break;

            case Http2FrameType.PushPromise:
                // Push is disabled in our SETTINGS (RFC 9113 §8.4). Its header block would also have
                // to be decoded to keep the HPACK table in step, so it cannot be skipped either.
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "PUSH_PROMISE although push is disabled.");

            case Http2FrameType.Data:
                if (frame.StreamId == StreamId) HandleResponseData(frame);
                // Credit is returned for every DATA frame, including ones on streams we are not
                // tracking: the connection-level window is consumed regardless of which stream
                // the bytes belonged to, so skipping those would leak the connection window.
                await AcknowledgeDataAsync(frame, ct).ConfigureAwait(false);
                break;

            default:
                break; // PRIORITY and unknown frame types: ignore
        }
    }

    /// <summary>Reads the next frame, failing if the origin has made no progress for the idle
    /// timeout. Only real progress re-arms it (see <see cref="MarkProgress"/>): an origin that keeps
    /// the connection busy with PINGs or empty DATA frames but never answers is as stalled as a
    /// silent one, and would otherwise hold the request open for ever.</summary>
    private async Task<Http2Frame> ReadFrameAsync(CancellationToken ct)
    {
        if (_idleTimeout is not { } idle)
            return await Http2FrameReader.ReadRequiredAsync(stream, _localSettings.MaxFrameSize, ct).ConfigureAwait(false);

        var remaining = idle - Stopwatch.GetElapsedTime(_lastProgress);
        if (remaining <= TimeSpan.Zero) throw Stalled(idle);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(remaining);
        try
        {
            return await Http2FrameReader.ReadRequiredAsync(stream, _localSettings.MaxFrameSize, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Stalled(idle);
        }
    }

    // Reported as a parse failure, as HttpStreamReader reports its idle timeout, so it reaches the
    // caller as a named reason rather than a cancellation nobody asked for.
    private static HttpParseException Stalled(TimeSpan idle) =>
        new($"No HTTP/2 response data received for {idle.TotalSeconds:0.#}s; treating the connection as stalled.");

    /// <summary>Re-arms the idle timeout. Called for response header blocks, DATA that carries
    /// bytes, and each request DATA frame the origin's window allowed; not for control frames.</summary>
    private void MarkProgress() => _lastProgress = Stopwatch.GetTimestamp();

    /// <summary>Tells the origin why the connection is being abandoned (RFC 9113 §5.4.1: an endpoint
    /// SHOULD send GOAWAY before closing on a connection error). Best effort and bounded: the
    /// caller rethrows the original error, and the connection is dropped right after, so a write
    /// that fails or stalls leaves nothing to recover.</summary>
    private async Task TrySendGoAwayAsync(Http2ErrorCode code)
    {
        var payload = new byte[8]; // last-stream-id 0: no stream of the origin's was accepted
        payload[4] = (byte)((uint)code >> 24);
        payload[5] = (byte)((uint)code >> 16);
        payload[6] = (byte)((uint)code >> 8);
        payload[7] = (byte)(uint)code;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await Http2FrameWriter.WriteAsync(stream, Http2FrameType.GoAway, Http2FrameFlags.None, 0, payload, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The original protocol error is what the caller rethrows.
        }
    }

    /// <summary>Applies a peer SETTINGS frame. A changed SETTINGS_INITIAL_WINDOW_SIZE moves the open
    /// stream's window by the difference, which can leave it negative (RFC 9113 §6.9.2), and is a
    /// FLOW_CONTROL_ERROR if it pushes the window past 2^31-1. The server role does the same for
    /// each of its streams.</summary>
    private void ApplyPeerSettings(ReadOnlySpan<byte> payload)
    {
        var initialWindowBefore = _peerSettings.InitialWindowSize;
        _peerSettings.ApplyPeerPayload(payload);

        _peerStreamWindow += (long)_peerSettings.InitialWindowSize - initialWindowBefore;
        if (_peerStreamWindow > MaxWindow)
            throw new Http2ProtocolException(Http2ErrorCode.FlowControlError, "SETTINGS_INITIAL_WINDOW_SIZE pushes the stream window past 2^31-1.");
    }

    /// <summary>RFC 9113 §6.8. A last-stream-id below our stream means the origin never began the
    /// request. At or above it the response is still coming, so the connection is drained: frames
    /// keep being read until the stream ends (and an origin that then vanishes is an error, not a
    /// short success).</summary>
    private void HandleGoAway(Http2Frame frame)
    {
        if (frame.StreamId != 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "GOAWAY on a non-zero stream.");
        if (frame.Payload.Length < 8)
            throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError, "GOAWAY payload must be at least 8 bytes.");

        var span = frame.Payload.Span;
        var lastStreamId = ((span[0] & 0x7f) << 24) | (span[1] << 16) | (span[2] << 8) | span[3];
        var code = (Http2ErrorCode)(((uint)span[4] << 24) | ((uint)span[5] << 16) | ((uint)span[6] << 8) | span[7]);

        // RFC 9113 §6.8: an endpoint MUST NOT raise the last-stream-id it has already sent.
        if (_goAwayLastStreamId is { } previous && lastStreamId > previous)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "GOAWAY raised its last-stream-id.");
        _goAwayLastStreamId = lastStreamId;

        if (lastStreamId < StreamId)
            throw new Http2GoAwayException(code, lastStreamId, $"Origin sent GOAWAY ({code}) before it began processing the request.");
    }

    private void ApplyWindowUpdate(Http2Frame frame)
    {
        if (frame.Payload.Length != 4)
            throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError, "WINDOW_UPDATE payload must be 4 bytes.");

        var span = frame.Payload.Span;
        var increment = ((span[0] & 0x7f) << 24) | (span[1] << 16) | (span[2] << 8) | span[3];
        if (increment == 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "WINDOW_UPDATE increment of 0.");

        if (frame.StreamId == 0)
        {
            _peerConnectionWindow += increment;
            if (_peerConnectionWindow > MaxWindow)
                throw new Http2ProtocolException(Http2ErrorCode.FlowControlError, "WINDOW_UPDATE pushes the connection window past 2^31-1.");
        }
        else if (frame.StreamId == StreamId)
        {
            _peerStreamWindow += increment;
            if (_peerStreamWindow > MaxWindow)
                throw new Http2ProtocolException(Http2ErrorCode.FlowControlError, "WINDOW_UPDATE pushes the stream window past 2^31-1.");
        }
    }

    /// <summary>Returns flow-control credit for received DATA so the origin can keep sending.
    /// Credit goes back as each frame is read, and a frame is read only when the body's reader
    /// wants more, so how fast the body is consumed is what paces the origin.</summary>
    private async Task AcknowledgeDataAsync(Http2Frame frame, CancellationToken ct)
    {
        var length = frame.Payload.Length;
        if (length == 0) return;

        _connectionBytesToAck += length;
        if (_connectionBytesToAck >= WindowUpdateThreshold)
        {
            await WriteWindowUpdateAsync(0, _connectionBytesToAck, ct).ConfigureAwait(false);
            _connectionBytesToAck = 0;
        }

        if (frame.StreamId != StreamId) return;

        _streamBytesToAck += length;
        if (_streamBytesToAck >= WindowUpdateThreshold)
        {
            await WriteWindowUpdateAsync(StreamId, _streamBytesToAck, ct).ConfigureAwait(false);
            _streamBytesToAck = 0;
        }
    }

    private Task WriteWindowUpdateAsync(int streamId, long increment, CancellationToken ct)
    {
        var payload = new byte[4];
        var value = (uint)increment;
        payload[0] = (byte)((value >> 24) & 0x7f);
        payload[1] = (byte)(value >> 16);
        payload[2] = (byte)(value >> 8);
        payload[3] = (byte)value;
        return Http2FrameWriter.WriteAsync(stream, Http2FrameType.WindowUpdate, Http2FrameFlags.None, streamId, payload, ct);
    }

    private void HandleResponseHeaders(Http2Frame frame)
    {
        if (frame.Type == Http2FrameType.Headers)
        {
            // Only stream 1 is ever opened, so a header block for any other stream is not ours to
            // ignore: it would still have to be decoded to keep the shared HPACK table in step.
            if (frame.StreamId != StreamId)
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, $"HEADERS on stream {frame.StreamId}, but only stream {StreamId} is open.");
            _inHeaderBlock = true;
            if (frame.HasFlag(Http2FrameFlags.EndStream)) _sawEndStreamOnHeaders = true;
        }
        else if (!_inHeaderBlock)
        {
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "CONTINUATION without a preceding HEADERS.");
        }

        // Checked before the fragment is taken, so the buffer never holds more than the cap.
        var fragment = frame.HeaderBlockPayload;
        if ((long)_headerBlockFragment.WrittenCount + fragment.Length > MaxHeaderBlockSize)
            throw new Http2ProtocolException(Http2ErrorCode.EnhanceYourCalm, "Header block exceeds the advertised header list size.");
        _headerBlockFragment.Write(fragment.Span);
        MarkProgress();

        if (!frame.HasFlag(Http2FrameFlags.EndHeaders)) return;

        List<(string Name, string Value)> decoded;
        try
        {
            decoded = _hpackDecoder.Decode(_headerBlockFragment.WrittenSpan);
        }
        catch (HttpParseException ex)
        {
            // RFC 9113 §4.3: an HPACK decoding error is always a connection error.
            throw new Http2ProtocolException(Http2ErrorCode.CompressionError, $"HPACK decoding failed: {ex.Message}");
        }
        finally
        {
            _headerBlockFragment.ResetWrittenCount();
            _inHeaderBlock = false;
        }

        // The decoded list is capped too: one-byte references to a large dynamic-table entry turn a
        // small block into a huge one. Decode itself stays cheap (indexed fields share the table's
        // strings) and is bounded by the block cap above; this stops the list being materialised.
        // Sized as RFC 9113 §6.5.2 counts it: name + value + 32 per field. HpackDecoder builds each
        // string as Latin-1, so a char is exactly one wire octet.
        long listSize = 0;
        foreach (var (name, value) in decoded) listSize += name.Length + value.Length + 32;
        if (listSize > MaxHeaderBlockSize)
            throw new Http2ProtocolException(Http2ErrorCode.EnhanceYourCalm, "Response header list exceeds the advertised header list size.");

        // A second header block after the response head is trailers. They are dropped, as the
        // HTTP/1.1 relay drops chunked trailers, rather than being mistaken for the head.
        if (_responseFields is not null)
        {
            if (_sawEndStreamOnHeaders) _responseComplete = true;
            return;
        }

        var statusText = decoded.FirstOrDefault(f => f.Name == ":status").Value;
        if (int.TryParse(statusText, out var status) && status is >= 100 and < 200)
        {
            if (++_interimResponses > MaxInterimResponses)
                throw new Http2ProtocolException(Http2ErrorCode.EnhanceYourCalm, "Too many interim responses before the final one.");
            _sawEndStreamOnHeaders = false; // interim response (RFC 9113 §8.3.2): discard, keep waiting for the real one
            return;
        }

        _responseFields = decoded;
        if (_sawEndStreamOnHeaders) _responseComplete = true;
    }

    private void HandleResponseData(Http2Frame frame)
    {
        if (_responseFields is null)
            throw new HttpParseException("HTTP/2 DATA arrived before the response headers.");

        // Each frame's payload is its own freshly allocated array, so it can be held as it is.
        _pendingData = frame.DataPayload;
        if (!_pendingData.IsEmpty) MarkProgress(); // an empty frame costs the origin nothing, so it proves nothing
        if (frame.HasFlag(Http2FrameFlags.EndStream)) _responseComplete = true;
    }
}

/// <summary>
/// The origin sent GOAWAY naming a last-stream-id below the request's stream, so it never began
/// processing the request (RFC 9113 §6.8). Replaying the request on a fresh connection is safe;
/// this is what a connection pool needs in order to retry.
/// </summary>
public sealed class Http2GoAwayException(Http2ErrorCode errorCode, int lastStreamId, string message) : IOException(message)
{
    public Http2ErrorCode ErrorCode { get; } = errorCode;

    public int LastStreamId { get; } = lastStreamId;
}
