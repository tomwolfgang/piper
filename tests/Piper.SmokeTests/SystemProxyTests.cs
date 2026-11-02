using System.Text;
using System.Text.Json;
using Piper.App;

/// <summary>
/// The system proxy's undo record and the logic that restores it. Everything goes through
/// <see cref="FakeProxySettings"/> and a temporary directory: these tests must never change this
/// machine's real proxy settings or touch the real backup file.
/// </summary>
internal static class SystemProxyTests
{
    private const string Endpoint = "127.0.0.1:8888";

    /// <summary>A user's own corporate proxy, which every restore must put back exactly.</summary>
    private static readonly SystemProxy.Snapshot UserSettings =
        new(1, "corp-proxy:3128", "*.corp;<local>", null, null);

    public static Task RunBackupStoreAsync(TestRunner runner) => runner.RunAsync("system proxy backup store: atomic, bounded, damage reported", () =>
    {
        using var dir = new TempDir();
        var path = dir.File("system-proxy-backup.json");

        runner.AreEqual(SystemProxyBackupStatus.Missing, SystemProxyBackupStore.Load(path).Status, "no file is Missing");
        runner.AreEqual(SystemProxyBackupStatus.Missing,
            SystemProxyBackupStore.Load(Path.Combine(dir.Path, "absent", "backup.json")).Status, "no directory is Missing too");

        var original = SystemProxyBackup.From(Endpoint, UserSettings with { DefaultConnectionSettings = [1, 2, 3] });
        SystemProxyBackupStore.Save(original, path);
        var loaded = SystemProxyBackupStore.Load(path);
        runner.AreEqual(SystemProxyBackupStatus.Loaded, loaded.Status, "a saved backup loads");
        runner.AreEqual(Endpoint, loaded.Backup?.AppliedEndpoint, "endpoint round trips");
        runner.AreEqual("corp-proxy:3128", loaded.Backup?.ProxyServer, "proxy server round trips");
        runner.AreEqual("010203", Convert.ToHexString(loaded.Backup?.DefaultConnectionSettings ?? []), "connection blob round trips");

        // The process dying half way through a save must leave the previous record readable. With
        // File.WriteAllText the target itself is the half-written file.
        var replacement = SystemProxyBackup.From("127.0.0.1:9999", UserSettings);
        var crashed = false;
        try
        {
            SystemProxyBackupStore.Save(replacement, path, afterPartialWrite: () => throw new IOException("simulated crash"));
        }
        catch (IOException)
        {
            crashed = true;
        }

        runner.IsTrue(crashed, "the interrupted save reports its failure");
        var afterCrash = SystemProxyBackupStore.Load(path);
        runner.AreEqual(SystemProxyBackupStatus.Loaded, afterCrash.Status, "the record survives a save interrupted mid-write");
        runner.AreEqual(Endpoint, afterCrash.Backup?.AppliedEndpoint, "and is still the previous backup, not half of the new one");
        runner.AreEqual(0, Directory.GetFiles(dir.Path, "*.tmp").Length, "no temporary file is left behind");

        SystemProxyBackupStore.Save(replacement, path);
        runner.AreEqual("127.0.0.1:9999", SystemProxyBackupStore.Load(path).Backup?.AppliedEndpoint, "a completed save replaces the record");

        // Damaged files are reported as damaged, never as "no backup": that was the silent skip that
        // left a user on a dead proxy after a crash mid-write.
        var json = JsonSerializer.Serialize(original);
        Expect(path, json[..(json.Length / 2)], SystemProxyBackupStatus.Malformed, "a truncated file");
        Expect(path, "", SystemProxyBackupStatus.Malformed, "an empty file");
        Expect(path, "not json", SystemProxyBackupStatus.Malformed, "text that is not JSON");
        Expect(path, "[]", SystemProxyBackupStatus.Malformed, "a JSON array");
        Expect(path, """{"AppliedEndpoint":"127.0.0.1:8888","ProxyEnable":"yes"}""", SystemProxyBackupStatus.Malformed, "a field of the wrong type");
        Expect(path, """{"AppliedEndpoint":"127.0.0.1:8888","DefaultConnectionSettings":"%%%"}""", SystemProxyBackupStatus.Malformed, "a blob that is not base64");
        Expect(path, "null", SystemProxyBackupStatus.Incomplete, "JSON null");
        Expect(path, "{}", SystemProxyBackupStatus.Incomplete, "an object with no endpoint");
        Expect(path, """{"AppliedEndpoint":"   "}""", SystemProxyBackupStatus.Incomplete, "a blank endpoint");

        // Bounded read: exactly at the cap still loads, one byte over is refused unread.
        var valid = JsonSerializer.Serialize(original);
        Expect(path, valid + new string(' ', SystemProxyBackupStore.MaxBytes - valid.Length), SystemProxyBackupStatus.Loaded, "a file exactly at the size cap");
        Expect(path, valid + new string(' ', SystemProxyBackupStore.MaxBytes - valid.Length + 1), SystemProxyBackupStatus.TooLarge, "a file one byte over the cap");
        Expect(path, new string('x', 4 * SystemProxyBackupStore.MaxBytes), SystemProxyBackupStatus.TooLarge, "a much larger file");

        SystemProxyBackupStore.Save(original, path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var locked = SystemProxyBackupStore.Load(path);
            runner.AreEqual(SystemProxyBackupStatus.Unreadable, locked.Status, "a file another process has locked is Unreadable");
            runner.IsTrue(!string.IsNullOrEmpty(locked.Detail), "with the system's reason");
        }

        File.WriteAllText(path, "not json");
        runner.AreEqual(SystemProxyBackupStore.SetAsidePath(path), SystemProxyBackupStore.SetAside(path), "a damaged record can be set aside");
        runner.IsTrue(!File.Exists(path) && File.Exists(SystemProxyBackupStore.SetAsidePath(path)), "it is moved, not deleted");
        runner.AreEqual<string?>(null, SystemProxyBackupStore.SetAside(path), "setting aside a missing record reports that nothing moved");

        return Task.CompletedTask;

        void Expect(string target, string content, SystemProxyBackupStatus expected, string what)
        {
            File.WriteAllText(target, content, new UTF8Encoding(false));
            runner.AreEqual(expected, SystemProxyBackupStore.Load(target).Status, what);
        }
    });

