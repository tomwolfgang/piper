using System.Text.Json;

namespace Piper.Core.Sessions;

/// <summary>
/// The theme template the user picked. No file, or no theme in it, means "follow Windows".
/// </summary>
public sealed class ThemeSettings
{
    /// <summary>A template name. The application validates it against its own list on load.</summary>
    public string? Theme { get; set; }
}

/// <summary>
/// Persists the chosen theme under the user's local app-data directory. Like the other preference
/// stores, this is convenience state: missing, inaccessible, or malformed data falls back to the
/// application default rather than failing.
/// </summary>
public static class ThemeSettingsStore
{
    /// <summary>A real file is a few dozen bytes; anything far larger was not written by Piper.</summary>
    private const long MaxFileBytes = 4096;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "theme.json");

    public static void Save(ThemeSettings settings, string? path = null)
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

    public static ThemeSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaxFileBytes) return null;
            return JsonSerializer.Deserialize<ThemeSettings>(File.ReadAllText(path));
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
