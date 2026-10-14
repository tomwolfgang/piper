using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Piper.App.Controls;
using Piper.Core.Http;

// How the inspector uses WebpView: only a WebP goes through it, a missing runtime shows the fixed message,
// and the temp folder and the save items follow the selection. Needs the WebView2 Runtime, except the first.
internal static class WebpInspectorTests
{
    // A 16 x 8 lossless WebP, solid red, no profile.
    private static readonly byte[] Webp = Convert.FromBase64String("UklGRiAAAABXRUJQVlA4TBQAAAAvD8ABAAcQ/Y/+BwCC8D9uIaL/qQ==");

    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a missing WebView2 Runtime shows the fixed message in the inspector");
        using (var failingForm = new Form { Width = 700, Height = 500 })
        {
            var failing = new MessageInspector("test", showImageViewer: true, startWebpView: _ => throw new WebView2RuntimeNotFoundException("simulated")) { Dock = DockStyle.Fill };
            failingForm.Controls.Add(failing);
            failingForm.Show();
            failing.SetMessage(Message(), "webp");
            pumpUntil(() => TextOf(failing).Contains("needs the Microsoft Edge WebView2 Runtime"));
            check(TextOf(failing).Contains("Showing WebP needs the Microsoft Edge WebView2 Runtime."), "the inspector shows the fixed runtime message for a WebP");
        }

        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException)
        {
            Console.WriteLine("   (the WebView2 Runtime is not installed here; the browser-dependent checks were skipped)");
            return;
        }

        Console.WriteLine("== the inspector routes only WebP through the view, and saves nothing for it");
        using var form = new Form { Width = 700, Height = 500 };
        var inspector = new MessageInspector("test", showImageViewer: true) { Dock = DockStyle.Fill };
        form.Controls.Add(inspector);
        form.Show();
        inspector.SetMessage(Message(), "webp");
        pumpUntil(() => TextOf(inspector).Contains("WebP image (view only)"));
        check(TextOf(inspector).Contains("WebP image (view only)") && TextOf(inspector).Contains("image/webp"), "a WebP response is shown view-only with its MIME");
        var shown = Find(inspector, "WebpView");
        var shownFolder = FolderOf(shown);
        check(shown is { Visible: true } && shownFolder is not null && Directory.Exists(shownFolder), "the WebP view is visible with its temp folder");
        check(SaveItemsEnabled(inspector) == false, "Save as PNG/JPEG and Save image as are disabled for a WebP");

        using var png = new MemoryStream();
        using (var pixels = new Bitmap(5, 3)) pixels.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        inspector.SetMessage(Message(png.ToArray(), "image/png"), "png");
        pumpUntil(() => TextOf(inspector).Contains("5 x 3 px"));
        check(TextOf(inspector).Contains("5 x 3 px") && !Find(inspector, "WebpView")!.Visible && !Directory.Exists(shownFolder!), "selecting a PNG hides the WebP view and removes its temp folder");
        check(SaveItemsEnabled(inspector) == true, "the save items are enabled again for a PNG");

        inspector.SetMessage(Message(), "webp");
        pumpUntil(() => Find(inspector, "WebpView") is { Visible: true });
        var disposedFolder = FolderOf(Find(inspector, "WebpView"));
        form.Controls.Remove(inspector);
        inspector.Dispose();
        check(disposedFolder is not null && !Directory.Exists(disposedFolder), "disposing the inspector removes the WebP temp folder");
    }

    private static HttpResponseData Message(byte[]? body = null, string contentType = "image/webp")
    {
        var response = new HttpResponseData { Body = body ?? Webp };
        response.Headers.Add("Content-Type", contentType);
        return response;
    }

    private static string? FolderOf(Control? webpView) =>
        webpView?.GetType().GetProperty("Folder", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(webpView) as string;

    private static Control? Find(Control control, string typeName) =>
        control.GetType().Name == typeName ? control : control.Controls.Cast<Control>().Select(child => Find(child, typeName)).FirstOrDefault(found => found is not null);

    // Runs the image menu's enabling logic as its Opening event does; null if the three items disagree.
    private static bool? SaveItemsEnabled(MessageInspector inspector)
    {
        var imageView = (Control)typeof(MessageInspector).GetField("_imageView", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(inspector)!;
        var menu = imageView.ContextMenuStrip!;
        typeof(MessageInspector).GetMethod("EnableConversions", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(inspector, [menu.Items]);
        var tagged = menu.Items.Cast<ToolStripItem>().Where(item => item.Tag is not null).ToArray();
        return tagged.Length == 3 && tagged.All(item => item.Enabled == tagged[0].Enabled) ? tagged[0].Enabled : null;
    }

    private static string TextOf(Control control) =>
        control.Text + "\n" + string.Join("\n", control.Controls.Cast<Control>().Select(TextOf));
}
