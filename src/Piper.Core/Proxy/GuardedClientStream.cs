using System.Diagnostics;
using Piper.Core.Http;

namespace Piper.Core.Proxy;

/// <summary>
/// The stream to a client, with the two limits that keep a client from holding a connection (and so
/// a slot) for ever without being silent: every write has a deadline, so a client that asks for a
/// download and never reads it is let go; and, while armed, what is read must make a minimum of
/// progress in every window, so a request body trickled in a byte just inside the idle timeout is
/// let go too.
/// </summary>
/// <remarks>
/// Silence is already cut by <see cref="HttpStreamReader.IdleTimeout"/>, which is re-armed by every
/// read, and that is the point of the progress floor: a read that returns one byte re-arms it for
/// another full timeout, indefinitely. The floor is checked when data arrives, so a client is held
/// for at most about two windows.
/// </remarks>
internal sealed class GuardedClientStream(Stream inner, TimeSpan writeTimeout) : Stream
{
    private bool _floorArmed;
    private TimeSpan _window;
    private long _minimum;
    private long _windowStart;
    private long _windowBytes;

    /// <summary>From now on, at least <paramref name="minimumBytes"/> must arrive in every
    /// <paramref name="window"/>. A read that completes a window short of that throws
    /// <see cref="HttpStalledException"/>, which the request reader already reports as a stalled
    /// client.</summary>
    public void ArmProgressFloor(TimeSpan window, long minimumBytes)
    {
        if (window <= TimeSpan.Zero || window == Timeout.InfiniteTimeSpan || minimumBytes <= 0)
        {
            _floorArmed = false;
            return;
        }

        _window = window;
        _minimum = minimumBytes;
        _windowStart = Stopwatch.GetTimestamp();
        _windowBytes = 0;
        _floorArmed = true;
    }

    public void DisarmProgressFloor() => _floorArmed = false;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0 && _floorArmed) CountProgress(read);
        return read;
    }

    private void CountProgress(int bytes)
    {
        _windowBytes += bytes;
        var elapsed = Stopwatch.GetElapsedTime(_windowStart);
        if (elapsed < _window) return;

        if (_windowBytes < _minimum)
        {
            throw new HttpStalledException(
                $"Only {_windowBytes} bytes arrived in {elapsed.TotalSeconds:0.#}s; the minimum is {_minimum} bytes per {_window.TotalSeconds:0.#}s.");
        }

        _windowStart = Stopwatch.GetTimestamp();
        _windowBytes = 0;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(writeTimeout);
        try
        {
            await inner.WriteAsync(buffer, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // An IOException, so every path that already handles a client that went away handles this.
            throw new IOException(
                $"The client did not read for {writeTimeout.TotalSeconds:0.#}s; treating the connection as stalled.");
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

    public override void Flush() => inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
