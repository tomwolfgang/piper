using System.Buffers.Binary;

namespace Piper.App.Controls;

/// <summary>What <see cref="ImageGuard.Check"/> decided about a captured body.</summary>
internal enum ImageGuardVerdict
{
    Allowed,

    /// <summary>Not a format the inspector decodes (an error page labelled image/*, SVG, ICO, ...).</summary>
    Unrecognised,

    /// <summary>TIFF or BigTIFF.</summary>
    Tiff,

    /// <summary>The container carries or links an ICC colour profile.</summary>
    EmbeddedProfile,

    /// <summary>Longer than <see cref="ImageGuard.MaxBytes"/>.</summary>
    TooLarge,

    /// <summary>The header declares no pixels, or more than <see cref="ImageGuard.MaxPixels"/>.</summary>
    BadDimensions,

    /// <summary>Truncated, inconsistent or too finely chunked to walk within the bounds.</summary>
    Malformed,
}

/// <summary>
/// Decides from the raw bytes alone, without calling the image library, whether a captured HTTP body
/// may be decoded. The body is hostile, and the pinned SixLabors.ImageSharp 3.1.12 has advisories a
/// crafted file reaches (a BigTIFF IFD count that keeps the decoder spinning, an ICC profile whose
/// CLUT allocates from unvalidated dimensions). 4.x fixes them but needs a licence key at build time,
/// so until that is decided this refuses what reaches them: TIFF and anything outside the allow-list
/// (PNG, JPEG, GIF, BMP, WebP), any embedded ICC profile, and out-of-bounds sizes. The ICC refusal is
/// conservative: 3.1.12 parses a profile's tags only if a caller reads <c>IccProfile.Entries</c>, which
/// Piper does not.
/// </summary>
/// <remarks>
/// Only container headers are read, never pixel data, and every walk is bounded by
/// <see cref="MaxBytes"/> and <see cref="MaxSegments"/>. Truncated or hostile input yields a verdict,
/// never an exception. Walks run to the end of the container, not to the first image data, because
/// decoders take metadata chunks wherever they find them.
/// </remarks>
internal static class ImageGuard
{
    /// <summary>The longest body that may be decoded (32 MiB).</summary>
    public const int MaxBytes = 32 * 1024 * 1024;

    /// <summary>The most pixels a header may declare (64 Mi, so 8192 x 8192 fits).</summary>
    public const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>The most chunks or marker segments one walk visits.</summary>
    public const int MaxSegments = 65_536;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageGuardVerdict Check(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaxBytes) return ImageGuardVerdict.TooLarge;

        if (data.StartsWith(PngSignature)) return CheckPng(data);
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return CheckJpeg(data);
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return CheckGif(data);
        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M') return CheckBmp(data);
        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
            return WalkWebpChunks(data[12..], depth: 0);

