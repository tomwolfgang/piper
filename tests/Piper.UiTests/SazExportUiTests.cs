using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Piper.App.Controls;
using Piper.Core.Http;
using Piper.Core.Sessions;

// Saving a large selection as SAZ used to compress every body on the UI thread, freezing the window
// for the whole export. It now runs on a worker, behind a progress dialog with Cancel.
internal static class SazExportUiTests
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"Piper-UiTest-SazExport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            // Random bytes do not compress, so every byte costs the deflater work. The sessions share
            // one body array, which keeps the test's own memory small.
            var body = new byte[4 * 1024 * 1024];
            new Random(5).NextBytes(body);

            Console.WriteLine("== saving a large SAZ keeps the UI thread free and replaces the file once it is complete");
            using (var form = new Form { Width = 900, Height = 400, ShowInTaskbar = false })
            {
                var grid = new SessionListView(new SessionStore()) { Dock = DockStyle.Fill };
                form.Controls.Add(grid);
                form.Show();
                var notices = new List<string>();
                grid.SazSaveNotice += (_, message) => notices.Add(message);
                var target = Path.Combine(folder, "big.saz");
                File.WriteAllBytes(target, [1, 2, 3]);

                var gap = 0L;
                var last = Stopwatch.StartNew();
                using var ticker = new System.Windows.Forms.Timer { Interval = 10 };
                ticker.Tick += (_, _) => { gap = Math.Max(gap, last.ElapsedMilliseconds); last.Restart(); };
                ticker.Start();
                var watch = Stopwatch.StartNew();
                var save = Save(grid, target, Sessions(body, 12));
                var returned = watch.ElapsedMilliseconds;
                pumpUntil(() => save.IsCompleted);
                ticker.Stop();
                Console.WriteLine($"   the save held the UI thread {returned:N0} ms; longest gap between UI ticks {gap:N0} ms; done after {watch.ElapsedMilliseconds:N0} ms");

                check(save.IsCompletedSuccessfully, "the save ends without a fault");
                check(returned < 1000 && gap < 2000, $"the UI thread is never held for long (save {returned} ms, longest gap {gap} ms)");
                check(notices is ["Saved 12 sessions to big.saz."], $"the Log is told the file name, not the path (got {string.Join(" | ", notices)})");
                using (var archive = ZipFile.OpenRead(target))
                    check(archive.Entries.Count == 24 && archive.GetEntry("raw/12_s.txt")?.Length > body.Length,
                        $"the target now holds the whole export ({archive.Entries.Count} entries)");
                check(Directory.GetFiles(folder).Length == 1, "no temporary file is left beside it");
                check(!OpenProgressDialogs().Any(), "the progress dialog closed itself");
            }

            Console.WriteLine("== Cancel in the progress dialog keeps the existing file");
            using (var form = new Form { Width = 900, Height = 400, ShowInTaskbar = false })
            {
                var grid = new SessionListView(new SessionStore()) { Dock = DockStyle.Fill };
                form.Controls.Add(grid);
                form.Show();
                var notices = new List<string>();
                grid.SazSaveNotice += (_, message) => notices.Add(message);
                var target = Path.Combine(folder, "keep.saz");
                byte[] original = [.. "the user's earlier capture"u8];
                File.WriteAllBytes(target, original);

                // The dialog is modal, so its Cancel is pressed from a timer that ticks inside its loop.
                var sawDialog = false;
                using var canceller = new System.Windows.Forms.Timer { Interval = 20 };
                canceller.Tick += (_, _) =>
                {
                    if (OpenProgressDialogs().FirstOrDefault() is not { CancelButton: { } cancel }) return;
                    sawDialog = true;
                    canceller.Stop();
                    cancel.PerformClick();
                };
                canceller.Start();
                var save = Save(grid, target, Sessions(body, 150));
                pumpUntil(() => save.IsCompleted);
                canceller.Stop();

                check(sawDialog, "a long export shows the progress dialog");
                check(save.IsCompletedSuccessfully, "a cancelled save ends without a fault");
                check(File.ReadAllBytes(target).SequenceEqual(original), "the existing file is byte-identical");
                check(Directory.GetFiles(folder).Length == 2, "the temporary file was deleted");
                check(notices is [var notice] && notice.StartsWith("Saving keep.saz was cancelled."), $"the Log records the cancel (got {string.Join(" | ", notices)})");
                check(!OpenProgressDialogs().Any(), "the progress dialog closed itself");
            }

            Console.WriteLine("== closing the app mid-save cancels it and removes the temporary file");
            using (var form = new Form { Width = 900, Height = 400, ShowInTaskbar = false })
            {
                var grid = new SessionListView(new SessionStore()) { Dock = DockStyle.Fill };
                form.Controls.Add(grid);
                form.Show();
                var target = Path.Combine(folder, "close.saz");
                byte[] original = [.. "kept on close"u8];
                File.WriteAllBytes(target, original);

                // Before the progress dialog appears, the window can still be closed.
                var save = Save(grid, target, Sessions(body, 150));
                grid.CancelSazSave(TimeSpan.FromSeconds(10));
                var files = Directory.GetFiles(folder).Length;
                pumpUntil(() => save.IsCompleted);

                check(files == 3, $"the temporary file is gone once the cancel returns ({files} files)");
                check(File.ReadAllBytes(target).SequenceEqual(original), "the existing file is byte-identical");
                check(save.IsCompletedSuccessfully, "the cancelled save ends without a fault");
                check(!OpenProgressDialogs().Any(), "no progress dialog is left open");
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static Task Save(SessionListView grid, string path, Session[] sessions) =>
        (Task)typeof(SessionListView).GetMethod("SaveSazAsync", Instance)!.Invoke(grid, [path, sessions])!;

    private static IEnumerable<Form> OpenProgressDialogs() =>
        Application.OpenForms.Cast<Form>().Where(form => form.GetType().Name == "SazExportProgressDialog").ToArray();

    private static Session[] Sessions(byte[] body, int count) =>
    [
        .. Enumerable.Range(1, count).Select(id =>
        {
            var request = new HttpRequestData { Method = "GET", HttpVersion = "HTTP/1.1", RequestTarget = $"/file/{id}", Url = new Uri($"https://files.example.test/file/{id}") };
            request.Headers.Add("Host", "files.example.test");
            var response = new HttpResponseData { StatusCode = 200, ReasonPhrase = "OK", Body = body };
            response.Headers.Add("Content-Type", "application/octet-stream");
            return new Session { Request = request, Response = response };
        }),
    ];
}
