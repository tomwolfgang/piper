using System.Windows.Forms;

namespace Piper.App.Theme;

internal static class StatusImages
{
    /// <summary>
    /// Points <paramref name="item"/> at a replacement when it still shows an image that is about to
    /// be disposed. A status label that is skipped by the normal refresh (the capture label is while a
    /// capture toggle is in progress) would otherwise keep a disposed image, which throws when painted.
    /// </summary>
    public static void Retarget(ToolStripItem item, params (Image Old, Image New)[] swaps)
    {
        foreach (var (old, replacement) in swaps)
        {
            if (!ReferenceEquals(item.Image, old)) continue;
            item.Image = replacement;
            return;
        }
    }
}
