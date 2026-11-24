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

        var grid = new SessionListView(new SessionStore()) { Dock = DockStyle.Fill };
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

        using var form = new Form { Width = 1100, Height = 500, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        form.Controls.Add(grid);
        form.Show();
        var list = All(grid).OfType<ListView>().First();
        pumpUntil(() => list.IsHandleCreated && list.Columns[Path].Width > 0);

        var host = list.Columns[Host].Width;
        var path = list.Columns[Path].Width;
        form.Width = 1500;
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

        form.Close();
    }

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
