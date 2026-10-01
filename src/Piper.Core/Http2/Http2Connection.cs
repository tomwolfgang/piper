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
/// can release whatever it owns.
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

    /// <summary>One entry per stream handler that is still running; each removes itself when it
    /// stops, so this stays as small as the number of concurrent streams however long the
    /// connection lives. Awaited, bounded, when the connection ends.</summary>
    private readonly HashSet<Task> _inFlight = [];
    private readonly Lock _inFlightGate = new();

    private int _pendingControlFrames;

    /// <summary>Completed when the watchdog gives up on a write that will not come back.</summary>
    private readonly TaskCompletionSource _writerAbandoned = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Cancelled to end the reader loop from elsewhere: the idle watchdog, the control-frame
    /// cap, the last stream finishing after the peer's GOAWAY. Set for the length of
    /// <see cref="RunAsync"/>.</summary>
    private Http2CancellationSource? _readerStop;

    /// <summary>Cancelled to abandon the writer, which is blocked on a peer that will not read.</summary>
    private Http2CancellationSource? _writerStop;

    private const int NoCloseReason = -1;

    /// <summary>Why the connection is being closed from elsewhere than the reader (an
    /// <see cref="Http2ErrorCode"/> value), or <see cref="NoCloseReason"/>. First reason wins; the
    /// reader turns it into the GOAWAY.</summary>
    private int _closeReason = NoCloseReason;

    /// <summary>The peer has sent GOAWAY: it is shutting down and will not start new work, so the
    /// connection ends once the streams it already has are answered (RFC 9113 §6.8).</summary>
    private bool _peerGoingAway;

    private const long NoWrite = -1;
    private long _writeStartedTicks = NoWrite;
    private long _lastActivityTicks;

    private readonly Http2Settings _localSettings = Http2Settings.Advertised();
    private readonly Http2Settings _peerSettings = new();
    private readonly HpackDecoder _hpackDecoder = new(Http2Settings.Advertised().HeaderTableSize);

    /// <summary>The highest stream id the peer has opened. RFC 9113 §5.1.1: a new stream must
    /// exceed it, and GOAWAY reports it as the last stream this side may have processed.</summary>
    private int _highestStreamId;

    /// <summary>The header block being assembled, and the stream its HEADERS frame named (0 when
    /// none is). RFC 9113 §6.10: its CONTINUATION frames follow immediately, on that stream.</summary>
    private readonly List<byte> _headerBlock = [];
    private int _headerBlockStreamId;
    private bool _headerBlockEndsStream;

    /// <summary>The advertised header list size, enforced on both the compressed block (so
    /// CONTINUATION frames cannot buffer without limit, the 2024 "CONTINUATION flood") and the
    /// decoded list (see <see cref="CompleteHeaders"/>).</summary>
    private int MaxHeaderBlockSize => _localSettings.MaxHeaderListSize ?? 65_536;

    /// <summary>Streams this side reset while the peer was still sending on them; see
    /// <see cref="ResetStream"/>. Touched only by the reader loop.</summary>
    private readonly HashSet<int> _resetWhileOpen = [];
    private readonly Queue<int> _resetWhileOpenOrder = new();
    private const int MaxRememberedResets = 128;

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

    internal const long DefaultMaxRequestBodyBytes = 256L * 1024 * 1024;
    internal const long DefaultMaxBufferedRequestBytes = 1024L * 1024 * 1024;

    /// <summary>
    /// Largest request body one stream may accumulate, the same cap HTTP/1.1 and HTTP/3 bodies have.
    /// Window credit is granted as bytes arrive, so without it a client could stream one body until
    /// the process runs out of memory. A stream past it is reset. This bounds one stream; bodies
    /// still arriving across the connection are bounded by <see cref="MaxBufferedRequestBytes"/>,
    /// but bodies already handed to handlers are not. Settable so a test need not send the full amount.
    /// </summary>
    internal long MaxRequestBodyBytes { get; init; } = DefaultMaxRequestBodyBytes;

    /// <summary>
    /// Most request-body bytes all of a connection's undispatched streams may hold between them.
    /// With 100 concurrent streams each just under <see cref="MaxRequestBodyBytes"/>, one connection
    /// could otherwise buffer about 25 GB. The stream whose DATA would pass it is reset. Only bodies
    /// still arriving count: one already handed to a handler is not, and is bounded only by how long
    /// the handler holds it, so streams that finish in turn can still hold that much between them.
    /// </summary>
    internal long MaxBufferedRequestBytes { get; init; } = DefaultMaxBufferedRequestBytes;

    /// <summary>
    /// Told when a stream is reset for passing a body cap. The request never reaches the handler, so
    /// no session records it; this is what says it happened, as HTTP/1.1 logs its cap. Only sizes and
    /// the stream id, never captured content.
    /// </summary>
    internal Action<string>? Log { get; init; }

    internal static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the connection may sit with nothing to do before it is closed with GOAWAY(NO_ERROR):
    /// no frame from the peer and no handler running. A handler waiting on an origin is not idleness,
    /// so a slow response is never cut short by this. The same span bounds a single write that makes
    /// no progress, which is what a peer that stopped reading its socket causes. The write is then
    /// cancelled and, if it does not return within <see cref="WriterAbandonGrace"/>, abandoned: the
    /// connection ends without waiting for it, since the peer is not going to read a GOAWAY. Total
    /// bound: the idle span plus the grace, plus one watchdog tick.
    /// </summary>
    internal TimeSpan IdleTimeout { get; init; } = DefaultIdleTimeout;

    /// <summary>
    /// How long <see cref="RunAsync"/> waits, once the watchdog has given up on a stuck write, for the
    /// writer to notice. A pending socket send does not reliably honour cancellation (a TLS stream over
    /// a socket in particular), so the writer is abandoned after this, and it is the owner disposing
    /// the transport after <see cref="RunAsync"/> returns that actually releases the send.
    /// </summary>
    internal TimeSpan WriterAbandonGrace { get; init; } = TimeSpan.FromSeconds(5);

    internal const int DefaultMaxPendingControlFrames = 1000;

    /// <summary>
    /// Most control frames (PING and SETTINGS acknowledgements, RST_STREAM) that may be queued and
    /// not yet written. Each of these answers something the peer sent at no cost to itself, so a
    /// peer that sends them and never reads makes the queue grow for as long as it likes
    /// (CVE-2019-9512 ping flood, CVE-2019-9514 reset flood, CVE-2019-9515 settings flood). Past the
    /// cap the connection is closed with GOAWAY(ENHANCE_YOUR_CALM). Counts frames still unwritten,
    /// not frames ever sent, so a peer that keeps reading is never near it.
    /// </summary>
    internal int MaxPendingControlFrames { get; init; } = DefaultMaxPendingControlFrames;

    /// <summary>Control frames queued and not yet written. Lets a test know the connection has
    /// taken in everything a peer sent while its writer was held.</summary>
    internal int PendingControlFrames => Volatile.Read(ref _pendingControlFrames);

    /// <summary>Whether something other than the reader has already decided to end the connection.</summary>
    internal bool IsClosing => Volatile.Read(ref _closeReason) != NoCloseReason;

    /// <summary>The send window of an open stream, or null. Lets a test see that an overflowing
    /// WINDOW_UPDATE was refused rather than applied.</summary>
    internal long? SendWindowOf(int streamId) =>
        _streams.TryGetValue(streamId, out var open) ? Interlocked.Read(ref open.RemoteWindow) : null;

    /// <summary>Stream handlers still running. Lets a test see that finished ones are forgotten.</summary>
    internal int InFlightHandlers
    {
        get { lock (_inFlightGate) return _inFlight.Count; }
    }

    /// <summary>
    /// Whether a reset for <see cref="MaxBufferedRequestBytes"/> has been logged on this connection.
    /// Once the peer holds the budget full, every fresh stream it opens is reset for a few bytes, so
    /// logging each one would let it flood the log, and the UI thread that shows it, almost for
    /// free. A reset for the per-stream cap costs the peer that cap each time, so each is logged.
    /// </summary>
    private bool _bufferedCapLogged;

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
    /// then reads and dispatches frames until the peer closes, the peer's GOAWAY has been
    /// drained, the connection goes idle, or a fatal protocol error occurs. Awaits every
    /// in-flight stream handler before returning.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var readerStop = new Http2CancellationSource(ct);
        using var writerStop = new Http2CancellationSource(ct);
        using var watchdogStop = new CancellationTokenSource();
        _readerStop = readerStop;
        _writerStop = writerStop;
        Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);

        // Running before the preface is read, so a peer that connects and says nothing is bounded too.
        var watchdog = WatchdogAsync(watchdogStop.Token);
        try
        {
            await ValidatePrefaceAsync(readerStop.Token, ct).ConfigureAwait(false);

            var writerTask = WriterLoopAsync(writerStop.Token);
            EnqueueWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.Settings, Http2FrameFlags.None, 0, _localSettings.ToPayload(), ct2));

            try
            {
                await ReaderLoopAsync(readerStop.Token, ct).ConfigureAwait(false);
            }
            finally
            {
                // The connection is gone, however it ended -- GOAWAY, a protocol error, the peer
                // closing. Every stream is cancelled so none is left waiting for send window that can
                // no longer arrive, holding its upstream connection open.
                foreach (var open in _streams.Values) open.Cancel();

                // Whatever is queued, GOAWAY included, is still written, for as long as writes make
                // progress. A write that does not (a peer that stopped reading) is what the watchdog
                // gives up on; cancelling it is not guaranteed to free a socket send already in flight,
                // so after a short grace the writer is abandoned. The owner disposing the transport
                // once this returns is what releases it.
                _outbox.Writer.TryComplete();
                if (await Task.WhenAny(writerTask, _writerAbandoned.Task).ConfigureAwait(false) != writerTask)
                {
                    try
                    {
                        await writerTask.WaitAsync(WriterAbandonGrace).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Whatever it fails with when the transport goes is expected and is not reported.
                        _ = writerTask.ContinueWith(static t => t.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
                else
                {
                    await writerTask.ConfigureAwait(false);
                }

                Task[] pending;
                lock (_inFlightGate) pending = _inFlight.ToArray();

                // Bounded: a stream handler that wedges (a stalled upstream, a flow-control window
                // that never reopens) must not keep the whole connection -- and the socket behind it
                // -- alive indefinitely. Whatever has not finished by now is abandoned to the GC.
                try
                {
                    await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                }
                catch (TimeoutException) { /* abandoned, as above */ }

                // A stream that never reached a handler has nothing else to release it.
                foreach (var open in _streams.Values)
                    if (!open.Dispatched) open.Dispose();
            }
        }
        finally
        {
            await watchdogStop.CancelAsync().ConfigureAwait(false);
            await watchdog.ConfigureAwait(false);
        }
    }

    // ------------------------------------------------------------------- handshake

    private async Task ValidatePrefaceAsync(CancellationToken readCt, CancellationToken ct)
    {
        var buffer = new byte[PrefaceBytes.Length];
        var offset = 0;
        try
        {
            while (offset < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(offset), readCt).ConfigureAwait(false);
                if (n == 0) throw new IOException("Connection closed before the HTTP/2 preface was received.");
                offset += n;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Not the caller cancelling: the watchdog gave up waiting for the peer.
            throw new IOException("Timed out waiting for the HTTP/2 connection preface.");
        }
        if (!buffer.AsSpan().SequenceEqual(PrefaceBytes))
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "Invalid HTTP/2 connection preface.");
    }

    // ------------------------------------------------------------------- watchdog

    /// <summary>
    /// Ends a connection that is doing nothing (see <see cref="IdleTimeout"/>) and abandons one whose
    /// writer has been stuck in a single write for as long, which only a peer that has stopped reading
    /// causes: flow control keeps a peer that reads slowly from filling its socket. Runs until the
    /// connection is finished, flush of the write queue included.
    /// </summary>
    private async Task WatchdogAsync(CancellationToken stop)
    {
        var timeoutMs = (long)IdleTimeout.TotalMilliseconds;
        var period = TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs / 4, 10, 5_000));
        try
        {
            using var timer = new PeriodicTimer(period);
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                var now = Environment.TickCount64;

                var writeStarted = Volatile.Read(ref _writeStartedTicks);
                if (writeStarted != NoWrite && now - writeStarted >= timeoutMs)
                {
                    _writerStop?.Cancel();
                    _writerAbandoned.TrySetResult();
                    RequestClose(Http2ErrorCode.NoError);
                    return;
                }

                // A write in progress is not idleness either: a response is still going out even when
                // its handler has finished and the peer has nothing to say.
                if (writeStarted == NoWrite && InFlightHandlers == 0
                    && now - Volatile.Read(ref _lastActivityTicks) >= timeoutMs)
                    RequestClose(Http2ErrorCode.NoError);
            }
        }
        catch (OperationCanceledException) { /* the connection has finished */ }
    }

    /// <summary>Asks the reader loop to stop and answer with GOAWAY(<paramref name="reason"/>). The
    /// first reason given is the one used. Safe from any thread, at any time.</summary>
    private void RequestClose(Http2ErrorCode reason)
    {
        if (Interlocked.CompareExchange(ref _closeReason, (int)reason, NoCloseReason) == NoCloseReason)
            _readerStop?.Cancel();
    }

    /// <summary>The reason the connection is to be closed, if there is one. Also where a peer's GOAWAY
    /// becomes one: once the streams it left open are all answered there is nothing to wait for.</summary>
    private Http2ErrorCode? CloseReason()
    {
        if (Volatile.Read(ref _peerGoingAway) && _streams.IsEmpty) RequestClose(Http2ErrorCode.NoError);
        var reason = Volatile.Read(ref _closeReason);
        return reason == NoCloseReason ? null : (Http2ErrorCode)reason;
    }

    // --------------------------------------------------------------------- reader

    private async Task ReaderLoopAsync(CancellationToken readCt, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                if (CloseReason() is { } closing)
                {
                    EnqueueGoAway(closing);
                    return;
                }

                var frame = await Http2FrameReader.ReadAsync(stream, _localSettings.MaxFrameSize, readCt).ConfigureAwait(false);
                if (frame is null) return; // peer closed cleanly
                Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
                DispatchFrame(frame.Value);
            }
            catch (Http2ProtocolException ex)
            {
                EnqueueGoAway(ex.ErrorCode);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && CloseReason() is { } closing)
            {
                EnqueueGoAway(closing);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (IOException) { return; } // includes EndOfStreamException
        }
    }

    private void DispatchFrame(Http2Frame frame)
    {
        if (_headerBlockStreamId != 0 && (frame.Type != Http2FrameType.Continuation || frame.StreamId != _headerBlockStreamId))
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "Header block interrupted before END_HEADERS.");

        switch (frame.Type)
        {
            case Http2FrameType.Settings: HandleSettings(frame); break;
            case Http2FrameType.Headers: HandleHeaders(frame); break;
            case Http2FrameType.Continuation: HandleContinuation(frame); break;
            case Http2FrameType.Data: HandleData(frame); break;
            case Http2FrameType.WindowUpdate: HandleWindowUpdate(frame); break;
            case Http2FrameType.RstStream: HandleRstStream(frame); break;
            case Http2FrameType.Ping: HandlePing(frame); break;
            case Http2FrameType.GoAway: HandleGoAway(frame); break;
            case Http2FrameType.PushPromise:
                // RFC 9113 §8.4: only a server pushes, and a client that sent one is broken.
                throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "PUSH_PROMISE from a client.");
            case Http2FrameType.Priority: break; // parsed, discarded: no scheduling
            default: break; // unknown frame type: ignore, per RFC 9113 §4.1
        }
    }

    /// <summary>
    /// The peer is going away (RFC 9113 §6.8). It has said it starts nothing new, not that it has
    /// abandoned what it started, so the streams already open are answered before the connection
    /// ends; cancelling them, as the reader used to, threw away responses the peer was still waiting
    /// for. The wait is bounded like any other: by the peer closing, by the idle timeout, and by the
    /// handlers' own limits.
    /// </summary>
    private void HandleGoAway(Http2Frame frame)
    {
        if (frame.StreamId != 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "GOAWAY on a non-zero stream.");
        if (frame.Payload.Length < 8)
            throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError, "GOAWAY payload must be at least 8 bytes.");

        Volatile.Write(ref _peerGoingAway, true);
    }

    private void HandleSettings(Http2Frame frame)
    {
        if (frame.StreamId != 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "SETTINGS on a non-zero stream.");

        if (frame.HasFlag(Http2FrameFlags.Ack))
        {
            // RFC 9113 §6.5: an acknowledgement carries no settings.
            if (frame.Payload.Length != 0)
                throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError, "SETTINGS acknowledgement with a payload.");
            return; // peer acknowledged our SETTINGS; phase 1 gates nothing on this
        }

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
                foreach (var open in _streams.Values)
                {
                    if (Interlocked.Add(ref open.RemoteWindow, delta) > MaxFlowControlWindow)
                        throw new Http2ProtocolException(Http2ErrorCode.FlowControlError,
                            "SETTINGS_INITIAL_WINDOW_SIZE change pushes a stream's flow-control window past 2^31-1.");
                }
                SignalWindowGranted();
            }
        }

        EnqueueControlWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.Settings, Http2FrameFlags.Ack, 0, ReadOnlyMemory<byte>.Empty, ct2));
    }

    /// <summary>RFC 9113 §6.9.1: no flow-control window may exceed 2^31-1.</summary>
    private const long MaxFlowControlWindow = int.MaxValue;

    private void HandleHeaders(Http2Frame frame)
    {
        if (frame.StreamId == 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "HEADERS on stream 0.");

        _headerBlock.Clear();
        _headerBlockStreamId = frame.StreamId;
        _headerBlockEndsStream = frame.HasFlag(Http2FrameFlags.EndStream);
        AppendHeaderBlock(frame);
    }

    private void HandleContinuation(Http2Frame frame)
    {
        // One on the stream being assembled got past DispatchFrame; any other is unexpected.
        if (_headerBlockStreamId == 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "CONTINUATION without a preceding HEADERS.");
        AppendHeaderBlock(frame);
    }

    private void AppendHeaderBlock(Http2Frame frame)
    {
        _headerBlock.AddRange(frame.HeaderBlockPayload.Span);
        if (_headerBlock.Count > MaxHeaderBlockSize)
            throw new Http2ProtocolException(Http2ErrorCode.EnhanceYourCalm, "Header block exceeds the advertised header list size.");

        if (!frame.HasFlag(Http2FrameFlags.EndHeaders)) return;

        var streamId = _headerBlockStreamId;
        var block = _headerBlock.ToArray();
        _headerBlockStreamId = 0;
        _headerBlock.Clear();
        CompleteHeaders(streamId, block, _headerBlockEndsStream);
    }

    private void CompleteHeaders(int streamId, byte[] block, bool endStream)
    {
        // Decoded before anything else is decided, however the block is then treated: the dynamic
        // table is shared by every stream, so skipping a block that is about to be refused would
        // desynchronise it for all of them.
        List<(string Name, string Value)> fields;
        try
        {
            fields = _hpackDecoder.Decode(block);
        }
        catch (HttpParseException ex)
        {
            // RFC 9113 §4.3: an HPACK decoding error is always a *connection* error -- the
            // decoder's dynamic table state is now unrecoverable for every other stream too.
            throw new Http2ProtocolException(Http2ErrorCode.CompressionError, $"HPACK decoding failed: {ex.Message}");
        }

        // The decoded list is capped too: one-byte references to a large dynamic-table entry turn a
        // small block into a huge one, which is materialised when the request is forwarded. (Decode
        // itself stays cheap: indexed fields share the table's strings.) Size as RFC 9113 §6.5.2
        // counts it: name + value + 32 per field. The table is in sync by now, so only the
        // offending stream is reset, as §6.5.2 suggests for this advisory limit.
        long listSize = 0;
        foreach (var (name, value) in fields) listSize += name.Length + value.Length + 32;
        var tooLarge = listSize > MaxHeaderBlockSize;

        if (_streams.TryGetValue(streamId, out var open))
        {
            CompleteTrailers(open, fields, endStream, tooLarge);
            return;
        }

        // Sent before the peer saw this side's RST_STREAM: decoded above, otherwise discarded.
        if (_resetWhileOpen.Contains(streamId)) return;

        // RFC 9113 §5.1.1: a client opens odd ids, each higher than the last. A lower or repeated
        // one names a stream that is already closed.
        if ((streamId & 1) == 0 || streamId <= _highestStreamId)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, $"HEADERS on stream {streamId}, which cannot open a new stream.");
        _highestStreamId = streamId;

        // The peer has said it is going away (see HandleGoAway). What it already opened is finished;
        // anything new is refused, which is also what keeps the drain from never ending under a
        // peer that follows its GOAWAY with an endless run of new streams. REFUSED_STREAM tells it
        // nothing was processed, so it may retry on another connection.
        var maxConcurrent = _localSettings.MaxConcurrentStreams ?? int.MaxValue;
        if (Volatile.Read(ref _peerGoingAway) || _streams.Count >= maxConcurrent)
        {
            ResetStream(streamId, Http2ErrorCode.RefusedStream, peerStillSending: !endStream);
            return;
        }

        if (tooLarge)
        {
            ResetStream(streamId, Http2ErrorCode.EnhanceYourCalm, peerStillSending: !endStream);
            return;
        }

        HttpRequestData request;
        try
        {
            request = Http2MessageAdapter.ToRequest(fields);
        }
        catch (HttpParseException)
        {
            // RFC 9113 §8.1.1: a malformed request is a stream error; the other streams carry on.
            ResetStream(streamId, Http2ErrorCode.ProtocolError, peerStillSending: !endStream);
            return;
        }

        var http2Stream = new Http2Stream(streamId, request) { RemoteWindow = _peerSettings.InitialWindowSize };
        _streams[streamId] = http2Stream;

        if (endStream)
            DispatchRequest(http2Stream);
    }

    /// <summary>A second HEADERS on an open stream: the request's trailers (RFC 9113 §8.1).</summary>
    private void CompleteTrailers(Http2Stream http2Stream, List<(string Name, string Value)> fields, bool endStream, bool tooLarge)
    {
        if (ResetIfDispatched(http2Stream)) return;

        // Trailers end the stream and carry no pseudo-headers; anything else is malformed.
        if (tooLarge || !endStream || fields.Exists(f => f.Name.StartsWith(':')))
        {
            ResetStream(http2Stream.Id, tooLarge ? Http2ErrorCode.EnhanceYourCalm : Http2ErrorCode.ProtocolError,
                peerStillSending: !endStream);
            http2Stream.Cancel();
            ForgetUndispatched(http2Stream);
            return;
        }

        // The trailers themselves are discarded, as HTTP/1.1 chunked trailers are
        // (HttpParser.SkipTrailersAsync): HttpRequestData has nowhere to carry them.
        DispatchRequest(http2Stream);
    }

    /// <summary>Drops a stream that never reached a handler and releases what it holds. Reader thread
    /// only: a dispatched stream is the handler's, which disposes it when it stops.</summary>
    private void ForgetUndispatched(Http2Stream http2Stream)
    {
        _streams.TryRemove(http2Stream.Id, out _);
        http2Stream.Dispose();
    }

    /// <summary>Resets a stream from the header path. When the peer had not yet ended it, the id is
    /// remembered so that HEADERS it already had in flight are discarded rather than taken for a
    /// reused id (RFC 9113 §5.1, closed) -- which would end every other stream too.</summary>
    private void ResetStream(int streamId, Http2ErrorCode code, bool peerStillSending)
    {
        EnqueueRstStream(streamId, code);
        if (!peerStillSending || !_resetWhileOpen.Add(streamId)) return;

        // Bounded: a peer stops sending once it sees the reset, so only recent ids can still have
        // frames in flight. A forgotten one falls back to the reused-id PROTOCOL_ERROR, which ends
        // the whole connection, not just that stream -- reachable only by a peer with more than
        // this many reset-but-unacknowledged streams, far past the advertised concurrency limit.
        _resetWhileOpenOrder.Enqueue(streamId);
        if (_resetWhileOpenOrder.Count > MaxRememberedResets)
            _resetWhileOpen.Remove(_resetWhileOpenOrder.Dequeue());
    }

    /// <summary>
    /// RFC 9113 §5.1: after END_STREAM the peer may send no more HEADERS or DATA on the stream, a
    /// stream error of type STREAM_CLOSED. Only that stream is reset and its handler cancelled.
    /// §5.4.1 would allow ending the whole connection instead, but in a debugging proxy that would
    /// throw away every other stream's response for one peer's mistake.
    /// </summary>
    /// <remarks>
    /// The stream stays registered until its handler stops, as after a reset from the peer, so it
    /// keeps counting against MaxConcurrentStreams (Rapid Reset). Its token being cancelled already
    /// means a reset was sent or received, so further late frames are dropped without another one.
    /// </remarks>
    private bool ResetIfDispatched(Http2Stream http2Stream)
    {
        if (!http2Stream.Dispatched) return false;
        if (!http2Stream.IsCancelled)
        {
            // Remembered too: once the handler stops and the stream is gone, a further late
            // HEADERS must still be discarded, not taken for a reused id.
            ResetStream(http2Stream.Id, Http2ErrorCode.StreamClosed, peerStillSending: true);
            http2Stream.Cancel();
        }
        return true;
    }

    private void HandleData(Http2Frame frame)
    {
        // RFC 9113 §6.1: DATA belongs to a stream; and §5.1: a stream that was never opened (idle)
        // may receive HEADERS or PRIORITY and nothing else. Both are connection errors. Only a stream
        // that existed and is now closed may still draw late DATA, which is dropped below.
        if (frame.StreamId == 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "DATA on stream 0.");
        if (IsIdle(frame.StreamId))
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, $"DATA on idle stream {frame.StreamId}.");

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

        // DATA after END_STREAM resets only this stream. The bytes are never buffered, and the
        // connection credit for them was returned above, so the other streams are not stalled.
        if (ResetIfDispatched(http2Stream)) return;

        var incoming = frame.DataPayload.Length;
        var overStreamCap = http2Stream.Body.Length + incoming > MaxRequestBodyBytes;
        if (overStreamCap || BufferedRequestBytes() + incoming > MaxBufferedRequestBytes)
        {
            // No handler owns the stream yet. Forgetting it is what makes its later DATA frames fall
            // into the drop above.
            ForgetUndispatched(http2Stream);
            if (overStreamCap)
                Log?.Invoke($"HTTP/2 stream {frame.StreamId} reset: request body exceeds the {MaxRequestBodyBytes} byte cap.");
            else if (!_bufferedCapLogged)
            {
                _bufferedCapLogged = true;
                Log?.Invoke($"HTTP/2 stream {frame.StreamId} reset: request bodies on this connection exceed the " +
                            $"{MaxBufferedRequestBytes} byte cap. Later resets for this cap on the connection are not logged.");
            }
            // Remembered, so request trailers the peer already had in flight are discarded rather
            // than taken for a reused id, which would end every other stream too.
            ResetStream(frame.StreamId, Http2ErrorCode.EnhanceYourCalm, peerStillSending: !frame.HasFlag(Http2FrameFlags.EndStream));
            return;
        }

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

    /// <summary>
    /// Bytes held by streams whose requests have not been handed to a handler. Read on the frame
    /// reader, the only thread that writes those buffers. A stream the peer reset before finishing
    /// still counts, since its buffer is still held.
    /// </summary>
    // Runs for every DATA frame within its stream's own cap, which is every frame of a healthy
    // upload, and sums up to MaxConcurrentStreams (100) lengths. It enumerates the dictionary rather
    // than .Values, which would take every lock and copy the values out on each frame. Keep a running
    // total instead if the stream limit is ever raised far enough for the scan to show.
    private long BufferedRequestBytes()
    {
        long total = 0;
        foreach (var (_, open) in _streams)
            if (!open.Dispatched) total += open.Body.Length;
        return total;
    }

    private void HandleWindowUpdate(Http2Frame frame)
    {
        if (frame.Payload.Length != 4)
            throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError, "WINDOW_UPDATE payload must be 4 bytes.");

        var span = frame.Payload.Span;
        var increment = ((span[0] & 0x7f) << 24) | (span[1] << 16) | (span[2] << 8) | span[3];
        if (increment == 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "WINDOW_UPDATE increment of 0.");

        // RFC 9113 §6.9.1: a window may not pass 2^31-1. The sum is what the peer would have to be
        // sent against, so an overflow is the peer's arithmetic gone wrong (or hostile), not ours.
        if (frame.StreamId == 0)
        {
            // Checked before it is applied, inside the lock senders take their share under, so the
            // window is never above the legal maximum for a sender to spend.
            lock (_connectionWindowGate)
            {
                if (_peerConnectionWindow + increment > MaxFlowControlWindow)
                    throw new Http2ProtocolException(Http2ErrorCode.FlowControlError, "WINDOW_UPDATE pushes the connection's flow-control window past 2^31-1.");
                _peerConnectionWindow += increment;
            }
        }
        else if (IsIdle(frame.StreamId))
        {
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, $"WINDOW_UPDATE on idle stream {frame.StreamId}.");
        }
        else if (_streams.TryGetValue(frame.StreamId, out var http2Stream))
        {
            // Validated before it is applied, as for the connection: a compare-and-swap, because this
            // stream's sender is concurrently spending from the same field.
            if (!TryGrantStreamWindow(http2Stream, increment))
            {
                // A stream error: only this stream is reset. As elsewhere, a dispatched stream stays
                // registered until its handler has stopped (see HandleRstStream).
                ResetStream(http2Stream.Id, Http2ErrorCode.FlowControlError, peerStillSending: !http2Stream.Dispatched);
                http2Stream.Cancel();
                if (!http2Stream.Dispatched) ForgetUndispatched(http2Stream);
                return;
            }
        }
        else
        {
            return; // a grant for a stream that is already gone wakes nobody
        }

        SignalWindowGranted();
    }

    /// <summary>Adds <paramref name="increment"/> to a stream's send window unless that would pass
    /// 2^31-1, in which case the window is left untouched and false is returned.</summary>
    private static bool TryGrantStreamWindow(Http2Stream http2Stream, int increment)
    {
        while (true)
        {
            var current = Interlocked.Read(ref http2Stream.RemoteWindow);
            var next = current + increment;
            if (next > MaxFlowControlWindow) return false;
            if (Interlocked.CompareExchange(ref http2Stream.RemoteWindow, next, current) == current) return true;
        }
    }

    /// <summary>
    /// RFC 9113 §5.1, idle: no HEADERS has opened the stream. A client opens odd ids, each above the
    /// last, so an id above the highest seen is idle, and so is every even one (those are the
    /// server's to open, and this side never pushes). Reader thread only.
    /// </summary>
    private bool IsIdle(int streamId) => (streamId & 1) == 0 || streamId > _highestStreamId;

    private void HandleRstStream(Http2Frame frame)
    {
        if (frame.StreamId == 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "RST_STREAM on stream 0.");
        if (frame.Payload.Length != 4)
            throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError, "RST_STREAM payload must be 4 bytes.");
        if (IsIdle(frame.StreamId))
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, $"RST_STREAM on idle stream {frame.StreamId}.");

        if (!_streams.TryGetValue(frame.StreamId, out var http2Stream)) return;
        http2Stream.Cancel();

        // A stream still receiving its request is closed now, so a later HEADERS naming it must be
        // refused rather than taken for trailers. A dispatched one stays counted against
        // MaxConcurrentStreams until its handler has actually stopped (ProcessStreamAsync removes
        // it); freeing the slot on the reset alone would let HEADERS+RST_STREAM loops start
        // handlers without limit (CVE-2023-44487, "Rapid Reset").
        if (!http2Stream.Dispatched) ForgetUndispatched(http2Stream);
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
        EnqueueControlWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.Ping, Http2FrameFlags.Ack, 0, payload, ct2));
    }

    // -------------------------------------------------------------- stream dispatch

    private void DispatchRequest(Http2Stream http2Stream)
    {
        // Every caller resets a stream that is already dispatched instead of coming here, but a
        // second dispatch would hand the request to a second handler and read a disposed buffer.
        if (http2Stream.Dispatched) return;
        http2Stream.Request.Body = http2Stream.Body.ToArray();
        // The copy is all the handler reads, and the stream stays registered until the response is
        // sent, so holding the buffer would keep the body in memory twice for that long. Disposing
        // alone would not free it: a closed MemoryStream keeps its buffer. The flag goes first, so
        // nothing that skips dispatched streams can read the buffer once it is disposed.
        http2Stream.Dispatched = true;
        http2Stream.Body.SetLength(0);
        http2Stream.Body.Capacity = 0;
        http2Stream.Body.Dispose();

        // Registered before the handler can possibly finish, and by a task of its own rather than the
        // handler's: the handler removes it when it stops, and could otherwise do that before it was
        // added.
        var tracker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_inFlightGate) _inFlight.Add(tracker.Task);
        _ = Task.Run(() => RunHandlerAsync(http2Stream, tracker));
    }

    private async Task RunHandlerAsync(Http2Stream http2Stream, TaskCompletionSource tracker)
    {
        try
        {
            await ProcessStreamAsync(http2Stream).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Expected, and routine on a dropped upstream or a peer that went away mid-response: the
            // stream's relay was cancelled and failed with the transport's own exception rather than
            // OperationCanceledException (SendResponseAsync lets it through once the token is cancelled).
            // There is nothing to recover but the stream itself, which is reset in case part of the
            // response went out.
            EnqueueRstStream(http2Stream.Id, Http2ErrorCode.InternalError);
        }
        catch (Exception ex)
        {
            // Not expected from ProcessStreamAsync, which turns handler failures into a 502 or a reset.
            // Nothing awaits this task, so it is reported here rather than left to surface as an
            // unobserved task exception at some later collection. Type only: the message may carry
            // request data. The stream is reset because its state is unknown.
            Log?.Invoke($"HTTP/2 stream {http2Stream.Id} handler failed unexpectedly: {ex.GetType().Name}.");
            EnqueueRstStream(http2Stream.Id, Http2ErrorCode.InternalError);
        }
        finally
        {
            lock (_inFlightGate) _inFlight.Remove(tracker.Task);
            tracker.SetResult();
            // The idle clock runs from the last thing that happened, and a response finishing is one.
            Volatile.Write(ref _lastActivityTicks, Environment.TickCount64);
            // A peer that said GOAWAY is waiting only for what it already asked for.
            if (Volatile.Read(ref _peerGoingAway) && _streams.IsEmpty) RequestClose(Http2ErrorCode.NoError);
        }
    }

    private async Task ProcessStreamAsync(Http2Stream http2Stream)
    {
        try
        {
            Http2StreamResponse response;
            try
            {
                response = await handler(http2Stream.Request, http2Stream.Token).ConfigureAwait(false);
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

            await SendResponseAsync(http2Stream, response, http2Stream.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stream was reset by the peer, or by this side for a frame after END_STREAM, or the
            // connection is tearing down -- nothing to send.
        }
        finally
        {
            _streams.TryRemove(http2Stream.Id, out _);
            // The handler was the last reader of the token, and the reader loop's cancels are safe
            // from here on (Http2CancellationSource).
            http2Stream.Dispose();
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
            EnqueueRstStream(streamId, Http2ErrorCode.InternalError);
            if (response.RelayBody is { } unsent)
            {
                try
                {
                    await unsent(Stream.Null, new CancellationToken(canceled: true)).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Expected: it was told to stop. The stream is already reset, so there is no one
                    // left to report to.
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
            {
                // What the watchdog looks at to tell a peer that has stopped reading from one that is
                // merely idle: a write that has not come back.
                Volatile.Write(ref _writeStartedTicks, Environment.TickCount64);
                try
                {
                    await job(ct).ConfigureAwait(false);
                }
                finally
                {
                    Volatile.Write(ref _writeStartedTicks, NoWrite);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally
        {
            _outbox.Writer.TryComplete();
        }
    }

    private void EnqueueWrite(Func<CancellationToken, Task> job) => _outbox.Writer.TryWrite(job);

    /// <summary>
    /// Queues a frame that answers something the peer sent (a PING or SETTINGS acknowledgement, a
    /// RST_STREAM), counting it until it has been written. Past <see cref="MaxPendingControlFrames"/>
    /// unwritten the frame is not queued and the connection is closed with ENHANCE_YOUR_CALM: the peer
    /// is asking for replies faster than it reads them. Never throws, because RST_STREAM is also queued
    /// from stream handlers, which must not be the ones to take the connection down.
    /// </summary>
    private void EnqueueControlWrite(Func<CancellationToken, Task> job)
    {
        if (Interlocked.Increment(ref _pendingControlFrames) > MaxPendingControlFrames)
        {
            Interlocked.Decrement(ref _pendingControlFrames);
            RequestClose(Http2ErrorCode.EnhanceYourCalm);
            return;
        }

        var queued = _outbox.Writer.TryWrite(async ct2 =>
        {
            try
            {
                await job(ct2).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _pendingControlFrames);
            }
        });
        if (!queued) Interlocked.Decrement(ref _pendingControlFrames); // the writer has stopped: nothing will send it
    }

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
        EnqueueControlWrite(ct2 => Http2FrameWriter.WriteAsync(stream, Http2FrameType.RstStream, Http2FrameFlags.None, streamId, payload, ct2));
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
