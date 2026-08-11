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

        var read = await _stream.ReadAsync(_buffer.AsMemory(_end, _buffer.Length - _end), ct).ConfigureAwait(false);
        if (read <= 0)
        {
            EndOfStream = true;
            return false;
        }
        _end += read;
        return true;
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
    public async ValueTask<byte[]> ReadToEndAsync(long limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var scratch = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (ms.Length < limit)
            {
                var n = await ReadAsync(scratch.AsMemory(0, scratch.Length), ct).ConfigureAwait(false);
                if (n == 0) break;
                ms.Write(scratch, 0, n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
        return ms.ToArray();
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

public sealed class HttpParseException : Exception
{
    public HttpParseException(string message) : base(message) { }
    public HttpParseException(string message, Exception inner) : base(message, inner) { }
}