        // II*\0, MM\0*, and BigTIFF II+\0, MM\0+.
        return data.StartsWith("II*\0"u8) || data.StartsWith("MM\0*"u8) || data.StartsWith("II+\0"u8) || data.StartsWith("MM\0+"u8)
            ? ImageGuardVerdict.Tiff
            : ImageGuardVerdict.Unrecognised;
    }

    private static ImageGuardVerdict CheckDimensions(long width, long height) =>
        width <= 0 || height <= 0 || width > MaxPixels || height > MaxPixels || width * height > MaxPixels
            ? ImageGuardVerdict.BadDimensions
            : ImageGuardVerdict.Allowed;

    // PNG: length (4, big-endian), type (4), data, CRC (4). Only the headers are read.
    private static ImageGuardVerdict CheckPng(ReadOnlySpan<byte> data)
    {
        var position = 8;
        var sawHeader = false;
        for (var count = 0; position < data.Length; count++)
        {
            if (count >= MaxSegments || data.Length - position < 12) return ImageGuardVerdict.Malformed;

            var length = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
            var type = data.Slice(position + 4, 4);
            var end = (long)position + 12 + length;
            if (end > data.Length) return ImageGuardVerdict.Malformed;

            if (!sawHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13) return ImageGuardVerdict.Malformed;
                var header = data.Slice(position + 8, 13);
                var verdict = CheckDimensions(BinaryPrimitives.ReadUInt32BigEndian(header),
                    BinaryPrimitives.ReadUInt32BigEndian(header[4..]));
                if (verdict != ImageGuardVerdict.Allowed) return verdict;
                sawHeader = true;
            }
            else if (type.SequenceEqual("iCCP"u8))
            {
                return ImageGuardVerdict.EmbeddedProfile;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                return ImageGuardVerdict.Allowed;
            }

            position = (int)end;
        }

        return sawHeader ? ImageGuardVerdict.Allowed : ImageGuardVerdict.Malformed;
    }

    private static ImageGuardVerdict CheckJpeg(ReadOnlySpan<byte> data)
    {
        var position = 2;
        var sawFrame = false;
        for (var count = 0; count < MaxSegments; count++)
        {
            // Where the data ends is fine once a frame header has been seen (a truncated download).
            var atEnd = sawFrame ? ImageGuardVerdict.Allowed : ImageGuardVerdict.Malformed;

            // Between segments: optional 0xFF fill bytes, then the marker code.
            if (position >= data.Length) return atEnd;
            if (data[position] != 0xFF) return ImageGuardVerdict.Malformed;
            while (position < data.Length && data[position] == 0xFF) position++;
            if (position >= data.Length) return atEnd;

            var marker = data[position++];
            if (marker == 0xD9) return atEnd;
            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD8)) continue; // no length field

            if (data.Length - position < 2) return ImageGuardVerdict.Malformed;
            var length = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
            if (length < 2 || length > data.Length - position) return ImageGuardVerdict.Malformed;
            var segment = data.Slice(position + 2, length - 2);
            position += length;

            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                // precision (1), height (2), width (2). A zero height would come from a DNL
                // segment, which the decoder does not support either.
                if (segment.Length < 5) return ImageGuardVerdict.Malformed;
                var verdict = CheckDimensions(BinaryPrimitives.ReadUInt16BigEndian(segment[3..]),
                    BinaryPrimitives.ReadUInt16BigEndian(segment[1..]));
                if (verdict != ImageGuardVerdict.Allowed) return verdict;
                sawFrame = true;
            }
            else if (marker == 0xE2 && segment.StartsWith("ICC_PROFILE\0"u8))
            {
                return ImageGuardVerdict.EmbeddedProfile;
            }
            else if (marker == 0xDA)
            {
                position = SkipEntropyCodedData(data, position);
            }
        }

        return ImageGuardVerdict.Malformed;
    }

    /// <summary>Moves to the next real marker after a scan; 0xFF 0x00 (stuffing) and 0xFF 0xD0-0xD7
    /// (restart) are still inside it.</summary>
    private static int SkipEntropyCodedData(ReadOnlySpan<byte> data, int position)
    {
        while (position < data.Length)
        {
            var offset = data[position..].IndexOf((byte)0xFF);
            if (offset < 0 || position + offset + 1 >= data.Length) return data.Length;
            position += offset;

            var next = data[position + 1];
            if (next == 0xFF) position++;
            else if (next == 0x00 || next is >= 0xD0 and <= 0xD7) position += 2;
            else return position;
        }

        return data.Length;
    }

    // GIF: the logical screen descriptor follows the signature, width then height, little-endian.
    private static ImageGuardVerdict CheckGif(ReadOnlySpan<byte> data) =>
        data.Length < 13
            ? ImageGuardVerdict.Malformed
            : CheckDimensions(BinaryPrimitives.ReadUInt16LittleEndian(data[6..]), BinaryPrimitives.ReadUInt16LittleEndian(data[8..]));

    private const uint ProfileEmbedded = 0x4D424544; // 'MBED'
    private const uint ProfileLinked = 0x4C494E4B;   // 'LINK'

    // BMP: a 14-byte file header, then a DIB header whose first field is its own size.
    private static ImageGuardVerdict CheckBmp(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18) return ImageGuardVerdict.Malformed;
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data[14..]);
        if (headerSize is not (12 or 40 or 52 or 56 or 64 or 108 or 124) || data.Length < 14 + headerSize)
            return ImageGuardVerdict.Malformed;

        var core = headerSize == 12;
        long width = core ? BinaryPrimitives.ReadUInt16LittleEndian(data[18..]) : BinaryPrimitives.ReadInt32LittleEndian(data[18..]);
        long height = core ? BinaryPrimitives.ReadUInt16LittleEndian(data[20..])
            : Math.Abs((long)BinaryPrimitives.ReadInt32LittleEndian(data[22..])); // negative: top-down

        var verdict = CheckDimensions(width, height);
        if (verdict != ImageGuardVerdict.Allowed) return verdict;

        // bV4CSType / bV5CSType, 56 bytes into a V4 or V5 header.
        return headerSize >= 108 && BinaryPrimitives.ReadUInt32LittleEndian(data[(14 + 56)..]) is ProfileEmbedded or ProfileLinked
            ? ImageGuardVerdict.EmbeddedProfile
            : ImageGuardVerdict.Allowed;
    }

    // WebP: RIFF chunks of fourcc (4), size (4, little-endian), data, padded to even. The RIFF size
    // field is not trusted; the walk is bounded by the buffer.
    private static ImageGuardVerdict WalkWebpChunks(ReadOnlySpan<byte> chunks, int depth)
    {
        var position = 0;
        var sawImage = false;
        for (var count = 0; position < chunks.Length; count++)
        {
            if (count >= MaxSegments || chunks.Length - position < 8) return ImageGuardVerdict.Malformed;

            var type = chunks.Slice(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunks[(position + 4)..]);
            if (size > chunks.Length - position - 8) return ImageGuardVerdict.Malformed;
            var body = chunks.Slice(position + 8, (int)size);
            position += 8 + (int)size + (int)(size & 1);

            var verdict = ImageGuardVerdict.Allowed;
            if (type.SequenceEqual("ICCP"u8))
            {
                return ImageGuardVerdict.EmbeddedProfile;
            }
            else if (type.SequenceEqual("VP8X"u8))
            {
                // flags (1; 0x20 is ICC), reserved (3), canvas width - 1 (3), canvas height - 1 (3).
                if (body.Length < 10) return ImageGuardVerdict.Malformed;
                if ((body[0] & 0x20) != 0) return ImageGuardVerdict.EmbeddedProfile;
                verdict = CheckDimensions(Read24(body[4..]) + 1L, Read24(body[7..]) + 1L);
            }
            else if (type.SequenceEqual("VP8 "u8))
            {
                // frame tag (3), start code 9D 01 2A, then 14-bit width and height.
                if (body.Length < 10 || body[3] != 0x9D || body[4] != 0x01 || body[5] != 0x2A) return ImageGuardVerdict.Malformed;
                verdict = CheckDimensions(BinaryPrimitives.ReadUInt16LittleEndian(body[6..]) & 0x3FFF,
                    BinaryPrimitives.ReadUInt16LittleEndian(body[8..]) & 0x3FFF);
                sawImage = true;
            }
            else if (type.SequenceEqual("VP8L"u8))
            {
                // signature 0x2F, then width - 1 and height - 1 as 14 bits each.
                if (body.Length < 5 || body[0] != 0x2F) return ImageGuardVerdict.Malformed;
                var bits = BinaryPrimitives.ReadUInt32LittleEndian(body[1..]);
                verdict = CheckDimensions((bits & 0x3FFF) + 1L, ((bits >> 14) & 0x3FFF) + 1L);
                sawImage = true;
            }
            else if (type.SequenceEqual("ANMF"u8))
            {
                // X (3), Y (3), width - 1 (3), height - 1 (3), duration (3), flags (1), then the
                // frame's own chunks, walked like the file's.
                if (depth > 0 || body.Length < 16) return ImageGuardVerdict.Malformed;
                verdict = CheckDimensions(Read24(body[6..]) + 1L, Read24(body[9..]) + 1L);
                if (verdict == ImageGuardVerdict.Allowed) verdict = WalkWebpChunks(body[16..], depth + 1);
                sawImage = true;
            }

            if (verdict != ImageGuardVerdict.Allowed) return verdict;
        }

        // A file with only metadata has nothing to show; a frame may be empty.
        return sawImage || depth > 0 ? ImageGuardVerdict.Allowed : ImageGuardVerdict.Malformed;
    }

    private static int Read24(ReadOnlySpan<byte> bytes) => bytes[0] | bytes[1] << 8 | bytes[2] << 16;
}
