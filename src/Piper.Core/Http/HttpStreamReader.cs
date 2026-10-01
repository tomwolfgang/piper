using System.Buffers;
using System.Text;

namespace Piper.Core.Http;

/// <summary>
/// Buffered reader over a network stream that can alternate between line-oriented
/// reads (request/status lines, headers, chunk sizes) and exact byte reads (bodies)
/// without losing buffered data at the boundary.
/// </summary>
public sealed class HttpStreamReader : IDisposable
{
    private const int DefaultBufferSize = 16 * 1024;
    private const int MaxLineLength = 64 * 1024;

    private readonly Stream _stream;
    private byte[] _buffer;
    private int _start;   // first unconsumed byte
    private int _end;     // one past last valid byte
    private bool _disposed;

    public HttpStreamReader(Stream stream, int bufferSize = DefaultBufferSize)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(bufferSize, 4096));
    }

    public Stream BaseStream => _stream;

    /// <summary>
    /// How long a single read may wait for the peer to send something before the connection is
    /// treated as dead. <see cref="Timeout.InfiniteTimeSpan"/> (the default) waits for ever.
    /// </summary>
    /// <remarks>
    /// Deliberately an idle timeout rather than a budget for the whole message: a legitimate
    /// multi-gigabyte download is not a stall, and a server-sent-event stream that says nothing
    /// for a while is not either. What is never legitimate is silence with no end.
    /// </remarks>
    public TimeSpan IdleTimeout { get; set; } = Timeout.InfiniteTimeSpan;

    /// <summary>Bytes sitting in the buffer that have been read from the socket but not consumed.</summary>
    public int Buffered => _end - _start;

    /// <summary>True once the peer has closed and the buffer is drained.</summary>
    public bool EndOfStream { get; private set; }

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        if (_start == _end)
        {
            _start = _end = 0;
        }
        else if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            else
            {
                // Buffer full with a single unconsumed run - grow it.
                var bigger = ArrayPool<byte>.Shared.Rent(_buffer.Length * 2);
                Buffer.BlockCopy(_buffer, 0, bigger, 0, _end);
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = bigger;
            }
        }

        var read = await ReadWithIdleTimeoutAsync(ct).ConfigureAwait(false);
        if (read <= 0)
        {
            EndOfStream = true;
            return false;
        }
        _end += read;
        return true;
    }

    private async ValueTask<int> ReadWithIdleTimeoutAsync(CancellationToken ct)
    {
        var destination = _buffer.AsMemory(_end, _buffer.Length - _end);

        if (IdleTimeout == Timeout.InfiniteTimeSpan)
            return await _stream.ReadAsync(destination, ct).ConfigureAwait(false);

        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(IdleTimeout);
        try
        {
            return await _stream.ReadAsync(destination, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Reported as a parse failure rather than a cancellation so it reaches the caller as a
            // named reason: a silently abandoned read is indistinguishable from the hang it exists
            // to prevent.
            throw new HttpStalledException(
                $"No data received for {IdleTimeout.TotalSeconds:0.#}s; treating the connection as stalled.");
        }
    }

    /// <summary>
    /// Reads one CRLF- (or bare LF-) terminated line, returning it without the terminator.
    /// Returns null at end of stream.
    /// </summary>
    public async ValueTask<string?> ReadLineAsync(CancellationToken ct)
    {
        // Offset relative to _start of the run already known to hold no LF. Compaction
        // and growth both preserve the run's layout relative to _start, so this stays
        // valid across FillAsync calls where an absolute index would not.
        var scanned = 0;
        while (true)
        {
            var searchFrom = _start + scanned;
            if (_end > searchFrom)
            {
                var lf = Array.IndexOf(_buffer, (byte)'\n', searchFrom, _end - searchFrom);
                if (lf >= 0)
                {
                    var lineEnd = lf;
                    if (lineEnd > _start && _buffer[lineEnd - 1] == (byte)'\r') lineEnd--;
                    var line = Encoding.Latin1.GetString(_buffer, _start, lineEnd - _start);
                    _start = lf + 1;
                    return line;
                }
            }

            scanned = _end - _start;
            if (scanned > MaxLineLength)
                throw new HttpParseException($"Header line exceeded {MaxLineLength} bytes.");

            if (!await FillAsync(ct).ConfigureAwait(false))
            {
                if (_end == _start) return null;
                var tail = Encoding.Latin1.GetString(_buffer, _start, _end - _start);
                _start = _end;
                return tail;
            }
        }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, throwing if the stream ends early.</summary>
    public async ValueTask<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        if (count == 0) return [];
        var result = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var n = await ReadAsync(result.AsMemory(offset, count - offset), ct).ConfigureAwait(false);
            if (n == 0) throw new HttpParseException($"Stream ended after {offset} of {count} expected body bytes.");
            offset += n;
        }
        return result;
    }

    /// <summary>Reads into <paramref name="destination"/>, draining the internal buffer first.</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken ct)
    {
        if (destination.Length == 0) return 0;

        if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
            return 0;

        var available = Math.Min(destination.Length, _end - _start);
        _buffer.AsMemory(_start, available).CopyTo(destination);
        _start += available;
        return available;
    }

    /// <summary>Reads until the peer closes the connection. Used for HTTP/1.0-style delimited bodies.</summary>
    /// <remarks>
    /// Exceeding <paramref name="limit"/> throws rather than returning what fits. A short read here
    /// is not a partial answer, it is a wrong one: the caller goes on to advertise the truncated
    /// length downstream while the rest of the body is still queued on the socket, so the next
    /// message read from that connection starts mid-body.
    /// </remarks>
    public async ValueTask<byte[]> ReadToEndAsync(long limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var scratch = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (true)
            {
                var n = await ReadAsync(scratch.AsMemory(0, scratch.Length), ct).ConfigureAwait(false);
                if (n == 0) break;
                if (ms.Length + n > limit)
                    throw new HttpParseException($"Body delimited by connection close exceeded the {limit} byte cap.");
                ms.Write(scratch, 0, n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Hands back the bytes already pulled off the socket but not yet consumed, and forgets them.
    /// </summary>
    /// <remarks>
    /// Needed when a connection stops being HTTP and becomes something this reader must not touch
    /// again -- a 101 handing over to WebSocket. Reads are buffered, so by the time the 101 head
    /// has been parsed the peer's first frames may already be sitting here. Whoever relays the raw
    /// stream from now on has to be given these first, or they are silently dropped.
    /// </remarks>
    public byte[] TakeBuffered()
    {
        if (_start == _end) return [];
        var pending = _buffer.AsSpan(_start, _end - _start).ToArray();
        _start = _end = 0;
        return pending;
    }

    /// <summary>Peeks whether more data is available without consuming it. Used to detect idle keep-alive sockets.</summary>
    public async ValueTask<bool> HasMoreDataAsync(CancellationToken ct)
    {
        if (_start < _end) return true;
        return await FillAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }
}

public class HttpParseException : Exception
{
    public HttpParseException(string message) : base(message) { }
    public HttpParseException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The peer sent nothing for longer than <see cref="HttpStreamReader.IdleTimeout"/>. A
/// <see cref="HttpParseException"/>, so every existing handler still treats it as a failed read; it
/// is a type of its own so that a caller can tell a silent peer from a malformed message.
/// </summary>
public sealed class HttpStalledException(string message) : HttpParseException(message);
