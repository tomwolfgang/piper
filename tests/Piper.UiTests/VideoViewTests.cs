using System.Text;
using Microsoft.Web.WebView2.Core;
using Piper.App.Controls;

// The video preview (WebView2 with a lock-down, the body served from memory with Range support). Checks that
// need the WebView2 Runtime are skipped without it.
internal static class VideoViewTests
{
    // A 16 x 16 VP8 WebM, solid red, one second (679 bytes, made with ffmpeg from a JPEG).
    internal static readonly byte[] Webm = Convert.FromBase64String(
        "GkXfo59ChoEBQveBAULygQRC84EIQoKEd2VibUKHgQJChYECGFOAZwEAAAAAAAJ3EU2bdLpNu4tTq4QVSalmU6yBoU27i1OrhBZUrmtTrIHWTbuMU6uEElTDZ1OsggEnTbuMU6uEHFO7a1OsggJh7AEAAAAAAABZAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAVSalmsCrXsYMPQkBNgIxMYXZmNjEuMS4xMDBXQYxMYXZmNjEuMS4xMDBEiYhAj0AAAAAAABZUrmvMrgEAAAAAAABD14EBc8WIwTOt6URZqtmcgQAitZyDdW5kiIEAhoVWX1ZQOIOBASPjg4QJ7yGq4JSwgRC6gRCagQJVsIhVt4ECVbiBAhJUw2f6c3OfY8CAZ8iZRaOHRU5DT0RFUkSHjExhdmY2MS4xLjEwMHNz1WPAi2PFiMEzrelEWarZZ8igRaOHRU5DT0RFUkSHk0xhdmM2MS4zLjEwMCBsaWJ2cHhnyKFFo4hEVVJBVElPTkSHkzAwOjAwOjAxLjAwMDAwMDAwMAAfQ7Z1QLXngQCjvYEAAICwAgCdASoQABAAAEcIhYWIhYSIAgICdaoD+AIM/SgA/vzelf+zu/Tu/Tu/wMf/yY7+THfyY7/5KACjlYEApwCxAQABEBAAGAAYWC/0AAgAAKOVgQFNALEBAAEQEAAYABhYL/QACAAAo5WBAfQAsQEAARAQABgAGFgv/QACAACjlYECmwCxAQABEBAAGAAYWC/0AAgAAKOVgQNBALEBAAEQEAAYABhYL/QACAAAHFO7a5G7j7OBALeK94EB8YIBpvCBAw==");

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
        using (var missing = new VideoView(_ => throw new WebView2RuntimeNotFoundException("simulated")))
            check(Await(missing.ShowAsync(Webm, "video/webm")) == VideoShowResult.RuntimeUnavailable && !missing.HoldsBody && !missing.Visible,
                "a missing runtime reports RuntimeUnavailable and holds nothing");
        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException)
        {
            Console.WriteLine("   (the WebView2 Runtime is not installed here; the browser-dependent video checks were skipped)");
            return;
        }

        Console.WriteLine("== a video is served from memory to a locked-down WebView2");
        using var form = new Form { Width = 500, Height = 400 };
        using var view = new VideoView();
        form.Controls.Add(view);
        form.Show();
        var served = new List<(string? Range, int Status)>();
        view.MediaServed += (range, status) => served.Add((range, status));

        check(Await(view.ShowAsync(Webm, "video/webm")) == VideoShowResult.Shown && view.HoldsBody, "the wrapper page loads (NavigationCompleted succeeded)");
        var core = view.View!.CoreWebView2;
        var pageUri = view.PageUri!;
        var mediaUri = view.MediaUri!;
        var page = pageUri.AbsoluteUri;
        var media = mediaUri.AbsoluteUri;
        check(pageUri.Segments[1].Length == 33 && mediaUri.Segments[1] == pageUri.Segments[1], "the page and the video share a random 128-bit token path on the virtual host");
        check(view.View.Source is { Scheme: "https" } source && source.Host == VideoView.Host && core.DocumentTitle == "Piper", $"the final URL is the wrapper on the virtual host, not file:// ({view.View.Source})");

        var s = core.Settings;
        check(!s.IsScriptEnabled && !s.IsWebMessageEnabled && !s.AreDevToolsEnabled && !s.IsStatusBarEnabled && !s.AreDefaultScriptDialogsEnabled
            && !s.AreDefaultContextMenusEnabled && !s.IsPasswordAutosaveEnabled && !s.IsGeneralAutofillEnabled && !s.AreHostObjectsAllowed
            && !s.AreBrowserAcceleratorKeysEnabled && !s.IsSwipeNavigationEnabled && !s.IsBuiltInErrorPageEnabled,
            "scripts, web messages, dev tools, status bar, dialogs, menus, autofill, host objects and accelerator keys are off");

        Console.WriteLine("== the engine's own media requests are answered, with ranges, and the video plays");
        string Eval(string script) => Await(core.ExecuteScriptAsync(script));
        // Script is on for these probes only; the page itself has none (its CSP forbids it).
        s.IsScriptEnabled = true;
        const string Probe = "(function(){var v=document.querySelector('video');return [v.readyState,v.videoWidth,v.videoHeight,Math.round(v.duration*10)/10,v.error?v.error.code:0].join('|')})()";
        var state = "";
        pumpUntil(() => !(state = Eval(Probe)).StartsWith("\"0|", StringComparison.Ordinal)); // metadata is in
        check(state.StartsWith("\"", StringComparison.Ordinal) && state.EndsWith("|16|16|1|0\"", StringComparison.Ordinal),
            $"the engine decoded the 16 x 16, one-second video served from memory (readyState|width|height|duration|error = {state})");
        Console.WriteLine($"   (requests the engine made: {string.Join(", ", served.Select(r => $"{r.Range ?? "no Range"} -> {r.Status}"))})");
        check(served.Count > 0 && served.All(r => r.Status is 200 or 206) && served.Any(r => r.Range is not null && r.Status == 206),
            "the engine's media requests were all answered, and a Range request got 206");

        Console.WriteLine("== a long video: seeking asks for a later range and gets 206");
        // A silent 24 MB WAV (an audio-only file plays in a video element) is too big to be fetched in one piece.
        var wav = Wav(seconds: 3000);
        check(Await(view.ShowAsync(wav, null)) == VideoShowResult.Shown, "a 24 MB body loads");
        pumpUntil(() => !(state = Eval(Probe)).StartsWith("\"0|", StringComparison.Ordinal));
        check(state.EndsWith("|0|0|3000|0\"", StringComparison.Ordinal) || state.EndsWith("|3000|0\"", StringComparison.Ordinal), $"the engine reads its length from the served bytes ({state})");
        served.Clear();
        Eval("(function(){var v=document.querySelector('video');v.currentTime=2000;return true})()");
        pumpUntil(() => served.Any(r => StartOf(r.Range) > 10_000_000));
        Console.WriteLine($"   (requests after the seek: {string.Join(", ", served.Select(r => $"{r.Range ?? "no Range"} -> {r.Status}"))})");
        check(served.Any(r => StartOf(r.Range) > 10_000_000 && r.Status == 206), "a seek to the middle asks for bytes from a later offset and is answered 206");
        pumpUntil(() => Eval("(function(){var v=document.querySelector('video');return !v.seeking && v.readyState>=2 && v.currentTime>=1999})()") == "true");
        check(Eval("(function(){var v=document.querySelector('video');return v.currentTime>=1999 && !v.error})()") == "true", "the seek completed, so the player was fed the later bytes");
        s.IsScriptEnabled = false;

        Console.WriteLine("== requests are answered by token, Referer and method; ranges become 206 and 416");
        check(Await(view.ShowAsync(Webm, "video/webm")) == VideoShowResult.Shown, "the small video is shown again");
        page = view.PageUri!.AbsoluteUri;
        media = view.MediaUri!.AbsoluteUri;
        bool Refused(string uri, string method = "GET", string? referer = null) => view.Serve(uri, method, referer, null) is null;
        check(view.Resolve(page, "GET", null) == VideoView.Resource.Page && view.Resolve(media, "GET", page) == VideoView.Resource.Media, "the page and, from the page, the video are served");
        check(new[] { null, "", media, "https://evil.example/", $"https://{VideoView.Host}/" }.All(r => Refused(media, referer: r)),
            "the video is refused with no Referer, an empty one, itself, another origin or the bare host");
        check(Refused(media.Replace(view.PageUri!.Segments[1], new string('0', 32) + "/"), referer: page) && Refused(media, "POST", page) && Refused(media, "HEAD", page)
            && Refused($"https://{VideoView.Host}/favicon.ico", referer: page) && Refused("https://localhost:1/other", referer: page), "a wrong token, a non-GET, another path and another host are refused");

        var whole = view.Serve(media, "GET", page, null)!.Value;
        check(whole.Status == 200 && whole.Body!.Length == Webm.Length && whole.Headers.Contains($"Content-Length: {Webm.Length}\r\n") && whole.Headers.Contains("Content-Type: video/webm\r\n")
            && whole.Headers.Contains("Accept-Ranges: bytes\r\n") && !whole.Headers.Contains("Content-Range"), "no Range: 200 with the whole body, its length and type, and Accept-Ranges");
        check(ReadAll(whole.Body!).SequenceEqual(Webm), "the whole body is the captured bytes");
        var pageAnswer = view.Serve(page, "GET", null, "bytes=0-5")!.Value;
        check(pageAnswer.Status == 200 && pageAnswer.Headers.Contains("Content-Security-Policy: default-src 'none'; media-src 'self'; style-src 'unsafe-inline'")
            && pageAnswer.Headers.Contains("X-Content-Type-Options: nosniff") && pageAnswer.Headers.Contains("Cache-Control: no-store"), "the page is 200 whatever its Range, with the CSP, nosniff and no-store");
        var pageHtml = Encoding.UTF8.GetString(ReadAll(pageAnswer.Body!));
        check(!pageHtml.Contains("<script", StringComparison.OrdinalIgnoreCase) && pageHtml.Contains("<video controls") && pageHtml.Contains("default-src 'none'; media-src 'self'"), "the page is one video element with a strict CSP and no script");

        var middle = view.Serve(media, "GET", page, "bytes=100-199")!.Value;
        check(middle.Status == 206 && middle.Body!.Length == 100 && middle.Headers.Contains($"Content-Range: bytes 100-199/{Webm.Length}\r\n") && middle.Headers.Contains("Content-Length: 100\r\n")
            && ReadAll(middle.Body).SequenceEqual(Webm[100..200]), "a middle range is 206 with Content-Range, Content-Length and exactly those bytes");
        var suffix = view.Serve(media, "GET", page, "bytes=-10")!.Value;
        check(suffix.Status == 206 && ReadAll(suffix.Body!).SequenceEqual(Webm[^10..]) && suffix.Headers.Contains($"Content-Range: bytes {Webm.Length - 10}-{Webm.Length - 1}/{Webm.Length}\r\n"), "a suffix range is the last bytes");
        var open = view.Serve(media, "GET", page, $"bytes={Webm.Length - 3}-")!.Value;
        check(open.Status == 206 && ReadAll(open.Body!).SequenceEqual(Webm[^3..]), "an open-ended range runs to the end");
        var beyond = view.Serve(media, "GET", page, $"bytes={Webm.Length}-")!.Value;
        check(beyond.Status == 416 && beyond.Body is null && beyond.Headers.Contains($"Content-Range: bytes */{Webm.Length}\r\n") && !beyond.Headers.Contains("Content-Type:"),
            "a start past the end is 416 with bytes */length and no body");
        check(new[] { "bytes=5-4", "bytes=junk", "bytes=0-99999999999999999999x" }.All(r => view.Serve(media, "GET", page, r)!.Value.Status == 416), "inverted and malformed ranges are 416");
        check(view.Serve(media, "GET", page, "bytes=0-99999999999999999999")!.Value is { Status: 206 } huge && huge.Body!.Length == Webm.Length, "a huge last byte is cut to the end");
        check(view.Serve(media, "GET", page, "bytes=0-9,20-29")!.Value is { Status: 200 } several && several.Body!.Length == Webm.Length, "several ranges get the whole body");

        // The slice is a view of the held array, not a copy of it: serving a big body many times allocates next to nothing.
        check(Await(view.ShowAsync(wav, null)) == VideoShowResult.Shown, "the 24 MB body is shown for the allocation check");
        var bigPage = view.PageUri!.AbsoluteUri;
        var bigMedia = view.MediaUri!.AbsoluteUri;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            _ = view.Serve(bigMedia, "GET", bigPage, null);
            _ = view.Serve(bigMedia, "GET", bigPage, $"bytes={i * 100000}-");
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(allocated < 4 << 20, $"400 answers over a 24 MB body allocate {allocated:N0} bytes, not a copy of it per request");

        // The cap: the oldest stream is ended, so a request loop cannot pile them up.
        var first = view.Serve(bigMedia, "GET", bigPage, "bytes=0-9")!.Value.Body!;
        for (var i = 0; i < 100; i++) _ = view.Serve(bigMedia, "GET", bigPage, "bytes=0-9");
        check(view.OpenStreams <= 16 && !first.CanRead, $"at most 16 streams stay open and the oldest is ended ({view.OpenStreams} open)");

        Console.WriteLine("== navigation, windows and downloads stay blocked");
        foreach (var url in new[] { "https://localhost:1/other", "http://localhost:1/other", "file:///C:/Windows/win.ini" })
        {
            var before = view.Blocked;
            core.Navigate(url);
            pumpUntil(() => view.Blocked > before);
            check(view.Blocked > before && view.View.Source.Host == VideoView.Host, $"navigation to {url} is cancelled and the wrapper stays");
        }

        s.IsScriptEnabled = true;
        Probe2("window.open('https://localhost:1/') === null", "a new window request is handled (window.open returns null)");
        Probe2("(function(){var a=document.createElement('a');a.href=document.querySelector('video').src;a.download='x.mp4';document.body.appendChild(a);a.click();return true})()", "a download is cancelled");
        s.IsScriptEnabled = false;

        void Probe2(string script, string name)
        {
            var before = view.Blocked;
            var result = Eval(script);
            pumpUntil(() => view.Blocked > before);
            check(result is "true" && view.Blocked > before, name);
        }

        form.Controls.Remove(view);
        view.Dispose();
        check(!view.HoldsBody && view.PageUri is null && view.OpenStreams == 0, "disposing the view drops the body");

        AssertNothingWritten(check, tempBefore, "showing, seeking and disposing a video");
    }

    // The files under %TEMP%\Piper right now (older builds may have left some there).
    internal static Dictionary<string, (long Length, DateTime Written)> TempSnapshot() =>
        !Directory.Exists(TempRoot) ? [] : Directory.EnumerateFiles(TempRoot, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (new FileInfo(path).Length, File.GetLastWriteTimeUtc(path)));

    internal static void AssertNothingWritten(Action<bool, string> check, Dictionary<string, (long Length, DateTime Written)> before, string what)
    {
        var changed = TempSnapshot().Where(file => !before.TryGetValue(file.Key, out var old) || old != file.Value).Select(file => file.Key).ToArray();
        check(!changed.Any(path => path.StartsWith(Path.Combine(TempRoot, "video-inspector"), StringComparison.OrdinalIgnoreCase)),
            $"{what} creates or changes nothing under %TEMP%\\Piper\\video-inspector");
        check(!changed.Any(path => new FileInfo(path).Length < 64 << 20 && File.ReadAllBytes(path).AsSpan().IndexOf(Webm) >= 0),
            $"{what} leaves no file under %TEMP%\\Piper that contains the body");
    }

    private static string TempRoot => Path.Combine(Path.GetTempPath(), "Piper");

    private static byte[] ReadAll(MemoryStream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    // The first byte a Range header asks for, or -1.
    private static long StartOf(string? range) =>
        range is not null && range.StartsWith("bytes=", StringComparison.Ordinal) && long.TryParse(range[6..].Split('-')[0], out var start) ? start : -1;

    // Unsigned 8-bit mono PCM at 8 kHz, silent: one byte per sample.
    private static byte[] Wav(int seconds)
    {
        const int rate = 8000;
        var data = rate * seconds;
        var wav = new byte[44 + data];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + data).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8);
        BitConverter.GetBytes(16).CopyTo(wav, 16);
        BitConverter.GetBytes((short)1).CopyTo(wav, 20); // PCM
        BitConverter.GetBytes((short)1).CopyTo(wav, 22); // mono
        BitConverter.GetBytes(rate).CopyTo(wav, 24);
        BitConverter.GetBytes(rate).CopyTo(wav, 28); // bytes per second
        BitConverter.GetBytes((short)1).CopyTo(wav, 32); // block align
        BitConverter.GetBytes((short)8).CopyTo(wav, 34); // bits per sample
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(data).CopyTo(wav, 40);
        Array.Fill(wav, (byte)0x80, 44, data);
        return wav;
    }
}
