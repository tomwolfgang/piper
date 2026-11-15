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
        DragLogToBottom(check, pumpUntil);
        DragReorders(check, pumpUntil);
        DragEscCancels(check, pumpUntil);
        ShortClickSelects(check, pumpUntil);
        SessionDropAfterDrag(check, pumpUntil);
        DragOutsideFloats(check, pumpUntil);
        DragOntoAutoHiddenArea(check, pumpUntil);
        EmptiedAutoHiddenArea(check, pumpUntil);
        FlyoutMinimumWidth(check, pumpUntil);
        FlyoutHeader(check, pumpUntil);
        BottomKeepsMinimum(check, pumpUntil);
        FloatingWindowChrome(check, pumpUntil);
        FloatingCaptionDocks(check, pumpUntil);
        FloatingKeyboardMenu(check, pumpUntil);
        CaptionDragWindowDisposed(check, pumpUntil);
        MoveKeepsFocus(check, pumpUntil);
        PassiveRevealKeepsFocus(check, pumpUntil);
        DeactivateCloseKeepsActivation(check, pumpUntil);
        NativePartsFollowTheme(check, pumpUntil);
        TabOverflowMenu(check, pumpUntil);
        FiltersFitNarrow(check, pumpUntil);
        SingleBorders(check, pumpUntil);
        FlyoutScreenshots(pumpUntil);
        DragScreenshots(pumpUntil);
    }

    private const int MkLButton = 0x0001;
    private const int VkEscape = 0x1B;

    /// <summary>Presses the left button on a tab and moves past the drag threshold, as the mouse would.</summary>
    private static void PressAndDrag(DarkTabControl strip, int index, Action<Func<bool>> pumpUntil)
    {
        var rect = strip.GetTabRect(index);
        var centre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
        PostMessage(strip.Handle, WmLButtonDown, MkLButton, MouseLParam(centre));
        var drag = SystemInformation.DragSize;
        PostMessage(strip.Handle, WmMouseMove, MkLButton, MouseLParam(new Point(centre.X + drag.Width + 2, centre.Y + drag.Height + 2)));
        PumpFor(pumpUntil, 60);
    }

    /// <summary>A mouse move to a screen point while <paramref name="source"/> holds the capture, which receives every move.</summary>
    private static void DragOver(DarkTabControl source, Point screen, Action<Func<bool>> pumpUntil)
    {
        PostMessage(source.Handle, WmMouseMove, MkLButton, MouseLParam(source.PointToClient(screen)));
        PumpFor(pumpUntil, 60);
    }

    private static void ReleaseAt(DarkTabControl source, Point screen, Action<Func<bool>> pumpUntil)
    {
        PostMessage(source.Handle, WmLButtonUp, 0, MouseLParam(source.PointToClient(screen)));
        PumpFor(pumpUntil, 150);
    }

    /// <summary>A screen point on the left edge of a tab: the gap before it.</summary>
    private static Point BeforeTab(DarkTabControl strip, int index)
    {
        var rect = strip.GetTabRect(index);
        return strip.PointToScreen(new Point(rect.X + 4, rect.Y + rect.Height / 2));
    }

    private static PanelId[] Order(Rig rig, DockArea area) => [.. rig.Host.Snapshot().DockedIn(area)];

    private static void DragLogToBottom(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== dragging Log to the Bottom strip moves it");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.MoveTo(PanelId.Composer, DockArea.Bottom);
        PumpFor(pumpUntil, 100);
        var right = StripOf(rig, PanelId.Log)!;
        var bottom = StripOf(rig, PanelId.Composer)!;
        var log = rig.Registry.PageOf(PanelId.Log);

        PressAndDrag(right, right.TabPages.IndexOf(log), pumpUntil);
        check(right.Capture, "past the threshold the dragged tab's strip takes the mouse capture");
        DragOver(right, BeforeTab(bottom, 0), pumpUntil);
        check(bottom.DropIndex == 0 && bottom.IsDropTarget, $"over the Bottom strip's first tab the marker sits before it (index {bottom.DropIndex})");
        check(right.DropIndex == -1 && !right.IsDropTarget, "and the strip it left shows no marker");
        ReleaseAt(right, BeforeTab(bottom, 0), pumpUntil);
        pumpUntil(() => log.Parent == bottom);
        check(log.Parent == bottom && Order(rig, DockArea.Bottom).SequenceEqual([PanelId.Log, PanelId.Composer]),
            $"the Log lands first on the Bottom strip ({string.Join(", ", Order(rig, DockArea.Bottom))})");
        check(bottom.SelectedTab == log && rig.Host.SelectedPanel(DockArea.Bottom) == PanelId.Log, "selected where it lands");
        check(!right.Capture && bottom.DropIndex == -1 && !bottom.IsDropTarget, "the capture and the marker are gone");
    }

    private static void DragReorders(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== reordering within a strip");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var right = StripOf(rig, PanelId.Log)!;
        var log = rig.Registry.PageOf(PanelId.Log);

        PressAndDrag(right, right.TabPages.IndexOf(log), pumpUntil);
        DragOver(right, BeforeTab(right, 0), pumpUntil);
        check(right.DropIndex == 0 && right.IsDropTarget, $"the marker sits before the first tab of its own strip (index {right.DropIndex})");
        ReleaseAt(right, BeforeTab(right, 0), pumpUntil);
        check(Order(rig, DockArea.Right).SequenceEqual([PanelId.Log, PanelId.Inspectors, PanelId.Composer, PanelId.Filters, PanelId.AutoResponder]),
            $"the Log moves to the front ({string.Join(", ", Order(rig, DockArea.Right))})");
        check(right.TabPages[0] == log && right.SelectedTab == log, "its tab is first and selected");

        var changes = 0;
        rig.Host.LayoutChanged += (_, _) => changes++;
        var inspectors = rig.Registry.PageOf(PanelId.Inspectors);
        right.SelectWithoutFocus(right.TabPages.IndexOf(inspectors));
        changes = 0;
        PressAndDrag(right, 1, pumpUntil);
        DragOver(right, BeforeTab(right, 2), pumpUntil);
        ReleaseAt(right, BeforeTab(right, 2), pumpUntil);
        check(changes == 0 && right.TabPages[1] == inspectors, $"dropping a tab where it already is changes nothing ({changes} layout changes)");
    }

    private static void DragEscCancels(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== Esc mid-drag changes nothing");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var right = StripOf(rig, PanelId.Log)!;
        var left = StripOf(rig, PanelId.Sessions)!;
        var log = rig.Registry.PageOf(PanelId.Log);
        var before = Order(rig, DockArea.Right);

        PressAndDrag(right, right.TabPages.IndexOf(log), pumpUntil);
        DragOver(right, BeforeTab(left, 0), pumpUntil);
        check(left.DropIndex == 0, "setup: the marker is on the Sessions strip");
        PostMessage(right.Handle, WmKeyDown, VkEscape, 0);
        PumpFor(pumpUntil, 60);
        check(!right.Capture && left.DropIndex == -1 && !left.IsDropTarget, "Esc releases the capture and clears the marker");
        ReleaseAt(right, BeforeTab(left, 0), pumpUntil);
        check(log.Parent == right && Order(rig, DockArea.Right).SequenceEqual(before), "and the mouse-up after it moves nothing");

        PressAndDrag(right, right.TabPages.IndexOf(log), pumpUntil);
        DragOver(right, BeforeTab(left, 0), pumpUntil);
        right.Capture = false; // WM_CAPTURECHANGED, as when another window takes the mouse
        PumpFor(pumpUntil, 60);
        check(left.DropIndex == -1 && !left.IsDropTarget, "losing the capture mid-drag clears the marker");
        ReleaseAt(right, BeforeTab(left, 0), pumpUntil);
        check(log.Parent == right && Order(rig, DockArea.Right).SequenceEqual(before), "and leaves the layout as it was");

        PressAndDrag(right, right.TabPages.IndexOf(log), pumpUntil);
        DragOver(right, BeforeTab(left, 0), pumpUntil);
        rig.Host.ShuttingDown = true;
        check(!right.Capture && left.DropIndex == -1, "shutting down mid-drag ends it");
        rig.Host.ShuttingDown = false;
        ReleaseAt(right, BeforeTab(left, 0), pumpUntil);
        check(log.Parent == right && Order(rig, DockArea.Right).SequenceEqual(before), "and the mouse-up after it moves nothing");
    }

    private static void ShortClickSelects(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a short click under the drag threshold just selects the tab");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var right = StripOf(rig, PanelId.Filters)!;
        // Filters, not Log: the rig's right strip clips the Log tab, and the native control does not
        // select a tab its scroll arrows half cover.
        var filters = rig.Registry.PageOf(PanelId.Filters);
        var before = Order(rig, DockArea.Right);
        var rect = right.GetTabRect(right.TabPages.IndexOf(filters));
        var centre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);

        PostMessage(right.Handle, WmLButtonDown, MkLButton, MouseLParam(centre));
        PostMessage(right.Handle, WmMouseMove, MkLButton, MouseLParam(centre with { X = centre.X + 1 }));
        PumpFor(pumpUntil, 60);
        check(right.DropIndex == -1 && !right.IsDropTarget, "a one-pixel move starts no drag");
        PostMessage(right.Handle, WmLButtonUp, 0, MouseLParam(centre with { X = centre.X + 1 }));
        PumpFor(pumpUntil, 100);
        check(right.SelectedTab == filters && Order(rig, DockArea.Right).SequenceEqual(before),
            $"the Filters tab is selected and nothing moved (selected {right.SelectedTab?.Text})");
        check(!right.Capture && right.DropIndex == -1 && !right.IsDropTarget, "and no capture or marker is left");

        var composer = rig.Registry.PageOf(PanelId.Composer);
        var composerRect = right.GetTabRect(right.TabPages.IndexOf(composer));
        var at = new Point(composerRect.X + composerRect.Width / 2, composerRect.Y + composerRect.Height / 2);
        const int WmRButtonDown = 0x0204, WmRButtonUp = 0x0205, MkRButton = 0x0002;
        PostMessage(right.Handle, WmRButtonDown, MkRButton, MouseLParam(at));
        PostMessage(right.Handle, WmMouseMove, MkRButton, MouseLParam(at with { X = at.X + 40 }));
        PumpFor(pumpUntil, 60);
        check(right.DropIndex == -1 && !right.IsDropTarget, "a right-button drag starts no tab drag");
        PostMessage(right.Handle, WmRButtonUp, 0, MouseLParam(at with { X = at.X + 40 }));
        PumpFor(pumpUntil, 100);
        rig.Host.TabMenu.Close();
        check(Order(rig, DockArea.Right).SequenceEqual(before), "and moves nothing");
    }

    private static void SessionDropAfterDrag(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a session OLE drop onto a strip still loads the Composer after a tab drag");
        using var rig = new Rig();
        var dropped = new List<PanelId>();
        var drop = new SessionDrop(rig.Registry, (id, _) => dropped.Add(id));
        rig.Host.StripCreated += (_, strip) => drop.Attach(strip);
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var right = StripOf(rig, PanelId.Composer)!;
        var composer = rig.Registry.PageOf(PanelId.Composer);

        PressAndDrag(right, right.TabPages.IndexOf(composer), pumpUntil);
        DragOver(right, BeforeTab(right, 0), pumpUntil);
        ReleaseAt(right, BeforeTab(right, 0), pumpUntil);
        check(right.TabPages[0] == composer, "setup: the Composer was dragged to the front");

        var session = new Session { Request = new HttpRequestData { Method = "GET", Url = new Uri("http://example.test/after-drag") } };
        DropOn(right, right.TabPages.IndexOf(composer), session);
        check(right.AllowDrop && dropped.SequenceEqual([PanelId.Composer]), "a session dropped on the Composer tab still reaches it");
    }

    private static void DragOutsideFloats(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a tab dropped outside the main window floats there; a floating window refuses tab drops");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var right = StripOf(rig, PanelId.Log)!;
        var outside = new Point(rig.Form.Bounds.Right + 80, rig.Form.Bounds.Top + 120);

        PressAndDrag(right, right.TabPages.IndexOf(rig.Registry.PageOf(PanelId.Log)), pumpUntil);
        DragOver(right, outside, pumpUntil);
        check(right.DropIndex == -1 && !right.IsDropTarget, "outside the window no strip is marked");
        ReleaseAt(right, outside, pumpUntil);
        var window = rig.Host.FloatingWindow(PanelId.Log);
        check(rig.Host.IsFloating(PanelId.Log) && window is { Visible: true }, "the Log floats in a window of its own");
        if (window is null) return;
        var screens = Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
        check(!screens.Any(area => area.Contains(outside)) || window.Location == outside, $"at the drop point ({window.Location} vs {outside})");

        var before = Order(rig, DockArea.Right);
        var composer = rig.Registry.PageOf(PanelId.Composer);
        var onWindow = window.Strip.PointToScreen(new Point(window.Strip.GetTabRect(0).Right + 20, window.Strip.GetTabRect(0).Y + 4));
        PressAndDrag(right, right.TabPages.IndexOf(composer), pumpUntil);
        DragOver(right, onWindow, pumpUntil);
        check(window.Strip.DropIndex == -1 && !window.Strip.IsDropTarget, "a floating window's strip shows no marker");
        ReleaseAt(right, onWindow, pumpUntil);
        check(composer.Parent == right && Order(rig, DockArea.Right).SequenceEqual(before) && rig.Host.FloatingWindow(PanelId.Log) == window,
            "and dropping on it moves nothing");
    }

    private static void DragOntoAutoHiddenArea(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a tab dropped on an auto-hidden area's strip joins that area, which stays auto-hidden");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        rig.Host.SetAutoHidden(DockArea.Bottom, true);
        PumpFor(pumpUntil, 100);
        var right = StripOf(rig, PanelId.Composer)!;
        var strip = rig.Host.AutoHideStripOf(DockArea.Bottom);
        var item = strip.ItemBounds(PanelId.Log);
        var onStrip = strip.PointToScreen(new Point(item.Right + 30, item.Y + item.Height / 2));

        PressAndDrag(right, right.TabPages.IndexOf(rig.Registry.PageOf(PanelId.Composer)), pumpUntil);
        DragOver(right, onStrip, pumpUntil);
        check(strip.IsDropTarget && right.DropIndex == -1, "the auto-hidden area's strip is outlined as the target");
        ReleaseAt(right, onStrip, pumpUntil);
        check(!strip.IsDropTarget, "and the outline goes with the drop");
        check(Order(rig, DockArea.Bottom).SequenceEqual([PanelId.Log, PanelId.Composer]) && rig.Host.IsAutoHidden(DockArea.Bottom),
            $"the Composer joins the auto-hidden Bottom ({string.Join(", ", Order(rig, DockArea.Bottom))})");
        check(strip.Panels.Select(panel => panel.Id).SequenceEqual([PanelId.Log, PanelId.Composer]), "and is named on its strip");
    }

    /// <summary>
    /// Copies the screen mid-drag, with the marker on the Bottom strip, into the directory named by
    /// PIPER_UI_SCREENSHOTS for a visual review, in both themes. Not a check: nothing is asserted.
    /// </summary>
    private static void DragScreenshots(Action<Func<bool>> pumpUntil)
    {
        if (Environment.GetEnvironmentVariable("PIPER_UI_SCREENSHOTS") is not { Length: > 0 } directory) return;
        Console.WriteLine($"== screenshots of a tab drag into {directory}");
        var wasLight = Palette.IsLightMode;
        try
        {
            foreach (var light in new[] { false, true })
            {
                if (Palette.IsLightMode != light) Palette.ToggleMode();
                using var rig = new Rig();
                rig.Host.MoveTo(PanelId.Composer, DockArea.Bottom);
                rig.Host.MoveTo(PanelId.AutoResponder, DockArea.Bottom);
                rig.Host.MoveTo(PanelId.Filters, DockArea.Bottom);
                rig.Host.ShowPanel(PanelId.Composer, PanelReveal.Explicit);
                rig.Form.Show();
                Palette.Apply(rig.Form);
                rig.Host.ApplyTheme();
                pumpUntil(() => rig.Grid.Width > 0);
                rig.Form.TopMost = true;
                PumpFor(pumpUntil, 300);
                var right = StripOf(rig, PanelId.Log)!;
                var bottom = StripOf(rig, PanelId.Composer)!;
                PressAndDrag(right, right.TabPages.IndexOf(rig.Registry.PageOf(PanelId.Log)), pumpUntil);
                // Between two unselected tabs, where the marker is not beside the selected tab's accent.
                DragOver(right, BeforeTab(bottom, 2), pumpUntil);
                PumpFor(pumpUntil, 200);
                var client = rig.Form.RectangleToScreen(rig.Form.ClientRectangle);
                using var bitmap = new Bitmap(client.Width, client.Height);
                using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(client.Location, Point.Empty, client.Size);
                bitmap.Save(Path.Combine(directory, light ? "task-8-drag-marker-light.png" : "task-8-drag-marker.png"));
                PostMessage(right.Handle, WmKeyDown, VkEscape, 0);
                PumpFor(pumpUntil, 60);
            }
        }
        finally
        {
            if (Palette.IsLightMode != wasLight) Palette.ToggleMode();
        }
    }

    private const int WmLButtonDown = 0x0201;
    private const int WmMouseMove = 0x0200;
    private const int WmMouseLeave = 0x02A3;

    /// <summary>Runs a key down through the whole pre-dispatch chain, as the message loop does: command keys, then input keys, then dialog keys.</summary>
    private static bool PreProcessKey(Control control, Keys key)
    {
        var message = Message.Create(control.Handle, WmKeyDown, (nint)key, 0);
        return control.PreProcessMessage(ref message);
    }

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
        SendMessage(flyout.Handle, WmMouseMove, 0, MouseLParam(new Point(2, flyout.Height / 2)));
        check(flyout.Cursor == Cursors.SizeWE, "over the grip the cursor is the resize one");
        SendMessage(flyout.Handle, WmMouseLeave, 0, 0);
        check(flyout.Cursor == Cursors.Default && flyout.Strip.Cursor == Cursors.Default,
            "once the pointer leaves the grip the flyout and the panel in it have the normal cursor again");
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
        check(PreProcessKey(focused, Keys.Escape), "Esc is taken by the flyout");
        PumpFor(pumpUntil, 100);
        check(!flyout.IsOpen && !flyout.FilterInstalled, "Esc closes it");
        check(rig.List.Focused, $"and the focus goes back to the grid (focus: {FocusedName(rig.Form)})");

        rig.Host.FocusPanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen && composer.ContainsFocus, "setup: the Composer flyout has the focus again");
        rig.List.Focus();
        PumpFor(pumpUntil, 150);
        check(!flyout.IsOpen && rig.List.Focused, $"a flyout opened with the focus closes when the focus leaves it, and the focus stays where it went (focus: {FocusedName(rig.Form)})");

        rig.Host.FocusPanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        var method = Descendants(composer).OfType<ComboBox>().FirstOrDefault();
        check(method is not null, "setup: the Composer has a ComboBox");
        if (method is not null)
        {
            method.Focus();
            method.DroppedDown = true;
            PumpFor(pumpUntil, 100);
            check(method.DroppedDown && method.Focused, "setup: the Composer's ComboBox has the focus and its list open");
            var handled = PreProcessKey(method, Keys.Escape);
            check(!handled && flyout.IsOpen, "Esc with a ComboBox list open is left to the list; the flyout stays open");
            method.DroppedDown = false;
            PumpFor(pumpUntil, 50);
            check(PreProcessKey(method, Keys.Escape) && !flyout.IsOpen, "with the list closed, Esc closes the flyout");
        }

        rig.Host.FocusPanel(PanelId.Composer);
        PumpFor(pumpUntil, 100);
        var typing = Descendants(composer).OfType<TextBox>().FirstOrDefault(box => box.CanFocus);
        check(typing is not null, "setup: the Composer has a text box");
        typing?.Focus();
        PumpFor(pumpUntil, 50);
        check(typing?.Focused == true && flyout.Page == rig.Registry.PageOf(PanelId.Composer), "setup: typing in the Composer flyout");
        rig.Host.ShowPanel(PanelId.Log, PanelReveal.Passive);
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen && flyout.Page == rig.Registry.PageOf(PanelId.Composer) && typing?.Focused == true,
            $"a Passive reveal leaves a flyout with the focus open, on its panel, with the focus (focus: {FocusedName(rig.Form)})");
        rig.List.Focus();
        PumpFor(pumpUntil, 150);

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
        var floatingWindow = log.FindForm() as FloatingPanelForm;
        check(floatingWindow is not null, "setup: the Log floats again");
        if (floatingWindow is null) return;
        var caption = MouseLParam(new Point(floatingWindow.Left + 60, floatingWindow.Top + 8));
        SendMessage(floatingWindow.Handle, WmNcLButtonDblClk, HtCaption, caption);
        PumpFor(pumpUntil, 100);
        check(log.Parent == bottom && !rig.Host.Snapshot().IsFloating(PanelId.Log), "double-clicking a floating window's title bar docks the panel");
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
            // HoldsBody turns true before the page's own navigation completes; a probe sent then would
            // supersede it and leave the view on about:blank. The view shows itself once the page loaded.
            bool Loaded() => view.Visible && webView.Source is { } source && source == Read<Uri?>(view, "PageUri");
            pumpUntil(Loaded);
            check(Loaded(), $"setup: after {names[i]} the WebP page has finished loading");
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
        check(PanelMenus.FocusChordTarget(new KeyEventArgs(Keys.Control | Keys.Alt | Keys.Shift | Keys.D1), false) is null,
            "nor is Ctrl+Alt+Shift+1: the chord is exactly Ctrl+Alt");
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

    private static void EmptiedAutoHiddenArea(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== moving the last panel out of an auto-hidden area stops it hiding, so a panel moved in later is docked and shown");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.SetAutoHidden(DockArea.Right, true);
        PumpFor(pumpUntil, 100);
        foreach (var id in new[] { PanelId.Inspectors, PanelId.Composer, PanelId.Filters, PanelId.AutoResponder, PanelId.Log })
            rig.Host.MoveTo(id, DockArea.Bottom);
        PumpFor(pumpUntil, 100);
        check(!rig.Host.IsAutoHidden(DockArea.Right), "the emptied Right area is no longer auto-hidden");
        check(!rig.Host.AutoHideStripOf(DockArea.Right).Visible, "and shows no strip");

        rig.Host.MoveTo(PanelId.Log, DockArea.Right);
        PumpFor(pumpUntil, 100);
        var log = rig.Registry.PageOf(PanelId.Log);
        check(StripOf(rig, PanelId.Log) is { Visible: true } strip && rig.Host.DockedAreaOf(strip) == DockArea.Right && log.Visible,
            "the Log moved in is docked in the Right strip and on screen");
        check(!rig.Host.IsAutoHidden(DockArea.Right) && !rig.Host.AutoHideStripOf(DockArea.Right).Visible, "not tucked behind an auto-hide strip");
    }

    /// <summary>A size at 96 DPI and 100% zoom, scaled as the host scales its minimums.</summary>
    private static int Scaled(Control control, int logical) => (int)Math.Round(control.LogicalToDeviceUnits(logical) * Palette.ZoomMultiplier);

    private static void FlyoutMinimumWidth(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a flyout is never narrower than its panel's minimum, unless the window is");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.SetAutoHidden(DockArea.Right, true);
        rig.Host.SetFlyoutFraction(DockArea.Right, 0.1);
        PumpFor(pumpUntil, 100);
        var flyout = rig.Host.FlyoutOf(DockArea.Right);

        foreach (var id in new[] { PanelId.Composer, PanelId.AutoResponder, PanelId.Inspectors, PanelId.Filters, PanelId.Log })
        {
            rig.Host.ShowPanel(id, PanelReveal.Explicit);
            PumpFor(pumpUntil, 100);
            var page = rig.Registry.PageOf(id);
            var minimum = Scaled(rig.Host, PanelLayout.MinFlyoutWidth(id));
            check(flyout.IsOpen && page.Width >= minimum, $"at a 10% fraction the {id} page is still {minimum} px wide ({page.Width})");
        }

        rig.Host.ShowPanel(PanelId.Composer, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        var composer = rig.Registry.ContentOf(PanelId.Composer);
        var history = Descendants(composer).OfType<SplitContainer>().FirstOrDefault(split => split.FixedPanel == FixedPanel.Panel1);
        check(history is not null && history.Panel2.Width >= Scaled(rig.Host, 360) - 1 && history.Panel1.Width >= Scaled(rig.Host, 160) - 1,
            $"the Composer at its minimum keeps the history and the editor usable ({history?.Panel1.Width} + {history?.Panel2.Width})");

        rig.Form.FormBorderStyle = FormBorderStyle.None; // so the window can be smaller than a caption allows
        rig.Form.Size = new Size(420, 400);
        PumpFor(pumpUntil, 200);
        var frame = rig.Host.ClientSize.Width - rig.Host.AutoHideStripOf(DockArea.Right).Width;
        check(flyout.IsOpen && flyout.Width == frame && rig.Host.ClientRectangle.Contains(flyout.Bounds),
            $"in a window narrower than the minimum it fills the window and stays inside ({flyout.Bounds} in {rig.Host.ClientRectangle})");
    }

    private static void FlyoutHeader(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== the flyout has a title bar: its panels button lists the area, the pin docks it, the close button hides the panel");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.SetAutoHidden(DockArea.Right, true);
        PumpFor(pumpUntil, 100);
        var flyout = rig.Host.FlyoutOf(DockArea.Right);
        var header = flyout.Header;

        rig.Host.FocusPanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);
        var filters = rig.Registry.PageOf(PanelId.Filters);
        check(flyout.IsOpen && header.Visible && header.Text == "Filters", $"the header names the panel ({header.Text})");
        check(header.Active, "and shows the accent while the focus is in the flyout");
        check(!flyout.Strip.ShowTabs && filters.Top <= 2 && filters.Bottom >= flyout.Strip.Height - 2,
            $"the one-tab row is gone: the page fills the strip under the header ({filters.Bounds} in {flyout.Strip.Size})");
        check(header.Bottom <= flyout.Strip.Top && header.Height == header.Font.Height + 10, $"the header sits above the page, Font.Height + 10 tall ({header.Height})");
        var accessible = header.AccessibilityObject;
        check(accessible.GetChildCount() == 3 && Enumerable.Range(0, 3).All(i => accessible.GetChild(i)?.Role == AccessibleRole.PushButton),
            "a screen reader sees its three push buttons");

        ClickHeader(header, ToolWindowHeader.Button.Panels);
        PumpFor(pumpUntil, 100);
        var menu = rig.Host.AreaMenu;
        var items = menu.Items.OfType<ToolStripMenuItem>().ToArray();
        check(menu.Visible && items.Select(item => item.Text).SequenceEqual(["Inspectors", "Composer", "Filters", "AutoResponder", "Log"]),
            $"the panels button lists the area's panels ({string.Join(", ", items.Select(item => item.Text))})");
        check(items.Single(item => item.Checked).Text == "Filters", "with the open one checked");
        items.First(item => item.Text == "Composer").PerformClick();
        menu.Close();
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen && flyout.Page == rig.Registry.PageOf(PanelId.Composer) && header.Text == "Composer"
              && rig.Registry.ContentOf(PanelId.Composer).ContainsFocus, "choosing one opens it in the flyout with the focus");

        SendMessage(header.Handle, WmRButtonDown, 2, MouseLParam(new Point(12, header.Height / 2)));
        SendMessage(header.Handle, WmRButtonUp, 0, MouseLParam(new Point(12, header.Height / 2)));
        PumpFor(pumpUntil, 50);
        check(rig.Host.TabMenu.Visible && rig.Host.TabMenu.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == Strings.Panels.AutoHide),
            "a right-click on the title opens the panel's tab menu");
        rig.Host.TabMenu.Close();
        PumpFor(pumpUntil, 50);

        ClickHeader(header, ToolWindowHeader.Button.Close);
        PumpFor(pumpUntil, 100);
        check(rig.Host.IsHidden(PanelId.Composer) && !flyout.IsOpen, "the close button hides the panel and closes the flyout");
        check(rig.Host.IsAutoHidden(DockArea.Right) && rig.Host.AutoHideStripOf(DockArea.Right).Panels.Count == 4, "and leaves the area auto-hidden");

        rig.Host.ShowPanel(PanelId.Filters, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        ClickHeader(header, ToolWindowHeader.Button.Pin);
        PumpFor(pumpUntil, 100);
        check(!rig.Host.IsAutoHidden(DockArea.Right) && !flyout.IsOpen, "the pin docks the area again and closes the flyout");
        check(StripOf(rig, PanelId.Filters) is { } strip && rig.Host.DockedAreaOf(strip) == DockArea.Right && strip.SelectedTab == filters,
            "with the panel it showed selected in the Right strip");
    }

    private static void ClickHeader(ToolWindowHeader header, ToolWindowHeader.Button button)
    {
        var bounds = header.ButtonBounds(button);
        var centre = MouseLParam(new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2));
        SendMessage(header.Handle, WmLButtonDown, 1, centre);
        SendMessage(header.Handle, WmLButtonUp, 0, centre);
    }

    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;

    private static void BottomKeepsMinimum(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== the Bottom area keeps the height its panels need at the default window size");
        using var rig = new Rig();
        rig.Form.Size = new Size(1500, 950); // as MainForm opens
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.MoveTo(PanelId.Inspectors, DockArea.Bottom);
        PumpFor(pumpUntil, 150);
        var inspectors = rig.Registry.PageOf(PanelId.Inspectors);
        var tall = Scaled(rig.Host, 260);
        check(inspectors.Height >= tall, $"the Inspectors at the bottom get at least {tall} px ({inspectors.Height})");
        var outer = (SplitContainer)StripOf(rig, PanelId.Inspectors)!.Parent!.Parent!;
        check(outer.Panel2MinSize >= tall, $"and the splitter cannot squeeze them below it ({outer.Panel2MinSize})");
        check(Math.Abs(rig.Host.Snapshot().BottomFraction - PanelLayout.DefaultBottomFraction) < 0.005,
            $"a splitter held by the minimum records no new fraction ({rig.Host.Snapshot().BottomFraction})");

        rig.Form.Size = new Size(1200, 800);
        PumpFor(pumpUntil, 150);
        check(inspectors.Height >= tall, $"at 1200x800 too ({inspectors.Height})");

        rig.Host.MoveTo(PanelId.Inspectors, DockArea.Right);
        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        PumpFor(pumpUntil, 150);
        check(outer.Panel2MinSize < tall && rig.Registry.PageOf(PanelId.Log).Height >= Scaled(rig.Host, 120),
            $"the Log alone needs less, 120 px ({outer.Panel2MinSize}, {rig.Registry.PageOf(PanelId.Log).Height})");
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    private static void FloatingWindowChrome(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a floating window is a plain sizable window titled once, with no minimise or maximise box");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.FloatPanel(PanelId.Composer, new Point(200, 200));
        PumpFor(pumpUntil, 150);
        var window = rig.Host.FloatingWindow(PanelId.Composer);
        check(window is not null, "setup: the Composer floats");
        if (window is null) return;
        check(!window.MinimizeBox && !window.MaximizeBox && !window.ShowIcon && window.FormBorderStyle == FormBorderStyle.Sizable,
            "no minimise or maximise box and no icon, but an ordinary sizable border");
        const int GwlExStyle = -20, WsExToolWindow = 0x80;
        var exStyle = GetWindowLongPtr(window.Handle, GwlExStyle);
        check((exStyle & WsExToolWindow) == 0, $"it is not a tool window, which Alt+Tab would leave out (ex-style 0x{exStyle:X})");
        var composer = rig.Registry.PageOf(PanelId.Composer);
        check(!window.Strip.ShowTabs && composer.Top <= 2 && composer.Height >= window.Strip.Height - 4,
            $"the one-tab row is gone, so the title shows once ({composer.Bounds} in {window.Strip.Size})");
        check(window.Text == "Composer", "the title bar names the panel");
    }

    private static void FloatingCaptionDocks(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a floating window's title bar stands in for its tab: menu, double-click and drag onto an area's tabs");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.FloatPanel(PanelId.Log, new Point(300, 300));
        PumpFor(pumpUntil, 150);
        var window = rig.Host.FloatingWindow(PanelId.Log);
        check(window is not null, "setup: the Log floats");
        if (window is null) return;
        var caption = MouseLParam(new Point(window.Left + 60, window.Top + 8));

        SendMessage(window.Handle, WmNcRButtonUp, HtCaption, caption);
        PumpFor(pumpUntil, 50);
        check(rig.Host.TabMenu.Visible && rig.Host.TabMenu.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == Strings.Panels.Dock),
            "a right-click on the title bar opens the tab menu, with Dock");
        rig.Host.TabMenu.Close();
        PumpFor(pumpUntil, 50);

        var right = StripOf(rig, PanelId.Composer)!;
        var target = BeforeTab(right, 1);
        window.PointerPosition = () => target;
        SendMessage(window.Handle, WmEnterSizeMove, 0, 0);
        window.Location = new Point(window.Left + 40, window.Top + 40);
        MovingMessage(window);
        PumpFor(pumpUntil, 60);
        check(right.IsDropTarget && right.DropIndex == 1, $"dragging the title bar over the Right tabs marks where it would dock (index {right.DropIndex})");
        SendMessage(window.Handle, WmExitSizeMove, 0, 0);
        pumpUntil(() => window.IsDisposed);
        check(!rig.Host.IsFloating(PanelId.Log) && Order(rig, DockArea.Right)[1] == PanelId.Log && !right.IsDropTarget,
            $"releasing it there docks the Log second on the right ({string.Join(", ", Order(rig, DockArea.Right))})");

        rig.Host.FloatPanel(PanelId.Log, new Point(300, 300));
        PumpFor(pumpUntil, 150);
        window = rig.Host.FloatingWindow(PanelId.Log)!;
        window.PointerPosition = () => target;
        SendMessage(window.Handle, WmEnterSizeMove, 0, 0);
        MovingMessage(window);
        SendMessage(window.Handle, WmExitSizeMove, 0, 0);
        PumpFor(pumpUntil, 100);
        check(rig.Host.IsFloating(PanelId.Log) && !window.IsDisposed && !right.IsDropTarget,
            "a drag that ends where it began, as Esc leaves it, docks nothing");

        var content = right.PointToScreen(new Point(right.Width / 2, right.GetTabRect(0).Bottom + (right.Height - right.GetTabRect(0).Bottom) / 2));
        window.PointerPosition = () => content;
        SendMessage(window.Handle, WmEnterSizeMove, 0, 0);
        window.Location = new Point(window.Left + 40, window.Top + 40);
        MovingMessage(window);
        PumpFor(pumpUntil, 60);
        check(!right.IsDropTarget, "over a panel's content, below the tabs, nothing is marked");
        SendMessage(window.Handle, WmExitSizeMove, 0, 0);
        PumpFor(pumpUntil, 150);
        check(rig.Host.IsFloating(PanelId.Log) && !window.IsDisposed,
            "releasing the title bar over a panel's content keeps the window floating where it was parked");
        if (window.IsDisposed) return;

        SendMessage(window.Handle, WmNcLButtonDblClk, HtCaption, caption);
        PumpFor(pumpUntil, 100);
        check(!rig.Host.IsFloating(PanelId.Log) && window.IsDisposed, "double-clicking the title bar docks the panel");
    }

    private static void FloatingKeyboardMenu(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a floating window's hidden strip is no tab stop, and Shift+F10 still opens the tab menu");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.FloatPanel(PanelId.Filters, new Point(300, 300));
        PumpFor(pumpUntil, 150);
        var window = rig.Host.FloatingWindow(PanelId.Filters);
        check(window is not null, "setup: the Filters float");
        if (window is null) return;
        var page = rig.Registry.PageOf(PanelId.Filters);

        check(!window.Strip.TabStop, "the strip, whose tab row is hidden, is not a tab stop");
        window.Activate();
        PumpFor(pumpUntil, 100);
        check(!window.Strip.Focused, $"activating the window does not focus the invisible strip (focus: {FocusedName(window)})");

        bool MenuShowsDock() => rig.Host.TabMenu.Visible
            && rig.Host.TabMenu.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == Strings.Panels.Dock);

        // Shift+F10 in a control with no menu of its own bubbles WM_CONTEXTMENU, with no position, up to the window.
        SendMessage(page.Handle, WmContextMenu, page.Handle, -1);
        PumpFor(pumpUntil, 100);
        check(MenuShowsDock(), "Shift+F10 in the panel's content opens the floating panel's tab menu");
        rig.Host.TabMenu.Close();
        PumpFor(pumpUntil, 50);

        SendMessage(window.Strip.Handle, WmContextMenu, window.Strip.Handle, -1);
        PumpFor(pumpUntil, 100);
        check(MenuShowsDock(), "and so does the strip's own keyboard menu request, should it ever hold the focus");
        rig.Host.TabMenu.Close();
        PumpFor(pumpUntil, 50);

        var at = page.PointToScreen(new Point(20, 20));
        SendMessage(page.Handle, WmContextMenu, page.Handle, (at.Y << 16) | (at.X & 0xFFFF));
        PumpFor(pumpUntil, 100);
        check(!rig.Host.TabMenu.Visible, "a right-click in the content is not taken for the tab");
        rig.Host.TabMenu.Close();
    }

    private static void MoveKeepsFocus(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== moving the panel that has the focus keeps the focus in it at its new place");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);

        rig.Host.FocusPanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);
        check(rig.Filters.ContainsFocus, $"setup: the Filters have the focus (focus: {FocusedName(rig.Form)})");
        rig.Host.MoveTo(PanelId.Filters, DockArea.Left);
        PumpFor(pumpUntil, 100);
        check(StripOf(rig, PanelId.Filters) == StripOf(rig, PanelId.Sessions), "setup: the Filters moved to the left");
        check(rig.Filters.ContainsFocus, $"a move with the tab menu keeps the focus in the Filters (focus: {FocusedName(rig.Form)})");

        rig.Host.FloatPanel(PanelId.Filters);
        PumpFor(pumpUntil, 150);
        rig.Host.FocusPanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);
        rig.Host.MoveTo(PanelId.Filters, DockArea.Bottom);
        PumpFor(pumpUntil, 150);
        check(rig.Filters.ContainsFocus && rig.Filters.FindForm() == rig.Form,
            $"and a floating panel moved to the Bottom brings the focus with it (focus: {FocusedName(rig.Form)})");

        rig.List.Focus();
        PumpFor(pumpUntil, 50);
        check(rig.List.Focused, "setup: the grid has the focus");
        // Rebuilding the strips takes their pages out, which can hand the focus to a strip.
        rig.Host.MoveTo(PanelId.Log, DockArea.Bottom);
        PumpFor(pumpUntil, 100);
        check(rig.List.Focused, $"moving another panel leaves the focus in the grid (focus: {FocusedName(rig.Form)})");
        rig.Host.MoveTo(PanelId.Log, DockArea.Right);
        PumpFor(pumpUntil, 100);
        check(rig.List.Focused, $"and so does moving it away again (focus: {FocusedName(rig.Form)})");
    }

    private static void PassiveRevealKeepsFocus(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a Passive reveal never takes the tab of a docked panel that has the focus");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);

        rig.Host.FocusPanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);
        check(rig.Filters.ContainsFocus, $"setup: the Filters have the focus (focus: {FocusedName(rig.Form)})");
        rig.Host.ShowPanel(PanelId.Log, PanelReveal.Passive);
        PumpFor(pumpUntil, 100);
        check(rig.Host.SelectedPanel(DockArea.Right) == PanelId.Filters && rig.Filters.ContainsFocus,
            $"the Filters stay selected with the focus (selected {rig.Host.SelectedPanel(DockArea.Right)}, focus: {FocusedName(rig.Form)})");

        rig.Host.ShowPanel(PanelId.Log, PanelReveal.Explicit);
        PumpFor(pumpUntil, 100);
        check(rig.Host.SelectedPanel(DockArea.Right) == PanelId.Log, "an Explicit reveal still selects the Log");
    }

    private static void DeactivateCloseKeepsActivation(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a flyout closed because the window lost activation does not take it back");
        using var rig = new Rig();
        // As MainForm wires it.
        rig.Form.Deactivate += (_, _) => rig.Host.CloseFlyouts(deactivated: true);
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.SetAutoHidden(DockArea.Right, true);
        PumpFor(pumpUntil, 100);
        var flyout = rig.Host.FlyoutOf(DockArea.Right);
        rig.Form.Activate();
        rig.Host.FocusPanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen && rig.Filters.ContainsFocus && Form.ActiveForm == rig.Form,
            $"setup: the Filters flyout has the focus in the active window (focus: {FocusedName(rig.Form)})");

        // Windows still reports the main window active while it handles its own deactivation, so the
        // close happens with the focus in the flyout; handing it to the grid then is a SetFocus that can
        // take the activation back.
        var gridFocused = 0;
        var reactivated = 0;
        rig.List.GotFocus += (_, _) => gridFocused++;
        rig.Form.Activated += (_, _) => reactivated++;
        using var other = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(1300, 100), Size = new Size(300, 200) };
        var box = new TextBox { Dock = DockStyle.Top };
        other.Controls.Add(box);
        other.Show();
        other.Activate();
        PumpFor(pumpUntil, 200);
        check(!flyout.IsOpen, "setup: the flyout closed when the window lost activation");
        check(Form.ActiveForm == other && reactivated == 0,
            $"the other window stays active and the main window is not activated again ({reactivated} activations, main active: {Form.ActiveForm == rig.Form})");
        check(gridFocused == 0 && !rig.Grid.ContainsFocus, $"and the grid is not given the focus ({gridFocused} times)");
        other.Close();
        PumpFor(pumpUntil, 100);

        rig.Form.Activate();
        rig.Host.FocusPanel(PanelId.Filters);
        PumpFor(pumpUntil, 100);
        check(flyout.IsOpen && rig.Filters.ContainsFocus, "setup: the flyout has the focus again");
        rig.Host.CloseFlyouts();
        PumpFor(pumpUntil, 50);
        check(!flyout.IsOpen && rig.List.Focused, $"closed any other way, the focus still goes to the grid (focus: {FocusedName(rig.Form)})");
    }

    private static void CaptionDragWindowDisposed(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a floating window disposed in the middle of a title-bar drag leaves no marker");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        rig.Host.FloatPanel(PanelId.Log, new Point(300, 300));
        PumpFor(pumpUntil, 150);
        var window = rig.Host.FloatingWindow(PanelId.Log);
        check(window is not null, "setup: the Log floats");
        if (window is null) return;
        var right = StripOf(rig, PanelId.Composer)!;
        var target = BeforeTab(right, 1);
        window.PointerPosition = () => target;
        SendMessage(window.Handle, WmEnterSizeMove, 0, 0);
        window.Location = new Point(window.Left + 40, window.Top + 40);
        MovingMessage(window);
        PumpFor(pumpUntil, 60);
        check(right.IsDropTarget, "setup: a title-bar drag marks the Right tabs");
        // Gone inside the move loop, so WM_EXITSIZEMOVE never comes; ShuttingDown keeps the close from
        // docking, whose rearrangement would clear the marker on its own.
        window.ShuttingDown = true;
        window.Dispose();
        PumpFor(pumpUntil, 60);
        check(!right.IsDropTarget && right.DropIndex == -1, "a window disposed mid-drag leaves no marker behind");
    }

    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmNcRButtonUp = 0x00A5;
    private const int WmMoving = 0x0216;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int HtCaption = 2;

    /// <summary>A WM_MOVING as the system move loop sends it, with the window's rectangle.</summary>
    private static void MovingMessage(Form window)
    {
        var rect = Marshal.AllocHGlobal(16);
        try
        {
            Marshal.WriteInt32(rect, 0, window.Left);
            Marshal.WriteInt32(rect, 4, window.Top);
            Marshal.WriteInt32(rect, 8, window.Right);
            Marshal.WriteInt32(rect, 12, window.Bottom);
            SendMessage(window.Handle, WmMoving, 9, rect);
        }
        finally
        {
            Marshal.FreeHGlobal(rect);
        }
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

    private static void NativePartsFollowTheme(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== scrollbars, trees and toolbars follow the palette");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        var wasLight = Palette.IsLightMode;
        try
        {
            if (Palette.IsLightMode) Palette.ToggleMode();
            Palette.Apply(rig.Form);
            rig.Host.ApplyTheme();
            PumpFor(pumpUntil, 100);

            var log = rig.Registry.ContentOf(PanelId.Log);
            var tree = Descendants(rig.Inspector).OfType<TreeView>().First();
            var hosts = Descendants(rig.Filters).OfType<ListBox>().First();
            var scrollBar = Descendants(rig.Inspector).OfType<ScrollBar>().FirstOrDefault();
            Control[] natives = [rig.List, tree, log, hosts, .. scrollBar is null ? Array.Empty<Control>() : [scrollBar]];
            check(scrollBar is not null, "setup: the hex view has a native scrollbar to theme");
            foreach (var control in natives)
            {
                // A page not on screen yet gets its handle now, as it would when first shown.
                _ = control.Handle;
                check(Palette.NativeThemeOf(control) == "DarkMode_Explorer",
                    $"dark: the {control.GetType().Name}'s scrollbars use DarkMode_Explorer ({Palette.NativeThemeOf(control) ?? "none"})");
            }

            typeof(Control).GetMethod("RecreateHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(tree, null);
            check(Palette.NativeThemeOf(tree) == "DarkMode_Explorer", "a recreated handle gets the dark theme again");

            Palette.ToggleMode();
            Palette.Apply(rig.Form);
            rig.Host.ApplyTheme();
            foreach (var control in natives)
                check(Palette.NativeThemeOf(control) == "Explorer", $"light: the {control.GetType().Name} goes back to Explorer ({Palette.NativeThemeOf(control) ?? "none"})");

            Palette.ToggleMode();
            Palette.Apply(rig.Form);
            rig.Host.ApplyTheme();
            rig.Host.ShowPanel(PanelId.AutoResponder, PanelReveal.Explicit);
            PumpFor(pumpUntil, 100);
            var responder = rig.Registry.ContentOf(PanelId.AutoResponder);
            var toolbar = Descendants(responder).OfType<ToolStrip>().First(strip => strip is not ToolStripDropDown);
            check(toolbar.Renderer is ToolStripProfessionalRenderer { ColorTable: not null } renderer
                  && renderer.ColorTable.OverflowButtonGradientBegin == Palette.SurfaceAlt,
                "the AutoResponder toolbar has the palette renderer, overflow button included");
            check(!toolbar.OverflowButton.Visible && toolbar.Items.Cast<ToolStripItem>().All(item => item.Placement == ToolStripItemPlacement.Main && item.Visible),
                "every AutoResponder toolbar item fits, so no overflow arrow shows");
        }
        finally
        {
            if (Palette.IsLightMode != wasLight) Palette.ToggleMode();
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    /// <summary>Every tab is either wholly in view, clear of the ▾ button, or reachable from its menu.</summary>
    private static bool EveryTabReachable(DarkTabControl strip, out string detail)
    {
        var button = strip.OverflowButtonBounds;
        var limit = button.IsEmpty ? strip.Width : button.Left;
        var hidden = Enumerable.Range(0, strip.TabCount)
            .Where(i => strip.GetTabRect(i) is var rect && (rect.Left < 0 || rect.Right > limit))
            .Select(i => strip.TabPages[i].Text).ToArray();
        detail = hidden.Length == 0 ? "all in view" : $"cut off: {string.Join(", ", hidden)}; button {(button.IsEmpty ? "absent" : "shown")}";
        return hidden.Length == 0 || !button.IsEmpty;
    }

    private static void TabOverflowMenu(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== tabs that do not fit are reached from a ▾ menu");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        Palette.Apply(rig.Form);
        PumpFor(pumpUntil, 100);
        var right = StripOf(rig, PanelId.Log)!;
        check(EveryTabReachable(right, out var detail), $"at 1200 px no Right tab is unreachable ({detail})");

        // Narrow enough that the five Right tabs cannot all fit.
        rig.Form.Width = 760;
        PumpFor(pumpUntil, 150);
        check(right.HasOverflow && !right.OverflowButtonBounds.IsEmpty, $"a narrow Right strip shows the ▾ button ({right.Width} px)");
        var upDown = FindWindowEx(right.Handle, 0, "msctls_updown32", null);
        check(upDown == 0 || !IsWindowVisible(upDown), "and the native ◀▶ arrows are hidden");
        var button = right.OverflowButtonBounds;
        check(button.Width == right.ItemSize.Height && button.Right == right.Width, "the button is a row-high square at the right end");
        var centre = new Point(button.X + button.Width / 2, button.Y + button.Height / 2);
        check(right.TabIndexAt(centre) == -1, "the button is not part of the tab beneath it");
        check(EveryTabReachable(right, out detail), $"every tab is in view or behind the button ({detail})");

        SendMessage(right.Handle, WmMouseMove, 0, MouseLParam(centre));
        check(right.IsOverflowHot, "hovering the button lights it");
        SendMessage(right.Handle, WmMouseMove, 0, MouseLParam(new Point(2, centre.Y)));
        check(!right.IsOverflowHot, "and moving off it clears that");

        var before = right.SelectedIndex;
        PostMessage(right.Handle, WmLButtonDown, MkLButton, MouseLParam(centre));
        PostMessage(right.Handle, WmLButtonUp, 0, MouseLParam(centre));
        PumpFor(pumpUntil, 150);
        var menu = right.OverflowMenu;
        check(menu is not null, "clicking the button opens its menu");
        check(right.SelectedIndex == before && !right.Capture, "and neither selects the tab beneath it nor starts a drag");
        if (menu is null) return;
        var items = menu.Items.OfType<ToolStripMenuItem>().ToArray();
        check(items.Select(item => item.Text).SequenceEqual(right.TabPages.Cast<TabPage>().Select(page => page.Text)),
            $"the menu lists every tab in order ({string.Join(", ", items.Select(item => item.Text))})");
        check(items.Count(item => item.Checked) == 1 && items[right.SelectedIndex].Checked, "with the selected tab checked");
        check(menu.BackColor == Palette.SurfaceAlt, "and the menu is themed");

        var last = right.TabCount - 1;
        items[last].PerformClick();
        menu.Close();
        PumpFor(pumpUntil, 150);
        check(right.SelectedIndex == last && rig.Host.SelectedPanel(DockArea.Right) == PanelId.Log, "choosing Log selects it");
        var rect = right.GetTabRect(last);
        check(rect.Left >= 0 && rect.Right <= right.OverflowButtonBounds.Left,
            $"and scrolls it into view, clear of the button ({rect.Left}..{rect.Right}, button at {right.OverflowButtonBounds.Left})");

        right.ShowTabs = false;
        check(!right.HasOverflow && right.OverflowButtonBounds.IsEmpty, "with the tab row hidden there is no button");
        right.ShowTabs = true;

        rig.Form.Width = 1900;
        PumpFor(pumpUntil, 150);
        check(!right.HasOverflow && right.OverflowButtonBounds.IsEmpty && right.GetTabRect(0).Left >= 0,
            $"once the tabs fit again the button goes and the row starts at the first tab ({right.Width} px)");

        // A window that opens too narrow overflows while its strips are still being built.
        using var narrow = new Rig();
        narrow.Form.Width = 900;
        narrow.Form.Show();
        pumpUntil(() => narrow.Grid.Width > 0);
        Palette.Apply(narrow.Form);
        PumpFor(pumpUntil, 300);
        var strips = Descendants(narrow.Form).OfType<DarkTabControl>().Where(strip => strip.Visible && strip.HasOverflow).ToArray();
        check(strips.Length > 0, $"setup: a 900 px window has overflowing strips ({strips.Length})");
        check(strips.All(strip => FindWindowEx(strip.Handle, 0, "msctls_updown32", null) is var arrows && (arrows == 0 || !IsWindowVisible(arrows))),
            "none of them shows the native ◀▶ arrows");
    }

    private static void FiltersFitNarrow(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== the Filters panel fits a narrow flyout");
        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0), FormBorderStyle = FormBorderStyle.None };
        var filters = new FilterPanel { Dock = DockStyle.Fill };
        form.Controls.Add(filters);
        form.Show();
        // 380 px is the Filters flyout's minimum at 96 DPI; try it unscaled too, the tightest case.
        foreach (var width in new[] { form.LogicalToDeviceUnits(380), 380 })
        {
            form.ClientSize = new Size(width, form.LogicalToDeviceUnits(640));
            Palette.Apply(form);
            PumpFor(pumpUntil, 100);

            foreach (var button in Descendants(filters).OfType<Button>())
            {
                var bounds = button.Bounds;
                var preferred = button.PreferredSize;
                var inView = filters.RectangleToClient(button.Parent!.RectangleToScreen(bounds));
                check(preferred.Width <= bounds.Width && preferred.Height <= bounds.Height && inView.Right <= filters.ClientSize.Width,
                    $"at {width} px the {button.Text} button is not clipped (preferred {preferred}, bounds {bounds}, right edge {inView.Right})");
            }

            var toolbar = Descendants(filters).OfType<ToolStrip>().First();
            check(!toolbar.OverflowButton.Visible && toolbar.Items.Cast<ToolStripItem>().All(item => item.Placement == ToolStripItemPlacement.Main)
                  && toolbar.PreferredSize.Width <= toolbar.Width,
                $"at {width} px the Actions button fits its toolbar ({toolbar.PreferredSize.Width} in {toolbar.Width})");
            var useFilters = Descendants(filters).OfType<CheckBox>().First(box => box.Text == Strings.Filters.UseFilters);
            var toolbarLeft = filters.RectangleToClient(toolbar.RectangleToScreen(toolbar.ClientRectangle)).Left;
            var useRight = filters.RectangleToClient(useFilters.RectangleToScreen(useFilters.ClientRectangle)).Right;
            check(useRight <= toolbarLeft, $"at {width} px Use Filters stops before Actions ({useRight} vs {toolbarLeft})");

            foreach (var box in Descendants(filters).OfType<CheckBox>())
                check(box.AutoEllipsis && (box.PreferredSize.Width <= box.Width || box.AutoEllipsis),
                    $"at {width} px the {box.Text} checkbox ends in an ellipsis rather than cutting its text");
        }
    }

    private static void SingleBorders(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== panel content draws one border, not a frame inside the page");
        using var rig = new Rig();
        rig.Form.Show();
        pumpUntil(() => rig.Grid.Width > 0);
        Palette.Apply(rig.Form);
        PumpFor(pumpUntil, 100);

        var fields = Descendants(rig.Inspector).OfType<TextBox>()
            .Where(box => box.Text.StartsWith(Strings.Inspector.Request, StringComparison.Ordinal)
                          || box.Text.StartsWith(Strings.Inspector.Response, StringComparison.Ordinal)).ToArray();
        var filterBox = Descendants(rig.Grid).OfType<TextBox>().First(box => box.PlaceholderText == Strings.SessionList.FilterPlaceholder);
        check(fields.Length == 2, $"setup: the Request and Response start lines are found ({fields.Length})");
        check(rig.List.BorderStyle == BorderStyle.None, "the session grid has no frame of its own");

        var wasZoom = false;
        try
        {
            foreach (var zoomed in new[] { false, true })
            {
                if (zoomed)
                {
                    wasZoom = true;
                    Zoom("ZoomIn");
                    Zoom("ZoomIn");
                    Palette.RescaleFonts();
                    Palette.Apply(rig.Form);
                    PumpFor(pumpUntil, 150);
                }
                var when = zoomed ? "zoomed in" : "at 100%";
                foreach (var box in fields.Append(filterBox))
                {
                    var row = box.Parent!;
                    var name = box == filterBox ? "filter box" : box.Text.Split(' ')[0] + " line";
                    check(box.BorderStyle == BorderStyle.None, $"{when} the {name} has no frame of its own");
                    check(box.Height >= box.PreferredHeight && box.Top > 0 && box.Bottom < row.ClientSize.Height,
                        $"{when} the {name} sits wholly inside its row (box {box.Top}..{box.Bottom}, preferred {box.PreferredHeight}, row {row.ClientSize.Height})");
                    check(row.Height >= box.Font.Height + row.LogicalToDeviceUnits(8),
                        $"{when} the {name}'s row is the font's height plus 8 px ({row.Height} for font {box.Font.Height})");
                }
            }
        }
        finally
        {
            if (wasZoom)
            {
                Zoom("Reset");
                Palette.RescaleFonts();
                Palette.Apply(rig.Form);
            }
        }

        static void Zoom(string method) =>
            typeof(Palette).Assembly.GetType("Piper.App.Theme.FontScale", throwOnError: true)!.GetMethod(method)!.Invoke(null, null);
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
