using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Piper.App.Theme;

namespace Piper.App.Controls;

internal enum VideoShowResult
{
    Shown,

    /// <summary>The WebView2 Runtime is missing or did not start.</summary>
    RuntimeUnavailable,

    Failed,

    /// <summary>Another message was selected, or the view cleared, while loading.</summary>
    Superseded,
}

/// <summary>
/// View-only video preview in WebView2. The body is hostile and parsed by a browser engine's media stack, so it
/// stays in memory, never on disk: a trusted wrapper page (one video element, no script) and the bytes are
/// answered from WebResourceRequested on a virtual https host under a random per-show token, the bytes only to a
/// GET from that page (Referer checked), with Range support (206) so the player can seek, no-store, never
/// file://. Scripts, web messages, dev tools, menus, dialogs, autofill, downloads, new windows and permissions are
/// off or denied; any other navigation or request is cancelled. The body is dropped on the next selection, on
/// clear and on dispose. See WebpView, whose lock-down this repeats.
/// </summary>
internal sealed class VideoView : Panel
{
    internal const string Host = "piper-video.example";
    private const int MaxStreams = 16; // a seek is one request; the oldest stream is ended past this, which bounds a request loop
    private const string Csp = "default-src 'none'; media-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";
    private const string Common = "X-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\n";
    internal static TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(15); // settable for tests

    private readonly Func<WebView2, Task> _ensureCore;
    private readonly List<MemoryStream> _streams = [];
    private WebView2? _view;
    private bool _hardened;
    private byte[]? _video;
    private string _mime = "application/octet-stream";
    private Uri? _pageUri;
    private Uri? _mediaUri;
    private ulong _navigationId;
    private TaskCompletionSource<bool>? _loaded;
    private int _generation;

    /// <param name="ensureCore">Starts the control's WebView2. A test seam; the default is the app-wide environment.</param>
    public VideoView(Func<WebView2, Task>? ensureCore = null)
    {
        _ensureCore = ensureCore ?? (view => view.EnsureCoreWebView2Async());
        Dock = DockStyle.Fill;
        Visible = false;
    }

    internal enum Resource { Blocked, Page, Media }

    /// <summary>What a request is answered with; <see cref="Body"/> is null for an empty answer.</summary>
    internal readonly record struct Served(int Status, string Reason, string Headers, MemoryStream? Body);

    /// <summary>Navigations, windows, downloads, permissions and requests refused so far.</summary>
    internal int Blocked { get; private set; }

    /// <summary>Test seam: told the Range header (or null) and the status of every media request answered.</summary>
    internal event Action<string?, int>? MediaServed;

    internal WebView2? View => _view;

    internal Uri? PageUri => _pageUri;

    internal Uri? MediaUri => _mediaUri;

    internal bool HoldsBody => _video is not null;

    internal int OpenStreams => _streams.Count;

    internal static bool IsStartFailure(Exception exception) =>
        exception is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or IOException or UnauthorizedAccessException;

