using System.Windows.Forms;

namespace Piper.App;

/// <summary>Restores and activates Piper after a second launch is handed off to this process.</summary>
internal static class WindowActivation
{
    public static void BringToFront(Form form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsDisposed) return;

        if (form.WindowState == FormWindowState.Minimized)
            form.WindowState = FormWindowState.Normal;

        form.BringToFront();
        form.Activate();
    }
}
