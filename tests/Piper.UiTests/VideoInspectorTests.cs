using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Piper.Core.Http;
using Piper.Core.Sessions;
using Piper.App.Controls;

// How the inspector uses VideoView: a video response is served from memory to the Video tab, the body
// follows the selection, a missing runtime shows the fixed message, and nothing is written to %TEMP%.
// Needs the WebView2 Runtime, except the first check.
internal static class VideoInspectorTests
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;
    private const int VideoTab = 6;

    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        var tempBefore = VideoViewTests.TempSnapshot();
        Console.WriteLine("== a missing WebView2 Runtime shows the fixed message on the Video tab");
        using (var failingForm = new Form { Width = 700, Height = 500 })
        {
            var failing = new MessageInspector("test", showImageViewer: true, startVideoView: _ => throw new WebView2RuntimeNotFoundException("simulated")) { Dock = DockStyle.Fill };
            failingForm.Controls.Add(failing);
            failingForm.Show();
            failing.SetMessage(Response(), "video");
            SelectVideoTab(failing);
            pumpUntil(() => TextOf(failing).Contains("needs the Microsoft Edge WebView2 Runtime"));
            check(TextOf(failing).Contains("Showing video needs the Microsoft Edge WebView2 Runtime.") && !TextOf(failing).Contains("Loading the embedded media player"),
                "the inspector shows the fixed runtime message for a video");
        }

        try { CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException)
        {
            Console.WriteLine("   (the WebView2 Runtime is not installed here; the browser-dependent checks were skipped)");
            return;
        }

        Console.WriteLine("== the Video tab serves the selected response from memory and lets go of it");
        using var form = new Form { Width = 700, Height = 500 };
        var inspector = new MessageInspector("test", showImageViewer: true) { Dock = DockStyle.Fill };
        form.Controls.Add(inspector);
        form.Show();
        inspector.SetMessage(Response(), "video");
        SelectVideoTab(inspector);
        pumpUntil(() => Find(inspector, "VideoView") is { Visible: true });
        var shown = Find(inspector, "VideoView");
        check(shown is { Visible: true } && HoldsBody(shown) && TextOf(inspector).Contains("video/webm") && !TextOf(inspector).Contains("Loading the embedded"),
            "a video response is shown in the Video tab with its MIME, and the view holds the body in memory");

        inspector.SetMessage(Response("text/html", "<html>"u8.ToArray()), "html");
        check(!HoldsBody(shown) && shown is { Visible: false }, "selecting another message clears the view and drops the body");
        SelectVideoTab(inspector);
        check(TextOf(inspector).Contains("Response does not have a video content type.") && !HoldsBody(shown), "a response without a video type shows the notice and the force panel, not the video");

        typeof(MessageInspector).GetMethod("TryRenderVideo", Instance)!.Invoke(inspector, []);
        pumpUntil(() => HoldsBody(shown));
        check(HoldsBody(shown), "Try anyway serves the non-video response to the player");

        Console.WriteLine("== a video dropped on the tab is shown while another session stays selected");
        inspector.SetMessage(Response("text/html", "<html>"u8.ToArray()), "html");
        var data = new DataObject();
        data.SetData(typeof(Session), new Session { Response = Response() });
        typeof(MessageInspector).GetMethod("LoadDroppedMedia", Instance)!.Invoke(inspector, [new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy), VideoTab]);
        pumpUntil(() => HoldsBody(shown) && TextOf(inspector).Contains("video/webm"));
        check(HoldsBody(shown) && TextOf(inspector).Contains("video/webm"), "the dropped video is served from memory");

        inspector.SetMessage(Response(), "video again");
        SelectVideoTab(inspector);
        pumpUntil(() => HoldsBody(shown));
        form.Controls.Remove(inspector);
        inspector.Dispose();
        check(!HoldsBody(shown), "disposing the inspector drops the video body");

        VideoViewTests.AssertNothingWritten(check, tempBefore, "the inspector's video preview");
    }

    private static HttpResponseData Response(string contentType = "video/webm", byte[]? body = null)
    {
        var response = new HttpResponseData { Body = body ?? VideoViewTests.Webm };
        response.Headers.Add("Content-Type", contentType);
        return response;
    }

    private static void SelectVideoTab(MessageInspector inspector)
    {
        var tabs = (TabControl)typeof(MessageInspector).GetField("_tabs", Instance)!.GetValue(inspector)!;
        tabs.SelectedIndex = VideoTab;
    }

    private static bool HoldsBody(Control? view) =>
        view?.GetType().GetProperty("HoldsBody", Instance)?.GetValue(view) is true;

    private static Control? Find(Control control, string typeName) =>
        control.GetType().Name == typeName ? control : control.Controls.Cast<Control>().Select(child => Find(child, typeName)).FirstOrDefault(found => found is not null);

    private static string TextOf(Control control) =>
        control.Text + "\n" + string.Join("\n", control.Controls.Cast<Control>().Select(TextOf));
}
