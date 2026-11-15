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
        string[] texts = [Strings.Panels.MoveLeft, Strings.Panels.MoveRight, Strings.Panels.MoveBottom, Strings.Panels.Hide];
        check(items.Select(item => item.Text).SequenceEqual(texts), $"the tab menu holds Move to Left/Right/Bottom and Hide ({string.Join(", ", items.Select(item => item.Text))})");
        check(items.Length == 4 && items[0].Enabled && items[1].Enabled && !items[2].Enabled && items[3].Enabled,
            "Move to Bottom is greyed out where the Log already is");
        if (items.Length == 4) items[1].PerformClick();
        PumpFor(pumpUntil, 100);
        check(StripOf(rig, PanelId.Log) == right && right.SelectedTab == log, "Move to Right puts the Log back on the right, selected");
        check(rig.Host.SelectedPanel(DockArea.Bottom) is null, "and leaves the bottom empty");
        foreach (var item in items) item.Dispose();

        var sessionItems = rig.Host.TabMenuItems(PanelId.Sessions).OfType<ToolStripMenuItem>().ToArray();
        check(sessionItems.Length == 4 && !sessionItems[0].Enabled && !sessionItems[3].Enabled,
            "for Sessions, Move to Left (where it is) and Hide are greyed out");
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
        check(strips.Count == 3 && strips.Contains(left) && strips.Contains(right),
            $"a late StripCreated subscriber still gets every docked strip ({strips.Count})");
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
