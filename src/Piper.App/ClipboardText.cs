using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Piper.App;

/// <summary>Puts text on the clipboard without letting a copy command crash the app.</summary>
/// <remarks>
/// <see cref="Clipboard.SetText(string)"/> throws for an empty string, and copying an empty header
/// value, an empty form field or an empty selection produces exactly that. The exception reached
/// the unhandled-exception handler, which also restores the system proxy, so one Ctrl+C on an empty
/// list quietly stopped browsers going through Piper while the status bar still said Capturing.
/// </remarks>
internal static class ClipboardText
{
    /// <summary>Copies <paramref name="text"/>; returns false and changes nothing when it cannot.</summary>
    public static bool TrySet(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (ExternalException)
        {
            // Another process is holding the clipboard open. Leaving the previous contents in place
            // is the only sensible outcome; the user can simply copy again.
            return false;
        }
    }
}
