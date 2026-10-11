using Piper.App.Controls;
using Piper.Core.Http;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

// The inspector's decode path, with the real library. A bare Configuration registers no image formats,
// which ImageGuard (it never calls the library) cannot notice. Kept apart from Program.cs because
// SixLabors.ImageSharp.Point clashes with System.Drawing.Point there.
internal static class ImageDecodingTests
{
    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== every allowed format decodes into a bitmap with its pixels intact");
        using var source = new Image<Rgba32>(5, 3);
        source[0, 0] = new Rgba32(10, 20, 30, 255);
        source[4, 2] = new Rgba32(200, 100, 50, 128);
        (string Name, bool Alpha, Action<Image, Stream> Save)[] formats =
        [
            ("PNG", true, (image, stream) => image.SaveAsPng(stream)),
            ("JPEG", false, (image, stream) => image.SaveAsJpeg(stream)),
            ("GIF", false, (image, stream) => image.SaveAsGif(stream)),
            ("BMP", false, (image, stream) => image.SaveAsBmp(stream)),
            ("lossy WebP", false, (image, stream) => image.SaveAsWebp(stream, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy })),
            ("lossless WebP", true, (image, stream) => image.SaveAsWebp(stream, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless })),
        ];
        foreach (var (name, alpha, save) in formats)
        {
            using var encoded = new MemoryStream();
            save(source, encoded);
            try
            {
                var bytes = encoded.ToArray();
                check(ImageGuard.Check(bytes) == ImageGuardVerdict.Allowed, $"{name} passes the guard");
                using var bitmap = ImageDecoding.DecodeToBitmap(bytes, CancellationToken.None);
                check(bitmap.Width == 5 && bitmap.Height == 3, $"{name} decodes to its size (got {bitmap.Width} x {bitmap.Height})");
                if (!alpha) continue;
                check(bitmap.GetPixel(0, 0).ToArgb() == System.Drawing.Color.FromArgb(255, 10, 20, 30).ToArgb(), $"{name} keeps the colour channels in order");
                check(bitmap.GetPixel(4, 2) is { A: 128, R: 200, B: 50 }, $"{name} keeps straight alpha");
            }
            catch (Exception ex)
            {
                check(false, $"{name} decodes ({ex.GetType().Name})");
            }
        }

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var png = new MemoryStream();
        source.SaveAsPng(png);
        try
        {
            ImageDecoding.DecodeToBitmap(png.ToArray(), cancelled.Token).Dispose();
            check(false, "a cancelled decode throws");
        }
        catch (OperationCanceledException)
        {
            check(true, "a cancelled decode throws OperationCanceledException");
        }

        Console.WriteLine("== the inspector shows a decoded image, refuses a hostile one and survives closing mid-decode");
        var hostile = new byte[330]; // a V5 BMP asking for a 1 GiB profile, with colour space sRGB
        hostile[0] = 0x42; hostile[1] = 0x4D; hostile[2] = 0x4A; hostile[3] = 0x01; hostile[10] = 0x8A;
        hostile[14] = 0x7C; hostile[18] = 8; hostile[22] = 8; hostile[26] = 1; hostile[28] = 24;
        hostile[0x46] = 0x73; hostile[0x47] = 0x52; hostile[0x48] = 0x47; hostile[0x49] = 0x42;
        hostile[0x7E] = 0x7C; hostile[0x85] = 0x40;

        using (var form = new Form { Width = 700, Height = 500 })
        {
            var inspector = new MessageInspector("test", showImageViewer: true) { Dock = DockStyle.Fill };
            form.Controls.Add(inspector);
            form.Show();

            inspector.SetMessage(Response("image/png", png.ToArray()), "png");
            pumpUntil(() => TextOf(inspector).Contains("5 x 3"));
            check(TextOf(inspector).Contains("5 x 3"), "a PNG response is decoded and its size shown");

            inspector.SetMessage(Response("image/bmp", hostile), "bmp");
            pumpUntil(() => TextOf(inspector).Contains("This image was not decoded."));
            check(TextOf(inspector).Contains("This image was not decoded."), "a BMP declaring a 1 GiB profile is refused with the fixed message");
        }

        using (var form = new Form { Width = 700, Height = 500 })
        {
            var inspector = new MessageInspector("test", showImageViewer: true) { Dock = DockStyle.Fill };
            form.Controls.Add(inspector);
            form.Show();
            inspector.SetMessage(Response("image/png", png.ToArray()), "png");
        } // disposed with the decode possibly still running

        var ticks = 0;
        pumpUntil(() => ++ticks > 30);
        check(true, "closing the window while an image decodes raises nothing");
    }

    private static HttpResponseData Response(string contentType, byte[] body)
    {
        var response = new HttpResponseData { Body = body };
        response.Headers.Add("Content-Type", contentType);
        return response;
    }

    private static string TextOf(Control control) =>
        control.Text + "\n" + string.Join("\n", control.Controls.Cast<Control>().Select(TextOf));
}
