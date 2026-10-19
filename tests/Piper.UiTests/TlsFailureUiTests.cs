using Piper.App;
using Piper.App.Controls;
using Piper.App.Theme;
using Piper.Core.Http;
using Piper.Core.Sessions;

// Failed client TLS handshakes in the real grid and inspector (X2 slice 3): TLS ERR and the reason in the
// grid, amber or red by kind in both themes, the explanation in the response pane, and ERR left alone for
// every other failure. The sessions are built here; nothing touches a proxy, a certificate or a setting.
internal static class TlsFailureUiTests
{
    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        var startMode = Palette.Mode;
        try
        {
            RunGrid(check, pumpUntil);
        }
        finally
        {
            Palette.SetMode(startMode);
        }
    }

    internal static Session Failure(TlsFailureKind kind, string host, int attempts = 1, string? alert = null, bool overflow = false) => new()
    {
        Request = new HttpRequestData { Method = "CONNECT", RequestTarget = $"{host}:443", Url = host.Length == 0 ? null : new Uri($"https://{host}") },
        IsTunnel = true,
        IsHttps = true,
        State = SessionState.Failed,
        ProcessName = "launcher.exe",
        Error = "TLS handshake with client failed: boom",
        Completed = DateTimeOffset.Now,
        TlsFailure = new TlsFailureInfo(kind, TlsFailurePhase.InHandshake, alert, alert is null ? null : 0x80090325u)
        {
            Attempts = attempts,
            Sni = host.Length == 0 ? null : host,
            IsOverflow = overflow,
            LastSeen = DateTimeOffset.Now,
        },
    };

    private static void RunGrid(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a failed TLS handshake is a TLS ERR row with its reason, coloured by kind in both themes");
        var store = new SessionStore();
        using var form = new Form { Width = 1200, Height = 700, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        var split = new SplitContainer { Dock = DockStyle.Fill };
        var grid = new SessionListView(store) { Dock = DockStyle.Fill };
        var inspector = new InspectorPanel { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(grid);
        split.Panel2.Controls.Add(inspector);
        form.Controls.Add(split);
        grid.SelectionChanged += (_, session) => inspector.Show(session); // as MainForm wires it
        grid.SelectedSessionRefreshed += (_, refresh) => inspector.Refresh(refresh.Session, refresh.Reload);
        form.Show();
        var list = All(grid).OfType<ListView>().First();

        var refused = Failure(TlsFailureKind.Rejected, "api.example.test", attempts: 12, alert: "UnknownCA");
        var left = Failure(TlsFailureKind.ClosedEarly, "early.example.test");
        var timeout = Failure(TlsFailureKind.Timeout, "slow.example.test");
        var overflow = Failure(TlsFailureKind.Other, "", attempts: 40, overflow: true);
        var failed = new Session
        {
            Request = new HttpRequestData { Method = "GET", Url = new Uri("http://down.example.test/x") },
            State = SessionState.Failed,
            Error = "Could not connect",
            Completed = DateTimeOffset.Now,
        };
        var okay = new Session
        {
            Request = new HttpRequestData { Method = "GET", Url = new Uri("http://up.example.test/x") },
            Response = new HttpResponseData { StatusCode = 200 },
            Completed = DateTimeOffset.Now,
        };
        Session[] sessions = [refused, left, timeout, overflow, failed, okay];
        foreach (var session in sessions) store.Add(session);
        pumpUntil(() => list.VirtualListSize == sessions.Length);
        check(list.VirtualListSize == sessions.Length, "setup: every session is in the grid");

        string Cell(Session session, int column) => list.Items[Array.IndexOf(sessions, session)].SubItems[column].Text;
        check(Cell(refused, 1) == "TLS ERR" && Cell(left, 1) == "TLS ERR" && Cell(overflow, 1) == "TLS ERR", "a failed handshake reads TLS ERR in the Result column");
        check(Cell(failed, 1) == "ERR", "any other failure still reads ERR");
        check(Cell(okay, 1) == "200", "and a response its status");
        check(Cell(refused, 2) == "CONNECT" && Cell(refused, 3) == "api.example.test", "Method and Host are the CONNECT's");
        check(Cell(refused, 4) == "client refused Piper's certificate: UnknownCA (12 attempts)", $"the Path cell carries the reason and the count (got '{Cell(refused, 4)}')");
        check(Cell(left, 4) == "client closed the tunnel before TLS started", "one attempt shows no count");
        check(Cell(overflow, 4) == "40 more TLS handshake failures are not shown individually", "the overflow row says the rest are not shown individually");
        check(Cell(failed, 4) == "/x", "other rows keep their path");

        foreach (var mode in new[] { ThemeMode.Dark, ThemeMode.Light })
        {
            Palette.SetMode(mode);
            check(Palette.ForStatus(refused) == Palette.StatusClientError && Palette.ForStatus(left) == Palette.StatusClientError,
                $"{mode}: a client that refused or left is amber");
            check(Palette.ForStatus(timeout) == Palette.StatusServerError && Palette.ForStatus(overflow) == Palette.StatusServerError
                  && Palette.ForStatus(failed) == Palette.StatusServerError, $"{mode}: other failures stay red");
            check(Palette.StatusClientError != Palette.StatusServerError && Palette.StatusClientError.R > Palette.StatusClientError.B,
                $"{mode}: amber is not the red, and it is warm");

            // The painted cell, not only the palette: the Result cell's text pixels of an amber and of a red row.
            Palette.Apply(form);
            list.Invalidate();
            var settled = Environment.TickCount64 + 300;
            pumpUntil(() => Environment.TickCount64 > settled);
            var amber = Palette.StatusClientError;
            var red = Palette.StatusServerError;
            var amberCell = Pixels(list, Array.IndexOf(sessions, refused), 1);
            var redCell = Pixels(list, Array.IndexOf(sessions, timeout), 1);
            check(amberCell.Any(p => Near(p, amber)) && !amberCell.Any(p => Near(p, red)), $"{mode}: the amber row's Result cell is painted amber");
            check(redCell.Any(p => Near(p, red)) && !redCell.Any(p => Near(p, amber)), $"{mode}: the red row's Result cell is painted red");
        }
        form.Close();
    }

    private static bool Near(Color pixel, Color colour) =>
        Math.Abs(pixel.R - colour.R) <= 24 && Math.Abs(pixel.G - colour.G) <= 24 && Math.Abs(pixel.B - colour.B) <= 24;

    /// <summary>The pixels of one cell as the owner-draw painted them.</summary>
    private static List<Color> Pixels(ListView list, int row, int column)
    {
        var bounds = list.Items[row].SubItems[column].Bounds;
        var pixels = new List<Color>();
        if (bounds.Width <= 0 || bounds.Height <= 0) return pixels;
        using var bitmap = new Bitmap(list.Width, list.Height);
        list.DrawToBitmap(bitmap, new Rectangle(0, 0, list.Width, list.Height));
        for (var y = Math.Max(0, bounds.Top); y < Math.Min(bitmap.Height, bounds.Bottom); y++)
        for (var x = Math.Max(0, bounds.Left); x < Math.Min(bitmap.Width, bounds.Right); x++)
            pixels.Add(bitmap.GetPixel(x, y));
        return pixels;
    }

    internal static IEnumerable<Control> All(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in All(child)) yield return descendant;
        }
    }
}
