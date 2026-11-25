using System.Drawing;
using Piper.App;
using Piper.App.Controls;
using Piper.App.Layout;
using Piper.Core.Sessions;

// Remembered session-list column widths in the real grid: restoring before the grid has a handle (as
// MainForm does in its constructor), a restored column keeping its width through a resize while the
// others flex, and the restore -> UserColumnWidths -> window-layout.json -> restore round trip.
internal static class SessionColumnWidthUiTests
{
    private const int Host = (int)SessionSortColumn.Host;
    private const int Path = (int)SessionSortColumn.Path;

    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== remembered session column widths restore before the handle, survive a resize and round trip");

        using (var clamped = new SessionListView(new SessionStore()))
        {
            clamped.RestoreColumnWidths(new Dictionary<SessionSortColumn, int> { [SessionSortColumn.Size] = 99_999, [SessionSortColumn.Time] = -4 });
            check(Describe(clamped.UserColumnWidths) == "Size=2000,Time=20", $"restored widths are clamped ({Describe(clamped.UserColumnWidths)})");
        }

        var grid = new SessionListView(new SessionStore()) { Location = Point.Empty, Size = new Size(1100, 400) };
        var threw = false;
        try
        {
            grid.RestoreColumnWidths(new Dictionary<SessionSortColumn, int> { [SessionSortColumn.Host] = 240 });
        }
        catch (Exception)
        {
            threw = true;
        }

        check(!threw, "restoring widths before the grid has a handle does not throw");
        check(Describe(grid.UserColumnWidths) == "Host=240", $"restored widths are kept ({Describe(grid.UserColumnWidths)})");

        // The grid is sized directly, not the form: a CI desktop can be narrower than the widths used
        // here, and Windows caps a top-level window at the screen's size.
        using var form = new Form { Width = 600, Height = 500, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        form.Controls.Add(grid);
        form.Show();
        var list = All(grid).OfType<ListView>().First();
        pumpUntil(() => list.IsHandleCreated && list.Columns[Path].Width > 0);

        var host = list.Columns[Host].Width;
        var path = list.Columns[Path].Width;
        grid.Width = 1500;
        pumpUntil(() => list.Columns[Path].Width != path);
        check(list.Columns[Host].Width == host, $"a restored column keeps its width through a resize ({host} -> {list.Columns[Host].Width})");
        check(list.Columns[Path].Width > path, $"the columns the user did not size still flex ({path} -> {list.Columns[Path].Width})");

        var file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"piper-columns-{Guid.NewGuid():N}.json");
        try
        {
            WindowLayoutStore.Save(new WindowLayoutStore.WindowLayout(new Rectangle(0, 0, 1200, 800), false, PanelLayout.Default())
            {
                SessionColumnWidths = grid.UserColumnWidths,
            }, file);
            var again = new SessionListView(new SessionStore());
            again.RestoreColumnWidths(WindowLayoutStore.Load(file)!.SessionColumnWidths);
            check(Describe(again.UserColumnWidths) == "Host=240",
                $"widths survive a save and load into a new grid ({Describe(again.UserColumnWidths)})");
            again.Dispose();
        }
        finally
        {
            File.Delete(file);
        }

        // Every column pinned leaves ColumnLayout.Fit an all-zero weight vector: nothing grows, the
        // filler takes the spare width, and neither a refit nor a resize throws.
        var all = Enum.GetValues<SessionSortColumn>().ToDictionary(column => column, _ => 60);
        var pinnedThrew = false;
        try
        {
            grid.RestoreColumnWidths(all);
            grid.RefitColumns();
            grid.Width = 1200;
            pumpUntil(() => list.Columns[Path].Width == list.Columns[Host].Width);
            grid.Width = 1600;
            pumpUntil(() => true);
        }
        catch (Exception)
        {
            pinnedThrew = true;
        }

        check(!pinnedThrew, "with every column sized by the user, a refit and resizes do not throw");
        check(list.Columns[Path].Width == list.Columns[Host].Width && Math.Abs(list.Columns[Host].Width - host / 4) <= 1,
            $"and every column keeps its own width ({list.Columns[Host].Width}, {list.Columns[Path].Width}, expected {host / 4})");

        grid.ResetColumnWidths();
        pumpUntil(() => list.Columns[Path].Width > list.Columns[Host].Width);
        check(grid.UserColumnWidths.Count == 0, "reset forgets every width the user set");
        check(list.Columns[Path].Width > list.Columns[Host].Width,
            $"and the columns flex again ({list.Columns[Host].Width}, {list.Columns[Path].Width})");

        // Regression: a header drag was never remembered, because the width-changed event was only
        // trusted while the real left button was down over the grid. The drag here is the header's own
        // mouse messages, as the native control sees them; nothing reads the physical mouse.
        var header = SendMessage(list.Handle, LvmGetHeader, 0, 0);
        var edge = Enumerable.Range(0, Host + 1).Sum(index => list.Columns[index].Width) - 1;
        var before = list.Columns[Host].Width;
        SendMessage(header, WmLButtonDown, MkLButton, HeaderPoint(edge, 5));
        SendMessage(header, WmMouseMove, MkLButton, HeaderPoint(edge + 40, 5));
        SendMessage(header, WmLButtonUp, 0, HeaderPoint(edge + 40, 5));
        pumpUntil(() => grid.UserColumnWidths.ContainsKey(SessionSortColumn.Host));
        check(list.Columns[Host].Width > before, $"setup: the header drag widened Host ({before} -> {list.Columns[Host].Width})");
        check(grid.UserColumnWidths.ContainsKey(SessionSortColumn.Host),
            $"a header drag is remembered for the layout file ({Describe(grid.UserColumnWidths)})");
        grid.Width = 1300;
        pumpUntil(() => true);
        check(grid.UserColumnWidths.Count == 1,
            $"a resize after the drag remembers nothing else ({Describe(grid.UserColumnWidths)})");

        form.Close();
    }

    private const int LvmGetHeader = 0x1000 + 31;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int MkLButton = 0x0001;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    private static nint HeaderPoint(int x, int y) => (y << 16) | (x & 0xFFFF);

    private static string Describe(IReadOnlyDictionary<SessionSortColumn, int> widths) =>
        string.Join(",", widths.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));

    private static IEnumerable<Control> All(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in All(child)) yield return descendant;
        }
    }
}
