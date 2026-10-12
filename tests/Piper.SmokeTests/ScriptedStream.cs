/// <summary>A stream whose reads and writes are whatever a test scripts, called with the number of the
/// call. By default every read gives one byte and every write completes on the spot.</summary>
internal sealed class ScriptedStream : Stream
{
    private int _writes;
    private int _reads;

    public Func<int, CancellationToken, ValueTask> OnWrite { get; set; } = static (_, _) => ValueTask.CompletedTask;
    public Func<int, CancellationToken, ValueTask<int>> OnRead { get; set; } = static (_, _) => new ValueTask<int>(1);
    public volatile bool Block;

    public int Writes => Volatile.Read(ref _writes);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        OnWrite(Interlocked.Increment(ref _writes), cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        OnRead(Interlocked.Increment(ref _reads), cancellationToken);

    public override bool CanRead => true;
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
