using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Piper.App.Controls;

// The inspector hands captured bodies to SixLabors.ImageSharp 3.1.12, which has unfixed advisories
// (S11). ImageGuard decides from the bytes alone, so these tests build container headers by hand and
// need no image library.
internal static class ImageGuardTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("image guard allows small PNG, JPEG, GIF, BMP and WebP", () =>
        {
            foreach (var (name, bytes) in Valid())
                runner.AreEqual(ImageGuardVerdict.Allowed, ImageGuard.Check(bytes), $"{name} is allowed");

            runner.AreEqual(ImageGuardVerdict.Allowed, ImageGuard.Check(Png(8192, 8192)), "8192 x 8192 (exactly 64 Mi pixels) is allowed");
            runner.AreEqual(ImageGuardVerdict.Allowed,
                ImageGuard.Check(Jpeg(1, 1, extra: Concat(Segment(0xE2, "MPF\0"u8.ToArray()), Segment(0xE1, "Exif\0\0"u8.ToArray())))),
                "JPEG APP1 and APP2 segments that are not ICC profiles are allowed");
            return Task.CompletedTask;
        });

        await runner.RunAsync("image guard refuses TIFF, BigTIFF and anything outside the allow-list", () =>
        {
            byte[][] tiffs =
            [
                [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0], [0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8],
                BigTiff(0, littleEndian: true), BigTiff(0x00FFFFFFFFFFFFFF, littleEndian: true), BigTiff(0, littleEndian: false),
                "II+\0"u8.ToArray(),
            ];
            foreach (var tiff in tiffs)
                runner.AreEqual(ImageGuardVerdict.Tiff, ImageGuard.Check(tiff), $"TIFF header {Convert.ToHexString(tiff.AsSpan(0, 4))} ({tiff.Length} bytes)");

            byte[][] others =
            [
                [], [0], "<html>not an image</html>"u8.ToArray(), "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(),
                [0, 0, 1, 0, 1, 0], "P6 1 1 255 \0\0\0"u8.ToArray(), [0x89, 0x50, 0x4E, 0x47], "RIFF\0\0\0\0AVI "u8.ToArray(),
            ];
            foreach (var other in others)
                runner.AreEqual(ImageGuardVerdict.Unrecognised, ImageGuard.Check(other), $"unknown format, {other.Length} bytes");
            return Task.CompletedTask;
        });

        await runner.RunAsync("image guard refuses an embedded ICC profile in every container", () =>
        {
            var icc = "ICC_PROFILE\0\x01\x01"u8.ToArray();
            ImageGuardVerdict profile = ImageGuardVerdict.EmbeddedProfile;
            runner.AreEqual(profile, ImageGuard.Check(Png(2, 2, extra: Chunk("iCCP", "p\0\0"u8.ToArray()))), "PNG iCCP before the pixel data");
            runner.AreEqual(profile, ImageGuard.Check(Png(2, 2, trailing: Chunk("iCCP", "p\0\0"u8.ToArray()))), "PNG iCCP after IDAT (decoders read chunks wherever they are)");
            runner.AreEqual(profile, ImageGuard.Check(Jpeg(2, 2, extra: Segment(0xE2, icc))), "JPEG APP2 ICC_PROFILE");
            runner.AreEqual(profile, ImageGuard.Check(Jpeg(2, 2, afterScan: Segment(0xE2, icc))), "JPEG APP2 ICC_PROFILE after the first scan");
            runner.AreEqual(profile, ImageGuard.Check(Jpeg(2, 2, extra: Concat(Segment(0xE1, [1]), [0xFF, 0xFF, 0xFF], Segment(0xE2, icc)))), "JPEG with fill bytes before the marker");
            runner.AreEqual(profile, ImageGuard.Check(WebpFile(Riff("VP8X", Vp8X(2, 2, 0)), Riff("ICCP", [1, 2, 3, 4]), Riff("VP8 ", Vp8(2, 2)))), "WebP ICCP chunk");
            runner.AreEqual(profile, ImageGuard.Check(WebpFile(Riff("VP8X", Vp8X(2, 2, 0x20)), Riff("VP8 ", Vp8(2, 2)))), "WebP VP8X flag claiming a profile");
            runner.AreEqual(profile, ImageGuard.Check(WebpFile(Riff("VP8L", Vp8L(2, 2)), Riff("ICCP", [1]))), "WebP ICCP after the image data");
            runner.AreEqual(profile, ImageGuard.Check(Bmp(2, 2, headerSize: 124, colourSpace: 0x4D424544)), "BMP v5 PROFILE_EMBEDDED");
            runner.AreEqual(profile, ImageGuard.Check(Bmp(2, 2, headerSize: 124, colourSpace: 0x4C494E4B)), "BMP v5 PROFILE_LINKED");
            runner.AreEqual(profile, ImageGuard.Check(Bmp(2, 2, headerSize: 108, colourSpace: 0x4D424544)), "BMP v4 with the same marker");
            runner.AreEqual(ImageGuardVerdict.Allowed, ImageGuard.Check(Bmp(2, 2, headerSize: 124, colourSpace: 0x73524742)), "BMP v5 with sRGB is allowed");
            runner.AreEqual(ImageGuardVerdict.Allowed, ImageGuard.Check(WebpFile(Riff("VP8X", Vp8X(2, 2, 0x08)), Riff("EXIF", [1, 2, 3]), Riff("VP8 ", Vp8(2, 2)))), "WebP with EXIF but no profile is allowed");
            return Task.CompletedTask;
        });

        await runner.RunAsync("image guard refuses zero, overflowing and oversized dimensions", () =>
        {
            (string Name, byte[] Bytes)[] cases =
            [
                ("PNG 0 x 0", Png(0, 0)), ("PNG 1 x 0", Png(1, 0)), ("PNG 65535 x 65535", Png(65535, 65535)),
                ("PNG uint.MaxValue squared", Png(uint.MaxValue, uint.MaxValue)), ("PNG 8193 x 8192", Png(8193, 8192)),
                ("JPEG 65535 x 65535", Jpeg(65535, 65535)), ("JPEG height 0", Jpeg(10, 0)), ("JPEG 8193 x 8192", Jpeg(8193, 8192)),
                ("GIF 65535 x 65535", Gif(65535, 65535)), ("GIF 0 x 5", Gif(0, 5)),
                ("BMP 100000 x 100000", Bmp(100000, 100000)), ("BMP negative width", Bmp(-5, 5)), ("BMP height int.MinValue", Bmp(5, int.MinValue)),
                ("BMP int.MaxValue squared", Bmp(int.MaxValue, int.MaxValue)), ("BMP 0 x 0", Bmp(0, 0)),
                ("WebP VP8X 16777216 squared", WebpFile(Riff("VP8X", Vp8X(16777216, 16777216, 0)), Riff("VP8 ", Vp8(2, 2)))),
                ("WebP VP8L 16384 squared", WebpFile(Riff("VP8L", Vp8L(16384, 16384)))), ("WebP VP8 16383 squared", WebpFile(Riff("VP8 ", Vp8(16383, 16383)))),
                ("WebP frame 16777216 squared", WebpFile(Riff("VP8X", Vp8X(2, 2, 0x02)), Riff("ANMF", Anmf(16777216, 16777216)))),
            ];
            foreach (var (name, bytes) in cases)
                runner.AreEqual(ImageGuardVerdict.BadDimensions, ImageGuard.Check(bytes), $"{name} is refused");
            return Task.CompletedTask;
        });

        await runner.RunAsync("image guard refuses a body above the byte cap", () =>
        {
            var atCap = Png(2, 2, padTo: ImageGuard.MaxBytes);
            runner.AreEqual(ImageGuard.MaxBytes, atCap.Length, "the sample is exactly the cap");
            runner.AreEqual(ImageGuardVerdict.Allowed, ImageGuard.Check(atCap), "a body exactly at the cap is allowed");

            var over = new byte[ImageGuard.MaxBytes + 1];
            Array.Copy(atCap, over, 100);
            runner.AreEqual(ImageGuardVerdict.TooLarge, ImageGuard.Check(over), "one byte over the cap is refused");
            return Task.CompletedTask;
        });

        await runner.RunAsync("image guard never throws on truncated, mutated or random bytes", () =>
        {
            var started = Stopwatch.StartNew();
            byte[][] samples =
            [
                .. Valid().Select(v => v.Bytes),
                Png(2, 2, extra: Chunk("iCCP", "p\0\0"u8.ToArray())), Jpeg(2, 2, extra: Segment(0xE2, "ICC_PROFILE\0\x01\x01"u8.ToArray())),
                WebpFile(Riff("VP8X", Vp8X(2, 2, 0x22)), Riff("ICCP", [1, 2]), Riff("VP8 ", Vp8(2, 2))),
                Bmp(2, 2, headerSize: 124, colourSpace: 0x4D424544), BigTiff(5, littleEndian: true),
            ];

            var random = new Random(11);
            var checks = 0;
            var thrown = 0;
            void Probe(byte[] bytes)
            {
                checks++;
                try { ImageGuard.Check(bytes); }
                catch (Exception) { thrown++; }
            }

            foreach (var sample in samples)
            {
                for (var length = 0; length <= sample.Length; length++) Probe(sample[..length]); // a truncated download
                for (var position = 0; position < sample.Length; position++)
                {
                    var mutated = (byte[])sample.Clone();
                    mutated[position] = (byte)random.Next(256);
                    Probe(mutated);
                }

                for (var attempt = 0; attempt < 100; attempt++)
                {
                    var noise = new byte[random.Next(0, 300)];
                    random.NextBytes(noise); // random bytes behind a real signature
                    sample.AsSpan(0, Math.Min(random.Next(0, 33), Math.Min(sample.Length, noise.Length))).CopyTo(noise);
                    Probe(noise);
                }
            }

            runner.AreEqual(0, thrown, $"{checks} truncated, mutated and random bodies produced no exception");
            runner.IsTrue(started.Elapsed < Patience, $"and were decided in {started.ElapsedMilliseconds} ms");
            return Task.CompletedTask;
        });

        await runner.RunAsync("image guard bounds hostile chunk lengths and chunk counts", () =>
        {
            var started = Stopwatch.StartNew();
            var malformed = ImageGuardVerdict.Malformed;
            runner.AreEqual(malformed, ImageGuard.Check(Png(2, 2, extra: Chunk("tEXt", [1], declaredLength: 0x7FFFFFFF))), "PNG chunk length past the buffer");
            runner.AreEqual(malformed, ImageGuard.Check(Png(2, 2, extra: Chunk("iCCP", [1], declaredLength: 0xFFFFFFFF))), "PNG chunk length 0xFFFFFFFF");

            // Millions of empty chunks or segments in a body that is within the byte cap.
            runner.AreEqual(malformed, ImageGuard.Check(Repeat(Png(2, 2, noEnd: true), Chunk("tEXt", []))), "PNG chunk count far beyond the bound");
            runner.AreEqual(malformed, ImageGuard.Check(Repeat(Jpeg(2, 2, noEnd: true), Segment(0xFE, []))), "JPEG segment count far beyond the bound");
            runner.AreEqual(malformed, ImageGuard.Check(Repeat(WebpFile(Riff("VP8 ", Vp8(2, 2))), Riff("JUNK", []))), "WebP chunk count far beyond the bound");

            var fill = Repeat([0xFF, 0xD8, 0xFF], [0xFF]);
            runner.AreEqual(malformed, ImageGuard.Check(fill), "JPEG that is all 0xFF fill bytes");
            var stuffed = Repeat(Concat(Jpeg(2, 2, noEnd: true), [0xFF, 0xDA, 0x00, 0x02]), [0xFF, 0x00]);
            runner.AreEqual(ImageGuardVerdict.Allowed, ImageGuard.Check(stuffed), "a scan of stuffed bytes up to the cap is read in one pass");

            runner.IsTrue(started.Elapsed < Patience, $"every hostile body was decided in {started.ElapsedMilliseconds} ms");
            return Task.CompletedTask;
        });
    }

    private static (string Name, byte[] Bytes)[] Valid() =>
    [
        ("PNG", Png(4, 4)), ("PNG with sRGB and text", Png(4, 4, extra: Concat(Chunk("sRGB", [0]), Chunk("tEXt", "a\0b"u8.ToArray())))),
        ("JPEG", Jpeg(4, 4)), ("JPEG with restart markers", Jpeg(4, 4, scan: [0x12, 0xFF, 0x00, 0xFF, 0xD0, 0x34])),
        ("GIF87a", Gif(4, 4, "GIF87a")), ("GIF89a", Gif(4, 4)),
        ("BMP", Bmp(4, 4)), ("BMP top-down", Bmp(4, -4)), ("BMP core header", Bmp(4, 4, headerSize: 12)),
        ("WebP lossy", WebpFile(Riff("VP8 ", Vp8(4, 4)))), ("WebP lossless", WebpFile(Riff("VP8L", Vp8L(4, 4)))),
        ("WebP extended", WebpFile(Riff("VP8X", Vp8X(4, 4, 0x10)), Riff("ALPH", [0, 1, 2]), Riff("VP8 ", Vp8(4, 4)))),
        ("WebP animated", WebpFile(Riff("VP8X", Vp8X(4, 4, 0x02)), Riff("ANIM", [0, 0, 0, 0, 0, 0]), Riff("ANMF", Anmf(4, 4)))),
    ];

    /// <summary>The prefix followed by as many copies of the unit as fit in <see cref="ImageGuard.MaxBytes"/>.</summary>
    private static byte[] Repeat(byte[] prefix, byte[] unit)
    {
        var data = new byte[prefix.Length + (ImageGuard.MaxBytes - prefix.Length) / unit.Length * unit.Length];
        prefix.CopyTo(data, 0);
        for (var offset = prefix.Length; offset < data.Length; offset += unit.Length) unit.CopyTo(data, offset);
        return data;
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    private static byte[] U32(uint value, bool bigEndian = true)
    {
        var bytes = new byte[4];
        if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        else BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Chunk(string type, byte[] data, uint? declaredLength = null) =>
        Concat(U32(declaredLength ?? (uint)data.Length), Encoding.ASCII.GetBytes(type), data, [0, 0, 0, 0]);

    private static byte[] Png(uint width, uint height, byte[]? extra = null, byte[]? trailing = null, bool noEnd = false, int padTo = 0)
    {
        byte[] signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        var header = Chunk("IHDR", Concat(U32(width), U32(height), [8, 2, 0, 0, 0]));
        var idat = Chunk("IDAT", [0x78, 0x9C, 0, 0, 0, 0]);
        var data = Concat(signature, header, extra ?? [], idat, trailing ?? [], noEnd ? [] : Chunk("IEND", []));
        return padTo <= data.Length ? data
            : Concat(signature, header, Chunk("IDAT", new byte[padTo - data.Length + 6]), Chunk("IEND", [])); // one IDAT sized to fit
    }

    private static byte[] Segment(byte marker, byte[] payload) => Concat([0xFF, marker, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2)], payload);

    private static byte[] Jpeg(int width, int height, byte[]? extra = null, byte[]? afterScan = null, byte[]? scan = null, bool noEnd = false) =>
        Concat([0xFF, 0xD8], Segment(0xE0, "JFIF\0\x01\x01\0\0\x01\0\x01\0\0"u8.ToArray()), extra ?? [],
            Segment(0xC0, [8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0]),
            Segment(0xDA, [1, 1, 0, 0, 0x3F, 0]), scan ?? [0x12, 0x34], afterScan ?? [], noEnd ? [] : [0xFF, 0xD9]);

    private static byte[] Gif(int width, int height, string signature = "GIF89a") =>
        Concat(Encoding.ASCII.GetBytes(signature), [(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0, 0], [0x3B]);

    private static byte[] Bmp(int width, int height, int headerSize = 40, uint colourSpace = 0)
    {
        var dib = new byte[headerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(dib, (uint)headerSize);
        if (headerSize == 12)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(4), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(6), (ushort)height);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), width);
            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), height);
            if (headerSize >= 108) BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(56), colourSpace);
        }

        var offset = U32(14 + (uint)headerSize, bigEndian: false);
        return Concat("BM"u8.ToArray(), offset, [0, 0, 0, 0], offset, dib);
    }

    // The 32-byte file that hangs ImageSharp 3.1.12's TIFF decoder when the entry count is huge.
    private static byte[] BigTiff(ulong entryCount, bool littleEndian)
    {
        var count = BitConverter.GetBytes(entryCount);
        var firstIfd = BitConverter.GetBytes(16UL);
        if (!littleEndian) { Array.Reverse(count); Array.Reverse(firstIfd); }
        return littleEndian
            ? Concat("II+\0"u8.ToArray(), [8, 0, 0, 0], firstIfd, count, firstIfd)
            : Concat("MM\0+"u8.ToArray(), [0, 8, 0, 0], firstIfd, count, firstIfd);
    }

    private static byte[] Riff(string type, byte[] data) =>
        Concat(Encoding.ASCII.GetBytes(type), U32((uint)data.Length, bigEndian: false), data, data.Length % 2 == 1 ? [0] : []);

    private static byte[] WebpFile(params byte[][] chunks)
    {
        var body = Concat(chunks);
        return Concat("RIFF"u8.ToArray(), U32((uint)body.Length + 4, bigEndian: false), "WEBP"u8.ToArray(), body);
    }

    private static byte[] Vp8(int width, int height) =>
        [0x10, 0x02, 0x00, 0x9D, 0x01, 0x2A, (byte)width, (byte)(width >> 8 & 0x3F), (byte)height, (byte)(height >> 8 & 0x3F), 0, 0];

    private static byte[] Vp8L(int width, int height) =>
        Concat([0x2F], U32((uint)(width - 1) | (uint)(height - 1) << 14, bigEndian: false), [0]);

    private static byte[] Vp8X(long width, long height, byte flags) =>
        [flags, 0, 0, 0, (byte)(width - 1), (byte)(width - 1 >> 8), (byte)(width - 1 >> 16), (byte)(height - 1), (byte)(height - 1 >> 8), (byte)(height - 1 >> 16)];

    private static byte[] Anmf(long width, long height) =>
        Concat([0, 0, 0, 0, 0, 0, (byte)(width - 1), (byte)(width - 1 >> 8), (byte)(width - 1 >> 16), (byte)(height - 1), (byte)(height - 1 >> 8), (byte)(height - 1 >> 16), 0, 0, 0, 0],
            Riff("VP8 ", Vp8(2, 2)));
}