    public async Task<VideoShowResult> ShowAsync(byte[] video, string? contentType)
    {
        var generation = ++_generation;
        var started = false;
        Release();
        try
        {
            var view = EnsureControl();
            await _ensureCore(view);
            if (generation != _generation || IsDisposed || view.CoreWebView2 is not { } core) return VideoShowResult.Superseded;

            if (!_hardened)
            {
                Harden(core);
                _hardened = true;
            }

            started = true;

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); // a new URL per show, so a stale page cannot read this video
            _video = video;
            _mime = VideoRange.MimeFor(contentType, video);
            _pageUri = new Uri($"https://{Host}/{token}/page.html");
            _mediaUri = new Uri($"https://{Host}/{token}/media");
            var loaded = _loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.Navigate(_pageUri.AbsoluteUri);
            var finished = await Task.WhenAny(loaded.Task, Task.Delay(LoadTimeout));
            if (generation != _generation || IsDisposed) return VideoShowResult.Superseded;

            var ok = finished == loaded.Task && loaded.Task.Result;
            Visible = ok;
            if (!ok) Release(); // a page that failed or timed out must not keep the body answerable
            return ok ? VideoShowResult.Shown : VideoShowResult.Failed;
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            // A newer show owns the page now.
            if (generation != _generation || IsDisposed) return VideoShowResult.Superseded;

            Release();
            Visible = false;
            return started ? VideoShowResult.Failed : VideoShowResult.RuntimeUnavailable; // after start, it was the page
        }
    }

    /// <summary>Hides the view, blanks the page and drops the body.</summary>
    public void Clear()
    {
        _generation++;
        Release();
        Visible = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _generation++;
            Release();
        }

        base.Dispose(disposing);
    }

    private WebView2 EnsureControl()
    {
        if (_view is not null) return _view;
        _view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Palette.Surface };
        Controls.Add(_view);
        return _view;
    }

    private void Release()
    {
        _pageUri = _mediaUri = null;
        _navigationId = 0; // a late completion of the previous page must not finish the next show
        _video = null;
        _loaded?.TrySetResult(false);
        foreach (var stream in _streams) stream.Dispose();
        _streams.Clear();
        if (_view?.CoreWebView2 is { } core) core.Navigate("about:blank");
    }

    private static string Page() =>
        "<!doctype html><html><head><meta charset=\"utf-8\">"
        + $"<meta http-equiv=\"Content-Security-Policy\" content=\"{Csp}\">"
        + "<title>Piper</title><style>html,body{margin:0;height:100%;background:transparent}video{display:block;width:100%;height:100%;object-fit:contain}</style></head>"
        + "<body><video controls preload=\"metadata\" controlslist=\"nodownload noremoteplayback\" disablepictureinpicture disableremoteplayback src=\"media\"></video></body></html>";

    private void Harden(CoreWebView2 core)
    {
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsBuiltInErrorPageEnabled = false;

        core.NavigationStarting += (_, e) =>
        {
            if (IsAllowedNavigation(e.Uri))
            {
                if (_pageUri is { } page && e.Uri == page.AbsoluteUri) _navigationId = e.NavigationId;
                return;
            }

            e.Cancel = true;
            Blocked++;
        };
        core.FrameNavigationStarting += (_, e) => { e.Cancel = true; Blocked++; };
        core.NavigationCompleted += (_, e) =>
        {
            if (e.NavigationId == _navigationId) _loaded?.TrySetResult(e.IsSuccess);
        };
        core.NewWindowRequested += (_, e) => { e.Handled = true; Blocked++; };
        core.DownloadStarting += (_, e) => { e.Cancel = true; e.Handled = true; Blocked++; };
        core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.SavesInProfile = false; e.Handled = true; Blocked++; };
        // Every request, on any host, is answered here, so none reaches the network or the HTTP cache.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (e.Request.Uri == "about:blank") return; // the blank page Clear loads; it has no network
            // Refuse first, so nothing below can throw its way to the network.
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
            var headers = e.Request.Headers; // GetHeader throws for a header that is absent
            var served = Serve(e.Request.Uri, e.Request.Method, headers.Contains("Referer") ? headers.GetHeader("Referer") : null,
                headers.Contains("Range") ? headers.GetHeader("Range") : null);
            if (served is { } answer)
                e.Response = core.Environment.CreateWebResourceResponse(answer.Body, answer.Status, answer.Reason, answer.Headers);
            else
                Blocked++;
        };
    }

    /// <summary>Which of this show's two resources a request is for; the video only for a request from its own page.</summary>
    internal Resource Resolve(string uri, string method, string? referer)
    {
        if (method != "GET") return Resource.Blocked;
        if (_pageUri is { } page && uri == page.AbsoluteUri) return Resource.Page;
        return _mediaUri is { } media && uri == media.AbsoluteUri && _pageUri is { } owner && referer == owner.AbsoluteUri
            ? Resource.Media
            : Resource.Blocked;
    }

    /// <summary>The answer to a request, or null when it is refused. The video is read in place: a slice is a view of the array, never a copy.</summary>
    internal Served? Serve(string uri, string method, string? referer, string? range)
    {
        switch (Resolve(uri, method, referer))
        {
            case Resource.Page:
                return new Served(200, "OK", $"Content-Type: text/html; charset=utf-8\r\n{Common}Content-Security-Policy: {Csp}\r\nReferrer-Policy: same-origin\r\n",
                    Keep(new MemoryStream(Encoding.UTF8.GetBytes(Page()), writable: false)));
            case Resource.Media when _video is { } video:
                var answer = VideoRange.Resolve(range, video.Length);
                MediaServed?.Invoke(range, answer.Status);
                if (answer.Status == 416)
                    return new Served(416, "Range Not Satisfiable", $"{Common}Accept-Ranges: bytes\r\nContent-Range: {answer.ContentRange}\r\n", null);

                var body = Keep(new MemoryStream(video, checked((int)answer.Start), checked((int)answer.Length), writable: false));
                var common = $"Content-Type: {_mime}\r\n{Common}Accept-Ranges: bytes\r\nContent-Length: {answer.Length}\r\n";
                return answer.Status == 206
                    ? new Served(206, "Partial Content", $"{common}Content-Range: {answer.ContentRange}\r\n", body)
                    : new Served(200, "OK", common, body);
            default:
                return null;
        }
    }

    // Kept so a clear can end it; past the cap the oldest is ended.
    private MemoryStream Keep(MemoryStream content)
    {
        if (_streams.Count >= MaxStreams)
        {
            _streams[0].Dispose();
            _streams.RemoveAt(0);
        }

        _streams.Add(content);
        return content;
    }

    private bool IsAllowedNavigation(string uri) =>
        uri == "about:blank" || (_pageUri is { } page && uri == page.AbsoluteUri);
}
