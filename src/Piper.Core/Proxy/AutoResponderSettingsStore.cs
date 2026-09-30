using System.Text.Json;

namespace Piper.Core.Proxy;

/// <summary>Why a rule set could not be read.</summary>
public enum AutoResponderLoadStatus
{
    Loaded,

    /// <summary>There is no file. Not a problem: nothing was ever saved.</summary>
    Missing,

    /// <summary>The file could not be opened or read (locked, denied, a directory).</summary>
    Unreadable,

    /// <summary>The file is not a JSON rule set.</summary>
    Malformed,

    /// <summary>The file is larger than <see cref="AutoResponderSettingsStore.MaxFileBytes"/>.</summary>
    TooLarge,

    /// <summary>The file holds more than <see cref="AutoResponderSettingsStore.MaxRules"/> rules.</summary>
    TooManyRules,
}

/// <param name="Status">Whether the rules were read, and if not, why.</param>
/// <param name="Settings">The rules when <paramref name="Status"/> is <see cref="AutoResponderLoadStatus.Loaded"/>.</param>
/// <param name="Detail">The system's or parser's wording, for <c>Unreadable</c> and <c>Malformed</c>.</param>
public sealed record AutoResponderLoadResult(
    AutoResponderLoadStatus Status, AutoResponderSettings? Settings = null, string? Detail = null);

/// <summary>Why a rule set could not be written.</summary>
public enum AutoResponderSaveStatus
{
    Saved,

    /// <summary>The disk or the path refused the write. The previous file is untouched.</summary>
    Failed,

    /// <summary>More rules than <see cref="AutoResponderSettingsStore.MaxRules"/>: it could not be loaded back.</summary>
    TooManyRules,

    /// <summary>Larger than <see cref="AutoResponderSettingsStore.MaxFileBytes"/>: it could not be loaded back.</summary>
    TooLarge,
}

/// <param name="Status">Whether the rules were written, and if not, why.</param>
/// <param name="Detail">The system's wording for <see cref="AutoResponderSaveStatus.Failed"/>.</param>
public readonly record struct AutoResponderSaveResult(AutoResponderSaveStatus Status, string? Detail = null)
{
    public bool Succeeded => Status == AutoResponderSaveStatus.Saved;
}

/// <summary>
/// Persists the AutoResponder rule set under the user's local app-data directory.
/// </summary>
/// <remarks>
/// Kept out of configuration.json deliberately: that file holds small, stable proxy settings, while
/// a rule set is user data with an unbounded size and its own import/export story. The same
/// serialisation serves both, so exporting a rule set is this file written somewhere else.
///
/// The file is hostile input. It is read at launch, on the UI thread, before any window exists, and
/// Import reads whatever the user points it at, so <see cref="Load"/> never throws, reads at most
/// <see cref="MaxFileBytes"/>, and reports what went wrong instead of returning an empty answer that
/// looks like "no rules". <see cref="Save"/> refuses what <see cref="Load"/> would refuse, so a rule
/// set can never be written that cannot be read back.
/// </remarks>
public static class AutoResponderSettingsStore
{
    /// <summary>
    /// Largest rule file that is read or written. Generous for rules with inline bodies, small enough
    /// that reading it, and the several copies the panel and the engine make of it, stay cheap.
    /// </summary>
    public const long MaxFileBytes = 8L * 1024 * 1024;

    /// <summary>Most rules a set may hold. Every request walks the list, so it is also a latency bound.</summary>
    public const int MaxRules = 5000;

