using System.Text;
using System.Windows.Forms;

namespace Piper.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Legacy charsets (windows-1252, shift_jis, ...) still show up in real traffic and
        // are not in the default .NET provider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        // Last line of defence for the system proxy. The window's own shutdown path clears the
        // undo record once it has put the settings back, so by the time this runs there is
        // normally nothing left to do - but an exit that never reaches that path must not leave
        // the machine routing traffic to a Piper that has gone away.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreSystemProxyLeftovers();

        var startupFiles = args.Where(SazFileRelay.IsSazFile).ToArray();
        using var mutex = new Mutex(initiallyOwned: true, SazFileRelay.MutexName, out var firstInstance);
        _ownsSystemProxy = firstInstance;
        if (!firstInstance)
        {
            // File association launches and command-line opens both come through here. A short
            // retry handles the race where the first instance has created its mutex but has not
            // begun listening on its pipe yet.
            SazFileRelay.TryForward(startupFiles);
            return;
        }

        using var form = new MainForm();
        using var relay = new SazFileRelay(files =>
        {
            if (form.IsDisposed || files.Count == 0) return;
            try { form.BeginInvoke(() => form.ImportSazFiles(files)); }
            catch (InvalidOperationException) { }
        });
        form.Shown += (_, _) => form.ImportSazFiles(startupFiles);
        Application.Run(form);
    }

    private static bool _ownsSystemProxy;

    /// <summary>
    /// Undoes a system proxy this process left behind, but only if it is the instance that could
    /// have set one. A launch that just forwards .saz files to the running Piper and exits must
    /// not pull the proxy out from under it.
    /// </summary>
    private static void RestoreSystemProxyLeftovers()
    {
        if (_ownsSystemProxy) SystemProxy.RestoreLeftovers();
    }

    /// <summary>Path of the crash log, also used by the smoke harness to diagnose startup failures.</summary>
    public static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "crash.log");

    private static void ReportCrash(Exception? exception)
    {
        if (exception is null) return;

        // Before the dialog, which blocks until the user dismisses it: the crash must not cost
        // them their connection while they read it.
        RestoreSystemProxyLeftovers();

        // Write to disk first: a modal dialog during startup blocks the message loop, so
        // the log is the only reliable record when the window never appears.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:u}] {exception.GetType().FullName}: {exception.Message}{Environment.NewLine}"
                + $"{exception.StackTrace}{Environment.NewLine}{new string('-', 70)}{Environment.NewLine}");
        }
        catch (IOException) { /* nothing useful to do if even logging fails */ }

        MessageBox.Show(
            $"{exception.GetType().Name}: {exception.Message}\r\n\r\n{exception.StackTrace}",
            "Piper - unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
