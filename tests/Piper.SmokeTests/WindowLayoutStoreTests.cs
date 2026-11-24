using System.Drawing;
using Piper.App;
using Piper.App.Layout;
using Piper.Core.Sessions;

/// <summary>The v2 window-layout file: round trips, the v1 upgrade, and a hostile file never breaking startup.</summary>
internal static class WindowLayoutStoreTests
{
    private static readonly Rectangle[] Screen = [new Rectangle(0, 0, 1920, 1040)];

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("layout store round trips bounds, maximised and a moved layout", () =>
        {
            WithFile(path =>
            {
                var panels = PanelLayout.Default();
                panels.Move(PanelId.Log, DockArea.Bottom);
                panels.Float(PanelId.Filters, new Rectangle(100, 120, 500, 400), 144);
                panels.SetTopMost(PanelId.Filters, true);
                panels.Hide(PanelId.Composer);
                panels.LeftFraction = 0.4;
                panels.SetFlyoutFraction(DockArea.Right, 0.5);
                panels.SetAutoHidden(DockArea.Right, true);
                WindowLayoutStore.Save(new WindowLayoutStore.WindowLayout(new Rectangle(10, 20, 1200, 800), true, panels), path);

                var loaded = WindowLayoutStore.Load(path);
                runner.IsTrue(loaded is not null, "saved layout loads");
                runner.AreEqual(2, loaded!.Version, "version is 2");
                runner.AreEqual(new Rectangle(10, 20, 1200, 800), loaded.ToRectangle(), "bounds round trip");
                runner.IsTrue(loaded.Maximized, "maximised round trips");
                var p = loaded.Panels;
                p.Normalise(Screen);
                runner.AreEqual(DockArea.Bottom, p.AreaOf(PanelId.Log), "log stays at the bottom");
                runner.AreEqual(PanelId.Log, p.SelectedIn(DockArea.Bottom), "log is selected at the bottom");
                runner.IsTrue(p.IsFloating(PanelId.Filters), "filters still floats");
                runner.AreEqual(new Rectangle(100, 120, 500, 400), p.FloatingBounds(PanelId.Filters), "floating bounds round trip");
                runner.AreEqual(144, p.FloatingDpi(PanelId.Filters), "floating dpi round trips");
                runner.IsTrue(p.TopMost(PanelId.Filters), "top-most round trips");
                runner.IsTrue(p.IsHidden(PanelId.Composer), "hidden round trips");
                runner.AreEqual(0.4, p.LeftFraction, "left fraction round trips");
                runner.AreEqual(0.5, p.FlyoutFraction(DockArea.Right), "flyout round trips");
                runner.IsTrue(p.IsAutoHidden(DockArea.Right), "auto-hide round trips");
                runner.AreEqual("Inspectors,AutoResponder", Join(p.DockedIn(DockArea.Right)), "right area order is kept");
            });
            return Task.CompletedTask;
        });

        await runner.RunAsync("layout store upgrades a v1 file and rejects unusable files", () =>
        {
            WithFile(path =>
            {
                File.WriteAllText(path, """{"X":1,"Y":2,"Width":900,"Height":700,"Maximized":true}""");
                var v1 = WindowLayoutStore.Load(path)!;
                runner.AreEqual(new Rectangle(1, 2, 900, 700), v1.ToRectangle(), "v1 bounds load");
                runner.IsTrue(v1.Maximized, "v1 maximised loads");
                runner.AreEqual(Describe(PanelLayout.Default()), Describe(v1.Panels), "v1 gets the default panels");

                File.WriteAllText(path, "{ not json");
                runner.IsTrue(WindowLayoutStore.Load(path) is null, "malformed JSON gives null");
                File.WriteAllText(path, "[1,2]");
                runner.IsTrue(WindowLayoutStore.Load(path) is null, "a non-object root gives null");
                File.WriteAllText(path, """{"Width":0,"Height":0,"X":0,"Y":0}""");
                runner.IsTrue(WindowLayoutStore.Load(path) is null, "empty bounds give null");
                File.WriteAllText(path, """{"X":0,"Y":0,"Width":900}""");
                runner.IsTrue(WindowLayoutStore.Load(path) is null, "missing bounds give null");

                File.WriteAllText(path, """{"X":0,"Y":0,"Width":900,"Height":700,"Panels":""" + Nest(20) + "}");
                runner.IsTrue(WindowLayoutStore.Load(path) is null, "excess nesting gives null");

                const string Core = "{\"X\":0,\"Y\":0,\"Width\":900,\"Height\":700}";
                File.WriteAllText(path, Core + new string(' ', WindowLayoutStore.MaxFileBytes + 1 - Core.Length));
                runner.AreEqual(WindowLayoutStore.MaxFileBytes + 1, (int)new FileInfo(path).Length, "fixture is one byte over the cap");
                runner.IsTrue(WindowLayoutStore.Load(path) is null, "a file over the cap gives null");
                File.WriteAllText(path, Core + new string(' ', WindowLayoutStore.MaxFileBytes - Core.Length));
                runner.IsTrue(WindowLayoutStore.Load(path) is not null, "a file exactly at the cap loads");

                runner.IsTrue(WindowLayoutStore.Load(path + ".missing") is null, "a missing file gives null");
            });
            return Task.CompletedTask;
        });

