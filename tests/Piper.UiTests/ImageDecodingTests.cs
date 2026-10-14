using System.Diagnostics;
using System.Drawing.Imaging;
using Piper.App.Controls;
using Piper.Core.Http;

// The inspector's decode path, with the real GDI+ decoders. ImageGuard never calls a decoder, so only
// these tests can notice a format GDI+ refuses or reads with the wrong channel order.
internal static class ImageDecodingTests
{
    // A 16 x 8 lossless WebP, solid red, no profile. GDI+ cannot encode WebP, so the bytes are embedded.
    private static readonly byte[] Webp = Convert.FromBase64String("UklGRiAAAABXRUJQVlA4TBQAAAAvD8ABAAcQ/Y/+BwCC8D9uIaL/qQ==");

    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== every GDI+ format decodes into a bitmap with its pixels intact");
        using var source = new Bitmap(5, 3, PixelFormat.Format32bppArgb);
        source.SetPixel(0, 0, Color.FromArgb(255, 10, 20, 30));
        source.SetPixel(4, 2, Color.FromArgb(128, 200, 100, 50));
        (string Name, string Mime, bool Alpha, ImageFormat Format)[] formats =
        [
            ("PNG", "image/png", true, ImageFormat.Png),
            ("JPEG", "image/jpeg", false, ImageFormat.Jpeg),
            ("GIF", "image/gif", false, ImageFormat.Gif),
            ("BMP", "image/bmp", false, ImageFormat.Bmp),
        ];
        var encodedByName = new Dictionary<string, byte[]>();
        foreach (var (name, _, alpha, format) in formats)
        {
            using var encoded = new MemoryStream();
            source.Save(encoded, format);
            try
            {
                var bytes = encodedByName[name] = encoded.ToArray();
                check(ImageGuard.Check(bytes) == ImageGuardVerdict.Allowed, $"{name} passes the guard");
                using var bitmap = ImageDecoding.DecodeToBitmap(bytes, CancellationToken.None);
                check(bitmap.Width == 5 && bitmap.Height == 3, $"{name} decodes to its size (got {bitmap.Width} x {bitmap.Height})");
                if (!alpha) continue;
                check(bitmap.GetPixel(0, 0).ToArgb() == Color.FromArgb(255, 10, 20, 30).ToArgb(), $"{name} keeps the colour channels in order");
                check(bitmap.GetPixel(4, 2) is { A: 128, R: 200, B: 50 }, $"{name} keeps straight alpha");
            }
            catch (Exception ex)
            {
                check(false, $"{name} decodes ({ex.GetType().Name})");
            }
        }

