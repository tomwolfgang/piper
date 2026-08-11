using System.Text.Json;
using Piper.Core.Proxy;

namespace Piper.Core.Sessions;

/// <summary>
/// Persists the Composer's own send history to disk so it survives an app restart. Mirrors
/// the on-disk pattern already used by <see cref="Security.CertificateAuthority"/>: plain
/// files under the user's local app-data folder, best-effort, never a hard dependency for
/// startup to succeed.
/// </summary>
public static class ComposerHistoryStore
{
    private sealed class Entry
    {
        public string Raw { get; set; } = string.Empty;
        public DateTimeOffset SavedAt { get; set; }
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "composer-history.json");

    /// <summary>Saves the full current set of composed sessions (overwrite, not append).</summary>
    public static void Save(IReadOnlyCollection<Session> composedSessions, string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            var entries = composedSessions
                .Where(s => s.Request is not null)
                .Select(s => new Entry
                {
                    Raw = RequestExecutor.ToRawText(s.Request!),
                    SavedAt = s.Completed ?? s.Started,
                })
                .ToList();

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
        catch (IOException)
        {
            // Best-effort -- losing history must never crash or block the app.
        }
        catch (JsonException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Loads previously-saved composed sessions back as fresh <see cref="Session"/> objects
    /// (<see cref="Session.IsComposed"/> = true, <see cref="Session.Request"/> populated,
    /// <see cref="Session.Response"/> left null). Returns an empty list on any failure (missing
    /// file, corrupt JSON, etc.) -- this is a convenience feature, never something that should
    /// crash startup.
    /// </summary>
    public static List<Session> Load(string? path = null)
    {
        path ??= DefaultPath;
        var restored = new List<Session>();

        try
        {
            if (!File.Exists(path)) return restored;

            var json = File.ReadAllText(path);
            var entries = JsonSerializer.Deserialize<List<Entry>>(json);
            if (entries is null) return restored;

            foreach (var entry in entries)
            {
                if (!RequestExecutor.TryParseRaw(entry.Raw, out var request, out _)) continue;

                restored.Add(new Session
                {
                    Request = request,
                    IsComposed = true,
                    Completed = entry.SavedAt,
                });
            }
        }
        catch (IOException)
        {
            return [];
        }
        catch (JsonException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        return restored;
    }
}
