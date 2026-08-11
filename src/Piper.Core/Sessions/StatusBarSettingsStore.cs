using System.Text.Json;

namespace Piper.Core.Sessions;

/// <summary>
/// The user-selectable state represented by Piper's status bar. It is stored separately from
/// captured sessions, which are intentionally transient, so the next application session can
/// begin with the same capture mode and process scope.
/// </summary>
public sealed class StatusBarSettings
{
    public bool CaptureEnabled { get; set; } = true;

    public string CaptureScope { get; set; } = "AllProcesses";
}

/// <summary>
/// Persists status-bar choices under the user's local app-data directory. This is convenience
/// state: missing, inaccessible, or malformed data simply falls back to the application's
/// defaults.
/// </summary>
public static class StatusBarSettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "status-bar-settings.json");

    public static void Save(StatusBarSettings settings, string? path = null)
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

    public static StatusBarSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<StatusBarSettings>(File.ReadAllText(path));
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
