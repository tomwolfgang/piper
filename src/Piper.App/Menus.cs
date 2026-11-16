using System.Windows.Forms;

namespace Piper.App;

/// <summary>Builds menu items that show their accelerator in the shortcut column.</summary>
/// <remarks>
/// A tab in <see cref="ToolStripItem.Text"/> is a Win32 menu convention that
/// <see cref="ToolStripMenuItem"/> does not implement. It renders as nothing at all, so
/// "Resend request\tCtrl+R" reached the screen as "Resend requestCtrl+R" with the label and the
/// accelerator run together. WinForms wants the accelerator in
/// <see cref="ToolStripMenuItem.ShortcutKeyDisplayString"/>, which is laid out in its own
/// right-aligned column.
///
/// Display only: every accelerator these items advertise is handled by a <c>KeyDown</c> handler on
/// the control that owns it, which is why <see cref="ToolStripMenuItem.ShortcutKeys"/> stays unset
/// here. Setting it would register a form-wide accelerator and take the key away from the focused
/// control.
/// </remarks>
internal static class Menus
{
    public static ToolStripMenuItem Item(string text, string shortcut, EventHandler onClick) =>
        new(text, null, onClick) { ShortcutKeyDisplayString = shortcut };

    /// <summary>
    /// Clicks the enabled item of <paramref name="menu"/> whose <see cref="ToolStripMenuItem.ShortcutKeys"/>
    /// is <paramref name="keyData"/>, as WinForms does for the window that holds the menu, and says whether
    /// there was one. A floating panel's window needs this, because WinForms only routes a menu's
    /// accelerators from its own window.
    /// </summary>
    public static bool PerformShortcut(ToolStrip menu, Keys keyData)
    {
        ArgumentNullException.ThrowIfNull(menu);
        if (keyData == Keys.None || Find(menu.Items, keyData) is not { } item) return false;
        item.PerformClick();
        return true;

        static ToolStripMenuItem? Find(ToolStripItemCollection items, Keys keys)
        {
            foreach (var item in items.OfType<ToolStripMenuItem>())
            {
                if (!item.Enabled) continue;
                if (item.ShortcutKeys == keys) return item;
                if (item.HasDropDownItems && Find(item.DropDownItems, keys) is { } found) return found;
            }

            return null;
        }
    }
}
