using System.Text.Json;

namespace Piper.App;

/// <summary>
/// What the system proxy looked like before Piper pointed it at itself, together with the
/// endpoint it was pointed at.
/// </summary>
public sealed class SystemProxyBackup
{
    /// <summary>The endpoint Piper installed, used to recognise its own leftovers later.</summary>
    public string AppliedEndpoint { get; set; } = string.Empty;

    public int? ProxyEnable { get; set; }

    public string? ProxyServer { get; set; }

    public string? ProxyOverride { get; set; }

    public byte[]? DefaultConnectionSettings { get; set; }

    public byte[]? SavedLegacySettings { get; set; }

    public static SystemProxyBackup From(string endpoint, SystemProxy.Snapshot snapshot) => new()
    {
        AppliedEndpoint = endpoint,
        ProxyEnable = snapshot.ProxyEnable,
        ProxyServer = snapshot.ProxyServer,
        ProxyOverride = snapshot.ProxyOverride,
        DefaultConnectionSettings = snapshot.DefaultConnectionSettings,
        SavedLegacySettings = snapshot.SavedLegacySettings,
    };

    public SystemProxy.Snapshot ToSnapshot() =>
        new(ProxyEnable, ProxyServer, ProxyOverride, DefaultConnectionSettings, SavedLegacySettings);
}

/// <summary>What reading the undo record found.</summary>
public enum SystemProxyBackupStatus
{
    /// <summary>There is no record: Piper is not holding the system proxy.</summary>
    Missing,

    Loaded,

    /// <summary>The file is not valid JSON for a backup (for example cut off by a crash mid-write).</summary>
    Malformed,

    /// <summary>The file is bigger than any backup Piper writes, so it is not read at all.</summary>
    TooLarge,

    /// <summary>The JSON parses but lacks the endpoint needed to recognise Piper's own leftovers.</summary>
    Incomplete,

    /// <summary>The file exists but could not be opened or read (locked, or access denied).</summary>
    Unreadable,
}

public sealed record SystemProxyBackupLoad(
    SystemProxyBackupStatus Status, SystemProxyBackup? Backup = null, string? Detail = null)
{
    /// <summary>The file is there but its content can never be used, as opposed to merely unreadable right now.</summary>
    public bool IsCorrupt => Status is SystemProxyBackupStatus.Malformed
        or SystemProxyBackupStatus.TooLarge or SystemProxyBackupStatus.Incomplete;
}

/// <summary>
/// Keeps the undo record on disk for as long as Piper is holding the system proxy.
/// </summary>
/// <remarks>
/// Unlike the other settings stores this is not convenience state: it is the only way back if the
/// process never gets to run its shutdown path, so it is written before the registry is touched
/// and deleted only once the settings have actually been put back. A stale file is harmless -
/// startup checks whether the machine still points at the recorded endpoint before using it. For
/// the same reason a damaged file is never treated as "no backup": <see cref="Load"/> says what is
/// wrong with it so the caller can tell the user instead of silently leaving them without a
/// connection.
/// </remarks>
public static class SystemProxyBackupStore
{
    /// <summary>
    /// Far above a real backup (two short strings and two connection blobs of a few hundred bytes),
    /// and the most that is ever read: anything bigger is not something Piper wrote.
    /// </summary>
    public const int MaxBytes = 64 * 1024;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "system-proxy-backup.json");

    /// <summary>Where <see cref="SetAside"/> moves a damaged record, so it never blocks a later backup.</summary>
    public static string SetAsidePath(string path) => path + ".corrupt";

    public static void Save(SystemProxyBackup backup, string? path = null) => Save(backup, path, afterPartialWrite: null);

    /// <summary>
    /// Writes the record atomically: to a temporary file beside it, flushed to disk, then moved over
    /// the old one, so a crash or power loss at any point leaves either the previous record or the
    /// new one - never a truncated file the next launch cannot read.
    /// </summary>
    /// <param name="afterPartialWrite">
    /// Test hook, run once half of the bytes have been written and flushed, to stand in for the
    /// process dying mid-write.
    /// </param>
    internal static void Save(SystemProxyBackup backup, string? path, Action? afterPartialWrite)
    {
        ArgumentNullException.ThrowIfNull(backup);
        path ??= DefaultPath;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);

        // Deliberately not swallowing IO errors the way the other stores do: without this file a
        // crash would strand the user without a working connection, so the caller needs to know.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(backup);
        var temp = Path.Combine(directory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var half = bytes.Length / 2;
                stream.Write(bytes, 0, half);
                if (afterPartialWrite is not null)
                {
                    stream.Flush(flushToDisk: true);
                    afterPartialWrite();
                }

                stream.Write(bytes, half, bytes.Length - half);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // The temporary file is never the record; take it away and let the failure reach the
            // caller, which must not change the registry without a backup in place.
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Reads the record. Never throws: every way the file can be missing, damaged or unreadable is
    /// reported in the result, because this runs on startup and exit paths with nothing above it.
    /// </summary>
    public static SystemProxyBackupLoad Load(string? path = null)
    {
        path ??= DefaultPath;

        byte[] buffer;
        int length;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            // Read at most one byte past the limit rather than trusting Length, so a file that grows
            // while it is read still cannot make this allocate more than the cap.
            buffer = new byte[MaxBytes + 1];
            length = 0;
            int read;
            while (length < buffer.Length && (read = stream.Read(buffer, length, buffer.Length - length)) > 0)
                length += read;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SystemProxyBackupLoad(SystemProxyBackupStatus.Missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new SystemProxyBackupLoad(SystemProxyBackupStatus.Unreadable, Detail: ex.Message);
        }

        if (length > MaxBytes) return new SystemProxyBackupLoad(SystemProxyBackupStatus.TooLarge);

        SystemProxyBackup? backup;
        try
        {
            backup = JsonSerializer.Deserialize<SystemProxyBackup>(buffer.AsSpan(0, length));
        }
        catch (JsonException ex)
        {
            return new SystemProxyBackupLoad(SystemProxyBackupStatus.Malformed, Detail: ex.Message);
        }

        return backup is null || string.IsNullOrWhiteSpace(backup.AppliedEndpoint)
            ? new SystemProxyBackupLoad(SystemProxyBackupStatus.Incomplete)
            : new SystemProxyBackupLoad(SystemProxyBackupStatus.Loaded, backup);
    }

    public static void Clear(string? path = null) => TryDelete(path ?? DefaultPath);

    /// <summary>
    /// Moves a damaged record out of the way once the user has been told about it, keeping it for
    /// diagnosis. Returns where it went, or null if it could not be moved (it then stays in place
    /// and is reported again next launch, which is the safe direction to fail in).
    /// </summary>
    public static string? SetAside(string? path = null)
    {
        path ??= DefaultPath;
        var target = SetAsidePath(path);
        try
        {
            File.Move(path, target, overwrite: true);
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a stale record is harmless (startup checks it still applies first), and
            // a stray temporary file is never read.
        }
    }
}
