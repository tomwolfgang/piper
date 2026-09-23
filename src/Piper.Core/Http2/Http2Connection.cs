using System.Collections.Concurrent;
using System.Threading.Channels;
using Piper.Core.Http;
using Piper.Core.Http2.Hpack;

namespace Piper.Core.Http2;

/// <summary>
/// What a handler answers one stream with: the head, and either a body already in hand or one
/// still to be relayed from wherever it is coming from.
/// </summary>
/// <param name="Head">Status and headers. Carries the body too when <paramref name="RelayBody"/> is null.</param>
/// <param name="RelayBody">
/// Writes the body into the stream it is given, which turns those writes into flow-controlled DATA
/// frames. Null when the body is already in <paramref name="Head"/>. Throwing resets the stream
/// rather than ending it, so a body that failed part-way is not reported as complete. Always run
/// once handed over -- with an already-cancelled token when the head could not be sent -- so it
/// can release whatever it owns. It must honour a cancelled token promptly rather than await
/// anything first: the stream's task, and the connection's teardown behind it, wait on it.
/// </param>
public sealed record Http2StreamResponse(HttpResponseData Head, Func<Stream, CancellationToken, Task>? RelayBody = null)
{
    public static implicit operator Http2StreamResponse(HttpResponseData head) => new(head);
}