    public static Task RunRecoveryAsync(TestRunner runner) => runner.RunAsync("system proxy leftovers are restored, or reported, never silently dropped", () =>
    {
        using var dir = new TempDir();
        var path = dir.File("system-proxy-backup.json");

        var settings = new FakeProxySettings(UserSettings);
        runner.AreEqual(LeftoverOutcome.Nothing, SystemProxyRecovery.RestoreLeftovers(settings, path).Outcome, "no record, nothing to do");

        // A previous run crashed holding the proxy: the record is there and the machine points at Piper.
        SystemProxyBackupStore.Save(SystemProxyBackup.From(Endpoint, UserSettings), path);
        settings.Enable(Endpoint);
        var restored = SystemProxyRecovery.RestoreLeftovers(settings, path);
        runner.AreEqual(LeftoverOutcome.Restored, restored.Outcome, "a leftover is restored");
        runner.AreEqual(Endpoint, restored.Endpoint, "naming the endpoint it undid");
        runner.IsTrue(Same(UserSettings, settings.Current), "the user's settings are back exactly");
        runner.IsTrue(!File.Exists(path), "and the record is deleted once they are");

        // The user has since pointed the machine elsewhere: their choice wins and the record goes.
        SystemProxyBackupStore.Save(SystemProxyBackup.From(Endpoint, UserSettings), path);
        var theirs = new SystemProxy.Snapshot(1, "other-proxy:8080", null, null, null);
        settings.Current = theirs;
        runner.AreEqual(LeftoverOutcome.Stale, SystemProxyRecovery.RestoreLeftovers(settings, path).Outcome, "a record that no longer applies is stale");
        runner.IsTrue(ReferenceEquals(theirs, settings.Current), "and the user's newer settings are left alone");
        runner.IsTrue(!File.Exists(path), "the stale record is dropped");

        // A damaged record: report it, change nothing, and keep it unless told to set it aside.
        settings.Enable(Endpoint);
        var pointedAtPiper = settings.Current;
        var restoresBefore = settings.Restores;
        File.WriteAllText(path, """{"AppliedEndpoint":"127.0.0.1:88""");
        var corrupt = SystemProxyRecovery.RestoreLeftovers(settings, path);
        runner.AreEqual(LeftoverOutcome.Corrupt, corrupt.Outcome, "a truncated record is reported as corrupt, not as nothing");
        runner.AreEqual(SystemProxyBackupStatus.Malformed, corrupt.BackupStatus, "with what is wrong with it");
        runner.AreEqual(restoresBefore, settings.Restores, "nothing is guessed back into the registry");
        runner.IsTrue(ReferenceEquals(pointedAtPiper, settings.Current), "the settings are untouched");
        runner.IsTrue(File.Exists(path), "the exit paths leave it for the next launch to report");

        var setAside = SystemProxyRecovery.RestoreLeftovers(settings, path, setAsideCorrupt: true);
        runner.AreEqual(LeftoverOutcome.Corrupt, setAside.Outcome, "startup reports it too");
        runner.AreEqual(SystemProxyBackupStore.SetAsidePath(path), setAside.SetAsidePath, "and says where it kept it");
        runner.IsTrue(!File.Exists(path) && File.Exists(SystemProxyBackupStore.SetAsidePath(path)), "so the next backup is not blocked by it");

        File.WriteAllText(path, new string(' ', SystemProxyBackupStore.MaxBytes + 1));
        var oversize = SystemProxyRecovery.RestoreLeftovers(settings, path);
        runner.AreEqual(LeftoverOutcome.Corrupt, oversize.Outcome, "an oversize record is corrupt");
        runner.AreEqual(SystemProxyBackupStatus.TooLarge, oversize.BackupStatus, "because it is too large");
        File.Delete(path);

        // A record that cannot be opened right now is kept for next time.
        SystemProxyBackupStore.Save(SystemProxyBackup.From(Endpoint, UserSettings), path);
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            runner.AreEqual(LeftoverOutcome.Unreadable, SystemProxyRecovery.RestoreLeftovers(settings, path, setAsideCorrupt: true).Outcome,
                "a locked record is reported as unreadable");
        }

        runner.IsTrue(File.Exists(path), "and is not set aside or deleted");

        // Writing the settings back fails: the good record must survive for the next attempt.
        settings.RestoreFailure = new UnauthorizedAccessException("registry denied");
        var failed = SystemProxyRecovery.RestoreLeftovers(settings, path);
        runner.AreEqual(LeftoverOutcome.RestoreFailed, failed.Outcome, "a failed write is reported");
        runner.AreEqual("registry denied", failed.Detail, "with the reason");
        runner.IsTrue(File.Exists(path), "and the record is kept for a retry (it used to be deleted in a finally)");

        settings.RestoreFailure = null;
        runner.AreEqual(LeftoverOutcome.Restored, SystemProxyRecovery.RestoreLeftovers(settings, path).Outcome, "the retry then restores it");
        runner.IsTrue(Same(UserSettings, settings.Current), "to the user's settings");

        return Task.CompletedTask;
    });

    public static Task RunSessionAsync(TestRunner runner) => runner.RunAsync("system proxy session: enable, restore, undo and re-entrancy", async () =>
    {
        using var dir = new TempDir();
        var path = dir.File("system-proxy-backup.json");
        var settings = new FakeProxySettings(UserSettings);
        var session = new SystemProxySession(settings, path);

        runner.IsTrue(!session.Restore(), "restoring before enabling does nothing");
        runner.IsTrue(session.Enable(Endpoint), "enable takes the proxy");
        runner.IsTrue(session.IsHeld, "and holds it");
        runner.IsTrue(settings.Current.PointsAt(Endpoint), "the machine points at Piper");
        var backup = SystemProxyBackupStore.Load(path);
        runner.AreEqual(Endpoint, backup.Backup?.AppliedEndpoint, "the undo record was written");
        runner.AreEqual("corp-proxy:3128", backup.Backup?.ProxyServer, "holding the user's own settings");

        // A second enable would capture Piper's own endpoint as the user's settings.
        runner.IsTrue(!session.Enable(Endpoint), "a second enable is refused");
        runner.AreEqual("corp-proxy:3128", SystemProxyBackupStore.Load(path).Backup?.ProxyServer, "and does not overwrite the record");
        runner.AreEqual(LeftoverOutcome.Nothing, session.RecoverLeftovers(setAsideCorrupt: true).Outcome,
            "leftover recovery leaves a record the session holds alone");
        runner.IsTrue(File.Exists(path), "so it is still on disk");

        runner.IsTrue(session.Restore(), "restore gives it back");
        runner.IsTrue(!session.IsHeld, "no longer held");
        runner.IsTrue(Same(UserSettings, settings.Current), "the user's settings are back");
        runner.IsTrue(!File.Exists(path), "the record is gone");
        runner.IsTrue(!session.Restore(), "restoring twice is a no-op");

        // Settings a previous Piper left on the same endpoint are not the user's.
        settings.Current = new SystemProxy.Snapshot(1, Endpoint, "<local>", null, null);
        session.Enable(Endpoint);
        runner.AreEqual(0, SystemProxyBackupStore.Load(path).Backup?.ProxyEnable, "a dead Piper proxy is recorded as a direct connection");
        session.Restore();
        runner.AreEqual(0, settings.Current.ProxyEnable, "so closing does not put a dead proxy back");

        // Enable fails half way: the partial change is undone and nothing is left held.
        settings.Current = UserSettings;
        settings.EnableFailure = new IOException("disk on fire");
        var thrown = Throws<SystemProxyEnableException>(() => session.Enable(Endpoint));
        runner.AreEqual("disk on fire", thrown?.InnerException?.Message, "the failure is reported");
        runner.AreEqual<Exception?>(null, thrown?.UndoFailure, "the partial change was undone");
        runner.IsTrue(Same(UserSettings, settings.Current), "the user's settings are back");
        runner.IsTrue(!session.IsHeld && !File.Exists(path), "nothing is held and the record is gone");

        // ... and when the undo fails as well, the record is kept so close or the next launch retries.
        settings.RestoreFailure = new UnauthorizedAccessException("denied");
        thrown = Throws<SystemProxyEnableException>(() => session.Enable(Endpoint));
        runner.AreEqual("denied", thrown?.UndoFailure?.Message, "the undo failure is reported too");
        runner.IsTrue(session.IsHeld && File.Exists(path), "the session still owes a restore and the record is kept");
        settings.EnableFailure = null;
        settings.RestoreFailure = null;
        runner.IsTrue(session.Restore(), "a later restore succeeds");
        runner.IsTrue(Same(UserSettings, settings.Current) && !File.Exists(path), "and cleans up");

        // No backup, no change: when the record cannot be written the registry is never touched.
        var blocked = new SystemProxySession(settings, dir.Path); // a directory, so the move fails
        var enablesBefore = settings.Enables;
        runner.IsTrue(Throws<Exception>(() => blocked.Enable(Endpoint)) is not null, "enable fails without a record");
        runner.AreEqual(enablesBefore, settings.Enables, "and never pointed the machine at Piper");
        runner.IsTrue(!blocked.IsHeld, "nor holds anything");

        // A stop arriving while a start is still writing (both off the UI thread) waits for it and
        // then undoes it, instead of finding nothing to restore and leaving the proxy set.
        using var enableEntered = new ManualResetEventSlim();
        using var releaseEnable = new ManualResetEventSlim();
        settings.OnEnable = () =>
        {
            enableEntered.Set();
            releaseEnable.Wait(TimeSpan.FromSeconds(10));
        };
        var enabling = Task.Run(() => session.Enable(Endpoint));
        runner.IsTrue(enableEntered.Wait(TimeSpan.FromSeconds(10)), "enable is in flight");
        var restoring = Task.Run(session.Restore);
        await Task.Delay(100);
        runner.IsTrue(!restoring.IsCompleted, "a concurrent restore waits for it");
        releaseEnable.Set();
        settings.OnEnable = null;
        runner.IsTrue(await enabling, "the enable completes");
        runner.IsTrue(await restoring, "then the restore undoes it");
        runner.IsTrue(Same(UserSettings, settings.Current) && !session.IsHeld && !File.Exists(path), "leaving the user's settings and no record");
    });

    private static T? Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
            return null;
        }
        catch (T ex)
        {
            return ex;
        }
    }

    private static bool Same(SystemProxy.Snapshot expected, SystemProxy.Snapshot actual) =>
        expected.ProxyEnable == actual.ProxyEnable
        && expected.ProxyServer == actual.ProxyServer
        && expected.ProxyOverride == actual.ProxyOverride
        && (expected.DefaultConnectionSettings ?? []).AsSpan().SequenceEqual(actual.DefaultConnectionSettings ?? [])
        && (expected.SavedLegacySettings ?? []).AsSpan().SequenceEqual(actual.SavedLegacySettings ?? []);

    /// <summary>Stands in for the registry and WinINET. Thread-safe enough for one writer at a time.</summary>
    private sealed class FakeProxySettings(SystemProxy.Snapshot initial) : ISystemProxySettings
    {
        public volatile SystemProxy.Snapshot Current = initial;
        public Exception? EnableFailure;
        public Exception? RestoreFailure;
        public Action? OnEnable;
        public int Enables;
        public int Restores;

        public SystemProxy.Snapshot Read() => Current;

        public void Enable(string endpoint)
        {
            Interlocked.Increment(ref Enables);
            OnEnable?.Invoke();

            // Like the real one, a failure can come after some values are already written.
            Current = Current with { ProxyEnable = 1, ProxyServer = endpoint, ProxyOverride = "<local>" };
            if (EnableFailure is { } failure) throw failure;
        }

        public void Restore(SystemProxy.Snapshot snapshot)
        {
            Interlocked.Increment(ref Restores);
            if (RestoreFailure is { } failure) throw failure;
            Current = snapshot;
        }
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"piper-system-proxy-{Guid.NewGuid():N}");

        public TempDir() => Directory.CreateDirectory(Path);

        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
        }
    }
}
