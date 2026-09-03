using Piper.Core.Http;
using Piper.Core.Sessions;

// Coverage for the drop-routing table behind MainForm.EnableDrop. DragDropRouting is the pure,
// UI-free extraction of that decision, specifically so it can be exercised without a WinForms
// message loop. The shadowing bug it was written for -- a file-only handler on every descendant
// swallowing dragged sessions -- lives in WinForms' own drop-target resolution and is verified
// by hand instead; see the pull request.
internal static class DragDropRoutingTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("DragDropRouting routes a drop by payload and zone", () =>
        {
            var session = SessionWithRequest();

            runner.AreEqual(DragDropAction.LoadIntoComposer,
                DragDropRouting.Resolve(0, session, SessionDropZone.Composer),
                "a captured session dropped on the Composer loads into it");

            runner.AreEqual(DragDropAction.AddAutoResponderRule,
                DragDropRouting.Resolve(0, session, SessionDropZone.AutoResponder),
                "the same session dropped on AutoResponder becomes a rule");

            // Not None: the caller must leave the drag effect alone so an inner control that
            // already claimed the payload keeps its decision, which is what the inspector's
            // media drop relies on.
            runner.AreEqual(DragDropAction.Ignore,
                DragDropRouting.Resolve(0, session, SessionDropZone.None),
                "a session dropped outside either tab is left to whoever else claimed it");

            runner.AreEqual(DragDropAction.ImportSazFiles,
                DragDropRouting.Resolve(2, session, SessionDropZone.Composer),
                "SAZ files win over a session payload, even over the Composer");

            runner.AreEqual(DragDropAction.ImportSazFiles,
                DragDropRouting.Resolve(1, null, SessionDropZone.None),
                "a SAZ file is accepted anywhere in the window");

            runner.AreEqual(DragDropAction.Ignore,
                DragDropRouting.Resolve(0, new Session(), SessionDropZone.Composer),
                "a session with no captured request has nothing to load");

            runner.AreEqual(DragDropAction.Ignore,
                DragDropRouting.Resolve(0, null, SessionDropZone.Composer),
                "an empty payload is ignored");

            return Task.CompletedTask;
        });
    }

    private static Session SessionWithRequest() => new()
    {
        Request = new HttpRequestData
        {
            Method = "GET",
            Url = new Uri("https://example.test/api/items"),
        },
    };
}
