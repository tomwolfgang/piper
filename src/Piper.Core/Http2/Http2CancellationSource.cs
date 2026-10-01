namespace Piper.Core.Http2;

/// <summary>
/// A <see cref="CancellationTokenSource"/> that may be cancelled from any thread, including after
/// its owner has finished with it and disposed it.
/// </summary>
/// <remarks>
/// The frame reader cancels a stream while that stream's handler task may be finishing, and the
/// handler is the one that disposes it; a plain source throws <see cref="ObjectDisposedException"/>
/// from <c>Cancel</c> once disposed. Here the two are serialised, and a cancel that loses the race
/// has nothing left to cancel.
/// </remarks>
internal sealed class Http2CancellationSource : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly Lock _gate = new();
    private bool _disposed;

    /// <param name="parent">A token whose cancellation cancels this one, or none.</param>
    public Http2CancellationSource(CancellationToken parent = default) =>
        _source = parent.CanBeCanceled ? CancellationTokenSource.CreateLinkedTokenSource(parent) : new CancellationTokenSource();

    /// <summary>The token. Read it before disposing: a disposed source refuses to hand one out.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>True once cancelled, and still true after disposal.</summary>
    public bool IsCancellationRequested => _source.IsCancellationRequested;

    public void Cancel()
    {
        lock (_gate)
        {
            if (!_disposed) _source.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _source.Dispose();
        }
    }
}
