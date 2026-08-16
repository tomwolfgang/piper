using System.Text.Json;

namespace Piper.Core.Proxy;

/// <summary>
/// Persists the AutoResponder rule set under the user's local app-data directory.
/// </summary>
/// <remarks>
/// Kept out of configuration.json deliberately: that file holds small, stable proxy settings, while
/// a rule set is user data with an unbounded size and its own import/export story. The same
/// serialisation serves both, so exporting a rule set is this file written somewhere else.
/// </remarks>
public static class AutoResponderSettingsStore
{
    private static readonly JsonSerializerOptions ExportOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "autoresponder-rules.json");

    /// <summary>Where captured responses served by <c>*raw:</c> rules are kept.</summary>
    public static string ResponseDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "autoresponder");

    public static void Save(AutoResponderSettings settings, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        path ??= DefaultPath;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            // Indented: a rule set is meant to be read, diffed and hand-edited, unlike the other
            // settings files which are pure machine state.
            File.WriteAllText(path, JsonSerializer.Serialize(settings, ExportOptions));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static AutoResponderSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            var settings = JsonSerializer.Deserialize<AutoResponderSettings>(File.ReadAllText(path));
            if (settings is null) return null;

            // Rules written before ids existed, or hand-edited in, still need one to key hit counts.
            foreach (var rule in settings.Rules)
                if (string.IsNullOrWhiteSpace(rule.Id)) rule.Id = Guid.NewGuid().ToString("N");

            return settings;
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