    private static readonly JsonSerializerOptions ExportOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "autoresponder-rules.json");

    /// <summary>Where captured responses served by <c>*raw:</c> rules are kept.</summary>
    public static string ResponseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "autoresponder");

    public static AutoResponderSaveResult Save(AutoResponderSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        path ??= DefaultPath;

        if (settings.Rules.Count > MaxRules) return new AutoResponderSaveResult(AutoResponderSaveStatus.TooManyRules);

        // Indented: a rule set is meant to be read, diffed and hand-edited, unlike the other
        // settings files which are pure machine state.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, ExportOptions);
        if (bytes.LongLength > MaxFileBytes) return new AutoResponderSaveResult(AutoResponderSaveStatus.TooLarge);

        // Written beside the target and moved over it, so a failure or a crash half way leaves the
        // previous file whole. Truncating it in place is how a rule set ends up as truncated JSON.
        string? temporary = null;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            SweepStaleTemporaries(path);
            return new AutoResponderSaveResult(AutoResponderSaveStatus.Saved);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            DeleteQuietly(temporary);
            return new AutoResponderSaveResult(AutoResponderSaveStatus.Failed, ex.Message);
        }
    }

    public static AutoResponderLoadResult Load(string? path = null)
    {
        var usingDefaultPath = path is null;
        path ??= DefaultPath;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!TryReadBounded(stream, out var content, out var length))
                return new AutoResponderLoadResult(AutoResponderLoadStatus.TooLarge);

            // File.ReadAllText used to drop a UTF-8 byte order mark that Notepad and friends write;
            // the JSON reader treats it as garbage, so it is dropped here instead.
            var start = length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF ? 3 : 0;
            AutoResponderSettings? settings;
            try
            {
                settings = JsonSerializer.Deserialize<AutoResponderSettings>(content.AsSpan(start, length - start));
            }
            catch (ArgumentException ex)
            {
                // From a property setter given hostile content, not from the file system.
                return new AutoResponderLoadResult(AutoResponderLoadStatus.Malformed, Detail: ex.Message);
            }

            if (settings is null)
                return new AutoResponderLoadResult(AutoResponderLoadStatus.Malformed, Detail: "the document is null");

            EnsureUniqueIds(settings);
            if (usingDefaultPath) SweepStaleTemporaries(path);
            return new AutoResponderLoadResult(AutoResponderLoadStatus.Loaded, settings);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AutoResponderLoadResult(AutoResponderLoadStatus.Missing);
        }
        catch (RuleCountExceededException)
        {
            return new AutoResponderLoadResult(AutoResponderLoadStatus.TooManyRules);
        }
        catch (JsonException ex)
        {
            return new AutoResponderLoadResult(AutoResponderLoadStatus.Malformed, Detail: ex.Message);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new AutoResponderLoadResult(AutoResponderLoadStatus.Unreadable, Detail: ex.Message);
        }
    }

    /// <summary>
    /// Moves a rule file that cannot be used aside as <c>*.invalid</c> and returns where it went, or
    /// null when it could not be moved. Without this the first edit made after a failed load would
    /// overwrite the only copy of rules the user may have spent an afternoon writing.
    /// </summary>
    public static string? SetAside(string? path = null)
    {
        path ??= DefaultPath;
        var destination = path + ".invalid";
        try
        {
            File.Move(path, destination, overwrite: true);
            return destination;
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return null;
        }
    }

    /// <summary>Reads at most <see cref="MaxFileBytes"/>; false when the stream holds more.</summary>
    private static bool TryReadBounded(Stream stream, out byte[] content, out int length)
    {
        content = [];
        length = 0;

        // The length is checked while reading rather than up front: it is only a hint, and a file
        // that grows after it was asked for must not be read past the cap.
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        var remaining = MaxFileBytes + 1;
        int read;
        while (remaining > 0 && (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, remaining))) > 0)
        {
            buffer.Write(chunk, 0, read);
            remaining -= read;
        }

        if (buffer.Length > MaxFileBytes) return false;
        content = buffer.GetBuffer();
        length = (int)buffer.Length;
        return true;
    }

    /// <summary>
    /// Every rule needs an id to key its hit counts. Rules written before ids existed, or hand-edited
    /// in, have none, and two rules pasted from one another share one; either would merge counters.
    /// </summary>
    private static void EnsureUniqueIds(AutoResponderSettings settings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in settings.Rules)
            if (string.IsNullOrWhiteSpace(rule.Id) || !seen.Add(rule.Id))
            {
                rule.Id = Guid.NewGuid().ToString("N");
                seen.Add(rule.Id);
            }
    }

    /// <summary>
    /// Removes <c>{file}.{32 hex digits}.tmp</c> siblings older than <see cref="StaleTemporaryAge"/>:
    /// what a save leaves behind when the process is killed between writing and moving. Only that
    /// exact shape is touched, and a recent one may be another instance's save in flight. Best effort.
    /// </summary>
    private static void SweepStaleTemporaries(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(directory)) return;

            var prefix = Path.GetFileName(path) + ".";
            foreach (var candidate in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
            {
                var middle = Path.GetFileName(candidate).AsSpan(prefix.Length);
                middle = middle[..^".tmp".Length];
                if (middle.Length != 32 || !IsHex(middle)) continue;
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(candidate) < StaleTemporaryAge) continue;
                DeleteQuietly(candidate);
            }
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // Housekeeping only: the caller's own work has already succeeded.
        }

        static bool IsHex(ReadOnlySpan<char> text)
        {
            foreach (var c in text)
                if (!char.IsAsciiHexDigit(c)) return false;
            return true;
        }
    }

    private static readonly TimeSpan StaleTemporaryAge = TimeSpan.FromMinutes(10);

    private static bool IsFileFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException
            or System.Security.SecurityException;

    private static void DeleteQuietly(string? path)
    {
        if (path is null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            // Best effort: a stray temporary file beside the rules is harmless, and the failure that
            // brought us here is the one worth reporting.
        }
    }
}
