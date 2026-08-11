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
public sealed class Http2ClientConnection(Stream stream)
{
    private static readonly byte[] PrefaceBytes = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();

    // Client-initiated stream IDs are odd (RFC 9113 §5.1.1); only one is ever opened here.
    private const int StreamId = 1;

    private readonly Http2Settings _localSettings = Http2Settings.Advertised();
    private readonly Http2Settings _peerSettings = new();
    private readonly HpackDecoder _hpackDecoder = new(Http2Settings.Advertised().HeaderTableSize);

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

    private readonly List<byte> _headerBlockFragment = [];
    private readonly MemoryStream _responseBody = new();
    private List<(string Name, string Value)>? _responseFields;
    private bool _sawEndStreamOnHeaders;
    private bool _responseComplete;

    public async Task<HttpResponseData> SendRequestAsync(HttpRequestData request, CancellationToken ct)
    {
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

        while (!_responseComplete)
            await ReadAndProcessFrameAsync(ct).ConfigureAwait(false);

        return BuildResponse();
    }

    /// <summary>Sends the request body respecting the peer's flow-control window, reading and
    /// processing incoming frames whenever the window is exhausted (which is also how a
    /// same-stream early response -- e.g. a 4xx rejecting the upload outright -- gets noticed
    /// and short-circuits the rest of the send).</summary>
    private async Task SendBodyAsync(byte[] body, CancellationToken ct)
    {
        var offset = 0;
        while (offset < body.Length && !_responseComplete)
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
        }
    }

    private async Task ReadAndProcessFrameAsync(CancellationToken ct)
    {
        var frame = await Http2FrameReader.ReadRequiredAsync(stream, _localSettings.MaxFrameSize, ct).ConfigureAwait(false);

        switch (frame.Type)
        {
            case Http2FrameType.Settings:
                if (!frame.HasFlag(Http2FrameFlags.Ack))
                {
                    if (frame.Payload.Length > 0) _peerSettings.ApplyPeerPayload(frame.Payload.Span);
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
                throw new IOException("Origin sent GOAWAY before completing the response.");

            case Http2FrameType.Headers:
            case Http2FrameType.Continuation:
                if (frame.StreamId == StreamId) HandleResponseHeaders(frame);
                break;

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

    private void ApplyWindowUpdate(Http2Frame frame)
    {
        if (frame.Payload.Length != 4)
            throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError, "WINDOW_UPDATE payload must be 4 bytes.");

        var span = frame.Payload.Span;
        var increment = ((span[0] & 0x7f) << 24) | (span[1] << 16) | (span[2] << 8) | span[3];
        if (frame.StreamId == 0) _peerConnectionWindow += increment;
        else if (frame.StreamId == StreamId) _peerStreamWindow += increment;
    }

    /// <summary>Returns flow-control credit for received DATA so the origin can keep sending.
    /// Piper buffers the whole body in memory anyway, so there is nothing to gain by withholding
    /// credit -- the only job here is to never let the peer run out.</summary>
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
        _headerBlockFragment.AddRange(frame.HeaderBlockPayload.ToArray());
        if (frame.HasFlag(Http2FrameFlags.EndStream)) _sawEndStreamOnHeaders = true;

        if (!frame.HasFlag(Http2FrameFlags.EndHeaders)) return;

        var decoded = _hpackDecoder.Decode(_headerBlockFragment.ToArray());
        _headerBlockFragment.Clear();

        var statusText = decoded.FirstOrDefault(f => f.Name == ":status").Value;
        if (int.TryParse(statusText, out var status) && status is >= 100 and < 200)
        {
            _sawEndStreamOnHeaders = false; // interim response (RFC 9113 §8.3.2): discard, keep waiting for the real one
            return;
        }

        _responseFields = decoded;
        if (_sawEndStreamOnHeaders) _responseComplete = true;
    }

    private void HandleResponseData(Http2Frame frame)
    {
        _responseBody.Write(frame.DataPayload.Span);
        if (frame.HasFlag(Http2FrameFlags.EndStream)) _responseComplete = true;
    }

    private HttpResponseData BuildResponse()
    {
        if (_responseFields is null) throw new HttpParseException("HTTP/2 response ended before its headers completed.");
        var response = Http2MessageAdapter.ToResponse(_responseFields);
        response.Body = _responseBody.ToArray();
        return response;
    }
}
