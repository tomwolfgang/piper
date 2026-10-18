using Piper.Core.Http;
using Piper.Core.Sessions;

namespace Piper.Core.Proxy;

/// <summary>
/// Collapses repeated client TLS failures into one counted row per (process, host, port, kind), so a
/// launcher retrying a refused certificate costs one row, not one per retry. Failure path only.
/// </summary>
/// <remarks>
/// Process and host are client-chosen, and a hostile process can cycle both, hence the bounds: at most
/// <see cref="MaxKeys"/> keys (strings cut to <see cref="MaxKeyText"/>, least recently seen evicted); a row
/// counts for at most <see cref="Episode"/>; at most <see cref="MaxNewRowsPerWindow"/> new rows per
/// <see cref="Window"/>, the overflow counted in one row per process (same allowance) and, past that, only
/// in <see cref="SuppressedCount"/>. Locks: this lock is never held across a store call, and a row the
/// store no longer holds is replaced, so a trimmed or cleared row is never updated unseen.
/// </remarks>
internal sealed class TlsFailureAggregator(SessionStore store, TimeProvider? time = null)
{
    internal const int MaxKeys = 256;
    internal const int MaxNewRowsPerWindow = 20;
    internal const int MaxKeyText = 255;
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan Episode = TimeSpan.FromMinutes(10);

    /// <summary>A row is announced to the grid at most this often.</summary>
    internal static readonly TimeSpan NotifyInterval = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<Key, Entry> _entries = new(KeyComparer.Instance);
    private long _windowStart;
    private bool _windowOpen;
    private int _newRows;
    private int _newOverflowRows;
    private long _suppressed;
    private CancellationTokenSource _stop = new();

    /// <summary>Keys currently counted.</summary>
    internal int KeyCount
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Failures that fitted in no row: past the new-row limit and past the overflow rows' too.</summary>
    internal long SuppressedCount
    {
        get { lock (_gate) return _suppressed; }
    }

    /// <summary>Counts a failure against its row, adding the row when there is none that is current.</summary>
    /// <param name="createRow">Builds the row for a first failure. Not called when it is counted into one.</param>
    public void Record(string process, string host, int port, TlsFailureInfo info, Func<Session> createRow)
    {
        var key = new Key(Cut(process), NormalizeHost(host), port, info.Kind, Overflow: false);
        var overflowKey = new Key(key.Process, string.Empty, 0, TlsFailureKind.Other, Overflow: true);

        // A round that finds its row gone from the store drops the entry and goes again, to a fresh row.
        for (var round = 0; round < 3; round++)
        {
            Entry? counted = null;
            Entry? fresh = null;
            var announce = false;
            var schedule = false;
            var added = false;

            lock (_gate)
            {
                var now = _time.GetTimestamp();
                if (TryCount(key, now, out counted, out announce, out schedule, out added)) { }
                else if (TryReserve(now, overflow: false)) fresh = Insert(key, createRow(), now);
                else if (TryCount(overflowKey, now, out counted, out announce, out schedule, out added)) { }
                else if (TryReserve(now, overflow: true)) fresh = Insert(overflowKey, CreateOverflowRow(process, info), now);
                else
                {
                    _suppressed++;
                    return;
                }
            }

            if (counted is { Declined: true }) return; // the store would not take its row: counted, nothing else

            if (counted is not null)
            {
                if (!added || store.IsRetained(counted.Row))
                {
                    if (announce) store.NotifyUpdated(counted.Row);
                    else if (schedule) _ = AnnounceLaterAsync(counted);
                    return;
                }

                lock (_gate)
                {
                    if (_entries.TryGetValue(counted.Key, out var current) && ReferenceEquals(current, counted))
                        _entries.Remove(counted.Key);
                }
                continue;
            }

            // Not admitted (capture scope, filterset) is told by the store not holding the row. Such a key must not
            // take the allowance again with every retry, or one excluded client would starve the in-scope ones.
            var admitted = true;
            try
            {
                store.Add(fresh!.Row);
                admitted = store.IsRetained(fresh.Row);
            }
            finally
            {
                lock (_gate)
                {
                    fresh!.Added = true;
                    if (!admitted) Decline(fresh);
                    // Failures counted while the row was being added were not announced.
                    schedule = fresh.Attempts > 1 && !fresh.AnnouncePending;
                    if (schedule) fresh.AnnouncePending = true;
                }
            }

            if (schedule) _ = AnnounceLaterAsync(fresh);
            return;
        }
    }

    /// <summary>Counts into the entry for the key if its episode is current. Lock held.</summary>
    private bool TryCount(Key key, long now, out Entry? entry, out bool announce, out bool schedule, out bool added)
    {
        announce = schedule = added = false;
        if (!_entries.TryGetValue(key, out entry)) return false;
        // A declined key is asked again once per window: the scope or filter may have changed since.
        if (_time.GetElapsedTime(entry.FirstTicks, now) >= (entry.Declined ? Window : Episode))
        {
            _entries.Remove(key);
            entry = null;
            return false;
        }

        entry.Attempts++;
        entry.LastTicks = now;
        if (entry.Declined) return true;

        // Replaced, never edited: the grid reads the record from another thread.
        entry.Row.TlsFailure = entry.Row.TlsFailure! with { Attempts = entry.Attempts, LastSeen = _time.GetLocalNow() };
        added = entry.Added;
        if (_time.GetElapsedTime(entry.AnnouncedTicks, now) >= NotifyInterval)
        {
            entry.AnnouncedTicks = now;
            announce = true;
        }
        else if (!entry.AnnouncePending)
        {
            entry.AnnouncePending = true;
            schedule = true;
        }
        return true;
    }

