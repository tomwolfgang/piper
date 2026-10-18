namespace Piper.Core.Security;

/// <summary>
/// Remembers the answer to "is the Piper root trusted?" so the status bar can ask every second without
/// reading the Windows certificate store every second. The answer is re-read when <see cref="Invalidate"/>
/// is called (after the user trusts or removes the root) and otherwise when it is older than the
/// lifetime (never, for <see cref="Timeout.InfiniteTimeSpan"/>), which also notices a root trusted or removed outside Piper. Safe to call from any thread;
/// the store is read outside the lock, so <see cref="Last"/> and <see cref="NeedsRead"/> never wait for a read.
/// </summary>
public sealed class TrustStateCache(Func<bool> query, TimeSpan lifetime, TimeProvider? clock = null)
{
    private readonly Func<bool> _query = query ?? throw new ArgumentNullException(nameof(query));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private bool _valid;
    private bool _hasValue;
    private bool _value;
    private long _readAt;
    private int _generation;

    /// <summary>How many times the underlying check has run.</summary>
    public int QueryCount
    {
        get { lock (_gate) return _queryCount; }
    }

    private int _queryCount;

    /// <summary>The current answer, reading the store first when there is none or it has aged out.</summary>
    public bool IsTrusted
    {
        get
        {
            int generation;
            lock (_gate)
            {
                if (IsFresh()) return _value;
                generation = _generation;
            }

            var value = _query();
            lock (_gate)
            {
                _queryCount++;
                // An Invalidate that arrived during the read means the value may predate the change
                // it was called for (a slow worker finishing after the user clicked Trust or Remove).
                // It is returned to its own caller but never stored, so neither Last nor the next
                // read can see the old answer.
                if (generation == _generation)
                {
                    _value = value;
                    _hasValue = true;
                    _readAt = _clock.GetTimestamp();
                    _valid = true;
                }
            }

            return value;
        }
    }

    /// <summary>The most recent answer without reading the store, or null before the first read.</summary>
    public bool? Last
    {
        get { lock (_gate) return _hasValue ? _value : null; }
    }

    /// <summary>True when <see cref="IsTrusted"/> would read the store.</summary>
    public bool NeedsRead
    {
        get { lock (_gate) return !IsFresh(); }
    }

    /// <summary>Forces the next read to ask the store again.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _valid = false;
            _generation++;
        }
    }

    private bool IsFresh() =>
        _valid && (lifetime == Timeout.InfiniteTimeSpan || _clock.GetElapsedTime(_readAt) < lifetime);
}