        await runner.RunAsync("layout store round trips the inspector orientation and defaults to stacked", () =>
        {
            WithFile(path =>
            {
                var bounds = new Rectangle(1, 2, 900, 700);
                WindowLayoutStore.Save(new WindowLayoutStore.WindowLayout(bounds, false, PanelLayout.Default()) { InspectorSideBySide = true }, path);
                runner.IsTrue(WindowLayoutStore.Load(path)!.InspectorSideBySide, "side by side round trips");
                WindowLayoutStore.Save(new WindowLayoutStore.WindowLayout(bounds, false, PanelLayout.Default()), path);
                runner.IsTrue(!WindowLayoutStore.Load(path)!.InspectorSideBySide, "stacked round trips");

                foreach (var value in new[] { "\"true\"", "1", "null", "{}", "[true]" })
                {
                    File.WriteAllText(path, $$"""{"X":1,"Y":2,"Width":900,"Height":700,"InspectorSideBySide":{{value}}}""");
                    var loaded = WindowLayoutStore.Load(path);
                    runner.IsTrue(loaded is { InspectorSideBySide: false }, $"a malformed orientation ({value}) is stacked");
                }

                File.WriteAllText(path, """{"X":1,"Y":2,"Width":900,"Height":700}""");
                runner.IsTrue(!WindowLayoutStore.Load(path)!.InspectorSideBySide, "a file without the setting is stacked");
            });
            return Task.CompletedTask;
        });

        await runner.RunAsync("layout store falls back to defaults for bad panel data", () =>
        {
            WithFile(path =>
            {
                var empty = Load(path, "\"Panels\":[]");
                runner.AreEqual(Describe(PanelLayout.Default()), Describe(empty), "an empty panel list is the default");

                var text = Load(path, "\"Panels\":\"x\"");
                runner.AreEqual(Describe(PanelLayout.Default()), Describe(text), "a non-array panel list is the default");

                var mixed = Load(path, """
                    "Panels":[{"Id":"log","Area":"top"},{"Id":"filters","Area":"bottom","Order":0,"Selected":true},{"Id":"zzz","Area":"left"},5,null]
                    """);
                runner.AreEqual(DockArea.Right, mixed.AreaOf(PanelId.Log), "an invalid area leaves log at its default");
                runner.AreEqual(DockArea.Bottom, mixed.AreaOf(PanelId.Filters), "a valid entry beside it is kept");

                var dup = Load(path, """
                    "Panels":[{"Id":"log","Area":"bottom"},{"Id":"log","Area":"left"}]
                    """);
                runner.AreEqual(DockArea.Bottom, dup.AreaOf(PanelId.Log), "the first duplicate wins");

                var badFloat = Load(path, """
                    "Panels":[{"Id":"filters","Area":"bottom","Floating":{"X":"a"}}]
                    """);
                runner.AreEqual(DockArea.Right, badFloat.AreaOf(PanelId.Filters), "a bad floating block falls back to the default entry");
                runner.IsTrue(!badFloat.IsFloating(PanelId.Filters), "and does not float");
            });
            return Task.CompletedTask;
        });

