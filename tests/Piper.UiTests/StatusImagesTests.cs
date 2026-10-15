using Piper.App.Theme;

// The capture status label is skipped by the normal refresh while a capture toggle is in progress.
// RefreshStatusIcons then disposed the label's icon and left it assigned, and a disposed image throws
// when WinForms paints it. Retarget moves the label onto the replacement before the old one is disposed.
internal static class StatusImagesTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("== a status label never keeps an image that was disposed");
        var oldOn = new Bitmap(16, 16);
        var oldOff = new Bitmap(16, 16);
        using var newOn = new Bitmap(16, 16);
        using var newOff = new Bitmap(16, 16);
        using var label = new ToolStripStatusLabel { Image = oldOff };

        StatusImages.Retarget(label, (oldOn, newOn), (oldOff, newOff));
        oldOn.Dispose();
        oldOff.Dispose();

        check(ReferenceEquals(label.Image, newOff), "a label that showed the old off icon shows the new off icon");
        check(IsUsable(label.Image), "the image the label holds is still drawable after the old icons are disposed");

        using var other = new Bitmap(16, 16);
        using var untouched = new ToolStripStatusLabel { Image = other };
        StatusImages.Retarget(untouched, (new Bitmap(4, 4), newOn));
        check(ReferenceEquals(untouched.Image, other), "a label showing some other image is left alone");

        using var empty = new ToolStripStatusLabel();
        StatusImages.Retarget(empty, (oldOn, newOn));
        check(empty.Image is null, "a label with no image stays without one");

        // The failure this prevents: the old behaviour left the label on the disposed bitmap.
        var disposed = new Bitmap(16, 16);
        using var stale = new ToolStripStatusLabel { Image = disposed };
        disposed.Dispose();
        check(!IsUsable(stale.Image), "control: a label left on a disposed bitmap holds an image that cannot be drawn");
    }

    private static bool IsUsable(Image? image)
    {
        if (image is null) return false;
        try
        {
            using var target = new Bitmap(16, 16);
            using var graphics = Graphics.FromImage(target);
            graphics.DrawImage(image, 0, 0);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
