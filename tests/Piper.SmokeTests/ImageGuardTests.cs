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
        // Every body in a table gets the same verdict; the name says what the body is.
        void Expect(ImageGuardVerdict verdict, params (string Name, byte[] Bytes)[] bodies)
        {
            foreach (var (name, bytes) in bodies)
                runner.AreEqual(verdict, ImageGuard.Check(bytes), name);
        }

        await runner.RunAsync("image guard allows small PNG, JPEG, GIF, BMP and WebP", () =>
        {
            Expect(ImageGuardVerdict.Allowed, [.. Valid(),
                ("PNG 4096 x 4096 (exactly 16 Mi pixels)", Png(4096, 4096)),
                ("JPEG APP1 and APP2 that are not ICC", Jpeg(1, 1, extra: Concat(Segment(0xE2, "MPF\0"u8.ToArray()), Segment(0xE1, "Exif\0\0"u8.ToArray())))),
                ("a progressive JPEG with the most scans", JpegScans(ImageGuard.MaxScans)),
                ("BMP v5 with sRGB and no profile", Bmp(2, 2, headerSize: 124, colourSpace: 0x73524742)),
                ("BMP v4 with sRGB (no profile fields)", Bmp(8, 8, headerSize: 108, colourSpace: 0x42475273)),
                ("WebP with EXIF but no profile", WebpFile(Riff("VP8X", Vp8X(2, 2, 0x08)), Riff("EXIF", [1, 2, 3]), Riff("VP8 ", Vp8(2, 2))))]);
            return Task.CompletedTask;
        });

        await runner.RunAsync("image guard refuses TIFF, BigTIFF and anything outside the allow-list", () =>
        {
            Expect(ImageGuardVerdict.Tiff,
                ("II*", [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0]), ("MM*", [0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 8]),
                ("BigTIFF little-endian", BigTiff(0, littleEndian: true)), ("BigTIFF big-endian", BigTiff(0, littleEndian: false)),
                ("BigTIFF with an IFD count that never ends (hangs 3.1.12)", BigTiff(0x00FFFFFFFFFFFFFF, littleEndian: true)),
                ("BigTIFF signature only", "II+\0"u8.ToArray()));
            Expect(ImageGuardVerdict.Unrecognised,
                ("empty", []), ("one byte", [0]), ("HTML", "<html>not an image</html>"u8.ToArray()), ("SVG", "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()),
                ("ICO", [0, 0, 1, 0, 1, 0]), ("PBM", "P6 1 1 255 \0\0\0"u8.ToArray()), ("a PNG signature cut short", [0x89, 0x50, 0x4E, 0x47]), ("RIFF AVI", "RIFF\0\0\0\0AVI "u8.ToArray()));
            return Task.CompletedTask;
        });

        await runner.RunAsync("image guard refuses an embedded ICC profile in every container", () =>
        {
            var icc = "ICC_PROFILE\0\x01\x01"u8.ToArray();
            var gib = 1u << 30;
            Expect(ImageGuardVerdict.EmbeddedProfile,
                ("PNG iCCP", Png(2, 2, extra: Chunk("iCCP", "p\0\0"u8.ToArray()))),
                ("PNG iCCP after IDAT (decoders read chunks wherever they are)", Png(2, 2, trailing: Chunk("iCCP", "p\0\0"u8.ToArray()))),
                ("JPEG APP2 ICC_PROFILE", Jpeg(2, 2, extra: Segment(0xE2, icc))),
                ("JPEG APP2 ICC_PROFILE after the first scan", Jpeg(2, 2, afterScan: Segment(0xE2, icc))),
                ("JPEG with fill bytes before the marker", Jpeg(2, 2, extra: Concat(Segment(0xE1, [1]), [0xFF, 0xFF, 0xFF], Segment(0xE2, icc)))),
                ("WebP ICCP chunk", WebpFile(Riff("VP8X", Vp8X(2, 2, 0)), Riff("ICCP", [1, 2, 3, 4]), Riff("VP8 ", Vp8(2, 2)))),
                ("WebP VP8X flag claiming a profile", WebpFile(Riff("VP8X", Vp8X(2, 2, 0x20)), Riff("VP8 ", Vp8(2, 2)))),
                ("WebP ICCP after the image data", WebpFile(Riff("VP8L", Vp8L(2, 2)), Riff("ICCP", [1]))),
                ("BMP v5 PROFILE_EMBEDDED", Bmp(2, 2, headerSize: 124, colourSpace: 0x4D424544)),
                ("BMP v5 PROFILE_LINKED", Bmp(2, 2, headerSize: 124, colourSpace: 0x4C494E4B)),
                ("BMP v4 with the same marker", Bmp(2, 2, headerSize: 108, colourSpace: 0x4D424544)));

            // 3.1.12 keeps a V5 profile whenever ProfileData and ProfileSize are set, whatever the colour
            // space says, and reads ProfileSize bytes into a plain array: a 330-byte file asked for 1 GiB.
            runner.AreEqual(330, ReviewerBmp(0x42475273, 0x7C, gib).Length, "the reproducer is 330 bytes");
            Expect(ImageGuardVerdict.EmbeddedProfile,
                ("BMP v5 1 GiB profile, colour space sRGB", ReviewerBmp(0x42475273, 0x7C, gib)),
                ("BMP v5 1 GiB profile, colour space zero", ReviewerBmp(0, 0x7C, gib)),
                ("BMP v5 1 GiB profile, colour space 'Win '", ReviewerBmp(0x57696E20, 0x7C, gib)),
                ("BMP v5 1-byte profile", ReviewerBmp(0x42475273, 0x7C, 1)), ("BMP v5 4 GiB profile", ReviewerBmp(0x42475273, 0x7C, 0xFFFFFFFF)),
                ("BMP v5 profile offset but no size", ReviewerBmp(0x42475273, 0x7C, 0)), ("BMP v5 size but no offset", ReviewerBmp(0x42475273, 0, 64)));
            Expect(ImageGuardVerdict.Allowed, ("BMP v5 with neither offset nor size", ReviewerBmp(0x42475273, 0, 0)));
            return Task.CompletedTask;
        });

        await runner.RunAsync("image guard refuses zero, overflowing and oversized dimensions", () =>
        {
            Expect(ImageGuardVerdict.BadDimensions,
                ("PNG 0 x 0", Png(0, 0)), ("PNG 1 x 0", Png(1, 0)), ("PNG 65535 squared", Png(65535, 65535)), ("PNG uint.MaxValue squared", Png(uint.MaxValue, uint.MaxValue)),
                ("PNG 4097 x 4096", Png(4097, 4096)), ("PNG 8192 squared", Png(8192, 8192)),
                ("JPEG 65535 squared", Jpeg(65535, 65535)), ("JPEG height 0", Jpeg(10, 0)), ("JPEG 4097 x 4096", Jpeg(4097, 4096)),
                ("GIF 65535 squared", Gif(65535, 65535)), ("GIF 0 x 5", Gif(0, 5)),
                ("BMP 100000 squared", Bmp(100000, 100000)), ("BMP negative width", Bmp(-5, 5)), ("BMP height int.MinValue", Bmp(5, int.MinValue)),
                ("BMP int.MaxValue squared", Bmp(int.MaxValue, int.MaxValue)), ("BMP 0 x 0", Bmp(0, 0)),
                ("WebP VP8X 16777216 squared", WebpFile(Riff("VP8X", Vp8X(16777216, 16777216, 0)), Riff("VP8 ", Vp8(2, 2)))),
                ("WebP VP8L 16384 squared", WebpFile(Riff("VP8L", Vp8L(16384, 16384)))), ("WebP VP8 16383 squared", WebpFile(Riff("VP8 ", Vp8(16383, 16383)))),
                ("WebP frame 16777216 squared", WebpFile(Riff("VP8X", Vp8X(2, 2, 0x02)), Riff("ANMF", Anmf(16777216, 16777216)))));
            return Task.CompletedTask;
        });

        await runner.RunAsync("image guard refuses a body above the byte cap and a JPEG with too many scans", () =>
        {
            var atCap = Png(2, 2, padTo: ImageGuard.MaxBytes);
            runner.AreEqual(ImageGuard.MaxBytes, atCap.Length, "the sample is exactly the cap");
            var over = new byte[ImageGuard.MaxBytes + 1];
            Array.Copy(atCap, over, 100);
            Expect(ImageGuardVerdict.Allowed, ("a body exactly at the cap", atCap));
            Expect(ImageGuardVerdict.TooLarge, ("one byte over the cap", over));

            // Each scan costs the decoder about a millisecond at 8192 x 8192: 65,000 of them took 78 s.
            var started = Stopwatch.StartNew();
            Expect(ImageGuardVerdict.Malformed, ("one scan more than the cap", JpegScans(ImageGuard.MaxScans + 1)), ("65,000 scans in about 0.5 MB", JpegScans(65_000)));
            runner.IsTrue(started.Elapsed < Patience, $"decided in {started.ElapsedMilliseconds} ms");
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
                Bmp(2, 2, headerSize: 124, colourSpace: 0x4D424544), ReviewerBmp(0x42475273, 0x7C, 1u << 30), JpegScans(3), BigTiff(5, littleEndian: true),
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
            Expect(ImageGuardVerdict.Malformed,
                ("PNG chunk length past the buffer", Png(2, 2, extra: Chunk("tEXt", [1], declaredLength: 0x7FFFFFFF))),
                ("PNG chunk length 0xFFFFFFFF", Png(2, 2, extra: Chunk("iCCP", [1], declaredLength: 0xFFFFFFFF))),
                // Millions of empty chunks or segments in a body that is within the byte cap.
                ("PNG chunk count far beyond the bound", Repeat(Png(2, 2, noEnd: true), Chunk("tEXt", []))),
                ("JPEG segment count far beyond the bound", Repeat(Jpeg(2, 2, noEnd: true), Segment(0xFE, []))),
                ("WebP chunk count far beyond the bound", Repeat(WebpFile(Riff("VP8 ", Vp8(2, 2))), Riff("JUNK", []))),
                ("JPEG that is all 0xFF fill bytes", Repeat([0xFF, 0xD8, 0xFF], [0xFF])));
            Expect(ImageGuardVerdict.Allowed,
                ("a scan of stuffed bytes up to the cap, read in one pass", Repeat(Concat(Jpeg(2, 2, noEnd: true), [0xFF, 0xDA, 0x00, 0x02]), [0xFF, 0x00])));
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

    private static byte[] Repeat(byte[] prefix, byte[] unit, int count) => Concat(prefix, [.. Enumerable.Repeat(unit, count).SelectMany(u => u)]);

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

    // A progressive-style JPEG whose scans are empty: only the markers matter to the guard.
    private static byte[] JpegScans(int scans) =>
        Concat(Repeat(Jpeg(2, 2, noEnd: true), Concat(Segment(0xDA, [1, 1, 0, 0, 0x3F, 0]), [0x12]), scans - 1), [0xFF, 0xD9]);

    // The 330-byte file that made 3.1.12 allocate the declared profile size: a V5 8 x 8, 24 bpp bitmap
    // with the given colour space, profile offset and profile size, and 192 zero pixel bytes.
    private static byte[] ReviewerBmp(uint colourSpace, uint profileData, uint profileSize)
    {
        var file = new byte[330];
        file[0] = 0x42; file[1] = 0x4D;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(2), 330);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(10), 138);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(14), 124);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(18), 8);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(22), 8);
        file[26] = 1; file[28] = 24;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x46), colourSpace);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x7E), profileData);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x82), profileSize);
        return file;
    }

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
