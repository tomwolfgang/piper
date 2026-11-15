using System.Runtime.InteropServices;
using Piper.App;
using Piper.App.Controls;
using Piper.App.Layout;
using Piper.App.Theme;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

// PanelHost renders the main window's panels from a PanelLayout. These run it with the real panels, as
// MainForm builds them, because the behaviour that matters (focus, tab selection, check glyphs) lives in
// the native controls.
internal static class PanelHostTests
{
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmContextMenu = 0x007B;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        DefaultLayout(check, pumpUntil);
        ShowPanelKeepsFocus(check, pumpUntil);
        ArrowKeysKeepFocus(check, pumpUntil);
        SetTabChecked(check, pumpUntil);
        RevealHiddenPanel(check, pumpUntil);
        MoveLogToBottom(check, pumpUntil);
        TabMenuOpens(check, pumpUntil);
        EmptiedAreaCollapses(check, pumpUntil);
        HideThenReveal(check, pumpUntil);
        ResetRestoresDefault(check, pumpUntil);
        BottomPanelsKeepWorking(check, pumpUntil);
        ShrunkWindow(check, pumpUntil);
        SnapshotKeepsFractionsWhenTiny(check, pumpUntil);
        HiddenPanelThemed(check, pumpUntil);
        FocusChord(check);
        FloatThenDock(check, pumpUntil);
        FloatingTabMenu(check, pumpUntil);
        OwnerClosingDoesNotDock(check, pumpUntil);
        ShortcutsInFloatingWindow(check, pumpUntil);
        ZoomReachesFloatingWindow(check, pumpUntil);
        InspectorSurvivesFloating(check, pumpUntil);
        SessionsKeepViewState(check, pumpUntil);
        AutoHideRight(check, pumpUntil);
        AutoHideMenu(check, pumpUntil);
        FlyoutOverlaysLeft(check, pumpUntil);
        FlyoutCloses(check, pumpUntil);
        FlyoutInsideAtMinimumSize(check, pumpUntil);
        AutoHideStripTracksZoom(check, pumpUntil);
        AutoHideAccessibility(check, pumpUntil);
        FlyoutScreenshots(pumpUntil);
    }

    private const int WmLButtonDown = 0x0201;
    private const int WmMouseMove = 0x0200;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint GetFocus();

    private static nint MouseLParam(Point client) => (nint)(((client.Y & 0xFFFF) << 16) | (client.X & 0xFFFF));

    /// <summary>A left click on a strip item, delivered to the strip as the mouse would.</summary>
    private static void ClickStripItem(AutoHideStrip strip, PanelId id)
    {
        var item = strip.ItemBounds(id);
        var centre = MouseLParam(new Point(item.X + item.Width / 2, item.Y + item.Height / 2));
        SendMessage(strip.Handle, WmLButtonDown, 1, centre);
        SendMessage(strip.Handle, WmLButtonUp, 0, centre);
    }

    private static void AutoHideRight(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== auto-hiding Right gives the space to Left and shows a strip with five names");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var left = StripOf(rig, PanelId.Sessions)!;
        var widthBefore = left.Width;

        rig.Host.SetAutoHidden(DockArea.Right, true);
        PumpFor(pumpUntil, 100);
        var strip = rig.Host.AutoHideStripOf(DockArea.Right);
        check(rig.Host.IsAutoHidden(DockArea.Right) && rig.Host.Snapshot().IsAutoHidden(DockArea.Right), "the host and its layout have Right auto-hidden");
        check(strip.Visible && strip.Dock == DockStyle.Right, "a strip is shown along the right edge");
        string[] names = ["Inspectors", "Composer", "Filters", "AutoResponder", "Log"];
        check(strip.Panels.Select(panel => panel.Title).SequenceEqual(names), $"it names the five panels in order ({string.Join(", ", strip.Panels.Select(panel => panel.Title))})");
        check(Enum.GetValues<PanelId>().Where(id => id != PanelId.Sessions).All(id => rig.Registry.PageOf(id).Parent is null),
            "the five pages are out of every strip until a flyout shows one");
        check(left.Width > widthBefore && left.Right >= strip.Left - 1, $"the sessions area takes the space ({widthBefore} -> {left.Width}, strip at {strip.Left})");
        check(!rig.Host.AutoHideStripOf(DockArea.Left).Visible && !rig.Host.AutoHideStripOf(DockArea.Bottom).Visible, "no other strip is shown");

        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        rig.Host.SetAutoHidden(DockArea.Bottom, true);
        PumpFor(pumpUntil, 100);
        var bottom = rig.Host.AutoHideStripOf(DockArea.Bottom);
        check(bottom.Visible && bottom.Panels.Select(panel => panel.Id).SequenceEqual([PanelId.Log]), "an auto-hidden Bottom shows a strip with the Log");
        check(left.Bottom >= bottom.Top - 1 && left.Right >= strip.Left - 1, "with Right and Bottom auto-hidden the sessions area fills the window");

        rig.Host.SetAutoHidden(DockArea.Right, false);
        PumpFor(pumpUntil, 100);
        check(!strip.Visible && StripOf(rig, PanelId.Inspectors) is { } right && right.TabCount == 4 && right.FindForm() == rig.Form,
            "pinning Right again puts its panels back in the split");
        check(Math.Abs(left.Width - widthBefore) <= 2, $"and the sessions area its old width ({left.Width} vs {widthBefore})");

        rig.Host.SetAutoHidden(DockArea.Right, true);
        rig.Host.ShowPanel(PanelId.Composer, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        rig.Host.ResetLayout();
        PumpFor(pumpUntil, 100);
        check(!rig.Host.IsAutoHidden(DockArea.Right) && !rig.Host.IsAutoHidden(DockArea.Bottom) && !rig.Host.FlyoutOf(DockArea.Right).IsOpen,
            "Reset layout pins every area and closes the flyout");
        check(StripOf(rig, PanelId.Composer) == StripOf(rig, PanelId.Log) && StripOf(rig, PanelId.Log)?.TabCount == 5, "and every panel is back on the right");
    }

    private static void AutoHideMenu(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== the Auto-hide item is disabled for the sessions area");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        ToolStripMenuItem? Item(PanelId id) =>
            rig.Host.TabMenuItems(id).OfType<ToolStripMenuItem>().FirstOrDefault(item => item.Text == Strings.Panels.AutoHide);

        check(Item(PanelId.Sessions) is { Enabled: false, Checked: false }, "the sessions tab's Auto-hide area is greyed out");
        rig.Host.SetAutoHidden(DockArea.Left, true);
        check(!rig.Host.IsAutoHidden(DockArea.Left), "and the sessions area cannot be auto-hidden another way");
        check(Item(PanelId.Composer) is { Enabled: true, Checked: false }, "a right-hand tab's item is enabled and unchecked");
        Item(PanelId.Composer)?.PerformClick();
        PumpFor(pumpUntil, 100);
        check(rig.Host.IsAutoHidden(DockArea.Right), "clicking it auto-hides the Right area");
        check(Item(PanelId.Composer) is { Enabled: true, Checked: true }, "and the item shows checked next time");

        rig.Host.FloatPanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);
        check(Item(PanelId.Filters) is { Enabled: false }, "a floating panel's item is greyed out");
        rig.Host.DockPanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);

        rig.Host.ShowPanel(PanelId.Composer, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        var flyout = rig.Host.FlyoutOf(DockArea.Right);
        check(flyout.IsOpen && rig.Registry.PageOf(PanelId.Composer).Parent == flyout.Strip, "setup: the Composer shows in the flyout");
        Item(PanelId.Composer)?.PerformClick();
        PumpFor(pumpUntil, 100);
        check(!rig.Host.IsAutoHidden(DockArea.Right) && !flyout.IsOpen && StripOf(rig, PanelId.Composer)?.TabCount == 5,
            "unchecking it from the flyout's tab pins the area again and closes the flyout");
    }

    private static void FlyoutOverlaysLeft(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== clicking a strip name opens a flyout over Left without changing its width");
        using var rig = new Rig();
        var strips = new List<DarkTabControl>();
        rig.Host.StripCreated += (_, created) => strips.Add(created);
        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.SetAutoHidden(DockArea.Right, true);
        PumpFor(pumpUntil, 100);
        var flyout = rig.Host.FlyoutOf(DockArea.Right);
        check(strips.Contains(flyout.Strip), "the flyout's strip is handed to StripCreated, so session drops work on it");

        var left = StripOf(rig, PanelId.Sessions)!;
        var splits = Descendants(rig.Host).OfType<SplitContainer>().ToArray();
        var distances = splits.Select(split => split.SplitterDistance).ToArray();
        var leftBounds = left.Bounds;
        var gridWidth = rig.Grid.Width;

        ClickStripItem(rig.Host.AutoHideStripOf(DockArea.Right), PanelId.Filters);
        PumpFor(pumpUntil, 100);
        var filters = rig.Registry.PageOf(PanelId.Filters);
        check(flyout.IsOpen && flyout.Visible && filters.Parent == flyout.Strip, "the Filters show in the flyout");
        check(rig.Host.Controls.GetChildIndex(flyout) == 0, "the flyout is at the top of the z-order");
        check(rig.Host.AutoHideStripOf(DockArea.Right).OpenPanel == PanelId.Filters, "the strip marks the Filters as open");
        var overLeft = rig.Host.RectangleToScreen(flyout.Bounds);
        check(overLeft.IntersectsWith(left.RectangleToScreen(left.ClientRectangle)), "the flyout lies over the sessions area");
        check(left.Bounds == leftBounds && rig.Grid.Width == gridWidth, $"the sessions area did not move or resize ({left.Bounds} vs {leftBounds})");
        check(splits.Select(split => split.SplitterDistance).SequenceEqual(distances), "no splitter moved");
        check(rig.Registry.ContentOf(PanelId.Filters).ContainsFocus, $"a click on the strip moves the focus into the flyout (focus: {FocusedName(rig.Form)})");

        ClickStripItem(rig.Host.AutoHideStripOf(DockArea.Right), PanelId.Composer);
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen && rig.Registry.PageOf(PanelId.Composer).Parent == flyout.Strip && filters.Parent is null,
            "clicking another name swaps the flyout to that panel");

        rig.Host.AutoHideStripOf(DockArea.Right).DragHover(Center(rig.Host.AutoHideStripOf(DockArea.Right).ItemBounds(PanelId.AutoResponder)));
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen && rig.Registry.PageOf(PanelId.AutoResponder).Parent == flyout.Strip, "dragging a session over a name opens that panel's flyout");

        var width = flyout.Width;
        var grip = new Point(2, flyout.Height / 2);
        SendMessage(flyout.Handle, WmLButtonDown, 1, MouseLParam(grip));
        SendMessage(flyout.Handle, WmMouseMove, 1, MouseLParam(new Point(grip.X - 100, grip.Y)));
        SendMessage(flyout.Handle, WmLButtonUp, 0, MouseLParam(new Point(grip.X - 100, grip.Y)));
        PumpFor(pumpUntil, 100);
        check(Math.Abs(flyout.Width - (width + 100)) <= 2, $"dragging the inner edge widens the flyout ({width} -> {flyout.Width})");
        check(left.Bounds == leftBounds, "and still moves nothing under it");
        var fraction = rig.Host.Snapshot().FlyoutFraction(DockArea.Right);
        check(fraction > PanelLayout.DefaultFlyoutFraction, $"Snapshot records the new flyout fraction ({fraction})");

        static Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    private static void FlyoutCloses(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a mouse-down outside closes the flyout, Esc closes it, and a Passive reveal opens it without taking focus from the grid");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.SetAutoHidden(DockArea.Right, true);
        PumpFor(pumpUntil, 100);
        var flyout = rig.Host.FlyoutOf(DockArea.Right);
        var sessionsPage = rig.Registry.PageOf(PanelId.Sessions);

        rig.List.Focus();
        pumpUntil(() => rig.List.Focused);
        rig.Host.ShowPanel(PanelId.Log, PanelReveal.Passive);
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen && rig.Registry.PageOf(PanelId.Log).Parent == flyout.Strip, "a Passive reveal opens the Log's flyout");
        check(rig.List.Focused, $"and the grid keeps the focus (focus: {FocusedName(rig.Form)})");
        check(flyout.FilterInstalled, "the outside-click filter is installed while it is open");

        PostMessage(flyout.Strip.Handle, WmLButtonDown, 1, MouseLParam(new Point(5, flyout.Strip.Height - 5)));
        PostMessage(flyout.Strip.Handle, WmLButtonUp, 0, MouseLParam(new Point(5, flyout.Strip.Height - 5)));
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen, "a mouse-down inside the flyout leaves it open");

        PostMessage(sessionsPage.Handle, WmLButtonDown, 1, MouseLParam(new Point(20, 40)));
        PostMessage(sessionsPage.Handle, WmLButtonUp, 0, MouseLParam(new Point(20, 40)));
        PumpFor(pumpUntil, 150);
        check(!flyout.IsOpen && !flyout.Visible && rig.Registry.PageOf(PanelId.Log).Parent is null, "a mouse-down outside it closes it");
        check(!flyout.FilterInstalled, "and removes the filter");

        rig.Host.FocusPanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        var composer = rig.Registry.ContentOf(PanelId.Composer);
        check(flyout.IsOpen && composer.ContainsFocus, $"FocusPanel opens the flyout with the focus in it (focus: {FocusedName(rig.Form)})");
        var focused = Control.FromChildHandle(GetFocus()) ?? composer;
        check(ProcessCmdKey(focused, Keys.Escape), "Esc is taken by the flyout");
        PumpFor(pumpUntil, 100);
        check(!flyout.IsOpen && !flyout.FilterInstalled, "Esc closes it");
        check(rig.List.Focused, $"and the focus goes back to the grid (focus: {FocusedName(rig.Form)})");

        rig.Host.FocusPanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen && composer.ContainsFocus, "setup: the Composer flyout has the focus again");
        rig.List.Focus();
        PumpFor(pumpUntil, 150);
        check(!flyout.IsOpen && rig.List.Focused, $"a flyout opened with the focus closes when the focus leaves it, and the focus stays where it went (focus: {FocusedName(rig.Form)})");

        rig.Host.ShowPanel(PanelId.Filters, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen && rig.List.Focused, $"an Explicit ShowPanel opens it and the grid keeps the focus (open: {flyout.IsOpen}, focus: {FocusedName(rig.Form)})");
        rig.Host.CloseFlyouts();
        PumpFor(pumpUntil, 50);
        check(!flyout.IsOpen && !flyout.FilterInstalled, "CloseFlyouts, which the window calls on Deactivate, closes it");

        rig.Host.ShowPanel(PanelId.Filters, PanelReveal.Explicit);
        PumpFor(pumpUntil, 50);
        rig.Host.HidePanel(PanelId.Filters);
        PumpFor(pumpUntil, 50);
        check(!flyout.IsOpen && !flyout.FilterInstalled && rig.Host.AutoHideStripOf(DockArea.Right).Panels.Count == 4,
            "hiding the panel in the flyout closes it and takes the name off the strip");
        rig.Host.ShowPanel(PanelId.Filters, PanelReveal.Passive);
        PumpFor(pumpUntil, 50);
        check(!flyout.IsOpen, "a Passive reveal of a hidden panel still leaves it hidden");

        rig.Host.ShowPanel(PanelId.Log, PanelReveal.Explicit);
        PumpFor(pumpUntil, 50);
        check(flyout.FilterInstalled, "setup: the flyout is open again");
        rig.Form.Close();
        rig.Form.Dispose();
        check(!flyout.FilterInstalled, "disposing the window removes the filter");
    }

    private static void FlyoutInsideAtMinimumSize(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== the flyout stays inside the window at MinimumSize");
        using var rig = new Rig();
        rig.Form.MinimumSize = new Size(900, 600); // as MainForm
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        rig.Host.SetAutoHidden(DockArea.Right, true);
        rig.Host.SetAutoHidden(DockArea.Bottom, true);
        rig.Host.SetFlyoutFraction(DockArea.Right, 0.9);
        PumpFor(pumpUntil, 100);

        var right = rig.Host.FlyoutOf(DockArea.Right);
        var bottom = rig.Host.FlyoutOf(DockArea.Bottom);
        rig.Host.ShowPanel(PanelId.Inspectors, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        check(right.IsOpen && rig.Host.ClientRectangle.Contains(right.Bounds) && right.Width > 0, $"at full size the Right flyout is inside the host ({right.Bounds})");

        rig.Form.Size = rig.Form.MinimumSize;
        PumpFor(pumpUntil, 200);
        check(right.IsOpen && rig.Host.ClientRectangle.Contains(right.Bounds) && right.Width > 0 && right.Height > 0,
            $"at MinimumSize it is still inside ({right.Bounds} in {rig.Host.ClientRectangle})");
        check(!right.Bounds.IntersectsWith(rig.Host.AutoHideStripOf(DockArea.Right).Bounds)
              && !right.Bounds.IntersectsWith(rig.Host.AutoHideStripOf(DockArea.Bottom).Bounds), "and it covers neither strip");

        rig.Host.ShowPanel(PanelId.Log, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        check(!right.IsOpen && bottom.IsOpen, "opening the Bottom flyout closes the Right one");
        check(rig.Host.ClientRectangle.Contains(bottom.Bounds) && bottom.Height > 0, $"the Bottom flyout is inside too ({bottom.Bounds})");

        rig.Form.Size = new Size(1200, 800);
        PumpFor(pumpUntil, 200);
        check(rig.Host.ClientRectangle.Contains(bottom.Bounds) && bottom.Width == rig.Host.ClientSize.Width - rig.Host.AutoHideStripOf(DockArea.Right).Width,
            $"and follows the window as it grows ({bottom.Bounds})");
    }

    private static void AutoHideStripTracksZoom(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        // Piper's own zoom stops at 150%; on a 150% display that is the 200%-plus text the strips must fit.
        Console.WriteLine("== zoom 200% then 100%: the strip thickness tracks Font.Height");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        Palette.Apply(rig.Form);
        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        rig.Host.SetAutoHidden(DockArea.Right, true);
        rig.Host.SetAutoHidden(DockArea.Bottom, true);
        PumpFor(pumpUntil, 100);
        var right = rig.Host.AutoHideStripOf(DockArea.Right);
        var bottom = rig.Host.AutoHideStripOf(DockArea.Bottom);
        var normalHeight = right.Font.Height;
        check(right.Width == right.Font.Height + 12 && bottom.Height == bottom.Font.Height + 12, $"at 100% the strips are Font.Height + 12 thick ({right.Width}, {bottom.Height})");

        check(right.Panels.All(panel => right.ItemBounds(panel.Id).Height >= TextRenderer.MeasureText(panel.Title, right.Font).Width),
            "at 100% each name has room for its whole text");

        try
        {
            SetZoom(FontScaleSettingsStore.MaxStep);
            PumpFor(pumpUntil, 100);
            check(right.Font.Height >= normalHeight * 14 / 10, $"setup: the font grew to the largest zoom ({normalHeight} -> {right.Font.Height})");
            check(right.Width == right.Font.Height + 12 && bottom.Height == bottom.Font.Height + 12, $"at the largest zoom they are Font.Height + 12 thick ({right.Width}, {bottom.Height})");
            var items = right.Panels.Select(panel => right.ItemBounds(panel.Id)).ToArray();
            check(items.All(item => item.Width == right.Width && item.Height > 0 && item.Bottom <= right.Height) && items.Zip(items.Skip(1)).All(pair => pair.First.Bottom <= pair.Second.Top),
                $"every name stays on the strip without overlapping the next ({string.Join(", ", items.Select(item => $"{item.Top}-{item.Bottom}"))} in {right.Height})");
            var log = bottom.ItemBounds(PanelId.Log);
            check(log.Width >= TextRenderer.MeasureText("Log", bottom.Font).Width && log.Height == bottom.Height, "a name with room keeps its whole text");
            rig.Form.Height = 1400;
            PumpFor(pumpUntil, 100);
            check(right.Panels.All(panel => right.ItemBounds(panel.Id).Height >= TextRenderer.MeasureText(panel.Title, right.Font).Width),
                $"and in a taller window every name is whole again (strip {right.Height})");
        }
        finally
        {
            SetZoom(0);
        }

        PumpFor(pumpUntil, 100);
        check(right.Font.Height == normalHeight && right.Width == normalHeight + 12 && bottom.Height == bottom.Font.Height + 12, $"back at 100% they are thin again ({right.Width})");

        void SetZoom(int step)
        {
            typeof(Palette).Assembly.GetType("Piper.App.Theme.FontScale", throwOnError: true)!.GetMethod("SetStep")!.Invoke(null, [step]);
            Palette.RescaleFonts();
            Palette.Apply(rig.Form); // as ChangeFontScale does
            rig.Host.ApplyTheme();
        }
    }

    private static void AutoHideAccessibility(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== the accessible children are page tabs named after the panels");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.SetAutoHidden(DockArea.Right, true);
        PumpFor(pumpUntil, 100);
        var strip = rig.Host.AutoHideStripOf(DockArea.Right);
        var accessible = strip.AccessibilityObject;
        check(accessible.Role == AccessibleRole.PageTabList, $"the strip is a page tab list ({accessible.Role})");
        var children = Enumerable.Range(0, accessible.GetChildCount()).Select(accessible.GetChild).ToArray();
        check(children.Length == 5 && children.All(child => child?.Role == AccessibleRole.PageTab), "it has five page tabs");
        check(children.Select(child => child?.Name).SequenceEqual(["Inspectors", "Composer", "Filters", "AutoResponder", "Log"]), "named after the panels");
        check(children.All(child => child is not null && strip.RectangleToScreen(strip.ClientRectangle).Contains(child.Bounds)), "each lies on the strip");
        children[2]?.DoDefaultAction();
        PumpFor(pumpUntil, 100);
        check(rig.Host.FlyoutOf(DockArea.Right).IsOpen && rig.Registry.PageOf(PanelId.Filters).Parent == rig.Host.FlyoutOf(DockArea.Right).Strip,
            "its default action opens the panel's flyout");
        check(children[2]?.State.HasFlag(AccessibleStates.Selected) == true, "and the open one is selected");
    }

    /// <summary>
    /// Saves the Right flyout open in both themes, for a visual review, when PIPER_UI_SCREENSHOTS names
    /// a directory. Not a check: nothing is asserted.
    /// </summary>
    private static void FlyoutScreenshots(Action<Func<bool>> pumpUntil)
    {
        if (Environment.GetEnvironmentVariable("PIPER_UI_SCREENSHOTS") is not { Length: > 0 } directory) return;
        Console.WriteLine($"== screenshots of the Right flyout into {directory}");
        var wasLight = Palette.IsLightMode;
        try
        {
            foreach (var light in new[] { false, true })
            {
                if (Palette.IsLightMode != light) Palette.ToggleMode();
                using var rig = new Rig();
                rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
                rig.Form.Show();
                Palette.Apply(rig.Form);
                rig.Host.ApplyTheme();
                pumpUntil(() => rig.Grid.Width > 0);
                rig.Host.SetAutoHidden(DockArea.Right, true);
                rig.Host.ShowPanel(PanelId.Filters, PanelReveal.Explicit);
                PumpFor(pumpUntil, 300);
                // DrawToBitmap paints children in reverse z-order and would hide the flyout, so copy the screen.
                rig.Form.TopMost = true;
                PumpFor(pumpUntil, 300);
                var client = rig.Form.RectangleToScreen(rig.Form.ClientRectangle);
                using var bitmap = new Bitmap(client.Width, client.Height);
                using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(client.Location, Point.Empty, client.Size);
                bitmap.Save(Path.Combine(directory, light ? "task-7-flyout-light.png" : "task-7-flyout-dark.png"));
            }
        }
        finally
        {
            if (Palette.IsLightMode != wasLight) Palette.ToggleMode();
        }
    }

    private const int WmLButtonDblClk = 0x0203;
    private const int WmLButtonUp = 0x0202;

    private static void FloatThenDock(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== float then dock keeps the panel and its area");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        PumpFor(pumpUntil, 100);
        var right = StripOf(rig, PanelId.Composer)!;
        var bottom = StripOf(rig, PanelId.Log)!;
        var composer = rig.Registry.PageOf(PanelId.Composer);
        var log = rig.Registry.PageOf(PanelId.Log);

        rig.Host.FloatPanel(PanelId.Composer, new Point(120, 140));
        PumpFor(pumpUntil, 150);
        var window = rig.Host.FloatingWindow(PanelId.Composer);
        check(window is { Visible: true } && composer.FindForm() == window, "Float puts the Composer in a window of its own");
        if (window is null) return;
        check(Form.ActiveForm != window, "opening the window does not activate it");
        check(window.Owner == rig.Form && !window.ShowInTaskbar && window.Text == composer.Text && window.FormBorderStyle == FormBorderStyle.Sizable,
            "the window is owned by the main window, out of the taskbar, sizable and titled with the panel's name");
        check(window.Location == new Point(120, 140), $"it opens at the point asked for ({window.Location})");
        check(!right.TabPages.Contains(composer) && rig.Host.Snapshot().IsFloating(PanelId.Composer), "the Composer left the right strip and the layout has it floating");
        check(rig.Host.Snapshot().AreaOf(PanelId.Composer) == DockArea.Right, "and still remembers the right area");

        rig.List.Focus();
        pumpUntil(() => rig.List.Focused);
        rig.Host.ShowPanel(PanelId.Composer, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        check(rig.List.Focused && Form.ActiveForm != window, $"revealing a floating panel leaves the grid the focus (focus: {FocusedName(rig.Form)})");

        window.WindowState = FormWindowState.Minimized;
        PumpFor(pumpUntil, 100);
        rig.List.Focus();
        pumpUntil(() => window.WindowState == FormWindowState.Minimized && rig.List.Focused);
        check(window.WindowState == FormWindowState.Minimized, "setup: the floating Composer is minimised");
        rig.Host.ShowPanel(PanelId.Composer, PanelReveal.Explicit);
        PumpFor(pumpUntil, 150);
        check(window.WindowState != FormWindowState.Minimized && window.Visible, "revealing a minimised floating panel restores its window");
        check(rig.List.Focused && Form.ActiveForm != window, $"and the grid keeps the focus (focus: {FocusedName(rig.Form)})");

        rig.Host.DockPanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        check(composer.Parent == right && right.SelectedTab == composer, "Dock puts the Composer back on the right, selected");
        check(rig.Host.Snapshot().AreaOf(PanelId.Composer) == DockArea.Right && !rig.Host.Snapshot().IsFloating(PanelId.Composer), "the layout has it docked on the right again");
        check(window.IsDisposed && rig.Host.FloatingWindow(PanelId.Composer) is null, "and its window is gone");

        rig.Host.FloatPanel(PanelId.Log);
        PumpFor(pumpUntil, 100);
        var logWindow = rig.Host.FloatingWindow(PanelId.Log);
        check(logWindow is { Visible: true } && bottom.TabCount == 0, "the Log floats out of the bottom area");
        logWindow?.Close();
        PumpFor(pumpUntil, 100);
        check(log.Parent == bottom && rig.Host.Snapshot().AreaOf(PanelId.Log) == DockArea.Bottom && !rig.Host.Snapshot().IsFloating(PanelId.Log),
            "closing its window docks it at the bottom, where it came from");

        rig.Host.FloatPanel(PanelId.Log);
        PumpFor(pumpUntil, 100);
        var floatingStrip = log.Parent as DarkTabControl;
        check(floatingStrip is not null && floatingStrip.FindForm() is FloatingPanelForm, "setup: the Log floats again");
        if (floatingStrip is null) return;
        var tab = floatingStrip.GetTabRect(0);
        var lParam = ((tab.Y + tab.Height / 2) << 16) | (tab.X + tab.Width / 2);
        SendMessage(floatingStrip.Handle, WmLButtonDblClk, 1, lParam);
        SendMessage(floatingStrip.Handle, WmLButtonUp, 0, lParam);
        PumpFor(pumpUntil, 100);
        check(log.Parent == bottom && !rig.Host.Snapshot().IsFloating(PanelId.Log), "double-clicking a floating window's tab docks the panel");
    }

    private static void FloatingTabMenu(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== the tab menu floats, docks and keeps a floating window on top");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        check(Enum.GetValues<PanelId>().All(rig.Host.CanFloat), "every panel can float");

        ToolStripMenuItem[] Items() => rig.Host.TabMenuItems(PanelId.Filters).OfType<ToolStripMenuItem>().ToArray();
        ToolStripMenuItem? Item(ToolStripMenuItem[] items, string text) => items.FirstOrDefault(item => item.Text == text);

        var docked = Items();
        var topMost = Item(docked, Strings.Panels.TopMost);
        check(Item(docked, Strings.Panels.Float) is { Enabled: true } && Item(docked, Strings.Panels.Dock) is null, "a docked panel's menu offers Float, not Dock");
        check(topMost is { Enabled: false, Checked: false }, "Always on top is greyed out while docked");
        Item(docked, Strings.Panels.Float)?.PerformClick();
        PumpFor(pumpUntil, 100);
        var window = rig.Host.FloatingWindow(PanelId.Filters);
        check(window is { Visible: true }, "Float floats the Filters");

        var floating = Items();
        check(Item(floating, Strings.Panels.Dock) is { Enabled: true } && Item(floating, Strings.Panels.Float) is null, "a floating panel's menu offers Dock, not Float");
        string[] moves = [Strings.Panels.MoveLeft, Strings.Panels.MoveRight, Strings.Panels.MoveBottom];
        check(moves.All(text => Item(floating, text) is { Enabled: true }), "every Move to item is enabled for a floating panel");
        Item(floating, Strings.Panels.TopMost)?.PerformClick();
        check(window is { TopMost: true } && rig.Host.Snapshot().TopMost(PanelId.Filters), "Always on top makes the window topmost and the layout records it");
        check(Item(Items(), Strings.Panels.TopMost) is { Enabled: true, Checked: true }, "and the item shows checked next time");

        Item(Items(), Strings.Panels.Dock)?.PerformClick();
        PumpFor(pumpUntil, 100);
        check(StripOf(rig, PanelId.Filters) == StripOf(rig, PanelId.Inspectors) && window!.IsDisposed, "Dock puts the Filters back on the right and closes its window");

        Item(Items(), Strings.Panels.Float)?.PerformClick();
        PumpFor(pumpUntil, 100);
        check(rig.Host.FloatingWindow(PanelId.Filters) is { TopMost: true }, "floating it again keeps it on top");
        rig.Host.MoveTo(PanelId.Filters, DockArea.Bottom);
        PumpFor(pumpUntil, 100);
        check(rig.Host.FloatingWindow(PanelId.Filters) is null && StripOf(rig, PanelId.Filters) is { } strip && strip.FindForm() == rig.Form,
            "Move to Bottom docks a floating panel and closes its window");
        rig.Host.FloatPanel(PanelId.Filters);
        rig.Host.HidePanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);
        check(rig.Host.FloatingWindow(PanelId.Filters) is null && rig.Registry.PageOf(PanelId.Filters).Parent is null, "hiding a floating panel closes its window and keeps the page");
        rig.Host.FloatPanel(PanelId.Filters);
        rig.Host.ResetLayout();
        PumpFor(pumpUntil, 100);
        check(rig.Host.FloatingWindow(PanelId.Filters) is null && StripOf(rig, PanelId.Filters) == StripOf(rig, PanelId.Inspectors), "Reset layout docks every floating window");
    }

    private static void OwnerClosingDoesNotDock(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== closing a floating window docks it; owner closing does not");
        var rig = new Rig();
        var disposed = Enum.GetValues<PanelId>().ToDictionary(id => id, _ => 0);
        foreach (var id in Enum.GetValues<PanelId>()) rig.Registry.ContentOf(id).Disposed += (_, _) => disposed[id]++;
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.FloatPanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        var window = rig.Host.FloatingWindow(PanelId.Composer);
        var docks = 0;
        if (window is not null) window.DockRequested += (_, _) => docks++;
        check(window is { Visible: true }, "setup: the Composer floats");

        Exception? error = null;
        try
        {
            rig.Form.Close();
            PumpFor(pumpUntil, 100);
            check(docks == 0, "closing the owner does not ask to dock the floating window");
            check(!rig.Registry.ContentOf(PanelId.Composer).IsDisposed, "the floating Composer's content is left for the registry");
            rig.Form.Dispose();
            check(window?.IsDisposed != false, "the floating window goes with its owner");
            rig.Registry.Dispose();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        check(error is null, $"nothing throws ({error?.GetType().Name}: {error?.Message})");
        check(disposed.All(pair => pair.Value == 1), $"each panel's content is disposed exactly once ({string.Join(", ", disposed.Select(pair => $"{pair.Key}={pair.Value}"))})");

        Console.WriteLine("== while shutting down, closing a floating window returns its page without docking");
        using var second = new Rig();
        second.Form.Show();
        pumpUntil(() => second.Grid.Width > 0);
        second.Host.FloatPanel(PanelId.Log);
        PumpFor(pumpUntil, 100);
        var logWindow = second.Host.FloatingWindow(PanelId.Log);
        var logDocks = 0;
        if (logWindow is not null) logWindow.DockRequested += (_, _) => logDocks++;
        second.Host.FloatPanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);
        var filtersWindow = second.Host.FloatingWindow(PanelId.Filters);
        second.Host.ShuttingDown = true;
        check(logWindow is { Enabled: false } && filtersWindow is { Enabled: false },
            "once shutting down, every floating window is disabled, so the main window's shortcuts cannot run from it");
        logWindow?.Close();
        PumpFor(pumpUntil, 100);
        var logPage = second.Registry.PageOf(PanelId.Log);
        check(logDocks == 0 && logPage.Parent is null && !logPage.IsDisposed, "the page goes back to the registry, undisposed, and nothing docks");
        check(second.Host.Snapshot().IsFloating(PanelId.Log), "and the layout still has the Log floating, for the next start");
    }

    private static void ShortcutsInFloatingWindow(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== Ctrl+X in a floating Composer's text box doesn't clear the store, Ctrl+X on a floating grid does");
        using var rig = new Rig();
        var cleared = 0;
        // MainForm's Ctrl+X rule, through the same text-input check its dispatcher uses.
        rig.Host.RouteKeys(keys =>
        {
            if (keys != (Keys.Control | Keys.X) || TextInputFocus.IsTextInputFocused(Form.ActiveForm)) return false;
            rig.Store.Clear();
            cleared++;
            return true;
        }, (_, _) => { });
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Store.Add(new Session { Request = new HttpRequestData { Method = "GET", Url = new Uri("http://example.test/keep") } });

        rig.Host.FloatPanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        var composerWindow = rig.Host.FloatingWindow(PanelId.Composer);
        var box = Descendants(rig.Registry.ContentOf(PanelId.Composer)).OfType<TextBoxBase>().FirstOrDefault(control => control.Visible && control.Enabled);
        check(composerWindow is not null && box is not null, "setup: the Composer floats and has a text box");
        if (composerWindow is null || box is null) return;
        composerWindow.Activate();
        PumpFor(pumpUntil, 100);
        box.Focus();
        pumpUntil(() => Form.ActiveForm == composerWindow && box.Focused);
        check(Form.ActiveForm == composerWindow && box.Focused, $"setup: the floating Composer is active with its text box focused (active: {Form.ActiveForm?.Text})");

        var handled = ProcessCmdKey(box, Keys.Control | Keys.X);
        check(!handled && cleared == 0 && rig.Store.Count == 1, "Ctrl+X in the floating Composer's text box is left to the box");

        rig.Host.FloatPanel(PanelId.Sessions);
        PumpFor(pumpUntil, 100);
        var gridWindow = rig.Host.FloatingWindow(PanelId.Sessions);
        check(gridWindow is not null, "setup: the sessions grid floats");
        if (gridWindow is null) return;
        gridWindow.Activate();
        rig.List.Focus();
        pumpUntil(() => Form.ActiveForm == gridWindow && rig.List.Focused);
        check(ProcessCmdKey(rig.List, Keys.Control | Keys.X) && cleared == 1 && rig.Store.Count == 0, "Ctrl+X on the floating grid clears the capture");
    }

    private static void ZoomReachesFloatingWindow(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== zoom and theme reach a floating window");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        Palette.Apply(rig.Form);
        rig.Host.FloatPanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        var composer = rig.Registry.ContentOf(PanelId.Composer);
        var window = rig.Host.FloatingWindow(PanelId.Composer);
        var wasLight = Palette.IsLightMode;
        try
        {
            Zoom("ZoomIn");
            Zoom("ZoomIn");
            Palette.RescaleFonts();
            Palette.Apply(rig.Form); // as ChangeFontScale does
            rig.Host.ApplyTheme();
            check(composer.Font.Size == rig.Filters.Font.Size && composer.Font.Size == Palette.UiFont.Size,
                $"the floating Composer's font follows the zoom ({composer.Font.Size} vs docked {rig.Filters.Font.Size})");

            Palette.ToggleMode();
            Palette.Apply(rig.Form);
            rig.Host.ApplyTheme();
            check(window is not null && window.BackColor == Palette.Background && composer.BackColor == Palette.Background, "and a theme toggle reaches the window and its panel");
        }
        finally
        {
            if (Palette.IsLightMode != wasLight) Palette.ToggleMode();
            Zoom("Reset");
            Palette.RescaleFonts();
            Palette.Apply(rig.Form);
            rig.Host.ApplyTheme();
        }

        // FontScale is internal to Piper.App, and a linked copy would not be the one Palette reads.
        static void Zoom(string method) =>
            typeof(Palette).Assembly.GetType("Piper.App.Theme.FontScale", throwOnError: true)!.GetMethod(method)!.Invoke(null, null);
    }

    private static void InspectorSurvivesFloating(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== the inspector renders and is hardened after float, dock, float");
        using var rig = new Rig();
        if (!rig.Host.CanFloat(PanelId.Inspectors))
        {
            Console.WriteLine("   (the Inspectors cannot float; skipped)");
            return;
        }

        try { Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException)
        {
            Console.WriteLine("   (the WebView2 Runtime is not installed here; skipped)");
            return;
        }

        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var response = (MessageInspector)typeof(InspectorPanel).GetField("_response", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(rig.Inspector)!;

        // The inspector's WebpView is Piper.App's, not the copy linked into this assembly, so it is read by reflection.
        Control? View() => Descendants(response).FirstOrDefault(control => control.GetType().Name == "WebpView");
        T Read<T>(Control view, string name) =>
            (T)view.GetType().GetProperty(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(view)!;
        bool HoldsBody(Control? view) => view is not null && Read<bool>(view, "HoldsBody");

        Control? Shown(string summary)
        {
            var message = new HttpResponseData { Body = Convert.FromBase64String("UklGRiAAAABXRUJQVlA4TBQAAAAvD8ABAAcQ/Y/+BwCC8D9uIaL/qQ==") };
            message.Headers.Add("Content-Type", "image/webp");
            response.SetMessage(message, summary);
            // The first WebView2 start in a process can take well over the pump's 10 s.
            for (var attempt = 0; attempt < 6 && !HoldsBody(View()); attempt++) pumpUntil(() => HoldsBody(View()));
            return View();
        }

        var first = Shown("webp 0");
        check(HoldsBody(first), "setup: the docked inspector shows a WebP");
        Action[] moves = [() => rig.Host.FloatPanel(PanelId.Inspectors), () => rig.Host.DockPanel(PanelId.Inspectors), () => rig.Host.FloatPanel(PanelId.Inspectors)];
        string[] names = ["float", "dock", "float again"];
        for (var i = 0; i < moves.Length; i++)
        {
            moves[i]();
            PumpFor(pumpUntil, 200);
            var view = Shown($"webp {i + 1}");
            var webView = view is null ? null : Read<Microsoft.Web.WebView2.WinForms.WebView2?>(view, "View");
            check(HoldsBody(view) && webView?.CoreWebView2 is not null, $"after {names[i]} the WebP is shown again");
            if (view is null || webView?.CoreWebView2 is not { } core) continue;
            var before = Read<int>(view, "Blocked");
            core.Navigate("https://localhost:1/other");
            pumpUntil(() => Read<int>(view, "Blocked") > before);
            check(Read<int>(view, "Blocked") > before && webView.Source?.Host == WebpView.Host, $"after {names[i]} a navigation away is still cancelled");
        }
    }

    private static void SessionsKeepViewState(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== sessions floated keep their selection and top row");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        for (var i = 0; i < 200; i++)
            rig.Store.Add(new Session { Request = new HttpRequestData { Method = "GET", Url = new Uri($"http://example.test/{i}") } });
        var list = rig.List;
        pumpUntil(() => list.VirtualListSize == 200);
        rig.Grid.FollowTail = false;

        list.SelectedIndices.Add(50);
        list.SelectedIndices.Add(52);
        list.FocusedItem = list.Items[52];
        list.EnsureVisible(199);
        list.EnsureVisible(40);
        PumpFor(pumpUntil, 200);
        var top = list.TopItem?.Index;
        check(top is > 0, $"setup: the grid is scrolled (top {top})");

        void Expect(string after)
        {
            var selected = list.SelectedIndices.Cast<int>().ToArray();
            check(selected.SequenceEqual([50, 52]), $"after {after} rows 50 and 52 are still selected ({string.Join(",", selected)})");
            check(list.FocusedItem?.Index == 52, $"after {after} row 52 still has the focus rectangle ({list.FocusedItem?.Index})");
            check(list.TopItem?.Index == top, $"after {after} the same row is at the top ({list.TopItem?.Index}, was {top})");
        }

        rig.Host.FloatPanel(PanelId.Sessions);
        PumpFor(pumpUntil, 200);
        Expect("float");
        rig.Host.DockPanel(PanelId.Sessions);
        PumpFor(pumpUntil, 200);
        Expect("dock");
    }

    private static readonly System.Reflection.MethodInfo ProcessCmdKeyMethod =
        typeof(Control).GetMethod("ProcessCmdKey", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    /// <summary>Runs a key through the control's command-key chain, as the message loop does before the control sees it.</summary>
    private static bool ProcessCmdKey(Control control, Keys keys)
    {
        var message = Message.Create(control.Handle, WmKeyDown, (nint)(keys & Keys.KeyCode), 0);
        return ProcessCmdKeyMethod.Invoke(control, [message, keys]) is true;
    }

    private static void FocusChord(Action<bool, string> check)
    {
        Console.WriteLine("== Ctrl+Alt+1..6 focuses a panel, but AltGr+digit in a text box stays a character");
        check(PanelMenus.FocusChordTarget(new KeyEventArgs(Keys.Control | Keys.Alt | Keys.D1), false) == PanelId.Sessions, "Ctrl+Alt+1 is Sessions");
        check(PanelMenus.FocusChordTarget(new KeyEventArgs(Keys.Control | Keys.Alt | Keys.D6), false) == PanelId.Log, "Ctrl+Alt+6 is Log");
        check(PanelMenus.FocusChordTarget(new KeyEventArgs(Keys.Control | Keys.Alt | Keys.D3), true) is null,
            "in a text box the chord is left alone, so AltGr+3 still types its character");
        check(PanelMenus.FocusChordTarget(new KeyEventArgs(Keys.Control | Keys.Alt | Keys.D7), false) is null, "Ctrl+Alt+7 names no panel");
        check(PanelMenus.FocusChordTarget(new KeyEventArgs(Keys.Control | Keys.D1), false) is null, "Ctrl+1 without Alt is not the chord");
        check(PanelMenus.FocusChordTarget(new KeyEventArgs(Keys.Alt | Keys.D1), false) is null, "nor is Alt+1 without Ctrl");
    }

    private static void MoveLogToBottom(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== move log to bottom shows a bottom area with log selected");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var right = StripOf(rig, PanelId.Inspectors)!;

        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        PumpFor(pumpUntil, 100);
        var log = rig.Registry.PageOf(PanelId.Log);
        var bottom = StripOf(rig, PanelId.Log);
        check(bottom is not null && bottom != right && bottom != StripOf(rig, PanelId.Sessions), "the Log is in a strip of its own");
        if (bottom is null) return;
        check(bottom.TabCount == 1 && bottom.SelectedTab == log, "the bottom strip shows the Log page");
        check(rig.Host.SelectedPanel(DockArea.Bottom) == PanelId.Log, "and the host reports it selected there");
        string[] rest = ["Inspectors", "Composer", "Filters", "AutoResponder"];
        check(right.TabPages.Cast<TabPage>().Select(page => page.Text).SequenceEqual(rest), "the right strip closed the gap");

        var rightBottom = rig.Host.PointToClient(right.PointToScreen(new Point(0, right.Height))).Y;
        var bottomTop = rig.Host.PointToClient(bottom.PointToScreen(Point.Empty)).Y;
        check(bottom.Visible && bottom.Height > 0 && bottomTop >= rightBottom, $"the bottom area is shown below the others (top {bottomTop}, right ends {rightBottom})");
        var expected = rig.Host.Height * PanelLayout.DefaultBottomFraction;
        check(Math.Abs(bottom.Height - expected) <= 8, $"and takes 30% of the height ({bottom.Height} px, expected about {expected:0})");

        var items = rig.Host.TabMenuItems(PanelId.Log).OfType<ToolStripMenuItem>().ToArray();
        string[] texts = [Strings.Panels.MoveLeft, Strings.Panels.MoveRight, Strings.Panels.MoveBottom, Strings.Panels.Float, Strings.Panels.AutoHide, Strings.Panels.TopMost, Strings.Panels.Hide];
        check(items.Select(item => item.Text).SequenceEqual(texts), $"the tab menu holds Move to Left/Right/Bottom, Float, Auto-hide area, Always on top and Hide ({string.Join(", ", items.Select(item => item.Text))})");
        check(items.Length == 7 && items[0].Enabled && items[1].Enabled && !items[2].Enabled && items[6].Enabled,
            "Move to Bottom is greyed out where the Log already is");
        if (items.Length == 7) items[1].PerformClick();
        PumpFor(pumpUntil, 100);
        check(StripOf(rig, PanelId.Log) == right && right.SelectedTab == log, "Move to Right puts the Log back on the right, selected");
        check(rig.Host.SelectedPanel(DockArea.Bottom) is null, "and leaves the bottom empty");
        foreach (var item in items) item.Dispose();

        var sessionItems = rig.Host.TabMenuItems(PanelId.Sessions).OfType<ToolStripMenuItem>().ToArray();
        check(sessionItems.Length == 7 && !sessionItems[0].Enabled && !sessionItems[4].Enabled && !sessionItems[6].Enabled,
            "for Sessions, Move to Left (where it is), Auto-hide area and Hide are greyed out");
        foreach (var item in sessionItems) item.Dispose();
    }

    private static void TabMenuOpens(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== the tab menu opens from a right-click on a tab and from the keyboard");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var right = StripOf(rig, PanelId.Inspectors)!;
        var raised = new List<int>();
        right.TabMenuRequested += (_, index) => raised.Add(index);

        var filtersRect = right.GetTabRect(3);
        var centre = new Point(filtersRect.X + filtersRect.Width / 2, filtersRect.Y + filtersRect.Height / 2);
        check(right.TabIndexAt(centre) == 3, "TabIndexAt finds the Filters tab under a point on it");
        check(right.TabIndexAt(new Point(centre.X, right.Height - 5)) == -1, "and nothing for a point on the page");

        var screen = right.PointToScreen(centre);
        SendMessage(right.Handle, WmContextMenu, right.Handle, (screen.Y << 16) | (screen.X & 0xFFFF));
        PumpFor(pumpUntil, 100);
        check(raised.SequenceEqual([3]), $"a right-click on the Filters tab asks for its menu ({string.Join(",", raised)})");
        check(rig.Host.TabMenu.Visible && rig.Host.TabMenu.Items.Count > 0, "and the host shows the tab menu");
        rig.Host.TabMenu.Close();

        // Shift+F10 and the Menu key both arrive as WM_CONTEXTMENU with no position.
        SendMessage(right.Handle, WmContextMenu, right.Handle, -1);
        PumpFor(pumpUntil, 100);
        check(raised.Count == 2 && raised[1] == right.SelectedIndex, "the keyboard asks for the selected tab's menu");
        rig.Host.TabMenu.Close();

        SendMessage(right.Handle, WmContextMenu, rig.Inspector.Handle, -1);
        PumpFor(pumpUntil, 100);
        check(raised.Count == 2, "a context-menu key in a page's own control is not taken for the tab");
    }

    private static void EmptiedAreaCollapses(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== an emptied area collapses");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var left = StripOf(rig, PanelId.Sessions)!;
        var right = StripOf(rig, PanelId.Inspectors)!;
        var inner = (SplitContainer)left.Parent!.Parent!;
        check(!inner.Panel2Collapsed, "setup: the right side is shown");

        foreach (var id in new[] { PanelId.Inspectors, PanelId.Composer, PanelId.Filters, PanelId.AutoResponder, PanelId.Log })
            rig.Host.MoveTo(id, DockArea.Left);
        PumpFor(pumpUntil, 100);
        check(left.TabCount == 6 && right.TabCount == 0, $"every panel is on the left ({left.TabCount} left, {right.TabCount} right)");
        check(inner.Panel2Collapsed, "the empty right area collapses");
        check(rig.Host.SelectedPanel(DockArea.Left) == PanelId.Log, "the last panel moved is selected where it landed");

        rig.Host.MoveTo(PanelId.Log, DockArea.Right);
        PumpFor(pumpUntil, 100);
        check(!inner.Panel2Collapsed && right.TabCount == 1, "moving one back shows the right area again");
    }

    private static void HideThenReveal(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== hide then Passive reveal keeps it hidden; Explicit reveal shows it in its last area");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var right = StripOf(rig, PanelId.Inspectors)!;
        var composer = rig.Registry.PageOf(PanelId.Composer);

        rig.Host.FocusPanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        check(composer.ContainsFocus, "setup: the Composer has the focus");
        rig.Host.HidePanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        check(composer.Parent is null && rig.Host.IsHidden(PanelId.Composer), "HidePanel takes the Composer out of every strip");
        check(rig.Host.SelectedPanel(DockArea.Right) != PanelId.Composer, "and the right area selects another panel");
        check(rig.List.Focused, $"the focus goes back to the grid, not nowhere (focus: {FocusedName(rig.Form)})");

        rig.Host.ShowPanel(PanelId.Composer, PanelReveal.Passive);
        PumpFor(pumpUntil, 100);
        check(composer.Parent is null && rig.Host.IsHidden(PanelId.Composer), "a Passive reveal leaves it hidden");

        rig.Host.ShowPanel(PanelId.Composer, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        check(composer.Parent == right && right.TabPages.IndexOf(composer) == 1, "an Explicit reveal puts it back second on the right, where it was");
        check(right.SelectedTab == composer && !rig.Host.IsHidden(PanelId.Composer), "and selects it");

        rig.Host.HidePanel(PanelId.Sessions);
        PumpFor(pumpUntil, 100);
        check(StripOf(rig, PanelId.Sessions) is not null && !rig.Host.IsHidden(PanelId.Sessions), "Sessions cannot be hidden");
    }

    private static void ResetRestoresDefault(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== reset restores default placement and fractions");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);

        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        rig.Host.MoveTo(PanelId.Filters, DockArea.Left);
        rig.Host.MoveTo(PanelId.Sessions, DockArea.Bottom);
        rig.Host.HidePanel(PanelId.AutoResponder);
        PumpFor(pumpUntil, 100);
        var outer = rig.Host.Controls.OfType<SplitContainer>().Single();
        var inner = outer.Panel1.Controls.OfType<SplitContainer>().Single();
        outer.SplitterDistance = outer.Height / 3;
        inner.SplitterDistance = inner.Width / 4;
        PumpFor(pumpUntil, 100);
        var moved = rig.Host.Snapshot();
        check(Math.Abs(moved.LeftFraction - 0.25) < 0.01, $"setup: the dragged split is recorded ({moved.LeftFraction:0.000})");

        rig.Host.ResetLayout();
        PumpFor(pumpUntil, 100);
        var snapshot = rig.Host.Snapshot();
        var expected = PanelLayout.Default();
        foreach (var id in Enum.GetValues<PanelId>())
            check(snapshot.AreaOf(id) == expected.AreaOf(id) && snapshot.IsHidden(id) == expected.IsHidden(id), $"{id} is back in its default area and shown");
        foreach (var area in Enum.GetValues<DockArea>())
            check(snapshot.DockedIn(area).SequenceEqual(expected.DockedIn(area)) && snapshot.SelectedIn(area) == expected.SelectedIn(area),
                $"the {area} area holds its default panels and selection ({string.Join(",", snapshot.DockedIn(area))})");
        check(snapshot.LeftFraction == expected.LeftFraction && snapshot.BottomFraction == expected.BottomFraction,
            $"the fractions are the defaults ({snapshot.LeftFraction}, {snapshot.BottomFraction})");
        check(outer.Panel2Collapsed, "the emptied bottom area collapses again");
        var leftEdge = rig.Host.PointToClient(StripOf(rig, PanelId.Inspectors)!.PointToScreen(Point.Empty)).X;
        check(Math.Abs(leftEdge - rig.Host.Width * PanelLayout.DefaultLeftFraction) <= 8, $"and the split is back at 55% ({leftEdge} px)");
    }

    private static void BottomPanelsKeepWorking(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== send-to-composer, dropping a session on a tab and the check glyph work with Composer, AutoResponder and Filters moved to Bottom");
        using var rig = new Rig();
        var dropped = new List<PanelId>();
        var drop = new SessionDrop(rig.Registry, (id, _) => dropped.Add(id));
        rig.Host.StripCreated += (_, strip) => drop.Attach(strip);
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.SetTabChecked(PanelId.AutoResponder, true);

        foreach (var id in new[] { PanelId.Composer, PanelId.AutoResponder, PanelId.Filters })
            rig.Host.MoveTo(id, DockArea.Bottom);
        PumpFor(pumpUntil, 100);
        var bottom = StripOf(rig, PanelId.Composer);
        check(bottom is not null && StripOf(rig, PanelId.AutoResponder) == bottom && StripOf(rig, PanelId.Filters) == bottom
              && bottom != StripOf(rig, PanelId.Inspectors), "the three panels share the bottom strip");
        if (bottom is null) return;
        var composer = rig.Registry.PageOf(PanelId.Composer);
        var autoResponder = rig.Registry.PageOf(PanelId.AutoResponder);
        var filters = rig.Registry.PageOf(PanelId.Filters);

        check(bottom.IsTabChecked(autoResponder), "the AutoResponder's check glyph moved with it");
        rig.Host.SetTabChecked(PanelId.Filters, true);
        check(bottom.IsTabChecked(filters), "checking Filters marks its tab at the bottom");

        rig.List.Focus();
        pumpUntil(() => rig.List.Focused);
        rig.Host.ShowPanel(PanelId.Composer, PanelReveal.Explicit); // as Send to Composer does
        PumpFor(pumpUntil, 100);
        check(bottom.SelectedTab == composer && rig.Host.SelectedPanel(DockArea.Bottom) == PanelId.Composer, "send-to-composer selects the Composer at the bottom");
        check(rig.List.Focused, $"and the grid keeps the focus (focus: {FocusedName(rig.Form)})");

        var session = new Session { Request = new HttpRequestData { Method = "GET", Url = new Uri("http://example.test/drop") } };
        DropOn(bottom, bottom.TabPages.IndexOf(autoResponder), session);
        check(dropped.SequenceEqual([PanelId.AutoResponder]) && bottom.SelectedTab == autoResponder, "a session dropped on the AutoResponder tab reaches it");
        DropOn(bottom, bottom.TabPages.IndexOf(composer), session);
        check(dropped.SequenceEqual([PanelId.AutoResponder, PanelId.Composer]) && bottom.SelectedTab == composer, "and one dropped on the Composer tab reaches the Composer");
        DropOn(bottom, bottom.TabPages.IndexOf(filters), session);
        check(dropped.Count == 2, "the Filters tab refuses it");
    }

    private static void ShrunkWindow(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a window shrunk to MinimumSize with Bottom populated throws nothing");
        using var rig = new Rig();
        rig.Form.MinimumSize = new Size(900, 600); // as MainForm
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        rig.Host.MoveTo(PanelId.Composer, DockArea.Bottom);
        PumpFor(pumpUntil, 100);

        Exception? error = null;
        try
        {
            rig.Form.Size = rig.Form.MinimumSize;
            PumpFor(pumpUntil, 200);
            _ = rig.Host.Snapshot();
            rig.Host.MoveTo(PanelId.Filters, DockArea.Bottom);
            PumpFor(pumpUntil, 100);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        check(error is null, $"nothing throws ({error?.GetType().Name}: {error?.Message})");
        check(StripOf(rig, PanelId.Log)?.TabCount == 3 && StripOf(rig, PanelId.Log)!.Height > 0, "the bottom strip is still shown with its three panels");
    }

    private static void SnapshotKeepsFractionsWhenTiny(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a minimised or tiny window does not record splitter fractions");
        using var rig = new Rig();
        rig.Form.FormBorderStyle = FormBorderStyle.None; // so the window can be smaller than a caption allows
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        PumpFor(pumpUntil, 100);
        var before = rig.Host.Snapshot();

        rig.Form.WindowState = FormWindowState.Minimized;
        PumpFor(pumpUntil, 200);
        var minimised = rig.Host.Snapshot();
        check(minimised.LeftFraction == before.LeftFraction && minimised.BottomFraction == before.BottomFraction,
            $"minimised keeps the fractions ({minimised.LeftFraction}, {minimised.BottomFraction})");
        rig.Form.WindowState = FormWindowState.Normal;
        PumpFor(pumpUntil, 200);

        rig.Form.Size = new Size(40, 40);
        PumpFor(pumpUntil, 200);
        var tiny = rig.Host.Snapshot();
        check(rig.Host.Width < 60, $"setup: the host is tiny ({rig.Host.Width}x{rig.Host.Height})");
        check(tiny.LeftFraction == before.LeftFraction && tiny.BottomFraction == before.BottomFraction,
            $"a split smaller than its panels' minimum sizes keeps the fractions ({tiny.LeftFraction}, {tiny.BottomFraction})");
    }

    private static void HiddenPanelThemed(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a panel hidden during a theme toggle is themed when shown");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        Palette.Apply(rig.Form);
        var wasLight = Palette.IsLightMode;
        try
        {
            rig.Host.HidePanel(PanelId.Filters);
            Palette.ToggleMode();
            Palette.Apply(rig.Form); // as ToggleTheme does
            check(rig.Filters.BackColor != Palette.Background, "setup: walking the window misses the hidden Filters");
            rig.Host.ApplyTheme();
            check(rig.Filters.BackColor == Palette.Background, "ApplyTheme reaches the hidden Filters");

            Palette.ToggleMode();
            Palette.Apply(rig.Form);
            check(rig.Filters.BackColor != Palette.Background, "setup: a second toggle misses it again");
            rig.Host.ShowPanel(PanelId.Filters, PanelReveal.Explicit);
            PumpFor(pumpUntil, 100);
            check(rig.Filters.BackColor == Palette.Background, "showing it again re-themes it");
        }
        finally
        {
            if (Palette.IsLightMode != wasLight) Palette.ToggleMode();
        }
    }

    private static void DropOn(DarkTabControl strip, int index, Session session)
    {
        var rect = strip.GetTabRect(index);
        var screen = strip.PointToScreen(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
        var args = new DragEventArgs(new DataObject(session), 0, screen.X, screen.Y, DragDropEffects.Copy, DragDropEffects.Copy);
        OnDragDrop.Invoke(strip, [args]);
    }

    private static readonly System.Reflection.MethodInfo OnDragDrop =
        typeof(Control).GetMethod("OnDragDrop", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private static void RevealHiddenPanel(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a Passive reveal leaves a hidden panel hidden; an Explicit one shows it in its last area");
        var layout = PanelLayout.Default();
        layout.Hide(PanelId.Log);
        using var rig = new Rig(layout);
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);

        var log = rig.Registry.PageOf(PanelId.Log);
        check(log.Parent is null, "setup: the hidden Log is in no strip");

        rig.Host.ShowPanel(PanelId.Log, PanelReveal.Passive);
        PumpFor(pumpUntil, 100);
        check(log.Parent is null, "after a Passive reveal the Log is still in no strip");
        check(rig.Host.Snapshot().IsHidden(PanelId.Log), "and the layout still has it hidden");
        check(rig.Host.SelectedPanel(DockArea.Right) == PanelId.Inspectors, "and the right area's selection is unchanged");

        rig.Host.ShowPanel(PanelId.Log, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        var strip = StripOf(rig, PanelId.Log);
        check(strip is not null && strip == StripOf(rig, PanelId.Inspectors), "an Explicit reveal puts the Log back in its last area, the right");
        check(strip?.SelectedTab == log && rig.Host.SelectedPanel(DockArea.Right) == PanelId.Log, "and selects it");
        check(!rig.Host.Snapshot().IsHidden(PanelId.Log), "and the layout no longer has it hidden");
    }

    private static void DefaultLayout(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== default host shows sessions left, inspectors selected right");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0 && rig.List.Focused);

        check(rig.Host.SelectedPanel(DockArea.Left) == PanelId.Sessions, "Sessions is the selected panel on the left");
        check(rig.Host.SelectedPanel(DockArea.Right) == PanelId.Inspectors, "Inspectors is the selected panel on the right");
        check(rig.Host.SelectedPanel(DockArea.Bottom) is null, "nothing is in the bottom area");
        check(rig.List.Focused, $"the grid, not the Sessions tab, has the focus when the window first shows (focus: {FocusedName(rig.Form)})");

        var left = StripOf(rig, PanelId.Sessions);
        var right = StripOf(rig, PanelId.Inspectors);
        check(left is not null && right is not null && left != right, "sessions and inspectors sit in different strips");
        if (left is null || right is null) return;

        string[] expected = ["Inspectors", "Composer", "Filters", "AutoResponder", "Log"];
        var titles = right.TabPages.Cast<TabPage>().Select(page => page.Text).ToArray();
        check(titles.SequenceEqual(expected), $"the right strip holds the five panels in today's order ({string.Join(", ", titles)})");
        check(right.SelectedTab == rig.Registry.PageOf(PanelId.Inspectors), "and shows the Inspectors page");
        check(rig.Grid.Visible && rig.Inspector.Visible, "the grid and the inspector are both on screen");

        var leftEdge = rig.Host.PointToClient(left.PointToScreen(Point.Empty)).X;
        var rightEdge = rig.Host.PointToClient(right.PointToScreen(Point.Empty)).X;
        check(leftEdge == 0 && rightEdge > leftEdge, "sessions are left of the inspectors");
        var expectedSplit = (int)(rig.Host.Width * PanelLayout.DefaultLeftFraction);
        check(Math.Abs(rightEdge - expectedSplit) <= 8, $"the split sits at 55% of the width ({rightEdge} px, expected about {expectedSplit})");
        check(rig.Host.Height - right.Height <= 8, "with no bottom area the strips take the full height");
        check(Math.Abs(rig.Host.Snapshot().LeftFraction - PanelLayout.DefaultLeftFraction) < 0.01, "the snapshot keeps the 55% split");

        var strips = new List<DarkTabControl>();
        rig.Host.StripCreated += (_, strip) => strips.Add(strip);
        check(strips.Count == 6 && strips.Contains(left) && strips.Contains(right),
            $"a late StripCreated subscriber still gets every docked strip and every flyout's ({strips.Count})");
    }

    private static void ShowPanelKeepsFocus(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== ShowPanel Explicit selects without moving focus off the grid");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);

        rig.List.Focus();
        pumpUntil(() => rig.List.Focused);
        check(rig.List.Focused, "setup: the grid has focus");

        rig.Host.ShowPanel(PanelId.Composer, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        check(StripOf(rig, PanelId.Composer)?.SelectedTab == rig.Registry.PageOf(PanelId.Composer), "the Composer tab is selected");
        check(rig.Host.SelectedPanel(DockArea.Right) == PanelId.Composer, "and the host reports it as selected");
        check(rig.List.Focused, $"the grid keeps the focus (focus: {FocusedName(rig.Form)})");

        rig.Host.ShowPanel(PanelId.Log, PanelReveal.Passive);
        PumpFor(pumpUntil, 100);
        check(rig.Host.SelectedPanel(DockArea.Right) == PanelId.Log && rig.List.Focused, "a Passive reveal of a visible panel selects it, focus stays");

        rig.Host.FocusPanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);
        check(rig.Host.SelectedPanel(DockArea.Right) == PanelId.Filters && rig.Filters.ContainsFocus,
            $"FocusPanel selects Filters and moves the focus into it (focus: {FocusedName(rig.Form)})");
    }

    /// <summary>The arrow-key focus regression, run with the inspector inside the host as MainForm builds it.</summary>
    private static void ArrowKeysKeepFocus(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== arrow keys keep focus in the grid while the inspector re-renders");
        using var rig = new Rig();
        rig.Form.Show();

        (string? type, string body)[] responses =
        [
            ("text/html", "<html>one</html>"),
            ("application/json", "{\"a\":1}"),
            (null, ""),
            ("text/plain", "plain"),
            ("image/png", "not really a png"),
            ("application/json", "{\"b\":2}"),
            ("text/html", "<html>two</html>"),
        ];
        var next = 0;
        foreach (var (type, body) in responses)
        {
            var response = new HttpResponseData { StatusCode = 200, Body = System.Text.Encoding.UTF8.GetBytes(body) };
            if (type is not null) response.Headers.Add("Content-Type", type);
            var session = new Session
            {
                Request = new HttpRequestData { Method = "GET", Url = new Uri($"http://example.test/{next++}") },
                Response = response,
            };
            session.Completed = DateTimeOffset.Now;
            rig.Store.Add(session);
        }
        var list = rig.List;
        pumpUntil(() => list.VirtualListSize == responses.Length);

        list.Focus();
        list.SelectedIndices.Add(0);
        list.FocusedItem = list.Items[0];
        PumpFor(pumpUntil, 100);
        for (var i = 1; i < responses.Length; i++)
        {
            SendMessage(list.Handle, WmKeyDown, (nint)Keys.Down, 0);
            SendMessage(list.Handle, WmKeyUp, (nint)Keys.Down, 0);
            PumpFor(pumpUntil, 100);
            check(list.Focused && list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == i,
                $"after Down #{i} row {i} is selected and the grid has focus (focus: {FocusedName(rig.Form)})");
        }
    }

    private static void SetTabChecked(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== SetTabChecked draws on the panel's tab");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);

        var filters = rig.Registry.PageOf(PanelId.Filters);
        var autoResponder = rig.Registry.PageOf(PanelId.AutoResponder);
        var strip = StripOf(rig, PanelId.Filters);
        check(strip is not null && !strip.IsTabChecked(filters), "setup: the Filters tab starts unchecked");
        if (strip is null) return;

        rig.Host.SetTabChecked(PanelId.Filters, true);
        check(strip.IsTabChecked(filters), "checking Filters marks its tab");
        check(!strip.IsTabChecked(autoResponder), "and no other tab");
        rig.Host.SetTabChecked(PanelId.AutoResponder, true);
        rig.Host.SetTabChecked(PanelId.Filters, false);
        check(!strip.IsTabChecked(filters) && strip.IsTabChecked(autoResponder), "unchecking Filters clears only its mark");
    }

    private static DarkTabControl? StripOf(Rig rig, PanelId id) => rig.Registry.PageOf(id).Parent as DarkTabControl;

    private static string FocusedName(Control root)
    {
        var focused = Descendants(root).FirstOrDefault(control => control.Focused);
        return $"{focused?.GetType().Name ?? "none"}, parent {focused?.Parent?.GetType().Name}";
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void PumpFor(Action<Func<bool>> pumpUntil, int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        pumpUntil(() => Environment.TickCount64 >= deadline);
    }

    /// <summary>The six real panels in a registry and a default host, in a 1200x800 window, wired as MainForm wires them.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly SessionStore Store = new();
        public readonly Form Form = new() { Width = 1200, Height = 800, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        public readonly SessionListView Grid;
        public readonly InspectorPanel Inspector = new() { Dock = DockStyle.Fill };
        public readonly FilterPanel Filters = new() { Dock = DockStyle.Fill };
        public readonly PanelRegistry Registry;
        public readonly PanelHost Host;

        public Rig(PanelLayout? layout = null)
        {
            var options = new ProxyOptions();
            Grid = new SessionListView(Store) { Dock = DockStyle.Fill };
            Registry = new PanelRegistry(new Dictionary<PanelId, (Control Content, string Title)>
            {
                [PanelId.Sessions] = (Grid, "Sessions"),
                [PanelId.Inspectors] = (Inspector, "Inspectors"),
                [PanelId.Composer] = (new ComposerPanel(new RequestExecutor(options, Store)) { Dock = DockStyle.Fill }, "Composer"),
                [PanelId.Filters] = (Filters, "Filters"),
                [PanelId.AutoResponder] = (new AutoResponderPanel(options.AutoResponder) { Dock = DockStyle.Fill }, "AutoResponder"),
                [PanelId.Log] = (new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true }, "Log"),
            });
            Host = new PanelHost(Registry, layout ?? PanelLayout.Default()) { Dock = DockStyle.Fill };
            Form.Controls.Add(Host);
            // As MainForm does: the Sessions strip comes before the grid in tab order and would otherwise take the first focus.
            Form.ActiveControl = Grid;
            Grid.SelectionChanged += (_, session) => Inspector.Show(session); // as MainForm wires it
        }

        public ListView List => Descendants(Grid).OfType<ListView>().First();

        public void Dispose()
        {
            Form.Close();
            Form.Dispose();
            Registry.Dispose();
        }
    }
}
