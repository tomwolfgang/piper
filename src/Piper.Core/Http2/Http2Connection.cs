using System.Collections.Concurrent;
using System.Threading.Channels;
using Piper.Core.Http;
using Piper.Core.Http2.Hpack;

namespace Piper.Core.Http2;

/// <summary>
/// Server-role HTTP/2 connection (browser-facing). One task reads and demuxes frames off the
/// wire sequentially (required for HPACK, which is stateful and processed strictly in wire
/// order); each completed request is handed to <paramref name="handler"/> on its own tracked
/// task, running concurrently with other streams. Every stream-handler task writes its response
/// by enqueueing onto an outbox <see cref="Channel{T}"/> instead of touching the socket directly
/// -- a second task drains that queue and is the connection's sole writer, so concurrent streams
/// can never interleave bytes on the wire.
/// </summary>
public sealed class Http2Connection(Stream stream, Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
    : IAsyncDisposable
{
    private static readonly byte[] PrefaceBytes = "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();

    private readonly Channel<Func<CancellationToken, Task>> _outbox =
        Channel.CreateUnbounded<Func<CancellationToken, Task>>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<int, Http2Stream> _streams = new();
    private readonly List<Task> _inFlight = [];
    private readonly Lock _inFlightGate = new();

    private readonly Http2Settings _localSettings = Http2Settings.Advertised();
    private readonly Http2Settings _peerSettings = new();
    private readonly HpackDecoder _hpackDecoder = new(Http2Settings.Advertised().HeaderTableSize);

    private int _highestStreamId;

    // Guards _peerConnectionWindow. This budget is shared by every concurrent stream's sender
    // task, so "read the remaining window, decide how much to send, then subtract" is only safe
    // if the read-decide-subtract sequence is one atomic operation -- otherwise two streams can
    // each see the same not-yet-spent balance and, combined, send more than the peer actually
    // granted. A real browser enforces HTTP/2 flow control strictly and resets the connection
    // when it's violated, which is exactly what an unguarded Interlocked.Read + Interlocked.Add
    // pair (no atomicity *between* the two calls) allowed to happen under real concurrent traffic.
    private readonly Lock _connectionWindowGate = new();
    private long _peerConnectionWindow = 65_535; // RFC 9113 default until the peer says otherwise

    /// <summary>Received bytes not yet credited back to the peer with a WINDOW_UPDATE. RFC 9113
    /// §6.9.2: the connection-level window always starts at 65,535 and grows only via
    /// WINDOW_UPDATE -- SETTINGS_INITIAL_WINDOW_SIZE sizes stream windows only. Tracking what we
    /// have consumed (rather than guessing the peer's remaining balance from our own advertised
    /// settings) keeps the two sides' accounting in step, so large request bodies cannot stall.</summary>
    private long _connectionBytesToAck;

    /// <summary>Credit back once about half the initial 65,535-byte connection window is used.</summary>
    private const int WindowUpdateThreshold = 32 * 1024;

    /// <summary>Atomically takes up to <paramref name="maxWanted"/> bytes from the shared
    /// connection-level send window, returning how much was actually reserved (0 if none is
    /// currently available). The caller must not send more than what this returns.</summary>
    private long TryReserveConnectionWindow(long maxWanted)
    {
        lock (_connectionWindowGate)
        {
            if (_peerConnectionWindow <= 0) return 0;
            var take = Math.Min(_peerConnectionWindow, maxWanted);
            _peerConnectionWindow -= take;
            return take;
        }
    }

    /// <summary>Runs the connection to completion: validates the preface, exchanges SETTINGS,
    /// then reads and dispatches frames until the peer closes, sends GOAWAY, or a fatal
    /// protocol error occurs. Awaits every in-flight stream handler before returning.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        await ValidatePrefaceAsync(ct).ConfigureAwait(false);

        var writerTask = WriterLoopAsync(ct);
        EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.Settings, Http2FrameFlags.None, 0, _localSettings.ToPayload(), ct2));

        try
        {
            await ReaderLoopAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _outbox.Writer.TryComplete();
            await writerTask.ConfigureAwait(false);

            Task[] pending;
            lock (_inFlightGate) pending = _inFlight.ToArray();

            // Bounded: a stream handler that wedges (a stalled upstream, a flow-control window
            // that never reopens) must not keep the whole connection -- and the socket behind it
            // -- alive indefinitely. Whatever has not finished by now is abandoned to the GC.
            try
            {
                var all = Task.WhenAll(pending);
                await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None)).ConfigureAwait(false);
            }
            catch { /* individual failures are already converted to 502s inside ProcessStreamAsync */ }
        }
    }

    // ------------------------------------------------------------------- handshake

    private async Task ValidatePrefaceAsync(CancellationToken ct)
    {
        var buffer = new byte[PrefaceBytes.Length];
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("Connection closed before the HTTP/2 preface was received.");
            offset += n;
        }
        if (!buffer.AsSpan().SequenceEqual(PrefaceBytes))
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "Invalid HTTP/2 connection preface.");
    }

    // --------------------------------------------------------------------- reader

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            Http2Frame? frame;
            try
            {
                frame = await Http2FrameReader.ReadAsync(stream, _localSettings.MaxFrameSize, ct).ConfigureAwait(false);
                if (frame is null) return; // peer closed cleanly
                DispatchFrame(frame.Value, ct);
            }
            catch (Http2ProtocolException ex)
            {
                EnqueueGoAway(ex.ErrorCode);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { return; } // includes EndOfStreamException
        }
    }

    private void DispatchFrame(Http2Frame frame, CancellationToken ct)
    {
        _highestStreamId = Math.Max(_highestStreamId, frame.StreamId);

        switch (frame.Type)
        {
            case Http2FrameType.Settings: HandleSettings(frame); break;
            case Http2FrameType.Headers: HandleHeaders(frame); break;
            case Http2FrameType.Continuation: HandleContinuation(frame); break;
            case Http2FrameType.Data: HandleData(frame); break;
            case Http2FrameType.WindowUpdate: HandleWindowUpdate(frame); break;
            case Http2FrameType.RstStream: HandleRstStream(frame); break;
            case Http2FrameType.Ping: HandlePing(frame); break;
            case Http2FrameType.GoAway: throw new OperationCanceledException("Peer sent GOAWAY.");
            case Http2FrameType.Priority: break; // parsed, discarded: no scheduling
            default: break; // unknown frame type: ignore, per RFC 9113 §4.1
        }
    }

    private void HandleSettings(Http2Frame frame)
    {
        if (frame.StreamId != 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "SETTINGS on a non-zero stream.");

        if (frame.HasFlag(Http2FrameFlags.Ack))
            return; // peer acknowledged our SETTINGS; phase 1 gates nothing on this

        if (frame.Payload.Length > 0)
            _peerSettings.ApplyPeerPayload(frame.Payload.Span);

        EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.Settings, Http2FrameFlags.Ack, 0, ReadOnlyMemory<byte>.Empty, ct2));
    }

    private void HandleHeaders(Http2Frame frame)
    {
        if (frame.StreamId == 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "HEADERS on stream 0.");

        var maxConcurrent = _localSettings.MaxConcurrentStreams ?? int.MaxValue;
        if (!_streams.ContainsKey(frame.StreamId) && _streams.Count >= maxConcurrent)
        {
            EnqueueRstStream(frame.StreamId, Http2ErrorCode.RefusedStream);
            return;
        }

        var http2Stream = new Http2Stream(frame.StreamId)
        {
            RemoteWindow = _peerSettings.InitialWindowSize,
        };
        _streams[frame.StreamId] = http2Stream;

        http2Stream.HeaderBlockFragment.AddRange(frame.HeaderBlockPayload.ToArray());
        http2Stream.EndStreamOnHeaders = frame.HasFlag(Http2FrameFlags.EndStream);

        if (frame.HasFlag(Http2FrameFlags.EndHeaders))
            CompleteHeaders(http2Stream);
    }

    private void HandleContinuation(Http2Frame frame)
    {
        if (!_streams.TryGetValue(frame.StreamId, out var http2Stream)) return; // stream already gone; ignore trailing frames

        http2Stream.HeaderBlockFragment.AddRange(frame.HeaderBlockPayload.ToArray());
        if (frame.HasFlag(Http2FrameFlags.EndHeaders))
            CompleteHeaders(http2Stream);
    }

    private void CompleteHeaders(Http2Stream http2Stream)
    {
        http2Stream.HeadersComplete = true;

        List<(string Name, string Value)> fields;
        try
        {
            fields = _hpackDecoder.Decode(http2Stream.HeaderBlockFragment.ToArray());
        }
        catch (HttpParseException ex)
        {
            // RFC 9113 §4.3: an HPACK decoding error is always a *connection* error -- the
            // decoder's dynamic table state is now unrecoverable for every other stream too.
            throw new Http2ProtocolException(Http2ErrorCode.CompressionError, $"HPACK decoding failed: {ex.Message}");
        }

        http2Stream.Request = Http2MessageAdapter.ToRequest(fields, isHttps: true);

        if (http2Stream.EndStreamOnHeaders)
            DispatchRequest(http2Stream);
    }

    private void HandleData(Http2Frame frame)
    {
        var length = frame.Payload.Length;

        // Connection-level credit is owed for every DATA frame, even one on a stream we already
        // dropped -- those bytes still came out of the shared connection window.
        if (length > 0)
        {
            _connectionBytesToAck += length;
            if (_connectionBytesToAck >= WindowUpdateThreshold)
            {
                EnqueueWindowUpdate(0, _connectionBytesToAck);
                _connectionBytesToAck = 0;
            }
        }

        if (!_streams.TryGetValue(frame.StreamId, out var http2Stream)) return; // reset/unknown stream: drop the payload

        if (length > 0)
        {
            http2Stream.BytesToAck += length;
            if (http2Stream.BytesToAck >= WindowUpdateThreshold)
            {
                EnqueueWindowUpdate(frame.StreamId, http2Stream.BytesToAck);
                http2Stream.BytesToAck = 0;
            }
        }

        http2Stream.Body.Write(frame.DataPayload.Span);

        if (frame.HasFlag(Http2FrameFlags.EndStream))
            DispatchRequest(http2Stream);
    }

    private void HandleWindowUpdate(Http2Frame frame)
    {
        if (frame.Payload.Length != 4)
            throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError, "WINDOW_UPDATE payload must be 4 bytes.");

        var span = frame.Payload.Span;
        var increment = ((span[0] & 0x7f) << 24) | (span[1] << 16) | (span[2] << 8) | span[3];
        if (increment == 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "WINDOW_UPDATE increment of 0.");

        if (frame.StreamId == 0)
        {
            lock (_connectionWindowGate) { _peerConnectionWindow += increment; }
        }
        else if (_streams.TryGetValue(frame.StreamId, out var http2Stream))
        {
            Interlocked.Add(ref http2Stream.RemoteWindow, increment);
        }
    }

    private void HandleRstStream(Http2Frame frame)
    {
        if (_streams.TryGetValue(frame.StreamId, out var http2Stream))
            http2Stream.Cancellation.Cancel();
    }

    private void HandlePing(Http2Frame frame)
    {
        if (frame.StreamId != 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "PING on a non-zero stream.");
        if (frame.Payload.Length != 8)
            throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError, "PING payload must be 8 bytes.");
        if (frame.HasFlag(Http2FrameFlags.Ack))
            return; // reply to a PING we never sent in phase 1; ignore defensively

        var payload = frame.Payload.ToArray();
        EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.Ping, Http2FrameFlags.Ack, 0, payload, ct2));
    }

    // -------------------------------------------------------------- stream dispatch

    private void DispatchRequest(Http2Stream http2Stream)
    {
        if (http2Stream.Request is null) return; // END_STREAM arrived before headers ever completed; malformed, drop
        http2Stream.Request.Body = http2Stream.Body.ToArray();

        var task = Task.Run(() => ProcessStreamAsync(http2Stream));
        lock (_inFlightGate) _inFlight.Add(task);
    }

    private async Task ProcessStreamAsync(Http2Stream http2Stream)
    {
        try
        {
            HttpResponseData response;
            try
            {
                response = await handler(http2Stream.Request!, http2Stream.Cancellation.Token).ConfigureAwait(false);
            }
            catch (Http2StreamAbortException abort)
            {
                // A rule killed this stream on purpose. RST_STREAM says so without disturbing the
                // other streams sharing the connection.
                EnqueueRstStream(http2Stream.Id, abort.ErrorCode);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                response = HttpResponseData.Simple(502, "Bad Gateway",
                    $"Piper could not complete this HTTP/2 request.\r\n\r\n{ex.Message}");
            }

            await SendResponseAsync(http2Stream, response, http2Stream.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stream was reset by the peer, or the connection is tearing down -- nothing to send.
        }
        finally
        {
            _streams.TryRemove(http2Stream.Id, out _);
        }
    }

    private async Task SendResponseAsync(Http2Stream http2Stream, HttpResponseData response, CancellationToken ct)
    {
        var fields = Http2MessageAdapter.ToHeaderFields(response);
        var block = HpackEncoder.Encode(fields); // stateless encoder: safe to call from any task
        var hasBody = response.Body.Length > 0;
        var streamId = http2Stream.Id;

        EnqueueWrite(ct2 => Http2FrameWriter.WriteHeadersAsync(stream, streamId, block, endStream: !hasBody, _peerSettings.MaxFrameSize, ct2));

        if (hasBody)
            await SendBodyRespectingFlowControlAsync(http2Stream, response.Body, ct).ConfigureAwait(false);
    }

    /// <summary>Sends a response body as one or more DATA frames, never sending more than the
    /// peer's currently-granted connection- and stream-level flow-control windows allow. Real
    /// bodies almost always fit inside the generous windows both sides advertise, so the wait
    /// loop below is a rarely-exercised safety net, not the common case.</summary>
    private async Task SendBodyRespectingFlowControlAsync(Http2Stream http2Stream, byte[] body, CancellationToken ct)
    {
        var streamId = http2Stream.Id;
        var offset = 0;

        while (offset < body.Length)
        {
            int chunk;
            while (true)
            {
                // The stream window has exactly one spender (this task, for this stream), so
                // reading it and deciding how much to *ask for* doesn't need to be atomic with
                // the actual reservation. The connection window is shared across every
                // concurrently-sending stream, so reserving from it must be a single atomic step
                // (see TryReserveConnectionWindow) -- otherwise two streams can each act on the
                // same stale balance and together overspend it.
                var streamWindow = Interlocked.Read(ref http2Stream.RemoteWindow);
                var wanted = (int)Math.Max(0, Math.Min(streamWindow, Math.Min(body.Length - offset, _peerSettings.MaxFrameSize)));

                if (wanted > 0)
                {
                    var reserved = (int)TryReserveConnectionWindow(wanted);
                    if (reserved > 0) { chunk = reserved; break; }
                }

                await Task.Delay(20, ct).ConfigureAwait(false);
            }

            Interlocked.Add(ref http2Stream.RemoteWindow, -chunk);

            var isLast = offset + chunk >= body.Length;
            var slice = body.AsMemory(offset, chunk);
            EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.Data,
                isLast ? Http2FrameFlags.EndStream : Http2FrameFlags.None, streamId, slice, ct2));
            offset += chunk;
        }

        if (body.Length == 0)
            EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.Data, Http2FrameFlags.EndStream, streamId, ReadOnlyMemory<byte>.Empty, ct2));
    }

    // ---------------------------------------------------------------------- writer

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var job in _outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await job(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            _outbox.Writer.TryComplete();
        }
    }

    private void EnqueueWrite(Func<CancellationToken, Task> job) => _outbox.Writer.TryWrite(job);

    private void EnqueueWindowUpdate(int streamId, long increment)
    {
        var payload = new byte[4];
        var value = (uint)increment;
        payload[0] = (byte)((value >> 24) & 0x7f);
        payload[1] = (byte)(value >> 16);
        payload[2] = (byte)(value >> 8);
        payload[3] = (byte)value;
        EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.WindowUpdate, Http2FrameFlags.None, streamId, payload, ct2));
    }

    private void EnqueueRstStream(int streamId, Http2ErrorCode code)
    {
        var payload = new byte[4];
        var value = (uint)code;
        payload[0] = (byte)(value >> 24);
        payload[1] = (byte)(value >> 16);
        payload[2] = (byte)(value >> 8);
        payload[3] = (byte)value;
        EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.RstStream, Http2FrameFlags.None, streamId, payload, ct2));
    }

    private void EnqueueGoAway(Http2ErrorCode code)
    {
        var payload = new byte[8];
        var lastId = (uint)_highestStreamId;
        payload[0] = (byte)((lastId >> 24) & 0x7f);
        payload[1] = (byte)(lastId >> 16);
        payload[2] = (byte)(lastId >> 8);
        payload[3] = (byte)lastId;
        var value = (uint)code;
        payload[4] = (byte)(value >> 24);
        payload[5] = (byte)(value >> 16);
        payload[6] = (byte)(value >> 8);
        payload[7] = (byte)value;
        EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.GoAway, Http2FrameFlags.None, 0, payload, ct2));
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
