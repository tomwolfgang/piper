using Piper.App.Controls;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Image = SixLabors.ImageSharp.Image;

// A bare Configuration registers no image formats, so the inspector's first decoder options made every
// image fail while ImageGuard (which never calls the library) still allowed it. This decodes what
// ImageSharp itself encodes, through the options the inspector uses. Kept apart from Program.cs
// because SixLabors.ImageSharp.Point clashes with System.Drawing.Point there.
internal static class ImageDecodingTests
{
    public static void Run(Action<bool, string> check)
    {
        Console.WriteLine("== the inspector's decoder options decode every allowed format");
        using var source = new Image<Rgba32>(5, 3);
        (string Name, Action<Image, Stream> Save)[] formats =
        [
            ("PNG", (image, stream) => image.SaveAsPng(stream)),
            ("JPEG", (image, stream) => image.SaveAsJpeg(stream)),
            ("GIF", (image, stream) => image.SaveAsGif(stream)),
            ("BMP", (image, stream) => image.SaveAsBmp(stream)),
            ("lossy WebP", (image, stream) => image.SaveAsWebp(stream, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy })),
            ("lossless WebP", (image, stream) => image.SaveAsWebp(stream, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless })),
        ];
        foreach (var (name, save) in formats)
        {
            using var encoded = new MemoryStream();
            save(source, encoded);
            try
            {
                var bytes = encoded.ToArray();
                check(ImageGuard.Check(bytes) == ImageGuardVerdict.Allowed, $"{name} passes the guard");
                using var decoded = Image.Load(ImageDecoding.Options, bytes);
                check(decoded.Width == 5 && decoded.Height == 3, $"{name} decodes to its size (got {decoded.Width} x {decoded.Height})");
            }
            catch (Exception ex)
            {
                check(false, $"{name} decodes ({ex.GetType().Name})");
            }
        }
    }
}
