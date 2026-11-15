using System.Runtime.InteropServices;
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

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        DefaultLayout(check, pumpUntil);
        ShowPanelKeepsFocus(check, pumpUntil);
        ArrowKeysKeepFocus(check, pumpUntil);
        SetTabChecked(check, pumpUntil);
        RevealHiddenPanel(check, pumpUntil);
    }

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
