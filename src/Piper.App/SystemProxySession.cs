namespace Piper.App;

/// <summary>
/// The machine's proxy settings, as Piper reads and writes them. <c>SystemProxy.Settings</c>
/// is the real registry and WinINET; tests supply a fake so they never change the machine's proxy.
/// </summary>
public interface ISystemProxySettings
{
    SystemProxy.Snapshot Read();

    /// <summary>Points the machine at <paramref name="endpoint"/>.</summary>
    void Enable(string endpoint);

    /// <summary>Puts back exactly what <see cref="Read"/> returned.</summary>
    void Restore(SystemProxy.Snapshot snapshot);
}

/// <summary>
/// Enabling the system proxy failed. <see cref="Exception.InnerException"/> is why; when Piper could
/// not undo its partial change either, <see cref="UndoFailure"/> says why, and the undo record is
/// kept so closing Piper or the next launch tries again.
/// </summary>
public sealed class SystemProxyEnableException(Exception failure, Exception? undoFailure)
    : Exception(failure.Message, failure)
{
    public Exception? UndoFailure { get; } = undoFailure;
}

/// <summary>
/// Piper's hold on the system proxy for one window: the settings it found, the undo record on disk,
/// and the one lock every change goes through.
/// </summary>
/// <remarks>
/// The calls block on the registry and WinINET, so the window runs them on a worker thread. The lock
/// keeps two changes from interleaving - a stop that arrives while a start is still writing waits for
/// it and then undoes it, rather than reading a half-written state - and it is a plain monitor, never
/// released by a UI continuation, so the blocking shutdown path can take it from the UI thread
/// without deadlocking.
/// </remarks>
public sealed class SystemProxySession(ISystemProxySettings settings, string? backupPath = null)
{
    private readonly object _gate = new();
    private SystemProxy.Snapshot? _snapshot;

    /// <summary>True while Piper has the machine pointed at itself and owes it a restore.</summary>
    public bool IsHeld
    {
        get
        {
            lock (_gate) return _snapshot is not null;
        }
    }

    /// <summary>
    /// Records the current settings, writes the undo record, then points the machine at
    /// <paramref name="endpoint"/>. Returns false, changing nothing, when Piper already holds it:
    /// a second capture would record Piper's own endpoint as the user's settings.
    /// </summary>
    /// <exception cref="SystemProxyEnableException">The registry change failed.</exception>
    public bool Enable(string endpoint)
    {
        lock (_gate)
        {
            if (_snapshot is not null) return false;

            // Written to disk before the registry changes so an abrupt exit still leaves something
            // to undo. If either throws, nothing has been changed yet.
            var snapshot = settings.Read().ForBackupBefore(endpoint);
            SystemProxyBackupStore.Save(SystemProxyBackup.From(endpoint, snapshot), backupPath);
            _snapshot = snapshot;

            try
            {
                settings.Enable(endpoint);
                return true;
            }
            catch (Exception failure)
            {
                // Enable writes several values, so put back what was captured rather than leaving
                // the machine half-pointed at a proxy.
                Exception? undoFailure = null;
                try { RestoreHeld(); }
                catch (Exception ex) { undoFailure = ex; }

                throw new SystemProxyEnableException(failure, undoFailure);
            }
        }
    }

    /// <summary>
    /// Puts back the settings Piper found and drops the undo record. Returns false when Piper was
    /// not holding the proxy. On failure both the settings and the record are kept, so a retry (or
    /// the next launch) can still restore them.
    /// </summary>
    public bool Restore()
    {
        lock (_gate)
        {
            if (_snapshot is null) return false;
            RestoreHeld();
            return true;
        }
    }

    /// <summary>
    /// Undoes what an earlier run left behind (see <see cref="SystemProxyRecovery"/>), under the
    /// same lock as every other change. Does nothing while this session holds the proxy: the
    /// record on disk is then its own.
    /// </summary>
    public LeftoverResult RecoverLeftovers(bool setAsideCorrupt)
    {
        lock (_gate)
        {
            return _snapshot is not null
                ? new LeftoverResult(LeftoverOutcome.Nothing)
                : SystemProxyRecovery.RestoreLeftovers(settings, backupPath, setAsideCorrupt);
        }
    }

