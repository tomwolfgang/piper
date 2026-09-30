namespace Piper.Core.Sessions;

public sealed class SessionEventArgs(Session session) : EventArgs
{
    public Session Session { get; } = session;
}

/// <summary>
/// Thread-safe append-only log of captured sessions. Proxy threads write; the UI thread
/// snapshots. Reads take a copy rather than exposing the live list, so the UI can filter
/// and sort without holding the lock.
/// </summary>
public sealed class SessionStore
{
    private readonly List<Session> _sessions = new(1024);
    private readonly HashSet<Session> _pendingAdmission = [];
    private readonly Lock _gate = new();
    private Func<Session, bool>? _captureFilter;
    private Func<Session, bool>? _completedSessionFilter;
    private int _firstSession;
    private long _retainedBodyBytes;

    // What each retained session was last counted at. A proxied session is admitted before its
    // response exists, so its body has to be counted again whenever it is updated.
    private readonly Dictionary<Session, long> _countedBodyBytes = new(ReferenceEqualityComparer.Instance);

    /// <summary>Oldest sessions are dropped once the cap is hit. 0 disables trimming.</summary>
    public int Capacity { get; set; } = 20_000;

    /// <summary>
    /// How many bytes of captured bodies to keep across all retained sessions. Once past it, the
    /// oldest sessions give up their bodies. 0 disables the budget.
    /// </summary>
    /// <remarks>
    /// A count of sessions is not a bound on memory: twenty thousand sessions is nothing if they
    /// are API calls and several gigabytes if they are downloads. Only the bodies are released --
    /// the sessions stay, keeping their URL, status, timings and the length they weighed on the
    /// wire, because what a capture is mostly used for is seeing that a request happened at all.
    /// </remarks>
    public long RetainedBodyBudgetBytes { get; set; } = 512L * 1024 * 1024;

    public event EventHandler<SessionEventArgs>? SessionAdded;
    public event EventHandler<SessionEventArgs>? SessionUpdated;
    public event EventHandler? Cleared;

    /// <summary>
    /// Rejects sessions before they enter the store. Used for immediately-known scope such as
    /// process type, so out-of-scope traffic is neither counted nor retained.
    /// </summary>
    public Func<Session, bool>? CaptureFilter
    {
        get => Volatile.Read(ref _captureFilter);
        set => Volatile.Write(ref _captureFilter, value);
    }

    /// <summary>
    /// Optional admission filter evaluated once a response has completed. Response status, body,
    /// and content type are unavailable when a request begins, so sessions are kept outside the
    /// visible store until this predicate can be evaluated accurately.
    /// </summary>
    public Func<Session, bool>? CompletedSessionFilter
    {
        get => Volatile.Read(ref _completedSessionFilter);
        set => Volatile.Write(ref _completedSessionFilter, value);
    }

    public int Count
    {
        get
        {
            lock (_gate) return _sessions.Count - _firstSession;
        }
    }

    public void Add(Session session)
    {
        if (Admit(session)) AddAccepted(session);
    }

    /// <summary>
    /// Adds sessions that pass admission as one batch and returns how many of them are now in the
    /// store, so a caller reporting "imported N" reports what the user can actually see.
    /// </summary>
    /// <remarks>
    /// One pass over the lock, one capacity trim and one body-budget release for the whole batch:
    /// adding a large archive session by session would rescan every already-released body from the
    /// front of the list for each one. A session deferred until its response completes is not in the
    /// store yet and is not counted. A batch larger than <see cref="Capacity"/> keeps its newest
    /// sessions, and only those are counted.
    /// </remarks>
    public int AddRange(IReadOnlyCollection<Session> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var accepted = new List<Session>(sessions.Count);
        foreach (var session in sessions)
            if (Admit(session)) accepted.Add(session);
        if (accepted.Count == 0) return 0;

        int capacity;
        lock (_gate)
        {
            foreach (var session in accepted)
            {
                _sessions.Add(session);
                Recount(session);
            }
            capacity = Capacity;
            TrimToCapacity();
            ReleaseOldestBodiesIfOverBudget();
        }

        // Trimming drops the oldest first, so what survived is the end of the batch.
        var retained = capacity > 0 ? Math.Min(accepted.Count, capacity) : accepted.Count;
        for (var i = accepted.Count - retained; i < accepted.Count; i++)
            SessionAdded?.Invoke(this, new SessionEventArgs(accepted[i]));
        return retained;
    }

    /// <summary>
    /// Deliberate user actions are not intercepted traffic, so neither the capture scope nor the
    /// response filter applies to them: a Composer send is visible as soon as Send is pressed, and
    /// a session the user opened from a capture file is never quietly dropped.
    /// </summary>
    private static bool BypassesAdmission(Session session) => session.IsComposed || session.IsImported;

    /// <summary>
    /// Applies the admission filters. True when the session belongs in the store now; a session whose
    /// response is still to come is parked until <see cref="NotifyUpdated"/> and answers false.
    /// </summary>
    private bool Admit(Session session)
    {
        if (BypassesAdmission(session)) return true;
        if (CaptureFilter is { } captureFilter && !captureFilter(session)) return false;

        if (CompletedSessionFilter is not null && session.Completed is null)
        {
            lock (_gate) _pendingAdmission.Add(session);
            return false;
        }

        return CompletedSessionFilter is not { } completedFilter || completedFilter(session);
    }

