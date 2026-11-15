using System.Drawing;
using Piper.App.Layout;
using Piper.Core.Text;

/// <summary>The movable-panel placement model: where each panel lives and how a hostile layout is repaired.</summary>
internal static class PanelLayoutTests
{
    private static readonly PanelId[] All = Enum.GetValues<PanelId>();

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("default layout matches today's window", () =>
        {
            var layout = PanelLayout.Default();
            runner.AreEqual("Sessions", Join(layout.DockedIn(DockArea.Left)), "the sessions list is on the left");
            runner.AreEqual("Inspectors,Composer,Filters,AutoResponder,Log", Join(layout.DockedIn(DockArea.Right)), "the rest share the right area");
            runner.AreEqual("", Join(layout.DockedIn(DockArea.Bottom)), "nothing starts at the bottom");
            runner.AreEqual(PanelId.Inspectors, layout.SelectedIn(DockArea.Right), "inspectors is selected on the right");
            runner.AreEqual(0.55, layout.LeftFraction, "left fraction");
            runner.AreEqual(0.35, layout.BottomFraction, "bottom fraction");
            runner.AreEqual(0.45, layout.FlyoutFraction(DockArea.Right), "flyout fraction");
            return Task.CompletedTask;
        });

        await runner.RunAsync("move selects the panel at its target and keeps order dense", () =>
        {
            var layout = PanelLayout.Default();
            layout.Move(PanelId.Log, DockArea.Bottom);
            runner.AreEqual("Log", Join(layout.DockedIn(DockArea.Bottom)), "log moved to the bottom");
            runner.AreEqual(PanelId.Log, layout.SelectedIn(DockArea.Bottom), "and is selected there");
            runner.AreEqual("Inspectors,Composer,Filters,AutoResponder", Join(layout.DockedIn(DockArea.Right)), "the right area closed the gap");
            layout.Move(PanelId.Composer, DockArea.Bottom, 0);
            runner.AreEqual("Composer,Log", Join(layout.DockedIn(DockArea.Bottom)), "an index inserts at that position");
            layout.Move(PanelId.Composer, DockArea.Bottom, 99);
            runner.AreEqual("Log,Composer", Join(layout.DockedIn(DockArea.Bottom)), "an index past the end appends");
            layout.Move(PanelId.Composer, DockArea.Bottom, -5);
            runner.AreEqual("Composer,Log", Join(layout.DockedIn(DockArea.Bottom)), "a negative index inserts first");
            return Task.CompletedTask;
        });

