using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Piper.App.Theme;

namespace Piper.App.Controls;

internal enum WebpShowResult
{
    Shown,

    /// <summary>The WebView2 Runtime is missing or did not start.</summary>
    RuntimeUnavailable,

    Failed,

    /// <summary>Another message was selected, or the view cleared, while loading.</summary>
    Superseded,
}

/// <summary>
/// View-only WebP preview in WebView2 (GDI+ cannot read WebP). The body is hostile and parsed by a browser
/// engine, so: only a body ImageGuard allowed is accepted (re-checked here); it stays in memory, never on
/// disk, and is answered from WebResourceRequested with a trusted wrapper page, on a virtual https host
/// under a random per-show token, to that page only (Referer checked, no-store, never file://); the page's
/// CSP allows only that image; scripts, web messages, dev
/// tools, menus, dialogs, autofill, downloads, new windows and permissions are off or denied; any other
/// navigation or request is cancelled. The body is dropped on the next selection, on clear and on dispose.
/// </summary>
internal sealed class WebpView : Panel
{
    internal const string Host = "piper-webp.example";
    private const int MaxStreams = 8; // a show serves its page and image once; this only bounds a refetch loop
    private const string Csp = "default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'";
    internal static TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(15); // settable for tests

    private readonly Func<WebView2, Task> _ensureCore;
    private readonly List<MemoryStream> _streams = [];
    private WebView2? _view;
    private bool _hardened;
    private byte[]? _webp;
    private Uri? _pageUri;
    private Uri? _imageUri;
    private ulong _navigationId;
    private TaskCompletionSource<bool>? _loaded;
    private int _generation;

    /// <param name="ensureCore">Starts the control's WebView2. A test seam; the default is the app-wide environment.</param>
    public WebpView(Func<WebView2, Task>? ensureCore = null)
    {
        _ensureCore = ensureCore ?? (view => view.EnsureCoreWebView2Async());
        Dock = DockStyle.Fill;
        Visible = false;
    }

    internal enum Resource { Blocked, Page, Image }

    /// <summary>Navigations, windows, downloads, permissions and requests refused so far.</summary>
    internal int Blocked { get; private set; }

    internal WebView2? View => _view;

    internal Uri? PageUri => _pageUri;

    internal Uri? ImageUri => _imageUri;

    internal bool HoldsBody => _webp is not null;

    internal static bool IsStartFailure(Exception exception) =>
        exception is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or IOException or UnauthorizedAccessException;

    public async Task<WebpShowResult> ShowAsync(byte[] webp)
    {
        if (ImageGuard.Check(webp) != ImageGuardVerdict.Allowed || !ImageDecoding.IsWebp(webp))
            throw new ArgumentException("Only a WebP that ImageGuard allowed may be shown.", nameof(webp));

        var generation = ++_generation;
        var started = false;
        Release();
        try
        {
            var view = EnsureControl();
            await _ensureCore(view);
            if (generation != _generation || IsDisposed || view.CoreWebView2 is not { } core) return WebpShowResult.Superseded;

            if (!_hardened)
            {
                Harden(core);
                _hardened = true;
            }

            started = true;

            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(); // a new URL per show, so a stale page cannot read this image
            _webp = webp;
            _pageUri = new Uri($"https://{Host}/{token}/page.html");
            _imageUri = new Uri($"https://{Host}/{token}/image.webp");
            view.ZoomFactor = 1;
            var loaded = _loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.Navigate(_pageUri.AbsoluteUri);
            var finished = await Task.WhenAny(loaded.Task, Task.Delay(LoadTimeout));
            if (generation != _generation || IsDisposed) return WebpShowResult.Superseded;

            var ok = finished == loaded.Task && loaded.Task.Result;
            Visible = ok;
            return ok ? WebpShowResult.Shown : WebpShowResult.Failed;
        }
        catch (Exception ex) when (IsStartFailure(ex))
        {
            // A newer show owns the page now.
            if (generation != _generation || IsDisposed) return WebpShowResult.Superseded;

            Release();
            Visible = false;
            return started ? WebpShowResult.Failed : WebpShowResult.RuntimeUnavailable; // after start, it was the page
        }
    }

    /// <summary>Hides the view, blanks the page and drops the body.</summary>
    public void Clear()
    {
        _generation++;
        Release();
        Visible = false;
    }

    /// <summary>Zooms by a factor, or back to 100% for 0 (script is off, so this is the only zoom).</summary>
    public void ChangeZoom(double multiplier)
    {
        if (_view is { } view) view.ZoomFactor = multiplier == 0 ? 1 : Math.Clamp(view.ZoomFactor * multiplier, 0.25, 5);
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
        _pageUri = _imageUri = null;
        _navigationId = 0; // a late completion of the previous page must not finish the next show
        _webp = null;
        _loaded?.TrySetResult(false);
        foreach (var stream in _streams) stream.Dispose();
        _streams.Clear();
        if (_view?.CoreWebView2 is { } core) core.Navigate("about:blank");
    }

    private static string Page() =>
        "<!doctype html><html><head><meta charset=\"utf-8\">"
        + $"<meta http-equiv=\"Content-Security-Policy\" content=\"{Csp}\">"
        + "<title>Piper</title><style>html,body{margin:0;background:transparent}img{display:block;margin:0 auto}</style></head>"
        + "<body><img src=\"image.webp\" alt=\"\"></body></html>";

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
            var resource = Resolve(e.Request.Uri, e.Request.Method, headers.Contains("Referer") ? headers.GetHeader("Referer") : null);
            if (resource == Resource.Page)
                e.Response = Respond(core.Environment, new MemoryStream(Encoding.UTF8.GetBytes(Page()), writable: false),
                    "text/html; charset=utf-8", $"Content-Security-Policy: {Csp}\r\nReferrer-Policy: same-origin\r\n");
            else if (resource == Resource.Image && _webp is { } body)
                e.Response = Respond(core.Environment, new MemoryStream(body, writable: false), "image/webp", "");
            else
                Blocked++;
        };
    }

    /// <summary>Which of this show's two resources a request is for; the image only for a request from its own page.</summary>
    internal Resource Resolve(string uri, string method, string? referer)
    {
        if (method != "GET") return Resource.Blocked;
        if (_pageUri is { } page && uri == page.AbsoluteUri) return Resource.Page;
        return _imageUri is { } image && uri == image.AbsoluteUri && _pageUri is { } owner && referer == owner.AbsoluteUri
            ? Resource.Image
            : Resource.Blocked;
    }

    // The stream reads the caller's array in place (no copy); it is kept so a clear can end it.
    private CoreWebView2WebResourceResponse Respond(CoreWebView2Environment environment, MemoryStream content, string type, string extra)
    {
        if (_streams.Count >= MaxStreams)
        {
            _streams[0].Dispose();
            _streams.RemoveAt(0);
        }

        _streams.Add(content);
        return environment.CreateWebResourceResponse(content, 200, "OK",
            $"Content-Type: {type}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\n{extra}");
    }

    private bool IsAllowedNavigation(string uri) =>
        uri == "about:blank" || (_pageUri is { } page && uri == page.AbsoluteUri);
}
