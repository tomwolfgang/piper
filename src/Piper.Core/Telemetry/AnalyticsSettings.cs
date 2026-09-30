using System.Text.Json;

namespace Piper.Core.Telemetry;

/// <summary>
/// The user's analytics choice, plus the two identifiers a report needs to be useful.
/// </summary>
public sealed class AnalyticsSettings
{
    /// <summary>
    /// Off until the user asks for it. Collection is opt-in: Piper decrypts other people's HTTPS
    /// traffic, so the reasonable default is to gather nothing at all, and a user who never answers
    /// the question - or never sees it - is never collected from.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// A random value minted on first use, letting reports from one installation be grouped without
    /// identifying the machine or the person. Deliberately not derived from hardware, user name,
    /// domain, or anything else that survives a reinstall, and cleared whenever the user opts out.
    /// </summary>
    public string? InstallId { get; set; }

    /// <summary>
    /// The application version at which the user was last asked. Null means the question has not
    /// been put to them yet. Stored as a version rather than a flag because a user with reporting
    /// off is asked again once each time they update - see AnalyticsClient.ShouldAskConsent.
    /// </summary>
    public string? NoticeShownVersion { get; set; }
}

/// <summary>
/// Persists the analytics choice under the user's local app-data directory, alongside Piper's other
/// settings. A missing, inaccessible, or malformed file falls back to defaults - except that a file
/// that cannot be read also cannot carry a recorded notice, so the notice is shown again rather than
/// assumed.
/// </summary>
public static class AnalyticsSettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "analytics.json");

    /// <summary>Directory holding the pending-event spool. Shown to the user from the settings UI.</summary>
    public static string DefaultSpoolDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "analytics");

    public static void Save(AnalyticsSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        path ??= DefaultPath;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, JsonSerializer.Serialize(settings));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Three small fields, so anything beyond this is not a settings file Piper wrote. Reading it
    /// would otherwise be the one unbounded read on the launch path, where an OutOfMemoryException
    /// escapes every catch and costs the user the application rather than the setting.
    /// </summary>
    private const long MaxSettingsBytes = 64 * 1024;

    public static AnalyticsSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxSettingsBytes) return null;
            return JsonSerializer.Deserialize<AnalyticsSettings>(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
