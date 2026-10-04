using System.Diagnostics;
using System.Text;
using System.Windows.Forms;
using Piper.Core.Telemetry;

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

        // Only the instance that owns the window reports. A launch that exists solely to hand a
        // .saz file to the running Piper and exit is not a session and must not look like one.
        StartAnalytics();

        try
        {
            using var form = new MainForm();
            using var relay = new SazFileRelay(files =>
            {
                if (form.IsDisposed) return;
                try
                {
                    form.BeginInvoke(() =>
                    {
                        if (form.IsDisposed) return;
                        // An empty relay means Piper itself was launched again. ImportSazFiles
                        // restores and activates the window as part of a file handoff.
                        if (files.Count == 0) WindowActivation.BringToFront(form);
                        else form.ImportSazFiles(files);
                    });
                }
                catch (InvalidOperationException) { }
            });
            form.Shown += (_, _) => form.ImportSazFiles(startupFiles);
            Application.Run(form);
        }
        finally
        {
            // Guarded for the same reason StartAnalytics is. Shutdown drains the spool, and neither
            // it nor Dispose filters for everything a path can throw, so without this a failure to
            // write reports on the way out costs the user a crash as they close the window rather
            // than a missing report.
            try
            {
                Analytics.Shutdown();
            }
            catch (Exception shutdownFailure)
            {
                Debug.WriteLine($"Analytics failed to shut down: {shutdownFailure.GetType().Name}");
            }
        }
    }

    /// <summary>
    /// Brings up reporting. Deliberately total: a failure here must cost the user nothing, because
    /// analytics are the least important thing this process does.
    /// </summary>
    private static void StartAnalytics()
    {
        try
        {
            var settings = AnalyticsSettingsStore.Load() ?? new AnalyticsSettings();
            var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            Analytics.Initialize(new AnalyticsClient(
                settings, version,
                machineId: MachineIdStore.GetOrCreate,
                forgetMachineId: MachineIdStore.Delete));
            Analytics.Track(AnalyticsEvents.AppStarted);
        }
        catch (Exception ex)
        {
            // Total on purpose, matching the summary above: the filter used to name three exception
            // types, so anything else - a malformed path, a type initialiser failing while the
            // allowlists are built - escaped into Main and cost the user the launch because
            // reporting failed to start. There is no window to log to yet.
            Debug.WriteLine($"Analytics failed to start: {ex.GetType().Name}");
        }
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

        // The type and frames only - never the message, which routinely carries the URL that was
        // being processed. Written synchronously because the dialog below blocks until the user
        // dismisses it and the process may not survive to the next flush.
        // Guarded as a whole: this runs inside the unhandled-exception handler, so anything thrown
        // here would escape it and take the dialog below with it. A missing report is a far smaller
        // loss than a crash the user never sees.
        try
        {
            Analytics.TrackError("unhandled", exception, fatal: true);
            Analytics.FlushToDisk();
        }
        catch (Exception reportingFailure)
        {
            Debug.WriteLine($"Analytics failed on the crash path: {reportingFailure.GetType().Name}");
        }

        MessageBox.Show(
            Strings.App.CrashReport(exception.GetType().Name, exception.Message, exception.StackTrace),
            Strings.App.UnexpectedErrorCaption, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