    /// <summary>Takes one of the window's allowances for a new row. Lock held.</summary>
    private bool TryReserve(long now, bool overflow)
    {
        if (!_windowOpen || _time.GetElapsedTime(_windowStart, now) >= Window)
        {
            _windowStart = now;
            _windowOpen = true;
            _newRows = 0;
            _newOverflowRows = 0;
        }

        ref var taken = ref (overflow ? ref _newOverflowRows : ref _newRows);
        if (taken >= MaxNewRowsPerWindow) return false;
        taken++;
        return true;
    }

    /// <summary>Marks the key's row as refused by the store for one window and gives back the allowance it
    /// took, if that window is still the current one. Lock held.</summary>
    private void Decline(Entry entry)
    {
        entry.Declined = true;
        if (!_windowOpen || entry.WindowStart != _windowStart) return;
        if (entry.Key.Overflow) _newOverflowRows--;
        else _newRows--;
    }

    private Entry Insert(Key key, Session row, long now)
    {
        if (_entries.Count >= MaxKeys) EvictLeastRecentlySeen();
        var entry = new Entry(key, row, now) { WindowStart = _windowStart };
        _entries[key] = entry;
        return entry;
    }

    private void EvictLeastRecentlySeen()
    {
        Key? oldest = null;
        var oldestTicks = long.MaxValue;
        foreach (var (key, entry) in _entries)
        {
            if (entry.LastTicks >= oldestTicks) continue;
            oldest = key;
            oldestTicks = entry.LastTicks;
        }
        if (oldest is { } evicted) _entries.Remove(evicted);
    }

    private Session CreateOverflowRow(string process, TlsFailureInfo info)
    {
        var now = _time.GetLocalNow();
        return new Session(now)
        {
            Request = new HttpRequestData { Method = "CONNECT", RequestTarget = "(more TLS failures)" },
            IsTunnel = true,
            IsHttps = true,
            State = SessionState.Failed,
            ProcessName = Cut(process),
            Error = "More TLS handshake failures from this process than get rows of their own; they are counted here",
            TlsFailure = new TlsFailureInfo(TlsFailureKind.Other, info.Phase) { IsOverflow = true, LastSeen = now },
            Completed = now,
        };
    }

    /// <summary>Ends the pending trailing announcements, announcing each of those rows now, so that nothing
    /// is raised after the proxy has stopped and the grid still ends on the final counts.</summary>
    public void Stop()
    {
        Entry[] pending;
        CancellationTokenSource ended;
        lock (_gate)
        {
            ended = _stop;
            _stop = new CancellationTokenSource();
            pending = [.. _entries.Values.Where(e => e.AnnouncePending)];
            foreach (var entry in pending) entry.AnnouncePending = false;
        }
        ended.Cancel();
        ended.Dispose();
        foreach (var entry in pending)
            if (store.IsRetained(entry.Row)) store.NotifyUpdated(entry.Row);
    }

    /// <summary>The trailing announcement, so the count the grid ends on is the final one.</summary>
    private async Task AnnounceLaterAsync(Entry entry)
    {
        CancellationToken stop;
        lock (_gate) stop = _stop.Token;
        try { await Task.Delay(NotifyInterval, _time, stop).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; } // Stop announced it
        lock (_gate)
        {
            entry.AnnouncePending = false;
            entry.AnnouncedTicks = _time.GetTimestamp();
        }
        if (store.IsRetained(entry.Row)) store.NotifyUpdated(entry.Row);
    }

    private static string Cut(string text) => text.Length > MaxKeyText ? text[..MaxKeyText] : text;

    /// <summary>Until the CONNECT authority has one parser: cut, without a trailing dot (comparison ignores case).</summary>
    private static string NormalizeHost(string host) => Cut(host).TrimEnd('.');

    private readonly record struct Key(string Process, string Host, int Port, TlsFailureKind Kind, bool Overflow);

    private sealed class KeyComparer : IEqualityComparer<Key>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals(Key a, Key b) =>
            a.Port == b.Port && a.Kind == b.Kind && a.Overflow == b.Overflow
            && string.Equals(a.Process, b.Process, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(Key key) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.Process), StringComparer.OrdinalIgnoreCase.GetHashCode(key.Host),
            key.Port, key.Kind, key.Overflow);
    }

    private sealed class Entry(Key key, Session row, long now)
    {
        public Key Key { get; } = key;
        public Session Row { get; } = row;
        public long FirstTicks { get; } = now;
        public long LastTicks { get; set; } = now;
        public long AnnouncedTicks { get; set; } = now;
        public int Attempts { get; set; } = 1;
        public long WindowStart { get; set; }
        public bool Added { get; set; }
        public bool Declined { get; set; }
        public bool AnnouncePending { get; set; }
    }
}
