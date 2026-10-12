using System.Diagnostics;
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

        // Save-as: the private converter re-encodes through the same options, and the token stops it.
        var convert = typeof(MessageInspector).GetMethod("ConvertImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var format in new[] { "png", "jpg", "webp" })
        {
            var converted = ((Task<byte[]>)convert.Invoke(null, [png.ToArray(), format, CancellationToken.None])!).GetAwaiter().GetResult();
            using var reloaded = ImageDecoding.DecodeToBitmap(converted, CancellationToken.None);
            check(reloaded.Width == 5 && reloaded.Height == 3, $"Save as {format} converts to an image of the same size");
        }

        try
        {
            ((Task<byte[]>)convert.Invoke(null, [png.ToArray(), "png", cancelled.Token])!).GetAwaiter().GetResult();
            check(false, "a cancelled conversion throws");
        }
        catch (OperationCanceledException)
        {
            check(true, "a cancelled conversion throws OperationCanceledException");
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

        Console.WriteLine("== the guard scan of a hostile 32 MiB JPEG does not block the UI thread");
        using (var form = new Form { Width = 700, Height = 500 })
        {
            var inspector = new MessageInspector("test", showImageViewer: true) { Dock = DockStyle.Fill };
            form.Controls.Add(inspector);
            form.Show();

            // A tiny baseline frame whose one scan is 32 MiB of 0xFF 0x00 stuffing: the guard visits every
            // pair, which is its slowest walk. The control has the same size and shape but nothing to visit
            // (IndexOf skips zeros at memory speed), so SetMessage's other work on 32 MiB (the text and hex
            // views) cancels out and only the guard's cost is left. Both are measured on this machine.
            var slow = HostileJpeg(ImageGuard.MaxBytes, stuffed: true);
            var control = HostileJpeg(ImageGuard.MaxBytes, stuffed: false);
            double Guard(byte[] body) => Enumerable.Range(0, 3).Min(_ =>
            {
                var watch = Stopwatch.StartNew();
                ImageGuard.Check(body);
                return watch.Elapsed.TotalMilliseconds;
            });
            var guardCost = Guard(slow) - Guard(control);
            check(ImageGuard.Check(slow) == ImageGuardVerdict.Allowed && ImageGuard.Check(control) == ImageGuardVerdict.Allowed,
                "both JPEGs pass the guard, so the whole body is scanned");

            double UiThreadBlocked(byte[] body)
            {
                var watch = Stopwatch.StartNew();
                inspector.SetMessage(Response("image/jpeg", body), "jpg");
                var blocked = watch.Elapsed.TotalMilliseconds;
                pumpUntil(() => !TextOf(inspector).Contains("Decoding image..."));
                check(!TextOf(inspector).Contains("Decoding image..."), "the decode finishes or is refused");
                return blocked;
            }

            UiThreadBlocked(control); // warms the view code paths
            var baseline = Enumerable.Range(0, 3).Min(_ => UiThreadBlocked(control));
            var blockedByHostile = Enumerable.Range(0, 3).Min(_ => UiThreadBlocked(slow));
            Console.WriteLine($"   guard scan {guardCost:N0} ms; SetMessage held the UI thread {blockedByHostile:N0} ms (control {baseline:N0} ms)");
            if (guardCost < 20) Console.WriteLine("   (the guard scan is too fast on this machine to tell the threads apart; bound not asserted)");
            else check(blockedByHostile - baseline < guardCost / 2, $"SetMessage does not run the guard scan on the UI thread ({blockedByHostile - baseline:N0} ms extra against a {guardCost:N0} ms scan)");

            // Refusals still reach the status line with the fixed texts, now from the worker's verdict.
            (string Name, byte[] Body, string Expected)[] refused =
            [
                ("a body over the byte cap", new byte[ImageGuard.MaxBytes + 1], "larger than 16 megapixels or 32 MB."),
                ("a TIFF", [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0], "Piper does not decode TIFF images"),
                ("bytes that are not an image", "<html>not an image</html>"u8.ToArray(), "unsupported or invalid image data."),
            ];
            foreach (var (name, body, expected) in refused)
            {
                inspector.SetMessage(Response("image/png", body), "png");
                pumpUntil(() => TextOf(inspector).Contains(expected));
                check(TextOf(inspector).Contains(expected), $"{name} is refused with the fixed message");
            }
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

    // SOI, a baseline 8 x 8 frame, one scan, then 0xFF 0x00 pairs (or zeros) up to the given length, then EOI.
    private static byte[] HostileJpeg(int length, bool stuffed)
    {
        byte[] head = [0xFF, 0xD8, 0xFF, 0xC0, 0, 11, 8, 0, 8, 0, 8, 1, 1, 0x11, 0, 0xFF, 0xDA, 0, 8, 1, 1, 0, 0, 0x3F, 0];
        var data = new byte[length];
        head.CopyTo(data, 0);
        if (stuffed)
            for (var i = head.Length; i + 1 < length - 2; i += 2) data[i] = 0xFF;
        data[length - 2] = 0xFF;
        data[length - 1] = 0xD9;
        return data;
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
