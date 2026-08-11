using System.Text;
using System.Windows.Forms;

namespace Piper.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Legacy charsets (windows-1252, shift_jis, ...) still show up in real traffic and
        // are not in the default .NET provider.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) => ReportCrash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception);

        Application.Run(new MainForm());
    }

    /// <summary>Path of the crash log, also used by the smoke harness to diagnose startup failures.</summary>
    public static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Piper", "crash.log");

    private static void ReportCrash(Exception? exception)
    {
        if (exception is null) return;

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