        await runner.RunAsync("layout store output normalises to a dense, valid layout", () =>
        {
            WithFile(path =>
            {
                var order = Load(path, """
                    "Panels":[{"Id":"log","Area":"bottom","Order":-5},{"Id":"filters","Area":"bottom","Order":2147483647},{"Id":"composer","Area":"bottom","Order":7}]
                    """);
                order.Normalise(Screen);
                runner.AreEqual("Log,Composer,Filters", Join(order.DockedIn(DockArea.Bottom)), "orders sort and renumber");

                var sessions = Load(path, """
                    "Panels":[{"Id":"sessions","Area":"left","Hidden":true}]
                    """);
                sessions.Normalise(Screen);
                runner.IsTrue(!sessions.IsHidden(PanelId.Sessions), "sessions loads visible");

                var selection = Load(path, """
                    "Panels":[{"Id":"log","Area":"bottom","Selected":true},{"Id":"filters","Area":"bottom","Selected":true},{"Id":"composer","Area":"left","Selected":true}]
                    """);
                selection.Normalise(Screen);
                foreach (var area in Enum.GetValues<DockArea>())
                {
                    var docked = selection.DockedIn(area);
                    runner.IsTrue(docked.Count == 0 ? selection.SelectedIn(area) is null : docked.Contains(selection.SelectedIn(area)!.Value),
                        $"{area} has one valid selection");
                }

                var dpi = Load(path, """
                    "Panels":[{"Id":"log","Area":"bottom","Floating":{"X":0,"Y":0,"Width":400,"Height":300,"Dpi":-4}},{"Id":"filters","Area":"bottom","Floating":{"X":0,"Y":0,"Width":400,"Height":300,"Dpi":99999}}]
                    """);
                dpi.Normalise(Screen);
                runner.AreEqual(96, dpi.FloatingDpi(PanelId.Log), "a non-positive dpi becomes 96");
                runner.AreEqual(960, dpi.FloatingDpi(PanelId.Filters), "a huge dpi clamps to 960");

                var fractions = Load(path, """
                    "Areas":{"LeftFraction":"NaN","BottomFraction":1e308}
                    """);
                runner.AreEqual(0.55, fractions.LeftFraction, "a non-numeric fraction gives the default");
                runner.AreEqual(0.9, fractions.BottomFraction, "an enormous fraction clamps to 0.9");

                var overflow = Load(path, """
                    "Areas":{"LeftFraction":1e999,"left":{"Flyout":-3,"AutoHidden":true},"right":7}
                    """);
                runner.AreEqual(0.55, overflow.LeftFraction, "an overflowing fraction gives the default");
                runner.AreEqual(0.1, overflow.FlyoutFraction(DockArea.Left), "a negative flyout clamps to 0.1");
                overflow.Normalise(Screen);
                runner.IsTrue(!overflow.IsAutoHidden(DockArea.Left), "the sessions area never auto-hides");

                var hostile = Load(path, """
                    "Panels":[{"Id":"sessions","Area":"bottom","Hidden":true,"Floating":{"X":-90000,"Y":-90000,"Width":1,"Height":1}},
                              {"Id":"inspectors","Area":"left","Floating":{"X":2000000000,"Y":2000000000,"Width":2000000000,"Height":2000000000}},
                              {"Id":"composer","Area":"nowhere"},{"Id":"filters","Area":"right","Hidden":true},
                              {"Id":"filters","Area":"left"},{"Id":"log","Area":"bottom","Order":-2147483648}]
                    """);
                hostile.Normalise(Screen);
                foreach (var id in Enum.GetValues<PanelId>())
                {
                    if (hostile.IsHidden(id)) continue;
                    var placed = hostile.IsFloating(id) || hostile.DockedIn(hostile.AreaOf(id)).Contains(id);
                    runner.IsTrue(placed, $"{id} is docked or floating after repair");
                    if (hostile.FloatingBounds(id) is { } bounds)
                        runner.IsTrue(Screen[0].IntersectsWith(bounds), $"{id} floats on a screen");
                }
            });
            return Task.CompletedTask;
        });

        await runner.RunAsync("layout store tolerates a newer version and unknown fields", () =>
        {
            WithFile(path =>
            {
                File.WriteAllText(path, """
                    {"Version":99,"Extra":{"a":[1,2]},"X":5,"Y":6,"Width":1000,"Height":700,"Maximized":false,"Future":true,
                     "Panels":[{"Id":"log","Area":"bottom","Colour":"red"}],"Areas":{"Other":1}}
                    """);
                var loaded = WindowLayoutStore.Load(path);
                runner.IsTrue(loaded is not null, "a v99 file still loads");
                runner.AreEqual(DockArea.Bottom, loaded!.Panels.AreaOf(PanelId.Log), "known panel data is used");
            });
            return Task.CompletedTask;
        });

        await runner.RunAsync("layout store save leaves no temp files and never throws", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"piper-layout-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "window-layout.json");
                var layout = new WindowLayoutStore.WindowLayout(new Rectangle(1, 2, 900, 700), false, PanelLayout.Default());
                WindowLayoutStore.Save(layout, path);
                WindowLayoutStore.Save(layout, path);
                runner.AreEqual(1, Directory.GetFiles(directory).Length, "only the layout file remains");
                runner.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length, "no temp file remains");

