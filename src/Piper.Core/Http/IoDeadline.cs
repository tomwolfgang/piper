using System.Diagnostics;

namespace Piper.Core.Http;

/// <summary>
/// The deadline for the individual reads or writes of one stream, kept for the life of the stream
/// instead of being built again for every call. A linked <see cref="CancellationTokenSource"/> with
/// a timer is two allocations and a timer-queue entry per I/O call, on every request, in both
/// directions; this reuses one source and one timer, and arms the timer only for a call that did not
/// complete on the spot.
/// </summary>
/// <remarks>
/// <para>The bound is the one the per-call source gave: a call not complete <c>timeout</c> after it
/// started is cancelled. The timer is armed a few instructions after the call returns incomplete.</para>
/// <para>Reuse is safe only if a late timer can never cancel a later call. The timer callback acts
/// only while a call is in flight, armed and past its own deadline (otherwise it re-arms for the rest
/// or does nothing), it decides under the lock that the end of a call takes, and a source that was
/// cancelled, by the timer or the caller's token, is thrown away rather than reused.</para>
/// <para>One call at a time owns the shared source. A call that overlaps it, or follows disposal,
/// gets a source of its own, built as every call used to be, so it keeps its bound. Disposal does
/// not lift the bound from a call already waiting.</para>
/// </remarks>
internal sealed class IoDeadline : IDisposable
{
    private readonly Lock _gate = new();
    private Timer? _timer;
    private CancellationTokenSource? _source;
    private CancellationTokenRegistration _link;
    private CancellationToken _linkedTo;
    private long _deadlineAt;
    private bool _active;
    private bool _armed;
    private bool _expired;
    private bool _disposed;

    /// <summary>Starts a call. Pass <see cref="Lease.Token"/> to it, call <see cref="Lease.Arm"/> if it
    /// returns without having completed, and always call <see cref="Lease.End"/> when it is over.
    /// The token is cancelled by <paramref name="caller"/> and by the deadline, which only
    /// <see cref="Lease.Arm"/> starts.</summary>
    public Lease Begin(TimeSpan timeout, CancellationToken caller)
    {
        lock (_gate)
        {
            if (_active || _disposed) return Lease.Own(timeout, caller);
            _active = true;
            _expired = false;
        }

        // From here the call owns the source: nothing else touches it until End.
        try
        {
            if (_source is null)
            {
                _source = new CancellationTokenSource();
                _linkedTo = default;
            }

            if (_linkedTo != caller)
            {
                _link.Dispose();
                _link = caller.CanBeCanceled ? caller.UnsafeRegister(static s => ((CancellationTokenSource)s!).Cancel(), _source) : default;
                _linkedTo = caller;
            }
        }
        catch
        {
            End(expired: true);
            throw;
        }

        return new Lease(this, timeout, _source.Token);
    }

    private void Arm(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (!_active) return;
            _timer ??= new Timer(static state => ((IoDeadline)state!).OnTimer(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _deadlineAt = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            _armed = true;
            _timer.Change(timeout, Timeout.InfiniteTimeSpan);
        }
    }

    // Internal so a test can deliver the callback that a timer would deliver late.
    internal void OnTimer()
    {
        CancellationTokenSource source;
        lock (_gate)
        {
            if (!_active || !_armed) return;

            var remaining = _deadlineAt - Stopwatch.GetTimestamp();
            if (remaining > 0)
            {
                // Early (the timer's resolution), or a callback of an earlier call meeting this one.
                _timer!.Change(TimeSpan.FromMilliseconds(Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency)), Timeout.InfiniteTimeSpan);
                return;
            }

            _armed = false;
            _expired = true;
            source = _source!;
        }

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The call ended and discarded the source between the decision and here; nothing is
            // waiting on it any more.
        }
    }

    private void End(bool expired)
    {
        CancellationTokenSource? discard = null;
        CancellationTokenRegistration link = default;
        Timer? timer = null;
        lock (_gate)
        {
            if (!_active) return;
            if (_armed) _timer!.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _armed = false;
            _active = false;

            if (_disposed)
            {
                timer = _timer;
                _timer = null;
            }

            if (_expired || expired || _disposed || _source!.IsCancellationRequested)
            {
                discard = _source;
                link = _link;
                _source = null;
                _link = default;
                _linkedTo = default;
            }
        }

        timer?.Dispose();
        link.Dispose();
        discard?.Dispose();
    }

    public void Dispose()
    {
        Timer? timer = null;
        CancellationTokenSource? discard = null;
        CancellationTokenRegistration link = default;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            // A call still in flight keeps its deadline, and discards the source and the timer when
            // it ends: disposal is not a way to lift the bound from a call that is waiting.
            if (!_active)
            {
                timer = _timer;
                _timer = null;
                discard = _source;
                link = _link;
                _source = null;
                _link = default;
            }
        }

        timer?.Dispose();
        link.Dispose();
        discard?.Dispose();
    }

    /// <summary>One call's hold on the deadline.</summary>
    internal readonly struct Lease
    {
        private readonly IoDeadline? _owner;
        private readonly CancellationTokenSource? _own;
        private readonly TimeSpan _timeout;

        internal Lease(IoDeadline owner, TimeSpan timeout, CancellationToken token)
        {
            _owner = owner;
            _timeout = timeout;
            Token = token;
        }

        private Lease(CancellationTokenSource own, TimeSpan timeout)
        {
            _own = own;
            _timeout = timeout;
            Token = own.Token;
        }

        public CancellationToken Token { get; }

        internal static Lease Own(TimeSpan timeout, CancellationToken caller)
        {
            var own = CancellationTokenSource.CreateLinkedTokenSource(caller);
            try
            {
                own.CancelAfter(timeout);
            }
            catch
            {
                own.Dispose();
                throw;
            }

            return new Lease(own, timeout);
        }

        /// <summary>Starts the clock. Not needed for a call that completed before it returned, which
        /// is the whole point of leaving it out.</summary>
        public void Arm() => _owner?.Arm(_timeout);

        public void End()
        {
            _owner?.End(expired: false);
            _own?.Dispose();
        }
    }
}