    private void AddAccepted(Session session)
    {
        lock (_gate)
        {
            _sessions.Add(session);
            Recount(session);
            TrimToCapacity();
            ReleaseOldestBodiesIfOverBudget();
        }
        SessionAdded?.Invoke(this, new SessionEventArgs(session));
    }

    private void TrimToCapacity()
    {
        if (Capacity <= 0) return;
        var discardCount = _sessions.Count - _firstSession - Capacity;
        if (discardCount <= 0) return;

        // Do not shift the whole retained list for every new session once the cap is
        // reached. Clear discarded references immediately, then compact the prefix in
        // one amortized operation after enough additions have accumulated.
        var discardEnd = _firstSession + discardCount;
        for (var i = _firstSession; i < discardEnd; i++)
        {
            Uncount(_sessions[i]);
            _sessions[i] = null!;
        }
        _firstSession = discardEnd;
        CompactDiscardedPrefixIfNeeded();
    }

    /// <summary>Signals that a session has changed, admitting deferred sessions on completion.</summary>
    public void NotifyUpdated(Session session)
    {
        var wasDeferred = false;
        lock (_gate)
        {
            if (_pendingAdmission.Contains(session) && session.Completed is not null)
            {
                _pendingAdmission.Remove(session);
                wasDeferred = true;
            }
            else if (_countedBodyBytes.ContainsKey(session))
            {
                Recount(session);
                ReleaseOldestBodiesIfOverBudget();
            }
        }

        if (wasDeferred)
        {
            if (!BypassesAdmission(session) && CaptureFilter is { } captureFilter && !captureFilter(session)) return;
            if (!BypassesAdmission(session) && CompletedSessionFilter is { } completedFilter && !completedFilter(session)) return;
            AddAccepted(session);
            return;
        }

        SessionUpdated?.Invoke(this, new SessionEventArgs(session));
    }

    public Session[] Snapshot()
    {
        lock (_gate)
        {
            var result = new Session[_sessions.Count - _firstSession];
            _sessions.CopyTo(_firstSession, result, 0, result.Length);
            return result;
        }
    }

    /// <summary>Copies the retained sessions into a caller-owned reusable buffer.</summary>
    public void CopyTo(List<Session> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        lock (_gate)
        {
            destination.Clear();
            destination.EnsureCapacity(_sessions.Count - _firstSession);
            for (var i = _firstSession; i < _sessions.Count; i++)
                destination.Add(_sessions[i]);
        }
    }

    public Session? FindById(int id)
    {
        lock (_gate)
        {
            for (var i = _sessions.Count - 1; i >= _firstSession; i--)
                if (_sessions[i].Id == id) return _sessions[i];
        }
        return null;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _sessions.Clear();
            _firstSession = 0;
            _retainedBodyBytes = 0;
            _countedBodyBytes.Clear();
            _pendingAdmission.Clear();
        }
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveAll(Func<Session, bool> predicate)
    {
        lock (_gate)
        {
            CompactDiscardedPrefix();
            _sessions.RemoveAll(s =>
            {
                if (!predicate(s)) return false;
                Uncount(s);
                return true;
            });
        }
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Releases the bodies of the oldest sessions until the total retained is within budget.
    /// </summary>
    /// <remarks>
    /// Oldest first, because the session someone is about to look at is almost always a recent one.
    /// A session whose body has been released keeps reporting the length it weighed on the wire,
    /// so nothing starts claiming a large download was empty.
    /// </remarks>
    private void ReleaseOldestBodiesIfOverBudget()
    {
        if (RetainedBodyBudgetBytes <= 0 || _retainedBodyBytes <= RetainedBodyBudgetBytes) return;

        for (var i = _firstSession; i < _sessions.Count && _retainedBodyBytes > RetainedBodyBudgetBytes; i++)
        {
            var older = _sessions[i];
            if (_countedBodyBytes.GetValueOrDefault(older) == 0) continue;

            if (older.Request is { } request) request.ReleaseBody();
            if (older.Response is { } response) response.ReleaseBody();
            older.InvalidateSearchIndex();
            Recount(older);
        }
    }

    private void Recount(Session session)
    {
        var now = BodyBytesOf(session);
        _retainedBodyBytes += now - _countedBodyBytes.GetValueOrDefault(session);
        _countedBodyBytes[session] = now;
    }

    private void Uncount(Session session)
    {
        if (_countedBodyBytes.Remove(session, out var counted)) _retainedBodyBytes -= counted;
    }

    private static long BodyBytesOf(Session session) =>
        (session.Request?.Body.LongLength ?? 0) + (session.Response?.Body.LongLength ?? 0);

    private void CompactDiscardedPrefixIfNeeded()
    {
        if (_firstSession >= 4_096 && _firstSession >= _sessions.Count / 2)
            CompactDiscardedPrefix();
    }

    private void CompactDiscardedPrefix()
    {
        if (_firstSession == 0) return;
        _sessions.RemoveRange(0, _firstSession);
        _firstSession = 0;
    }

    /// <summary>Applies a compiled query against a snapshot.</summary>
    public Session[] Search(SearchQuery query)
    {
        var all = Snapshot();
        if (query.IsEmpty) return all;

        var results = new List<Session>(Math.Min(all.Length, 256));
        foreach (var session in all)
            if (query.Matches(session))
                results.Add(session);
        return results.ToArray();
    }
}
