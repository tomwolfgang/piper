using System.Runtime.InteropServices;
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
/// engine, so: only a body ImageGuard allowed is accepted (re-checked here); it is written under a random
/// name with a trusted wrapper page to a private random temp folder, served from a virtual https host
/// (deny-cross-origin, never file://); the page's CSP allows only that image; scripts, web messages, dev
/// tools, menus, dialogs, autofill, downloads, new windows and permissions are off or denied; any other
/// navigation or request is cancelled. The folder is deleted on the next selection and on dispose, and
/// stale ones from a crash are swept on first use.
/// </summary>
internal sealed class WebpView : Panel
{
    internal const string Host = "piper-webp.example";
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Piper", "image-inspector");
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);
    internal static TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(15); // settable for tests
    private static int _swept;

    private readonly Func<WebView2, Task> _ensureCore;
    private readonly List<string> _undeleted = [];
    private WebView2? _view;
    private bool _hardened;
    private string? _folder;
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

    /// <summary>Navigations, windows, downloads, permissions and requests refused so far.</summary>
    internal int Blocked { get; private set; }

    internal WebView2? View => _view;

    internal string? Folder => _folder;

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

            SweepStaleFolders();
            var folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
            var imageName = $"{Guid.NewGuid():N}.webp";
            var pageName = $"{Guid.NewGuid():N}.html"; // a new URL per show, so a cached page never points at a deleted image
            Directory.CreateDirectory(folder);
            _folder = folder;
            await File.WriteAllBytesAsync(Path.Combine(folder, imageName), webp);
            await File.WriteAllTextAsync(Path.Combine(folder, pageName), Page(imageName));
            if (generation != _generation || IsDisposed) return WebpShowResult.Superseded;

            _pageUri = new Uri($"https://{Host}/{pageName}");
            _imageUri = new Uri($"https://{Host}/{imageName}");
            core.SetVirtualHostNameToFolderMapping(Host, folder, CoreWebView2HostResourceAccessKind.DenyCors);
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
            // A newer show owns the folder and the page now (it deleted ours, which is why a write failed).
            if (generation != _generation || IsDisposed) return WebpShowResult.Superseded;

            Release();
            return started ? WebpShowResult.Failed : WebpShowResult.RuntimeUnavailable; // after start, it was the temp files or the page
        }
    }

    /// <summary>Hides the view, blanks the page and deletes the temp folder.</summary>
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
            foreach (var folder in _undeleted.ToArray()) TryDeleteFolder(folder);
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
        _loaded?.TrySetResult(false);
        if (_view?.CoreWebView2 is { } core)
        {
            core.ClearVirtualHostNameToFolderMapping(Host);
            core.Navigate("about:blank");
        }

        if (_folder is { } folder && !TryDeleteFolder(folder)) _undeleted.Add(folder);
        _folder = null;
        foreach (var leftover in _undeleted.ToArray())
            if (TryDeleteFolder(leftover)) _undeleted.Remove(leftover);
    }

    private static string Page(string imageName) =>
        "<!doctype html><html><head><meta charset=\"utf-8\">"
        + "<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; img-src 'self'; style-src 'unsafe-inline'; base-uri 'none'; form-action 'none'\">"
        + "<title>Piper</title><style>html,body{margin:0;background:transparent}img{display:block;margin:0 auto}</style></head>"
        + $"<body><img src=\"{imageName}\" alt=\"\"></body></html>";

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
        // Defence in depth: the CSP already stops the page asking for anything else, so no test reaches this.
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (IsAllowedResource(e.Request.Uri)) return;
            e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Forbidden", string.Empty);
            Blocked++;
        };
    }

    private bool IsAllowedNavigation(string uri) =>
        uri == "about:blank" || (_pageUri is { } page && uri == page.AbsoluteUri);

    private bool IsAllowedResource(string uri) =>
        (_pageUri is { } page && uri == page.AbsoluteUri) || (_imageUri is { } image && uri == image.AbsoluteUri);

    private static void SweepStaleFolders()
    {
        if (Interlocked.Exchange(ref _swept, 1) == 1 || !Directory.Exists(Root)) return;

        try
        {
            foreach (var folder in Directory.EnumerateDirectories(Root))
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(folder) > StaleAfter) TryDeleteFolder(folder);
        }
        catch (IOException)
        {
            // Another instance is deleting them too; the next start sweeps again.
        }
        catch (UnauthorizedAccessException)
        {
            // Not ours to delete.
        }
    }

    private static bool TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false; // the browser may still hold the image; retried on the next clear or dispose
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
