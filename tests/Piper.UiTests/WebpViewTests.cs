using System.Reflection;
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
            pumpUntil(() => task.IsCompleted);
            return task.IsCompleted ? task.GetAwaiter().GetResult() : throw new TimeoutException("the WebView2 call did not finish");
        }

        Console.WriteLine("== a missing or failing WebView2 Runtime is reported, not thrown");
        using (var missing = new WebpView(_ => throw new WebView2RuntimeNotFoundException("simulated")))
            check(Await(missing.ShowAsync(Webp)) == WebpShowResult.RuntimeUnavailable && missing.Folder is null && !missing.Visible,
                "a missing runtime reports RuntimeUnavailable and writes nothing");
        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException)
        {
            Console.WriteLine("   (the WebView2 Runtime is not installed here; the browser-dependent WebP checks were skipped)");
            return;
        }

        Console.WriteLine("== a WebP is served from a private temp folder to a locked-down WebView2");
        using var form = new Form { Width = 500, Height = 400 };
        using var view = new WebpView();
        form.Controls.Add(view);
        form.Show();

        check(Await(view.ShowAsync(Webp)) == WebpShowResult.Shown, "the wrapper page loads (NavigationCompleted succeeded)");
        var core = view.View!.CoreWebView2;
        var folder = view.Folder!;
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).ToArray();
        check(folder.StartsWith(Path.Combine(Path.GetTempPath(), "Piper", "image-inspector")) && files.Length == 2
            && files.Count(f => f!.EndsWith(".webp") && f.Length == 32 + 5) == 1 && files.Count(f => f!.EndsWith(".html")) == 1,
            "the body and the wrapper are two random-named files in a private temp subfolder");
        var page = File.ReadAllText(Directory.GetFiles(folder, "*.html")[0]);
        check(page.Contains("default-src 'none'") && page.Contains("img-src 'self'") && !page.Contains("<script"), "the wrapper has a strict CSP and no script");
        check(view.View.Source is { Scheme: "https" } source && source.Host == WebpView.Host && core.DocumentTitle == "Piper",
            $"the final URL is the wrapper on the virtual host, not file:// ({view.View.Source})");

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
        s.IsScriptEnabled = false;

        void Probe(string script, string name)
        {
            var before = view.Blocked;
            var result = Await(core.ExecuteScriptAsync(script));
            pumpUntil(() => view.Blocked > before);
            check(result is "true" && view.Blocked > before, name);
        }

        Console.WriteLine("== the temp folder goes on clear and dispose, and a second WebP replaces the first");
        WebpView.LoadTimeout = TimeSpan.FromMilliseconds(1);
        var failed = Await(view.ShowAsync(Webp));
        WebpView.LoadTimeout = TimeSpan.FromSeconds(15);
        check(failed == WebpShowResult.Failed && !view.Visible, "a page that does not load in time is Failed and leaves the view hidden");
        view.Clear();
        check(!Directory.Exists(folder) && view.Folder is null && !view.Visible, "Clear deletes the folder and hides the view");
        check(Await(view.ShowAsync(Webp)) == WebpShowResult.Shown && view.Folder != folder && Directory.Exists(view.Folder!), "a later WebP uses a new folder");
        var last = view.Folder!;
        check(Await(view.ShowAsync(Webp)) == WebpShowResult.Shown && !Directory.Exists(last), "showing another WebP deletes the previous folder");
        last = view.Folder!;
        form.Controls.Remove(view);
        view.Dispose();
        check(!Directory.Exists(last), "disposing the view deletes the folder");
    }
}
