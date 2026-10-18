using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using Piper.App.Controls;

// When VideoView lets go of the body: on clear, on a newer show, on a failed show and on dispose. Needs the
// WebView2 Runtime and is skipped without it.
internal static class VideoViewLifecycleTests
{
    private static readonly byte[] Webm = VideoViewTests.Webm;

    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException)
        {
            Console.WriteLine("   (the WebView2 Runtime is not installed here; the video lifecycle checks were skipped)");
            return;
        }

        T Await<T>(Task<T> task)
        {
            for (var attempt = 0; attempt < 6 && !task.IsCompleted; attempt++) pumpUntil(() => task.IsCompleted);
            return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException("the WebView2 call did not finish");
        }

        Console.WriteLine("== the body is dropped on clear and supersession, and a second video replaces the first");
        using var form = new Form { Width = 500, Height = 400 };
        using var view = new VideoView();
        form.Controls.Add(view);
        form.Show();
        bool Refused(string uri, string? referer = null) => view.Serve(uri, "GET", referer, null) is null;

        check(Await(view.ShowAsync(Webm, null)) == VideoShowResult.Shown, "a video is shown");
        var core = view.View!.CoreWebView2;
        var firstMedia = view.MediaUri;
        VideoView.LoadTimeout = TimeSpan.FromMilliseconds(1);
        var failed = Await(view.ShowAsync(Webm, null));
        VideoView.LoadTimeout = TimeSpan.FromSeconds(15);
        check(failed == VideoShowResult.Failed && !view.Visible && !view.HoldsBody && view.PageUri is null, "a page that does not load in time is Failed, hides the view and drops the body");
        var older = view.ShowAsync(Webm, null);
        var newer = view.ShowAsync(Webm, null);
        check(Await(older) == VideoShowResult.Superseded && Await(newer) == VideoShowResult.Shown && view.HoldsBody && view.MediaUri != firstMedia,
            "overlapping shows: the older is Superseded, and the newer is Shown under its own token");
        var shownPage = view.PageUri!.AbsoluteUri;
        var shownMedia = view.MediaUri!.AbsoluteUri;
        var live = view.Serve(shownMedia, "GET", shownPage, "bytes=0-9")!.Value.Body!;
        view.Clear();
        check(!view.HoldsBody && view.PageUri is null && view.MediaUri is null && !view.Visible && view.OpenStreams == 0 && !live.CanRead,
            "Clear drops the body and the addresses, ends the open streams and hides the view");
        check(Refused(shownMedia, shownPage) && Refused(shownPage), "after Clear neither the old page nor the old video is served");
        pumpUntil(() => view.View.Source?.AbsoluteUri == "about:blank");
        check(view.View.Source?.AbsoluteUri == "about:blank" && core.DocumentTitle != "Piper", "Clear blanks the page, so the video is not left in the view");
        var blockedClear = view.Blocked;
        core.Navigate(shownPage);
        pumpUntil(() => view.Blocked > blockedClear);
        check(view.Blocked > blockedClear && view.View.Source?.AbsoluteUri == "about:blank", "loading the old page again after Clear is cancelled");

        check(Await(view.ShowAsync(Webm, null)) == VideoShowResult.Shown && view.MediaUri!.AbsoluteUri != shownMedia && Refused(shownMedia, shownPage), "a later video gets a new token and the old one stays dead");
        var previous = (view.MediaUri!.AbsoluteUri, view.PageUri!.AbsoluteUri);
        check(Await(view.ShowAsync(Webm, null)) == VideoShowResult.Shown && Refused(previous.Item1, previous.Item2), "showing another video kills the previous token");

        // A body the view must not keep alive once cleared (the view serves it by reference).
        [MethodImpl(MethodImplOptions.NoInlining)]
        WeakReference ShowUnique()
        {
            var body = (byte[])Webm.Clone();
            check(Await(view.ShowAsync(body, null)) == VideoShowResult.Shown, "a distinct body is shown");
            return new WeakReference(body);
        }

        var weak = ShowUnique();
        view.Clear();
        for (var round = 0; round < 5 && weak.IsAlive; round++)
        {
            var spin = 0;
            pumpUntil(() => ++spin > 10);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        check(!weak.IsAlive, "after Clear nothing in the view keeps the body array alive");

        Console.WriteLine("== a failed show after a successful one hides the view");
        var starts = 0;
        using var flakyForm = new Form { Width = 500, Height = 400 };
        using var flaky = new VideoView(v => ++starts == 1 ? v.EnsureCoreWebView2Async() : throw new WebView2RuntimeNotFoundException("simulated"));
        flakyForm.Controls.Add(flaky);
        flakyForm.Show();
        check(Await(flaky.ShowAsync(Webm, null)) == VideoShowResult.Shown && flaky.Visible, "the first show works");
        check(Await(flaky.ShowAsync(Webm, null)) == VideoShowResult.RuntimeUnavailable && !flaky.Visible && !flaky.HoldsBody, "a failing second show leaves the view hidden and empty");
    }
}
