using System.Runtime.InteropServices;
using Piper.App.Controls;
using Piper.Core.Http;
using Piper.Core.Sessions;

// Regression: after Ctrl+X cleared a grid scrolled down to its newest rows, the next rows were drawn
// partway down the grid or not at all, and stayed that way. The native ListView's scroll origin
// was left past the end when the row count shrank, so later rows sat at a negative top index.
internal static class Program
{
    private const int LvmGetTopIndex = 0x1000 + 39;
    private static int _failures;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        Run("clearing a scrolled grid shows the next rows from the top", (store, grid, list) =>
        {
            store.Clear(); // MainForm's Ctrl+X handler
            PumpUntil(() => list.VirtualListSize == 0); // the grid redraws empty before traffic resumes
            AddSessions(store, 6);
            PumpUntil(() => list.VirtualListSize == 6);
            ExpectRowsFromTop(list, 6);

            AddSessions(store, 6);
            PumpUntil(() => list.VirtualListSize == 12);
            ExpectRowsFromTop(list, 12);
        });

        Run("a filter that matches far fewer rows shows them from the top", (store, grid, list) =>
        {
            grid.FilterText = "/item/29"; // a handful of rows, all fewer than the old top row
            PumpUntil(() => list.VirtualListSize < 300);
            Check(list.VirtualListSize is > 0 and < 50, $"the filter leaves a few rows (got {list.VirtualListSize})");
            ExpectRowsFromTop(list, list.VirtualListSize);
        });

        Console.WriteLine(_failures == 0 ? "UI tests passed." : $"{_failures} UI check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>Hosts a real grid, fills it past a screen so it follows the tail, then runs the case.</summary>
    private static void Run(string name, Action<SessionStore, SessionListView, ListView> body)
    {
        Console.WriteLine($"== {name}");
        var store = new SessionStore();
        using var form = new Form { Width = 1000, Height = 500, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        var grid = new SessionListView(store) { Dock = DockStyle.Fill };
        form.Controls.Add(grid);
        form.Show();
        var list = FindListView(grid);

        _next = 0;
        AddSessions(store, 300);
        PumpUntil(() => list.VirtualListSize == 300);
        Check(SendMessage(list.Handle, LvmGetTopIndex, 0, 0) > 100, "setup: the grid followed the tail");

        body(store, grid, list);
        form.Close();
    }

    private static void ExpectRowsFromTop(ListView list, int rows)
    {
        Check(list.VirtualListSize == rows, $"{rows} rows are listed (got {list.VirtualListSize})");
        if (list.VirtualListSize != rows || rows == 0) return;
        var topIndex = (int)SendMessage(list.Handle, LvmGetTopIndex, 0, 0);
        var first = list.GetItemRect(0);
        Check(topIndex == 0, $"the view starts at row 0 (top index {topIndex})");
        // Just under the header: one row height at most, not partway down the grid.
        Check(first.Top >= 0 && first.Top <= first.Height * 2, $"row 0 is drawn at the top (y={first.Top})");
        Check(list.GetItemRect(rows - 1).Bottom <= list.ClientSize.Height, "the newest row is on screen");
    }

    private static int _next;

    private static void AddSessions(SessionStore store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var session = new Session
            {
                Request = new HttpRequestData { Method = "GET", Url = new Uri($"http://example.test/item/{_next++}") },
                Response = new HttpResponseData { StatusCode = 200 },
            };
            session.Completed = DateTimeOffset.Now;
            store.Add(session);
        }
    }

    /// <summary>
    /// Runs the message loop until the grid's 150 ms refresh timer has rebuilt it to the expected
    /// state, or 10 seconds pass, so a slow machine cannot fail a check that was merely early.
    /// </summary>
    private static void PumpUntil(Func<bool> rebuilt)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!rebuilt() && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Application.DoEvents();
    }

    private static ListView FindListView(Control root) =>
        Find(root) ?? throw new InvalidOperationException("The session grid has no ListView.");

    private static ListView? Find(Control root)
    {
        foreach (Control child in root.Controls)
            if ((child as ListView ?? Find(child)) is { } list) return list;
        return null;
    }

    private static void Check(bool condition, string message)
    {
        Console.WriteLine($"   {(condition ? "ok  " : "FAIL")}  {message}");
        if (!condition) _failures++;
    }
}
