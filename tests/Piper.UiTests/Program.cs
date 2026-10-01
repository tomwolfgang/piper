using System.Runtime.InteropServices;
using Piper.App.Controls;
using Piper.Core.Http;
using Piper.Core.Proxy;
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
            grid.FilterText = $"host:{RareHost}"; // every 30th setup row: 10 rows, fewer than the old top row
            PumpUntil(() => list.VirtualListSize < 300);
            ExpectRowsFromTop(list, 10);
        });

        Run("an imported session stays in the grid under a capture scope and a filterset", (store, grid, list) =>
        {
            // Live traffic is hidden by both; a file the user opened is not live traffic.
            grid.VisibilityFilter = session => session.ProcessName == "browser";
            grid.FiltersetFilter = _ => false;
            PumpUntil(() => list.VirtualListSize == 0);
            Check(list.VirtualListSize == 0, "setup: the scope and the filterset hide captured traffic");

            var imported = new Session
            {
                IsImported = true,
                ProcessName = "Fiddler SAZ",
                Request = new HttpRequestData { Method = "GET", Url = new Uri("http://imported.test/one") },
                Response = new HttpResponseData { StatusCode = 200 },
                Completed = DateTimeOffset.Now,
            };
            store.AddRange([imported]);
            PumpUntil(() => list.VirtualListSize == 1);
            Check(list.VirtualListSize == 1, $"the imported session is listed (got {list.VirtualListSize})");

            AddSessions(store, 3);
            PumpFor(500);
            Check(list.VirtualListSize == 1, $"live traffic stays hidden (got {list.VirtualListSize})");
        });

        RunPanel("an AutoResponder rule whose pattern timed out is marked broken in the panel");

        Console.WriteLine(_failures == 0 ? "UI tests passed." : $"{_failures} UI check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>A rule that was skipped for timing out says so in its Last match cell, until it is edited.</summary>
    private static void RunPanel(string name)
    {
        Console.WriteLine($"== {name}");
        var broken = new AutoResponderRule { Match = @"NOT:REGEX:^http://api\.example\.test/(a+)+$", Action = "*418" };
        var healthy = new AutoResponderRule { Match = "orders", Action = "*503" };
        var settings = new AutoResponderSettings { Enabled = true, Rules = [broken, healthy] };

        var responder = new AutoResponder();
        responder.Apply(settings);
        using var form = new Form { Width = 1000, Height = 500, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        var panel = new AutoResponderPanel(responder) { Dock = DockStyle.Fill };
        form.Controls.Add(panel);
        form.Show();
        panel.ApplySettings(settings);
        var list = FindListView(panel);
        Check(list.Items.Count == 2, "setup: both rules are listed");
        Check(list.Items[0].SubItems[4].Text.Length == 0, "before any request the Last match cell is empty");

        var uri = new Uri("http://api.example.test/" + new string('a', 40) + "!");
        var trap = new Session { Request = new HttpRequestData { Method = "GET", Url = uri, RequestTarget = uri.PathAndQuery } };
        Check(responder.Evaluate(trap).Outcome == AutoResponderOutcome.Passthrough, "the catastrophic request is not answered");
        panel.RefreshStatistics();
        Check(list.Items[0].SubItems[4].Text.Length > 0, "the timed-out rule's Last match cell says so");
        Check(list.Items[0].ToolTipText.Length > 0, "and explains it in a tooltip");
        Check(list.Items[1].SubItems[4].Text.Length == 0 && list.Items[1].ToolTipText.Length == 0, "its neighbour is not marked");

        responder.Apply(settings); // an edit recompiles the rules
        panel.RefreshStatistics();
        Check(list.Items[0].SubItems[4].Text.Length == 0 && list.Items[0].ToolTipText.Length == 0, "editing the rules clears the mark");
        form.Close();
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
        // Only where every row fits, so a high-DPI runner with taller rows does not fail it spuriously.
        if (first.Top + rows * first.Height <= list.ClientSize.Height)
            Check(list.GetItemRect(rows - 1).Bottom <= list.ClientSize.Height, "the newest row is on screen");
    }

    private const string RareHost = "rare.test";
    private static int _next;

    private static void AddSessions(SessionStore store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var host = _next % 30 == 0 ? RareHost : "example.test";
            var session = new Session
            {
                Request = new HttpRequestData { Method = "GET", Url = new Uri($"http://{host}/item/{_next++}") },
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

    /// <summary>Runs the message loop for a fixed time, for asserting that something does not happen.</summary>
    private static void PumpFor(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
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