/// <summary>
/// Server-role HTTP/2 connection (browser-facing). One task reads and demuxes frames off the
/// wire sequentially (required for HPACK, which is stateful and processed strictly in wire
/// order); each completed request is handed to <paramref name="handler"/> on its own tracked
/// task, running concurrently with other streams. Every stream-handler task writes its response
/// by enqueueing onto an outbox <see cref="Channel{T}"/> instead of touching the socket directly
/// -- a second task drains that queue and is the connection's sole writer, so concurrent streams
/// can never interleave bytes on the wire.
/// </summary>
public sealed class Http2Connection(Stream stream, Func<HttpRequestData, CancellationToken, Task<Http2StreamResponse>> handler)
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

    /// <summary>
    /// Completed and replaced whenever the peer grants more send window, so a sender waiting for
    /// credit is woken by the grant itself.
    /// </summary>
    /// <remarks>
    /// A sender that polled instead could only discover credit on its next tick, which caps
    /// throughput at one window's worth per interval however promptly the peer replenishes it.
    /// That was tolerable while every body was buffered before being framed; now that bodies are
    /// relayed as they arrive, a large download is exactly the case that exhausts a window and
    /// refills it continuously.
    ///
    /// A waiter must take the task *before* testing the window. Taking it afterwards loses a grant
    /// that lands in between, and the sender then waits for a WINDOW_UPDATE that has already been
    /// and gone.
    /// </remarks>
    private TaskCompletionSource _windowGranted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task WindowGranted => Volatile.Read(ref _windowGranted).Task;

    private void SignalWindowGranted() =>
        Interlocked.Exchange(ref _windowGranted, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult();

    /// <summary>How many times a sender has had to wait for send window. Lets a test prove the
    /// waiting path was exercised rather than merely never reached.</summary>
    internal int WindowStalls => Volatile.Read(ref _windowStalls);

    private int _windowStalls;

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
            // The connection is gone, however it ended -- GOAWAY, a protocol error, the peer
            // closing. Every stream is cancelled so none is left waiting for send window that can
            // no longer arrive, holding its upstream connection open.
            foreach (var open in _streams.Values) open.Cancellation.Cancel();

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
        {
            var initialWindowBefore = _peerSettings.InitialWindowSize;
            _peerSettings.ApplyPeerPayload(frame.Payload.Span);

            // RFC 9113 6.9.2: a new initial window size moves every open stream's window by the
            // difference. A sender waiting for window is woken only by a signal, so this has to
            // signal too, or a stream the new setting unblocks would wait on for ever.
            var delta = (long)_peerSettings.InitialWindowSize - initialWindowBefore;
            if (delta != 0)
            {
                foreach (var open in _streams.Values) Interlocked.Add(ref open.RemoteWindow, delta);
                SignalWindowGranted();
            }
        }

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
        else
        {
            return; // a grant for a stream that is already gone wakes nobody
        }

        SignalWindowGranted();
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
            Http2StreamResponse response;
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

    private async Task SendResponseAsync(Http2Stream http2Stream, Http2StreamResponse response, CancellationToken ct)
    {
        var head = response.Head;
        var streamId = http2Stream.Id;

        byte[] block;
        try
        {
            block = HpackEncoder.Encode(Http2MessageAdapter.ToHeaderFields(head)); // stateless encoder: safe to call from any task
        }
        catch (Exception)
        {
            // Nothing has gone out, so the stream is reset rather than left waiting forever. A relay
            // still runs, with nowhere to write and a token already cancelled: it owns wherever its
            // body was coming from, and only it can let go of that.
            try
            {
                EnqueueRstStream(streamId, Http2ErrorCode.InternalError);
            }
            finally
            {
                if (response.RelayBody is { } unsent)
                {
                    try
                    {
                        await unsent(Stream.Null, new CancellationToken(canceled: true)).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Expected: it was told to stop. The stream is already reset, so there is no
                        // one left to report to.
                    }
                }
            }
            return;
        }

        // A relayed body has no length yet, so the headers must not claim there is no body.
        var hasBody = response.RelayBody is not null || head.Body.Length > 0;

        EnqueueWrite(ct2 => Http2FrameWriter.WriteHeadersAsync(stream, streamId, block, endStream: !hasBody, _peerSettings.MaxFrameSize, ct2));

        if (response.RelayBody is { } relay)
        {
            var data = new Http2DataStream(this, http2Stream, ct);
            try
            {
                await relay(data, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The relay failed after the head went out. Resetting the stream is the only way
                // left to tell the client the body is incomplete.
                EnqueueRstStream(streamId, Http2ErrorCode.InternalError);
                return;
            }

            // An empty DATA frame carries the END_STREAM the relayed bytes could not: nothing along
            // the way knew which write would turn out to be the last one.
            EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(
                stream, Http2FrameType.Data, Http2FrameFlags.EndStream, streamId, ReadOnlyMemory<byte>.Empty, ct2));
        }
        else if (hasBody)
        {
            await SendBodyRespectingFlowControlAsync(http2Stream, head.Body, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Turns writes into DATA frames, taking send window for each and waiting when there is none.
    /// </summary>
    /// <remarks>
    /// Bytes are copied before being handed to the writer. The frame goes out later, off a queue,
    /// while the caller is free to reuse its buffer for the next read -- which a relay reading into
    /// a pooled buffer certainly will, and the frame would then carry whatever happened to be there
    /// by the time it was written.
    /// </remarks>
    private sealed class Http2DataStream(Http2Connection connection, Http2Stream http2Stream, CancellationToken ct)
        : Stream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var take = await connection
                    .ReserveSendWindowAsync(http2Stream, buffer.Length - offset, ct).ConfigureAwait(false);

                connection.EnqueueDataFrame(http2Stream.Id, buffer.Slice(offset, take).ToArray());
                offset += take;
            }
        }

        public override Task FlushAsync(CancellationToken token) => Task.CompletedTask;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private void EnqueueDataFrame(int streamId, ReadOnlyMemory<byte> payload) =>
        EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(
            stream, Http2FrameType.Data, Http2FrameFlags.None, streamId, payload, ct2));

    /// <summary>
    /// Takes as much send window as is available, up to <paramref name="wanted"/> and one frame,
    /// waiting for the peer to grant more when there is none. Never returns zero.
    /// </summary>
    private async Task<int> ReserveSendWindowAsync(Http2Stream http2Stream, int wanted, CancellationToken ct)
    {
        while (true)
        {
            // Taken before the window is tested, so a grant arriving between the test and the wait
            // still completes this task rather than being missed.
            var granted = WindowGranted;

            // The stream window has exactly one spender (this task, for this stream), so reading it
            // and deciding how much to ask for need not be atomic with the reservation. The
            // connection window is shared across every concurrently-sending stream, so reserving
            // from it must be a single atomic step (see TryReserveConnectionWindow) -- otherwise
            // two streams can each act on the same stale balance and together overspend it.
            var streamWindow = Interlocked.Read(ref http2Stream.RemoteWindow);
            var ask = (int)Math.Max(0, Math.Min(streamWindow, Math.Min(wanted, _peerSettings.MaxFrameSize)));

            if (ask > 0)
            {
                var reserved = (int)TryReserveConnectionWindow(ask);
                if (reserved > 0)
                {
                    Interlocked.Add(ref http2Stream.RemoteWindow, -reserved);
                    return (int)reserved;
                }
            }

            Interlocked.Increment(ref _windowStalls);
            await granted.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Sends a response body as one or more DATA frames, never sending more than the
    /// peer's currently-granted connection- and stream-level flow-control windows allow. Waiting
    /// for credit is an ordinary part of sending anything large, not a rare safety net: a body of
    /// any size will exhaust the window the peer advertised and then move at the rate the peer
    /// replenishes it.</summary>
    private async Task SendBodyRespectingFlowControlAsync(Http2Stream http2Stream, byte[] body, CancellationToken ct)
    {
        var streamId = http2Stream.Id;
        var offset = 0;

        while (offset < body.Length)
        {
            var chunk = await ReserveSendWindowAsync(http2Stream, body.Length - offset, ct).ConfigureAwait(false);

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
