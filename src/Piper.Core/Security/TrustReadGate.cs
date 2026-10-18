namespace Piper.Core.Security;

/// <summary>
/// Lets one background trust read run at a time without a stuck read ever switching the refresh off.
/// A read that never comes back (a hung certificate store), or whose result could not be handed to the
/// UI because the window had no handle yet, is treated as abandoned after <c>abandonAfter</c>, and the
/// next tick starts a new one; the late result of the abandoned read can no longer clear the flag of
/// its successor. After a failed read the next one waits <c>retryAfterFailure</c>.
/// </summary>
/// <remarks>
/// An abandoned read still holds its worker thread until the store answers, so at most one abandoned
/// read may be outstanding: while it is, no further read is started, and a hung store costs two
/// threads, not one more every <c>abandonAfter</c> for the rest of the session.
/// </remarks>
public sealed class TrustReadGate(TimeSpan retryAfterFailure, TimeSpan abandonAfter)
{
    private readonly Lock _gate = new();
    private bool _inFlight;
    private int _abandoned;
    private long _startedAtMs;
    private long _retryAtMs;
    private int _id;

    /// <summary>The id of the read that may start now, or 0 when none may (one is running, a failure is still cooling down, or an abandoned read is still stuck).</summary>
    public int TryBegin(long nowMs)
    {
        lock (_gate)
        {
            if (nowMs < _retryAtMs) return 0;
            if (_inFlight)
            {
                if (nowMs - _startedAtMs < (long)abandonAfter.TotalMilliseconds) return 0;
                if (_abandoned >= 1) return 0;
                _abandoned++;
            }

            _inFlight = true;
            _startedAtMs = nowMs;
            return ++_id;
        }
    }

    /// <summary>The read <paramref name="id"/> came back. Only the current read clears the flag or schedules a retry.</summary>
    public void End(int id, bool failed, long nowMs)
    {
        lock (_gate)
        {
            if (id != _id)
            {
                Release(id);
                return;
            }

            _inFlight = false;
            if (failed) _retryAtMs = nowMs + (long)retryAfterFailure.TotalMilliseconds;
        }
    }

    /// <summary>The result of read <paramref name="id"/> could not be delivered; the next tick may try again at once.</summary>
    public void Abort(int id)
    {
        lock (_gate)
        {
            if (id == _id) _inFlight = false;
            else Release(id);
        }
    }

    // A stale id is an abandoned read finally returning: its thread is free again.
    private void Release(int id)
    {
        if (id < _id && _abandoned > 0) _abandoned--;
    }
}
