using System.Text.Json;

namespace Piper.Core.Sessions;

/// <summary>
/// The TextWizard choice worth remembering between runs.
/// </summary>
/// <remarks>
/// Only the name of the transform is kept. The text being converted is never written anywhere: it comes
/// from captured traffic and routinely holds credentials, cookies and tokens, so it stays in memory for as
/// long as the window is open and no longer.
/// </remarks>
public sealed class TextWizardSettings
{
    /// <summary>Name of the last <c>TextTransform</c> the user picked, or null before they have picked one.</summary>
    public string? LastTransform { get; set; }
}

/// <summary>
/// Persists the TextWizard's last transform under the user's local app-data directory, beside the other
/// convenience state. Missing, inaccessible or malformed data simply falls back to the default.
/// </summary>
public static class TextWizardSettingsStore
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "text-wizard-settings.json");

    public static void Save(TextWizardSettings settings, string? path = null)
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

    public static TextWizardSettings? Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<TextWizardSettings>(File.ReadAllText(path));
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