        await runner.RunAsync("hide and show return to the last area", () =>
        {
            var layout = PanelLayout.Default();
            layout.Move(PanelId.Log, DockArea.Bottom);
            runner.IsTrue(layout.Hide(PanelId.Log), "a panel can be hidden");
            runner.IsTrue(layout.IsHidden(PanelId.Log), "it is hidden");
            runner.AreEqual("", Join(layout.DockedIn(DockArea.Bottom)), "a hidden panel is not listed");
            layout.Show(PanelId.Log);
            runner.AreEqual(DockArea.Bottom, layout.AreaOf(PanelId.Log), "it returns to the bottom");
            runner.AreEqual("Log", Join(layout.DockedIn(DockArea.Bottom)), "and is listed again");
            runner.IsFalse(layout.Hide(PanelId.Sessions), "the sessions list cannot be hidden");
            runner.IsFalse(layout.IsHidden(PanelId.Sessions), "and stays visible");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a hidden panel comes back at its old place in the tab order", () =>
        {
            var layout = PanelLayout.Default();
            layout.Hide(PanelId.Composer);
            runner.AreEqual("Inspectors,Filters,AutoResponder,Log", Join(layout.DockedIn(DockArea.Right)), "hidden composer leaves a gap");
            layout.Move(PanelId.Log, DockArea.Bottom);
            layout.Show(PanelId.Composer);
            runner.AreEqual("Inspectors,Composer,Filters,AutoResponder", Join(layout.DockedIn(DockArea.Right)), "it returns second, not last");
            runner.AreEqual(PanelId.Composer, layout.SelectedIn(DockArea.Right), "and is selected");

            layout.Hide(PanelId.AutoResponder);
            layout.Hide(PanelId.Inspectors);
            layout.Show(PanelId.Inspectors);
            runner.AreEqual("Inspectors,Composer,Filters", Join(layout.DockedIn(DockArea.Right)), "the first panel returns first");
            return Task.CompletedTask;
        });

        await runner.RunAsync("floating then docking returns to the area it came from", () =>
        {
            var layout = PanelLayout.Default();
            layout.Move(PanelId.Filters, DockArea.Left);
            layout.Float(PanelId.Filters, new Rectangle(10, 10, 400, 300), 96);
            runner.IsTrue(layout.IsFloating(PanelId.Filters), "it floats");
            runner.AreEqual(DockArea.Left, layout.AreaOf(PanelId.Filters), "the last area is remembered");
            runner.AreEqual(new Rectangle(10, 10, 400, 300), layout.FloatingBounds(PanelId.Filters), "bounds are kept");
            runner.AreEqual(96, layout.FloatingDpi(PanelId.Filters), "dpi is kept");
            runner.AreEqual("Sessions", Join(layout.DockedIn(DockArea.Left)), "a floating panel is not docked");
            layout.Dock(PanelId.Filters);
            runner.AreEqual(DockArea.Left, layout.AreaOf(PanelId.Filters), "docked back on the left");
            runner.IsFalse(layout.IsFloating(PanelId.Filters), "no longer floating");
            runner.IsTrue(layout.FloatingBounds(PanelId.Filters) is null, "no floating bounds when docked");
            layout.Float(PanelId.Log, new Rectangle(0, 0, 400, 300), 96);
            layout.Move(PanelId.Log, DockArea.Bottom);
            runner.IsFalse(layout.IsFloating(PanelId.Log), "moving a floating panel docks it");
            return Task.CompletedTask;
        });

        await runner.RunAsync("the sessions area cannot auto-hide", () =>
        {
            var layout = PanelLayout.Default();
            runner.IsFalse(layout.SetAutoHidden(DockArea.Left, true), "the sessions area refuses");
            runner.IsFalse(layout.IsAutoHidden(DockArea.Left), "and stays shown");
            layout.Move(PanelId.Sessions, DockArea.Right);
            runner.IsFalse(layout.CanAutoHide(DockArea.Right), "the area holding sessions cannot auto-hide");
            runner.IsTrue(layout.CanAutoHide(DockArea.Left), "the area it left can");
            runner.IsTrue(layout.SetAutoHidden(DockArea.Left, true), "and does");
            runner.IsTrue(layout.IsAutoHidden(DockArea.Left), "it is auto-hidden");
            return Task.CompletedTask;
        });

        await runner.RunAsync("sessions landing in an auto-hidden area shows it without normalising", () =>
        {
            var layout = PanelLayout.Default();
            runner.IsTrue(layout.SetAutoHidden(DockArea.Right, true), "the right area auto-hides while it holds no sessions");
            layout.Move(PanelId.Sessions, DockArea.Right);
            runner.IsFalse(layout.IsAutoHidden(DockArea.Right), "moving sessions in stops the auto-hide");

            // Sessions floats out of Left, so Left may auto-hide; docking it back must undo that.
            var other = PanelLayout.Default();
            other.Float(PanelId.Sessions, new Rectangle(0, 0, 400, 300), 96);
            runner.IsTrue(other.SetAutoHidden(DockArea.Left, true), "an area whose sessions list floats can auto-hide");
            runner.IsTrue(other.IsAutoHidden(DockArea.Left), "and is auto-hidden before the dock");
            other.Dock(PanelId.Sessions);
            runner.IsFalse(other.IsAutoHidden(DockArea.Left), "docking sessions back stops the auto-hide");
            runner.IsFalse(other.Hide(PanelId.Sessions), "sessions cannot be hidden, so Show has nothing to restore");
            return Task.CompletedTask;
        });

        await runner.RunAsync("an area whose last panel leaves stops auto-hiding", () =>
        {
            var moved = PanelLayout.Default();
            moved.Move(PanelId.Log, DockArea.Bottom);
            runner.IsTrue(moved.SetAutoHidden(DockArea.Bottom, true), "setup: the bottom auto-hides");
            moved.Move(PanelId.Log, DockArea.Right);
            runner.IsFalse(moved.IsAutoHidden(DockArea.Bottom), "moving its last panel out clears the auto-hide");
            moved.Move(PanelId.Composer, DockArea.Bottom);
            runner.IsFalse(moved.IsAutoHidden(DockArea.Bottom), "so a panel moved in later is docked and shown");

            var two = PanelLayout.Default();
            two.Move(PanelId.Log, DockArea.Bottom);
            two.Move(PanelId.Filters, DockArea.Bottom);
            two.SetAutoHidden(DockArea.Bottom, true);
            two.Move(PanelId.Log, DockArea.Right);
            runner.IsTrue(two.IsAutoHidden(DockArea.Bottom), "an area that still holds a panel stays auto-hidden");

            var floated = PanelLayout.Default();
            floated.Move(PanelId.Log, DockArea.Bottom);
            floated.SetAutoHidden(DockArea.Bottom, true);
            floated.Float(PanelId.Log, new Rectangle(0, 0, 400, 300), 96);
            runner.IsFalse(floated.IsAutoHidden(DockArea.Bottom), "floating the last panel clears it too");

            var hidden = PanelLayout.Default();
            hidden.Move(PanelId.Log, DockArea.Bottom);
            hidden.SetAutoHidden(DockArea.Bottom, true);
            hidden.Hide(PanelId.Log);
            runner.IsFalse(hidden.IsAutoHidden(DockArea.Bottom), "and so does hiding it");
            return Task.CompletedTask;
        });

        await runner.RunAsync("each panel has a minimum flyout width and bottom height", () =>
        {
            runner.AreEqual(560, PanelLayout.MinFlyoutWidth(PanelId.Composer), "Composer");
            runner.AreEqual(460, PanelLayout.MinFlyoutWidth(PanelId.AutoResponder), "AutoResponder");
            runner.AreEqual(380, PanelLayout.MinFlyoutWidth(PanelId.Filters), "Filters");
            runner.AreEqual(420, PanelLayout.MinFlyoutWidth(PanelId.Inspectors), "Inspectors");
            runner.AreEqual(300, PanelLayout.MinFlyoutWidth(PanelId.Log), "Log");
            runner.AreEqual(260, PanelLayout.MinBottomHeight([PanelId.Log, PanelId.Inspectors]), "the Inspectors need 260");
            runner.AreEqual(260, PanelLayout.MinBottomHeight([PanelId.Composer]), "so does the Composer");
            runner.AreEqual(120, PanelLayout.MinBottomHeight([PanelId.Log, PanelId.Filters]), "anything else 120");
            runner.AreEqual(0, PanelLayout.MinBottomHeight([]), "an empty area needs nothing");
            return Task.CompletedTask;
        });

        await runner.RunAsync("fractions reject NaN, infinity and out of range", () =>
        {
            runner.AreEqual(0.55, PanelLayout.ClampFraction(double.NaN, 0.55), "NaN falls back");
            runner.AreEqual(0.3, PanelLayout.ClampFraction(double.PositiveInfinity, 0.3), "infinity falls back");
            runner.AreEqual(0.3, PanelLayout.ClampFraction(double.NegativeInfinity, 0.3), "negative infinity falls back");
            runner.AreEqual(0.1, PanelLayout.ClampFraction(-4, 0.5), "low is raised");
            runner.AreEqual(0.9, PanelLayout.ClampFraction(7, 0.5), "high is lowered");
            var layout = PanelLayout.Default();
            layout.LeftFraction = double.NaN;
            runner.AreEqual(0.55, layout.LeftFraction, "the setter clamps");
            layout.SetFlyoutFraction(DockArea.Bottom, 50);
            runner.AreEqual(0.9, layout.FlyoutFraction(DockArea.Bottom), "flyout clamps");
            runner.AreEqual(0.45, PanelLayout.Default().FlyoutFraction(DockArea.Right), "flyout default");
            return Task.CompletedTask;
        });

        await runner.RunAsync("normalise brings a floating window on a removed monitor back", () =>
        {
            Rectangle[] screen = [new Rectangle(0, 0, 1920, 1040)];

            var layout = PanelLayout.Default();
            layout.Float(PanelId.Log, new Rectangle(2500, 100, 800, 600), 96);
            layout.Normalise(screen);
            var bounds = layout.FloatingBounds(PanelId.Log)!.Value;
            runner.IsTrue(screen[0].Contains(bounds), $"a window on a removed monitor ends up fully inside ({bounds})");

            layout.Float(PanelId.Log, new Rectangle(100, -500, 800, 600), 96);
            layout.Normalise(screen);
            runner.IsTrue(layout.FloatingBounds(PanelId.Log)!.Value.Y >= 0, "a caption above the screen is pulled down");

            layout.Float(PanelId.Log, new Rectangle(0, 0, 100000, 100000), 96);
            layout.Normalise(screen);
            bounds = layout.FloatingBounds(PanelId.Log)!.Value;
            runner.IsTrue(bounds.Width <= 1920 && bounds.Height <= 1040, $"an enormous window is clamped ({bounds})");

            layout.Float(PanelId.Log, new Rectangle(0, 0, 10, 10), 96);
            layout.Normalise(screen);
            bounds = layout.FloatingBounds(PanelId.Log)!.Value;
            runner.IsTrue(bounds.Width >= 300 && bounds.Height >= 200, $"a tiny window is enlarged ({bounds})");

            layout.Float(PanelId.Log, new Rectangle(0, 0, 400, 300), int.MaxValue);
            layout.Normalise(screen);
            runner.AreEqual(960, layout.FloatingDpi(PanelId.Log), "an absurd dpi is clamped");
            layout.Float(PanelId.Log, new Rectangle(0, 0, 400, 300), 1);
            layout.Normalise(screen);
            runner.AreEqual(48, layout.FloatingDpi(PanelId.Log), "a tiny dpi is raised");

            layout.Float(PanelId.Log, new Rectangle(0, 0, 400, 300), 0);
            layout.Normalise(screen);
            runner.AreEqual(96, layout.FloatingDpi(PanelId.Log), "an unset dpi becomes 96, not the minimum");

            layout.Float(PanelId.Log, new Rectangle(0, 0, 400, 300), 96);
            layout.Normalise([]);
            runner.IsFalse(layout.IsFloating(PanelId.Log), "with no work area a floating panel is docked");
            runner.AreEqual(DockArea.Right, layout.AreaOf(PanelId.Log), "back where it came from");
            return Task.CompletedTask;
        });

        await runner.RunAsync("normalise leaves every visible panel reachable", () =>
        {
            Rectangle[][] workAreaSets = [[], [new Rectangle(0, 0, 1920, 1040)], [new Rectangle(-1920, 0, 1920, 1040), new Rectangle(0, 0, 1280, 720)]];
            var rng = new Random(12345);
            var problems = new List<string>();
            for (var round = 0; round < 300; round++)
            {
                var layout = PanelLayout.Default();
                foreach (var id in All)
                    switch (rng.Next(5))
                    {
                        case 0: layout.Move(id, (DockArea)rng.Next(3), rng.Next(-2, 8)); break;
                        case 1: layout.Float(id, new Rectangle(rng.Next(-5000, 5000), rng.Next(-5000, 5000), rng.Next(-50, 100000), rng.Next(-50, 100000)), 96); break;
                        case 2: layout.Hide(id); break;
                        case 3: layout.Select(id); break;
                        default: layout.Float(id, new Rectangle(0, 0, 400, 300), 120); layout.Hide(id); break;
                    }
                foreach (var area in Enum.GetValues<DockArea>()) layout.SetAutoHidden(area, rng.Next(2) == 0);

                layout.Normalise(workAreaSets[round % workAreaSets.Length]);

                var reachable = new HashSet<PanelId>();
                foreach (var area in Enum.GetValues<DockArea>())
                {
                    var docked = layout.DockedIn(area);
                    foreach (var id in docked)
                        if (!reachable.Add(id)) problems.Add($"round {round}: {id} is in more than one place");
                    if (docked.Count > 0 && !(layout.SelectedIn(area) is { } selected && docked.Contains(selected)))
                        problems.Add($"round {round}: {area} selects none of its panels");
                }

                foreach (var id in All)
                    if (layout.IsFloating(id) && !reachable.Add(id)) problems.Add($"round {round}: {id} floats and is docked");

                var visible = All.Where(id => !layout.IsHidden(id)).ToHashSet();
                if (!visible.SetEquals(reachable)) problems.Add($"round {round}: docked and floating panels are not exactly the visible ones");
                if (layout.IsHidden(PanelId.Sessions)) problems.Add($"round {round}: sessions is hidden");
                if (layout.IsAutoHidden(layout.AreaOf(PanelId.Sessions))) problems.Add($"round {round}: sessions' area is auto-hidden");
            }

            runner.IsTrue(problems.Count == 0, "every visible panel is reachable after repair: " + string.Join("; ", problems.Take(3)));
            return Task.CompletedTask;
        });

        await runner.RunAsync("clone is independent of the original", () =>
        {
            var layout = PanelLayout.Default();
            var clone = layout.Clone();
            clone.Move(PanelId.Log, DockArea.Bottom);
            clone.LeftFraction = 0.2;
            runner.AreEqual(DockArea.Right, layout.AreaOf(PanelId.Log), "the original did not move");
            runner.AreEqual(0.55, layout.LeftFraction, "or change its fraction");
            return Task.CompletedTask;
        });

        await runner.RunAsync("bounded read stops at the cap", () =>
        {
            using var over = new MemoryStream(new byte[70 * 1024]);
            runner.IsFalse(BoundedRead.TryRead(over, 65536, out _, out _), "more than the cap is refused");

            using var exact = new MemoryStream(new byte[65536]);
            runner.IsTrue(BoundedRead.TryRead(exact, 65536, out _, out var length), "exactly the cap is accepted");
            runner.AreEqual(65536, length, "all of it was read");

            using var empty = new MemoryStream();
            runner.IsTrue(BoundedRead.TryRead(empty, 65536, out _, out length), "an empty stream is accepted");
            runner.AreEqual(0, length, "with no bytes");
            return Task.CompletedTask;
        });
    }

    private static void IsFalse(this TestRunner runner, bool condition, string what) => runner.IsTrue(!condition, what);

    private static string Join(IEnumerable<PanelId> ids) => string.Join(",", ids);
}
