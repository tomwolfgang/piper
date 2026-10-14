using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Piper.App.Controls;

/// <summary>
/// Decodes a body that <see cref="ImageGuard"/> allowed. PNG, JPEG, GIF (first frame) and BMP use the GDI+
/// decoders that ship with Windows. GDI+ cannot read WebP, so a WebP never reaches it. The guard always
/// runs first: GDI+ is native code, and the guard's refusals and 16 MP / 32 MiB caps bound what it sees.
/// </summary>
internal static class ImageDecoding
{
    /// <summary>True for a RIFF/WEBP container; only meaningful once the guard allowed the body.</summary>
    public static bool IsWebp(ReadOnlySpan<byte> body) =>
        body.Length >= 12 && body.StartsWith("RIFF"u8) && body.Slice(8, 4).SequenceEqual("WEBP"u8);

    /// <summary>
    /// Runs the guard and, for an allowed GDI+ format, decodes; both belong off the UI thread. The bitmap
    /// is null for a refused body and for an allowed WebP. <paramref name="decode"/> is a test seam.
    /// </summary>
    public static (Bitmap? Bitmap, ImageGuardVerdict Verdict) GuardAndDecode(
        byte[] body, CancellationToken cancellationToken, Func<byte[], CancellationToken, Bitmap>? decode = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var verdict = ImageGuard.Check(body);
        if (verdict != ImageGuardVerdict.Allowed || IsWebp(body)) return (null, verdict);

        var bitmap = (decode ?? DecodeToBitmap)(body, cancellationToken);
        if (!cancellationToken.IsCancellationRequested) return (bitmap, verdict);

        bitmap.Dispose();
        throw new OperationCanceledException(cancellationToken);
    }

    public static Task<(Bitmap? Bitmap, ImageGuardVerdict Verdict)> GuardAndDecodeAsync(
        byte[] body, CancellationToken cancellationToken, Func<byte[], CancellationToken, Bitmap>? decode = null) =>
        RunUntilAbandonedAsync(() => GuardAndDecode(body, cancellationToken, decode), cancellationToken, result => result.Bitmap?.Dispose());

    /// <summary>
    /// Runs <paramref name="work"/> on the thread pool, or throws as soon as the token fires. GDI+ cannot be
    /// cancelled, so the work finishes on its own and a late result goes to <paramref name="discardLate"/>.
    /// </summary>
    public static async Task<T> RunUntilAbandonedAsync<T>(Func<T> work, CancellationToken cancellationToken, Action<T>? discardLate = null)
    {
        var running = Task.Run(work);
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), abandoned))
        {
            if (await Task.WhenAny(running, abandoned.Task) == running) return await running;
        }

        _ = running.ContinueWith(task =>
        {
            if (task.IsCompletedSuccessfully) discardLate?.Invoke(task.Result);
            else _ = task.Exception; // observed, so a late failure is not an unobserved task exception
        }, TaskScheduler.Default);
        throw new OperationCanceledException(cancellationToken);
    }

    /// <summary>
    /// Decodes into a bitmap that owns its pixels (GDI+ reads lazily from its stream, so the frame is cloned
    /// and the source and stream disposed). Clone, unlike drawing it, keeps translucent pixels exact.
    /// </summary>
    public static Bitmap DecodeToBitmap(byte[] body, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new MemoryStream(body, writable: false);
        using var image = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
        cancellationToken.ThrowIfCancellationRequested();
        var frame = image as Bitmap ?? throw new ArgumentException("Not a raster image.");
        return frame.Clone(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format32bppArgb);
    }

    /// <summary>What GDI+ throws for bytes it cannot decode.</summary>
    public static bool IsInvalidImage(Exception exception) => exception is ArgumentException or OutOfMemoryException or ExternalException;
}