    private void RestoreHeld()
    {
        settings.Restore(_snapshot!);
        _snapshot = null;
        SystemProxyBackupStore.Clear(backupPath);
    }
}

public enum LeftoverOutcome
{
    /// <summary>No undo record: nothing was left behind.</summary>
    Nothing,

    /// <summary>A previous run's change was undone.</summary>
    Restored,

    /// <summary>A record was there, but the machine no longer points at Piper, so it was dropped untouched.</summary>
    Stale,

    /// <summary>The record is damaged. Nothing was changed: guessing at the user's settings is worse than asking.</summary>
    Corrupt,

    /// <summary>The record could not be read right now. Nothing was changed, and it is kept for next time.</summary>
    Unreadable,

    /// <summary>The record was good but writing it back failed. It is kept for next time.</summary>
    RestoreFailed,
}

/// <param name="Endpoint">The endpoint the record says Piper had installed, when it could be read.</param>
/// <param name="BackupStatus">Why a <see cref="LeftoverOutcome.Corrupt"/> record could not be used.</param>
/// <param name="Detail">A system error message, for the log.</param>
/// <param name="SetAsidePath">Where a damaged record was moved, or null if it was left in place.</param>
public sealed record LeftoverResult(
    LeftoverOutcome Outcome,
    string? Endpoint = null,
    SystemProxyBackupStatus BackupStatus = SystemProxyBackupStatus.Missing,
    string? Detail = null,
    string? SetAsidePath = null);

/// <summary>
/// Undoes a proxy left behind by a run that ended without restoring - a crash, a kill from Task
/// Manager, or a Windows shutdown.
/// </summary>
public static class SystemProxyRecovery
{
    /// <summary>
    /// Never throws: it runs at startup and on the way out of the process, where there is nothing
    /// above it to catch. Every outcome a user needs to hear about is in the result instead.
    /// </summary>
    /// <param name="setAsideCorrupt">
    /// Move a damaged record aside once read. Only the startup path does this, because it is the one
    /// that tells the user; the exit paths leave it for the next launch to report.
    /// </param>
    public static LeftoverResult RestoreLeftovers(
        ISystemProxySettings settings, string? backupPath = null, bool setAsideCorrupt = false)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var loaded = SystemProxyBackupStore.Load(backupPath);
        if (loaded.Status == SystemProxyBackupStatus.Missing) return new LeftoverResult(LeftoverOutcome.Nothing);

        if (loaded.Status == SystemProxyBackupStatus.Unreadable)
            return new LeftoverResult(LeftoverOutcome.Unreadable, BackupStatus: loaded.Status, Detail: loaded.Detail);

        if (loaded.IsCorrupt)
        {
            return new LeftoverResult(LeftoverOutcome.Corrupt, BackupStatus: loaded.Status, Detail: loaded.Detail,
                SetAsidePath: setAsideCorrupt ? SystemProxyBackupStore.SetAside(backupPath) : null);
        }

        var backup = loaded.Backup!;
        try
        {
            // Only undo Piper's own leftovers. If the machine points somewhere else the user has
            // since changed it themselves, and putting the recorded settings back would undo that.
            if (!settings.Read().PointsAt(backup.AppliedEndpoint))
            {
                SystemProxyBackupStore.Clear(backupPath);
                return new LeftoverResult(LeftoverOutcome.Stale, backup.AppliedEndpoint);
            }

            settings.Restore(backup.ToSnapshot());
        }
        catch (Exception ex)
        {
            // Kept on disk: the record is still good, and the next launch (or this process's exit
            // hook) tries again instead of the user being left on a dead proxy for good.
            return new LeftoverResult(LeftoverOutcome.RestoreFailed, backup.AppliedEndpoint, Detail: ex.Message);
        }

        SystemProxyBackupStore.Clear(backupPath);
        return new LeftoverResult(LeftoverOutcome.Restored, backup.AppliedEndpoint);
    }
}
