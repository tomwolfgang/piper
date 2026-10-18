using System.Drawing.Imaging;
using System.Reflection;
using Piper.App.Controls;
using Piper.Core.Http;
using Piper.Core.Sessions;

// Save image writes what the Image tab shows. A response dragged onto the tab is shown while another
// session stays selected, and the save used to write the selected session's body instead.
internal static class InspectorSaveTests
{
    private const BindingFlags Instance = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== Save image writes the dropped response, not the selected session");
        var folder = Path.Combine(Path.GetTempPath(), $"Piper-UiTest-SaveImage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var selected = Png(3, 3);
            var dropped = Png(7, 5);
            string? asked = null;
            var target = Path.Combine(folder, "dropped.png");

            using var form = new Form { Width = 700, Height = 500 };
            var inspector = new MessageInspector("test", showImageViewer: true, chooseImageSavePath: format => { asked = format; return target; })
                { Dock = DockStyle.Fill };
            form.Controls.Add(inspector);
            form.Show();

            inspector.SetMessage(Response(selected), "selected");
            pumpUntil(() => TextOf(inspector).Contains("3 x 3 px"));

            var session = new Session { Response = Response(dropped) };
            var data = new DataObject();
            data.SetData(typeof(Session), session);
            var drop = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.Copy);
            typeof(MessageInspector).GetMethod("LoadDroppedMedia", Instance)!.Invoke(inspector, [drop, 5]);
            pumpUntil(() => TextOf(inspector).Contains("7 x 5 px"));
            check(TextOf(inspector).Contains("7 x 5 px"), "setup: the dropped image is on screen while another session is selected");

            typeof(MessageInspector).GetMethod("SaveCurrentImage", Instance)!.Invoke(inspector, ["png"]);
            pumpUntil(() => File.Exists(target) && new FileInfo(target).Length > 0);
            check(asked == "png", "the save asks for the requested format");
            using var saved = File.Exists(target) ? new Bitmap(target) : null;
            check(saved is { Width: 7, Height: 5 }, $"the saved file is the dropped 7 x 5 image (got {saved?.Width} x {saved?.Height})");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static byte[] Png(int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static HttpResponseData Response(byte[] body)
    {
        var response = new HttpResponseData { Body = body };
        response.Headers.Add("Content-Type", "image/png");
        return response;
    }

    private static string TextOf(Control control) =>
        control.Text + "\n" + string.Join("\n", control.Controls.Cast<Control>().Select(TextOf));
}
