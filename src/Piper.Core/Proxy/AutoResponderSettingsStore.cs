using System.Text;
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
    AutoResponderLoadStatus Status, AutoResponderSettings? Settings = null, string? Detail = null)
{
    /// <summary>
    /// The file is valid but bigger than Piper reads. It is somebody's real rule set (an upgrade can
    /// bring one), and there is no in-app way back from a set over the limits, so it must be left
    /// exactly where it is and never overwritten.
    /// </summary>
    public bool OverLimits => Status is AutoResponderLoadStatus.TooLarge or AutoResponderLoadStatus.TooManyRules;

    /// <summary>The file is not a rule set at all, so it is moved aside to make room for the next save.</summary>
    public bool SetAsideAdvised => Status == AutoResponderLoadStatus.Malformed;
}

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

    /// <summary>How many earlier unusable files <see cref="SetAside"/> keeps before it replaces the oldest.</summary>
    public const int SetAsideSlots = 5;

    private static readonly JsonSerializerOptions ExportOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "autoresponder-rules.json");

    /// <summary>Where captured responses served by <c>*raw:</c> rules are kept.</summary>
    public static string ResponseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "autoresponder");

    /// <summary>
    /// Whether <paramref name="settings"/> is within the limits <see cref="Save"/> enforces:
    /// <see cref="AutoResponderSaveStatus.Saved"/> when it is, otherwise the limit it breaks. For a
    /// caller that must refuse a change before applying it, not after it can no longer be saved.
    /// </summary>
    public static AutoResponderSaveStatus CheckLimits(AutoResponderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Rules.Count > MaxRules) return AutoResponderSaveStatus.TooManyRules;
        return JsonSerializer.SerializeToUtf8Bytes(settings, ExportOptions).LongLength > MaxFileBytes
            ? AutoResponderSaveStatus.TooLarge
            : AutoResponderSaveStatus.Saved;
    }

    public static AutoResponderSaveResult Save(AutoResponderSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Only Piper's own directory is swept afterwards; an export goes into a folder the user
        // chose, and an orphaned temporary next to an interrupted one is left for them to see.
        var usingDefaultPath = path is null;
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
            if (usingDefaultPath) SweepOnce(path, ref s_swept, inBackground: true);
            return new AutoResponderSaveResult(AutoResponderSaveStatus.Saved);
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            DeleteQuietly(temporary);
            return new AutoResponderSaveResult(AutoResponderSaveStatus.Failed, Bounded(ex.Message));
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

            AutoResponderSettings? settings;
            try
            {
                // Decoded the way File.ReadAllText did, so a file saved as UTF-8 with or without a
                // byte order mark, UTF-16 (either byte order) or UTF-32 by Notepad or another editor
                // still loads. The byte cap above has already bounded what is decoded.
                using var reader = new StreamReader(new MemoryStream(content, 0, length), Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                settings = JsonSerializer.Deserialize<AutoResponderSettings>(reader.ReadToEnd());
            }
            catch (ArgumentException ex)
            {
                // From a property setter given hostile content, not from the file system.
                return new AutoResponderLoadResult(AutoResponderLoadStatus.Malformed, Detail: Bounded(ex.Message));
            }

            if (settings is null) return new AutoResponderLoadResult(AutoResponderLoadStatus.Malformed);

            EnsureUniqueIds(settings);
            if (usingDefaultPath) SweepOnce(path, ref s_swept, inBackground: true);
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
        catch (RulesNotAnArrayException)
        {
            return new AutoResponderLoadResult(AutoResponderLoadStatus.Malformed);
        }
        catch (JsonException ex)
        {
            return new AutoResponderLoadResult(AutoResponderLoadStatus.Malformed, Detail: Bounded(ex.Message));
        }
        catch (Exception ex) when (IsFileFailure(ex))
        {
            return new AutoResponderLoadResult(AutoResponderLoadStatus.Unreadable, Detail: Bounded(ex.Message));
        }
    }

    /// <summary>
    /// Moves a rule file that cannot be used aside as <c>*.invalid</c> and returns where it went, or
    /// null when it could not be moved. Without this the first edit made after a failed load would
    /// overwrite the only copy of rules the user may have spent an afternoon writing. An earlier
    /// copy is not overwritten: the next free of <see cref="SetAsideSlots"/> names
    /// (<c>.invalid</c>, <c>.invalid.1</c>, ...) is used, and only when all are taken is the oldest replaced.
    /// </summary>
    public static string? SetAside(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var slots = Enumerable.Range(0, SetAsideSlots)
                .Select(index => index == 0 ? path + ".invalid" : $"{path}.invalid.{index}").ToArray();
            var destination = slots.FirstOrDefault(slot => !File.Exists(slot))
                ?? slots.MinBy(slot => File.GetLastWriteTimeUtc(slot))!;
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
    private static int s_swept;

    /// <summary>
    /// Runs <see cref="SweepStaleTemporaries"/> the first time it is called with a given
    /// <paramref name="gate"/>, so a process enumerates the folder once, not on every edit.
    /// </summary>
    internal static bool SweepOnce(string path, ref int gate, bool inBackground = false)
    {
        if (Interlocked.Exchange(ref gate, 1) != 0) return false;

        // Housekeeping never holds up the caller, which is the UI thread at launch and on every edit.
        // The sweep swallows its own file failures, so nothing is observed on the discarded task.
        if (inBackground) _ = Task.Run(() => SweepStaleTemporaries(path));
        else SweepStaleTemporaries(path);
        return true;
    }

    public static void SweepStaleTemporaries(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(directory)) return;

            var prefix = Path.GetFileName(path) + ".";
            foreach (var candidate in Directory.EnumerateFiles(directory, prefix + "*.tmp"))
            {
                // The pattern also matches 8.3 short names, which can be shorter than the prefix.
                var name = Path.GetFileName(candidate);
                if (name.Length != prefix.Length + 32 + ".tmp".Length
                    || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                var middle = name.AsSpan(prefix.Length, 32);
                if (!IsHex(middle)) continue;
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

    private const int MaxDetailLength = 200;

    /// <summary>
    /// A parser message embeds the JSON path, whose property names come from the file and can be as
    /// long as the file, and it goes to the log and a dialog on the UI thread.
    /// </summary>
    private static string Bounded(string message) =>
        message.Length <= MaxDetailLength ? message : message[..MaxDetailLength] + "...";

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
