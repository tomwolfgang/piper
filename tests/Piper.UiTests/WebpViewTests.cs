using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;
using Piper.App.Controls;

// The WebP preview (WebView2 with a lock-down). Checks that need the WebView2 Runtime are skipped without it.
internal static class WebpViewTests
{
    // A 16 x 8 lossless WebP, solid red, no profile.
    private static readonly byte[] Webp = Convert.FromBase64String("UklGRiAAAABXRUJQVlA4TBQAAAAvD8ABAAcQ/Y/+BwCC8D9uIaL/qQ==");

    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        T Await<T>(Task<T> task)
        {
            // The first WebView2 start in a process can take well over the pump's 10 s on a cold CI runner.
            for (var attempt = 0; attempt < 6 && !task.IsCompleted; attempt++) pumpUntil(() => task.IsCompleted);
            return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException("the WebView2 call did not finish");
        }

        var tempBefore = TempSnapshot();
        Console.WriteLine("== a missing or failing WebView2 Runtime is reported, not thrown");
        using (var missing = new WebpView(_ => throw new WebView2RuntimeNotFoundException("simulated")))
            check(Await(missing.ShowAsync(Webp)) == WebpShowResult.RuntimeUnavailable && !missing.HoldsBody && !missing.Visible,
                "a missing runtime reports RuntimeUnavailable and holds nothing");
        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException)
        {
            Console.WriteLine("   (the WebView2 Runtime is not installed here; the browser-dependent WebP checks were skipped)");
            return;
        }

        Console.WriteLine("== a WebP is served from memory to a locked-down WebView2");
        using var form = new Form { Width = 500, Height = 400 };
        using var view = new WebpView();
        form.Controls.Add(view);
        form.Show();

        check(Await(view.ShowAsync(Webp)) == WebpShowResult.Shown && view.HoldsBody, "the wrapper page loads (NavigationCompleted succeeded)");
        var core = view.View!.CoreWebView2;
        var pageUri = view.PageUri!;
        var imageUri = view.ImageUri!;
        check(pageUri.Segments[1].Length == 33 && imageUri.Segments[1] == pageUri.Segments[1], "the page and the image share a random 128-bit token path on the virtual host");
        check(view.View.Source is { Scheme: "https" } source && source.Host == WebpView.Host && core.DocumentTitle == "Piper", $"the final URL is the wrapper on the virtual host, not file:// ({view.View.Source})");

        Console.WriteLine("== only the wrapper page may read the image");
        var image = imageUri.AbsoluteUri;
        var pageText = pageUri.AbsoluteUri;
        bool Refused(string uri, string method = "GET", string? referer = null) => view.Resolve(uri, method, referer) == WebpView.Resource.Blocked;
        check(view.Resolve(pageText, "GET", null) == WebpView.Resource.Page && view.Resolve(image, "GET", pageText) == WebpView.Resource.Image, "the page and, from the page, the image are served");
        check(new[] { null, "", image, "https://evil.example/", $"https://{WebpView.Host}/" }.All(r => Refused(image, referer: r)),
            "the image is refused with no Referer, an empty one, itself, another origin or the bare host");
        check(Refused(image.Replace(pageUri.Segments[1], new string('0', 32) + "/"), referer: pageText) && Refused(image, "POST", pageText) && Refused($"https://{WebpView.Host}/favicon.ico", referer: pageText)
            && Refused("https://localhost:1/other", referer: pageText), "a wrong token, a non-GET, another path and another host are refused");

        var s = core.Settings;
        check(!s.IsScriptEnabled && !s.IsWebMessageEnabled && !s.AreDevToolsEnabled && !s.IsStatusBarEnabled && !s.AreDefaultScriptDialogsEnabled
            && !s.AreDefaultContextMenusEnabled && !s.IsPasswordAutosaveEnabled && !s.IsGeneralAutofillEnabled && !s.AreHostObjectsAllowed
            && !s.AreBrowserAcceleratorKeysEnabled && !s.IsSwipeNavigationEnabled && !s.IsBuiltInErrorPageEnabled,
            "scripts, web messages, dev tools, status bar, dialogs, menus, autofill, host objects and accelerator keys are off");

        // CapturePreview needs no script: a red pixel proves the WebP was decoded and drawn.
        using var shot = new MemoryStream();
        var ticks = 0;
        pumpUntil(() => ++ticks > 30); // let the first paint settle
        var capture = core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, shot);
        pumpUntil(() => capture.IsCompleted);
        shot.Position = 0;
        using var bitmap = new Bitmap(shot);
        check(Enumerable.Range(0, bitmap.Width * bitmap.Height).Any(i => bitmap.GetPixel(i % bitmap.Width, i / bitmap.Width) is { R: > 200, G: < 60, B: < 60 }),
            "the WebP is drawn (a red pixel is in the captured view)");

        foreach (var url in new[] { "https://localhost:1/other", "http://localhost:1/other", "file:///C:/Windows/win.ini" })
        {
            var before = view.Blocked;
            core.Navigate(url);
            pumpUntil(() => view.Blocked > before);
            check(view.Blocked > before && view.View.Source.Host == WebpView.Host, $"navigation to {url} is cancelled and the wrapper stays");
        }

        // The other handlers need script to be provoked, so script is on for these probes only.
        s.IsScriptEnabled = true;
        Probe("window.open('https://localhost:1/') === null", "a new window request is handled (window.open returns null)");
        Probe("(function(){var a=document.createElement('a');a.href=document.images[0].src;a.download='x.webp';document.body.appendChild(a);a.click();return true})()", "a download is cancelled");
        // Which permission prompts the engine raises depends on the machine (a runner without audio hardware raises none for the microphone), so any one will do.
        var permissionProbes = new[] { "Notification.requestPermission()", "navigator.mediaDevices.getUserMedia({audio:true}).catch(function(){})", "navigator.geolocation.getCurrentPosition(function(){},function(){})" };
        var denied = false;
        foreach (var request in permissionProbes.TakeWhile(_ => !denied))
        {
            var before = view.Blocked;
            Await(core.ExecuteScriptAsync("(function(){" + request + ";return true})()"));
            var waited = Environment.TickCount64 + 3000;
            pumpUntil(() => view.Blocked > before || Environment.TickCount64 > waited);
            denied = view.Blocked > before;
        }

        if (denied) check(true, "a permission request is denied (the engine raised it and the handler refused)");
        else Console.WriteLine("   (this machine raised no permission prompt for any probe; the permission handler was not exercised)");
        check(Await(core.ExecuteScriptAsync("document.querySelector('meta[http-equiv=\"Content-Security-Policy\"]').content + '|' + document.scripts.length")) is { } csp
            && csp.Contains("default-src 'none'; img-src 'self'") && csp.EndsWith("|0\""), "the wrapper has a strict CSP and no script");

        // The same URL, asked for by anything but the wrapper, gets no bytes: a page with no Referer here.
        core.Navigate("about:blank");
        pumpUntil(() => view.View.Source?.AbsoluteUri == "about:blank");
        var blockedBefore = view.Blocked;
        Await(core.ExecuteScriptAsync($"(function(){{window.__i=new Image();window.__i.src='{image}';return true}})()"));
        var waitedImage = Environment.TickCount64 + 5000;
        pumpUntil(() => view.Blocked > blockedBefore || Environment.TickCount64 > waitedImage);
        var settle = 0;
        pumpUntil(() => ++settle > 30); // let the failed load finish
        check(view.Blocked > blockedBefore && Await(core.ExecuteScriptAsync("window.__i.naturalWidth")) == "0",
            "the image URL requested from another document is refused and decodes nothing");
        s.IsScriptEnabled = false;

        void Probe(string script, string name)
        {
            var before = view.Blocked;
            var result = Await(core.ExecuteScriptAsync(script));
            pumpUntil(() => view.Blocked > before);
            check(result is "true" && view.Blocked > before, name);
        }

        Console.WriteLine("== the body is dropped on clear, supersession and dispose, and a second WebP replaces the first");
        WebpView.LoadTimeout = TimeSpan.FromMilliseconds(1);
        var failed = Await(view.ShowAsync(Webp));
        WebpView.LoadTimeout = TimeSpan.FromSeconds(15);
        check(failed == WebpShowResult.Failed && !view.Visible, "a page that does not load in time is Failed and leaves the view hidden");
        // Fast selection: the second show takes over while the first is still starting.
        var older = view.ShowAsync(Webp);
        var newer = view.ShowAsync(Webp);
        check(Await(older) == WebpShowResult.Superseded && Await(newer) == WebpShowResult.Shown && view.HoldsBody && view.ImageUri != imageUri,
            "overlapping shows: the older is Superseded, and the newer is Shown under its own token");
        var shownPage = view.PageUri!.AbsoluteUri;
        var shownImage = view.ImageUri!.AbsoluteUri;
        view.Clear();
        check(!view.HoldsBody && view.PageUri is null && view.ImageUri is null && !view.Visible, "Clear drops the body and the addresses and hides the view");
        check(view.Resolve(shownImage, "GET", shownPage) == WebpView.Resource.Blocked && view.Resolve(shownPage, "GET", null) == WebpView.Resource.Blocked,
            "after Clear neither the old page nor the old image is served");
        pumpUntil(() => view.View.Source?.AbsoluteUri == "about:blank");
        check(view.View.Source?.AbsoluteUri == "about:blank" && core.DocumentTitle != "Piper", "Clear blanks the page, so the image is not left in the view");
        var blockedClear = view.Blocked;
        core.Navigate(shownPage);
        pumpUntil(() => view.Blocked > blockedClear);
        check(view.Blocked > blockedClear && view.View.Source?.AbsoluteUri == "about:blank", "loading the old page again after Clear is cancelled");

        check(Await(view.ShowAsync(Webp)) == WebpShowResult.Shown && view.ImageUri!.AbsoluteUri != shownImage && Refused(shownImage, referer: shownPage), "a later WebP gets a new token and the old one stays dead");
        var previous = (view.ImageUri!.AbsoluteUri, view.PageUri!.AbsoluteUri);
        check(Await(view.ShowAsync(Webp)) == WebpShowResult.Shown && Refused(previous.Item1, referer: previous.Item2), "showing another WebP kills the previous token");

        // A body the view must not keep alive once cleared (the view serves it by reference).
        [MethodImpl(MethodImplOptions.NoInlining)]
        WeakReference ShowUnique()
        {
            var body = (byte[])Webp.Clone();
            check(Await(view.ShowAsync(body)) == WebpShowResult.Shown, "a distinct body is shown");
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
        using var flaky = new WebpView(v => ++starts == 1 ? v.EnsureCoreWebView2Async() : throw new WebView2RuntimeNotFoundException("simulated"));
        flakyForm.Controls.Add(flaky);
        flakyForm.Show();
        check(Await(flaky.ShowAsync(Webp)) == WebpShowResult.Shown && flaky.Visible, "the first show works");
        check(Await(flaky.ShowAsync(Webp)) == WebpShowResult.RuntimeUnavailable && !flaky.Visible && !flaky.HoldsBody, "a failing second show leaves the view hidden and empty");

        form.Controls.Remove(view);
        view.Dispose();
        check(!view.HoldsBody && view.PageUri is null, "disposing the view drops the body");

        AssertNothingWritten(check, tempBefore, "showing, clearing, superseding and disposing a WebP");
    }

    // The files under %TEMP%\Piper right now (the video preview and older builds may have left some there).
    private static Dictionary<string, (long Length, DateTime Written)> TempSnapshot() =>
        !Directory.Exists(TempRoot) ? [] : Directory.EnumerateFiles(TempRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));

    private static void AssertNothingWritten(Action<bool, string> check, Dictionary<string, (long Length, DateTime Written)> before, string what)
    {
        var changed = TempSnapshot().Where(file => !before.TryGetValue(file.Key, out var old) || old != file.Value).Select(file => file.Key).ToArray();
        check(!changed.Any(path => path.StartsWith(Path.Combine(TempRoot, "image-inspector"), StringComparison.OrdinalIgnoreCase)),
            $"{what} creates or changes nothing under %TEMP%\\Piper\\image-inspector");
        check(!changed.Any(path => new FileInfo(path).Length < 64 << 20 && File.ReadAllBytes(path).AsSpan().IndexOf(Webp) >= 0),
            $"{what} leaves no file under %TEMP%\\Piper that contains the body");
    }

    private static string TempRoot => Path.Combine(Path.GetTempPath(), "Piper");
}