                // A directory where the file should be makes the final move fail after the temp file is written.
                var blocked = Path.Combine(directory, "blocked");
                Directory.CreateDirectory(blocked);
                File.WriteAllText(Path.Combine(blocked, "keep"), "x");
                WindowLayoutStore.Save(layout, blocked);
                runner.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length, "a failed save deletes its temp file");

                // A parent that is a file cannot be created as a directory.
                WindowLayoutStore.Save(layout, Path.Combine(path, "child", "layout.json"));
                runner.IsTrue(true, "an uncreatable directory does not throw");

                var readOnly = Path.Combine(directory, "ro");
                Directory.CreateDirectory(readOnly);
                var roPath = Path.Combine(readOnly, "window-layout.json");
                File.WriteAllText(roPath, "{}");
                File.SetAttributes(roPath, FileAttributes.ReadOnly);
                try
                {
                    WindowLayoutStore.Save(layout, roPath);
                }
                finally
                {
                    File.SetAttributes(roPath, FileAttributes.Normal);
                }

                runner.AreEqual(0, Directory.GetFiles(readOnly, "*.tmp").Length, "a read-only target leaves no temp file");
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }

            return Task.CompletedTask;
        });

        await runner.RunAsync("layout store round trips session column widths and bounds hostile ones", () =>
        {
            WithFile(path =>
            {
                var layout = new WindowLayoutStore.WindowLayout(new Rectangle(0, 0, 1200, 800), false, PanelLayout.Default())
                {
                    SessionColumnWidths = new Dictionary<SessionSortColumn, int> { [SessionSortColumn.Host] = 240, [SessionSortColumn.Time] = 75 },
                };
                WindowLayoutStore.Save(layout, path);
                var loaded = WindowLayoutStore.Load(path)!.SessionColumnWidths;
                runner.AreEqual("Host=240,Time=75", Widths(loaded), "user widths round trip");

                runner.AreEqual("", Widths(WindowLayoutStore.Load(path + ".missing")?.SessionColumnWidths ?? new Dictionary<SessionSortColumn, int>()),
                    "a missing file gives no widths");
                runner.AreEqual("", Widths(Columns(path, "")), "a file without columns gives no widths");
                runner.AreEqual("", Widths(Columns(path, ",\"SessionColumns\":[1,2]")), "a non-object column block is ignored");
                runner.AreEqual("Path=20,Size=2000", Widths(Columns(path,
                    ",\"SessionColumns\":{\"path\":-50,\"size\":99999999,\"host\":\"wide\",\"time\":1.5,\"type\":1e999,\"evil\":300,\"Method\":90}")),
                    "widths clamp, non-integers and unknown or miscased ids are ignored");

                File.WriteAllText(path, "{ not json");
                runner.IsTrue(WindowLayoutStore.Load(path) is null, "malformed JSON falls back to defaults");
            });
            return Task.CompletedTask;
        });

        await runner.RunAsync("a hidden panel's place in the tab order survives a save and load", () =>
        {
            WithFile(path =>
            {
                var panels = PanelLayout.Default();
                panels.Hide(PanelId.Filters);
                WindowLayoutStore.Save(new WindowLayoutStore.WindowLayout(new Rectangle(0, 0, 1200, 800), false, panels), path);

                var loaded = WindowLayoutStore.Load(path)!.Panels;
                loaded.Normalise(Screen);
                runner.IsTrue(loaded.IsHidden(PanelId.Filters), "filters is still hidden");
                loaded.Show(PanelId.Filters);
                runner.AreEqual("Inspectors,Composer,Filters,AutoResponder,Log", Join(loaded.DockedIn(DockArea.Right)),
                    "shown again, filters is third, where it was");
            });
            return Task.CompletedTask;
        });
    }

    private static PanelLayout Load(string path, string members)
    {
        File.WriteAllText(path, "{\"X\":0,\"Y\":0,\"Width\":900,\"Height\":700," + members + "}");
        return WindowLayoutStore.Load(path)!.Panels;
    }

    private static IReadOnlyDictionary<SessionSortColumn, int> Columns(string path, string members)
    {
        File.WriteAllText(path, "{\"X\":0,\"Y\":0,\"Width\":900,\"Height\":700" + members + "}");
        return WindowLayoutStore.Load(path)!.SessionColumnWidths;
    }

    private static string Widths(IReadOnlyDictionary<SessionSortColumn, int> widths) =>
        string.Join(",", widths.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"));

    private static void WithFile(Action<string> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"piper-layout-{Guid.NewGuid():N}.json");
        try
        {
            body(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Nest(int depth) => new string('[', depth) + new string(']', depth);

    private static string Join(IEnumerable<PanelId> ids) => string.Join(",", ids);

    private static string Describe(PanelLayout layout) => string.Join(";", Enum.GetValues<DockArea>().Select(area =>
        $"{area}:{Join(layout.DockedIn(area))}:{layout.SelectedIn(area)}:{layout.IsAutoHidden(area)}")) + $";{layout.LeftFraction};{layout.BottomFraction}";
}
