using Piper.App;
using Piper.App.Controls;
using Piper.Core.Http;
using Piper.Core.Sessions;

// The explanation of a failed client TLS handshake in the real inspector (X2 slice 3). Selecting a row must
// show it, a moving count must reach it, and selecting anything else must leave none of it behind.
internal static class TlsFailureInspectorUiTests
{
    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== selecting a failed TLS handshake explains it in the response pane, and nothing of it outlives the selection");
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
        var list = TlsFailureUiTests.All(grid).OfType<ListView>().First();

        var refused = TlsFailureUiTests.Failure(TlsFailureKind.Rejected, "api.example.test", attempts: 12, alert: "UnknownCA");
        var left = TlsFailureUiTests.Failure(TlsFailureKind.ClosedEarly, "early.example.test");
        var overflow = TlsFailureUiTests.Failure(TlsFailureKind.Other, "", attempts: 40, overflow: true);
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
        Session[] sessions = [refused, left, overflow, failed, okay];
        foreach (var session in sessions) store.Add(session);
        pumpUntil(() => list.VirtualListSize == sessions.Length);

        var responseHeaders = TlsFailureUiTests.All(inspector).OfType<ListView>().Where(l => l.Columns.Count >= 2 && l.Columns[0].Text == "Name").Last();
        var summaries = TlsFailureUiTests.All(inspector).OfType<TextBox>().Where(t => !t.Multiline && t.ReadOnly).ToArray();
        string Rows() => string.Join("|", responseHeaders.Items.Cast<ListViewItem>().Select(i => i.Text + "=" + i.SubItems[1].Text));
        void Select(Session session)
        {
            list.SelectedIndices.Clear();
            list.SelectedIndices.Add(Array.IndexOf(sessions, session));
        }

        Select(refused);
        pumpUntil(() => Rows().Contains("Piper saw"));
        check(Rows().Contains("Piper saw=") && Rows().Contains("Piper cannot tell=") && Rows().Contains("What to do="), "selecting a TLS failure shows what Piper saw, cannot tell and what to do");
        check(Rows().Contains("App=launcher.exe") && Rows().Contains("Attempts=12") && Rows().Contains("SChannel code=0x80090325"), "with the app, the count and the SChannel code as a code");
        check(summaries.Any(t => t.Text == "Response   TLS ERR - client refused Piper's certificate: UnknownCA (12 attempts)"), "the summary line says TLS ERR and the reason");

        // A count that moves updates the panel without the row's state changing.
        refused.TlsFailure = refused.TlsFailure! with { Attempts = 13 };
        store.NotifyUpdated(refused);
        pumpUntil(() => Rows().Contains("Attempts=13"));
        check(Rows().Contains("Attempts=13"), "a new attempt count reaches the open panel, although the row's state did not change");
        check(summaries.Any(t => t.Text.EndsWith("(13 attempts)", StringComparison.Ordinal)) && list.Items[0].SubItems[4].Text.EndsWith("(13 attempts)", StringComparison.Ordinal),
            "and the summary line and the Path cell");

        Select(okay);
        pumpUntil(() => !Rows().Contains("Piper saw"));
        check(!Rows().Contains("Piper saw") && !Rows().Contains("launcher.exe"), "selecting an ordinary session leaves none of the explanation behind");
        Select(failed);
        pumpUntil(() => summaries.Any(t => t.Text.Contains("FAILED")));
        check(summaries.Any(t => t.Text.Contains("FAILED - Could not connect")) && !Rows().Contains("Piper saw"), "an ordinary failure keeps the FAILED summary and no explanation");
        Select(overflow);
        pumpUntil(() => Rows().Contains("Piper saw"));
        check(Rows().Contains("Piper saw=") && !Rows().Contains("Host="), "the overflow row, which has no host, is explained too");
        Select(left);
        pumpUntil(() => Rows().Contains("never saw"));
        check(Rows().Contains("Host=early.example.test"), "and each TLS row is explained in turn");
        form.Close();
    }
}