        var png = encodedByName["PNG"];
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            ImageDecoding.DecodeToBitmap(png, cancelled.Token).Dispose();
            check(false, "a cancelled decode throws");
        }
        catch (OperationCanceledException)
        {
            check(true, "a cancelled decode throws OperationCanceledException");
        }

        // Save-as: the private converter decodes and re-encodes with GDI+; the token is checked between the two.
        var convert = typeof(MessageInspector).GetMethod("ConvertImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        foreach (var format in new[] { "png", "jpg" })
        {
            var converted = (byte[])convert.Invoke(null, [png, format, CancellationToken.None])!;
            using var reloaded = ImageDecoding.DecodeToBitmap(converted, CancellationToken.None);
            check(reloaded.Width == 5 && reloaded.Height == 3, $"Save as {format} converts to an image of the same size");
        }

        try
        {
            convert.Invoke(null, [png, "png", cancelled.Token]);
            check(false, "a cancelled conversion throws");
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is OperationCanceledException)
        {
            check(true, "a cancelled conversion throws OperationCanceledException");
        }

        Console.WriteLine("== only an allowed, non-WebP body reaches GDI+");
        var reachedDecoder = 0;
        Bitmap CountingDecoder(byte[] bytes, CancellationToken token)
        {
            reachedDecoder++;
            return ImageDecoding.DecodeToBitmap(bytes, token);
        }

        var hostile = new byte[330]; // a V5 BMP asking for a 1 GiB profile, with colour space sRGB
        hostile[0] = 0x42; hostile[1] = 0x4D; hostile[2] = 0x4A; hostile[3] = 0x01; hostile[10] = 0x8A;
        hostile[14] = 0x7C; hostile[18] = 8; hostile[22] = 8; hostile[26] = 1; hostile[28] = 24;
        hostile[0x46] = 0x73; hostile[0x47] = 0x52; hostile[0x48] = 0x47; hostile[0x49] = 0x42;
        hostile[0x7E] = 0x7C; hostile[0x85] = 0x40;
        (string Name, byte[] Body, ImageGuardVerdict Expected)[] notDecoded =
        [
            ("a BMP declaring a 1 GiB profile", hostile, ImageGuardVerdict.EmbeddedProfile),
            ("a TIFF", [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0], ImageGuardVerdict.Tiff),
            ("bytes that are not an image", "<html>not an image</html>"u8.ToArray(), ImageGuardVerdict.Unrecognised),
            ("a body over the byte cap", new byte[ImageGuard.MaxBytes + 1], ImageGuardVerdict.TooLarge),
            ("a WebP, which GDI+ cannot read", Webp, ImageGuardVerdict.Allowed),
        ];
        foreach (var (name, body, expected) in notDecoded)
        {
            var (bitmap, verdict) = ImageDecoding.GuardAndDecode(body, CancellationToken.None, CountingDecoder);
            check(bitmap is null && verdict == expected && reachedDecoder == 0, $"{name} gets {expected} and no bitmap, and never reaches GDI+");
        }

        var (decoded, allowed) = ImageDecoding.GuardAndDecode(png, CancellationToken.None, CountingDecoder);
        check(decoded is { Width: 5 } && allowed == ImageGuardVerdict.Allowed && reachedDecoder == 1, "a PNG goes through the guard and then to the decoder exactly once");
        decoded?.Dispose();
        check(ImageDecoding.IsWebp(Webp) && !ImageDecoding.IsWebp(png) && !ImageDecoding.IsWebp([]),
            "only a RIFF....WEBP body is a WebP");

        Console.WriteLine("== a decode that outlives its timeout leaves the UI responsive, and its late bitmap is disposed");
        using (var release = new ManualResetEventSlim())
        using (var timeout = new CancellationTokenSource(150))
        {
            Bitmap? late = null;
            Bitmap SlowDecoder(byte[] bytes, CancellationToken token)
            {
                release.Wait();
                return late = new Bitmap(4, 4);
            }

            var watch = Stopwatch.StartNew();
            var task = ImageDecoding.GuardAndDecodeAsync(png, timeout.Token, SlowDecoder);
            pumpUntil(() => task.IsCompleted);
            check(task.IsCanceled && watch.ElapsedMilliseconds < 5000, $"the wait ends with a cancellation while the decoder is still blocked ({watch.ElapsedMilliseconds} ms)");

            release.Set();
            pumpUntil(() => late is not null && IsDisposed(late));
            check(late is not null && IsDisposed(late), "the bitmap that arrives after the caller gave up is disposed");
        }

        Console.WriteLine("== abandoned decodes cannot pile up");
        using (var release = new ManualResetEventSlim())
        {
            int running = 0, peak = 0, started = 0;
            Bitmap Blocked(byte[] bytes, CancellationToken token)
            {
                Interlocked.Increment(ref started);
                var now = Interlocked.Increment(ref running);
                lock (release) peak = Math.Max(peak, now);
                release.Wait();
                Interlocked.Decrement(ref running);
                return new Bitmap(1, 1);
            }

            var callers = Enumerable.Range(0, 8).Select(_ =>
            {
                var timeout = new CancellationTokenSource(150);
                return ImageDecoding.GuardAndDecodeAsync(png, timeout.Token, Blocked);
            }).ToArray();
            pumpUntil(() => callers.All(c => c.IsCompleted));
            check(callers.All(c => c.IsCanceled) && started == ImageDecoding.MaxConcurrentNativeWork && peak == started,
                $"8 callers gave up, and only {ImageDecoding.MaxConcurrentNativeWork} native decodes started (started {started}, peak {peak})");
            release.Set();
            var settle = 0;
            pumpUntil(() => running == 0 && ++settle > 30);
            check(started == ImageDecoding.MaxConcurrentNativeWork, $"abandoned waiters never start once a slot frees (started {started})");
        }

        Console.WriteLine("== the inspector shows a decoded image, refuses a hostile one and survives closing mid-decode");
        using (var form = new Form { Width = 700, Height = 500 })
        {
            var inspector = new MessageInspector("test", showImageViewer: true) { Dock = DockStyle.Fill };
            form.Controls.Add(inspector);
            form.Show();

            foreach (var (name, mime, _, _) in formats)
            {
                inspector.SetMessage(Response(mime, encodedByName[name]), name.ToLowerInvariant());
                bool Shown() => TextOf(inspector) is var text && text.Contains("5 x 3 px") && text.Contains(mime);
                pumpUntil(Shown);
                check(Shown(),$"a {name} response is decoded and shown as '5 x 3 px | {mime}'");
            }

            // A response dragged onto the Image tab renders without SetMessage clearing the view first.
            check(HasImage(inspector), "setup: the BMP is on screen");
            typeof(MessageInspector).GetMethod("RenderImage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(inspector, [Response("image/webp", Webp), true]);
            pumpUntil(() => TextOf(inspector).Contains("WebP"));
            check(TextOf(inspector).Contains("WebP") && !HasImage(inspector), "a WebP dropped over a shown image replaces it");

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
            inspector.SetMessage(Response("image/png", png), "png");
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

    // GDI+ throws ArgumentException for any member of a disposed image.
    private static bool IsDisposed(Bitmap bitmap)
    {
        try
        {
            _ = bitmap.Width;
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static HttpResponseData Response(string contentType, byte[] body)
    {
        var response = new HttpResponseData { Body = body };
        response.Headers.Add("Content-Type", contentType);
        return response;
    }

    private static bool HasImage(Control control) =>
        control is ShimmyMySherbet.WinForms.ZoomableImgBox.ZoomableImageBox { Image: not null } || control.Controls.Cast<Control>().Any(HasImage);

    private static string TextOf(Control control) =>
        control.Text + "\n" + string.Join("\n", control.Controls.Cast<Control>().Select(TextOf));
}
