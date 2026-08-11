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
    private readonly Lock _gate = new();

    /// <summary>Oldest sessions are dropped once the cap is hit. 0 disables trimming.</summary>
    public int Capacity { get; set; } = 20_000;

    public event EventHandler<SessionEventArgs>? SessionAdded;
    public event EventHandler<SessionEventArgs>? SessionUpdated;
    public event EventHandler? Cleared;

    public int Count
    {
        get
        {
            lock (_gate) return _sessions.Count;
        }
    }

    public void Add(Session session)
    {
        lock (_gate)
        {
            _sessions.Add(session);
            if (Capacity > 0 && _sessions.Count > Capacity)
                _sessions.RemoveRange(0, _sessions.Count - Capacity);
        }
        SessionAdded?.Invoke(this, new SessionEventArgs(session));
    }

    /// <summary>Signals that an already-added session has changed (response arrived, failed, etc.).</summary>
    public void NotifyUpdated(Session session) => SessionUpdated?.Invoke(this, new SessionEventArgs(session));

    public Session[] Snapshot()
    {
        lock (_gate) return _sessions.ToArray();
    }

    public Session? FindById(int id)
    {
        lock (_gate)
        {
            for (var i = _sessions.Count - 1; i >= 0; i--)
                if (_sessions[i].Id == id) return _sessions[i];
        }
        return null;
    }

    public void Clear()
    {
        lock (_gate) _sessions.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveAll(Func<Session, bool> predicate)
    {
        lock (_gate) _sessions.RemoveAll(s => predicate(s));
        Cleared?.Invoke(this, EventArgs.Empty);
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
