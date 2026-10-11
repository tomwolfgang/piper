using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.PixelFormats;
using ImageSharp = SixLabors.ImageSharp;

namespace Piper.App.Controls;

/// <summary>
/// How the inspector decodes a body that <see cref="ImageGuard"/> has allowed. One frame is all the
/// viewer shows. SkipMetadata stops the JPEG and WebP decoders from keeping an ICC profile, EXIF or
/// XMP, but the PNG and BMP decoders in 3.1.12 keep a profile regardless (checked against the
/// library), which is why the guard refuses profiles itself. The allocator limit bounds what the guard
/// cannot see (a GIF frame descriptor, say) but not a BMP profile, which the library reads into a
/// plain array; the guard refuses those.
/// </summary>
internal static class ImageDecoding
{
    public static DecoderOptions Options { get; } = new()
    {
        SkipMetadata = true,
        MaxFrames = 1,
        Configuration = CreateConfiguration(),
    };

    // A clone of the default keeps the format decoders registered (a bare new Configuration() has
    // none) and, unlike changing the default, leaves the process-wide allocator alone.
    private static ImageSharp.Configuration CreateConfiguration()
    {
        var configuration = ImageSharp.Configuration.Default.Clone();
        configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions { AllocationLimitMegabytes = 512 });
        return configuration;
    }

    /// <summary>
    /// Decodes straight into a GDI+ bitmap, row by row. Safe to call off the UI thread, and the
    /// decoder observes <paramref name="cancellationToken"/>. Going through a PNG encode and decode
    /// first (the earlier approach) cost up to 28 s of a 37 s total for a noisy 8192 x 8192 JPEG.
    /// </summary>
    public static Bitmap DecodeToBitmap(byte[] body, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(body, writable: false);
        using var image = ImageSharp.Image.LoadAsync<Bgra32>(Options, stream, cancellationToken).GetAwaiter().GetResult();

        var bitmap = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        try
        {
            var bits = bitmap.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[image.Width * 4];
                image.ProcessPixelRows(accessor =>
                {
                    for (var y = 0; y < accessor.Height; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        MemoryMarshal.AsBytes(accessor.GetRowSpan(y)).CopyTo(row);
                        Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
                    }
                });
            }
            finally
            {
                bitmap.UnlockBits(bits);
            }

            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }
}
