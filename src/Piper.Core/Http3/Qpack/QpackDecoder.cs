using System.Text;
using Piper.Core.Http;

namespace Piper.Core.Http3.Qpack;

/// <summary>
/// QPACK decoder (RFC 9204). Handles every representation that can appear when this side has
/// advertised <c>QPACK_MAX_TABLE_CAPACITY = 0</c>: indexed and literal field lines against the
/// static table, with or without Huffman coding.
/// </summary>
/// <remarks>
/// Because we advertise a zero-capacity dynamic table, a conformant peer cannot reference one --
/// so a dynamic reference arriving here means the peer violated our SETTINGS, and is reported
/// rather than guessed at. That is the whole reason the zero-capacity choice is safe: it converts
/// an entire class of state-synchronisation bugs into a single explicit error.
/// </remarks>
public static class QpackDecoder
{
    /// <summary>Per-field overhead in the size of a field section (RFC 9114 §4.2): name length plus
    /// value length plus 32.</summary>
    private const long FieldOverhead = 32;

    public static List<(string Name, string Value)> Decode(ReadOnlySpan<byte> block) => Decode(block, long.MaxValue);

    /// <summary>
    /// Decodes a field section and refuses one whose decoded size passes
    /// <paramref name="maxFieldSectionSize"/> -- the figure SETTINGS_MAX_FIELD_SECTION_SIZE is
    /// defined in -- with an <see cref="Http3ProtocolException"/> (H3_EXCESSIVE_LOAD), so the
    /// connection is closed with that code. The encoded size is no bound on it: one byte can stand
    /// for a static-table entry of dozens, so a small block can decode to a very large list, and the
    /// list is cut off here, while it is being built, instead of after it has been.
    /// </summary>
    public static List<(string Name, string Value)> Decode(ReadOnlySpan<byte> block, long maxFieldSectionSize)
    {
        var position = 0;
        long sectionSize = 0;

        // Field section prefix (§4.5.1).
        var requiredInsertCount = PrefixInteger.Read(block, ref position, 8);
        if (requiredInsertCount != 0)
            throw new HttpParseException(
                $"QPACK field section requires dynamic table state (insert count {requiredInsertCount}) after we advertised a zero-capacity table.");
        PrefixInteger.Read(block, ref position, 7); // Delta Base; irrelevant with no dynamic table

        var fields = new List<(string Name, string Value)>();

        while (position < block.Length)
        {
            var first = block[position];

            if ((first & 0x80) != 0)
            {
                // Indexed Field Line (§4.5.2): '1' T Index(6+)
                var isStatic = (first & 0x40) != 0;
                var index = ReadInt(block, ref position, 6);
                if (!isStatic) throw new HttpParseException("QPACK indexed field line references the dynamic table.");
                fields.Add(QpackStaticTable.Get(index));
                Account(fields[^1], ref sectionSize, maxFieldSectionSize);
            }
            else if ((first & 0x40) != 0)
            {
                // Literal with Name Reference (§4.5.4): '01' N T NameIndex(4+)
                var isStatic = (first & 0x10) != 0;
                var nameIndex = ReadInt(block, ref position, 4);
                if (!isStatic) throw new HttpParseException("QPACK literal field line references a dynamic table name.");
                var name = QpackStaticTable.Get(nameIndex).Name;
                var value = ReadString(block, ref position, prefixBits: 7);
                fields.Add((name, value));
                Account(fields[^1], ref sectionSize, maxFieldSectionSize);
            }
            else if ((first & 0x20) != 0)
            {
                // Literal with Literal Name (§4.5.6): '001' N H NameLen(3+)
                var name = ReadString(block, ref position, prefixBits: 3);
                var value = ReadString(block, ref position, prefixBits: 7);
                fields.Add((name, value));
                Account(fields[^1], ref sectionSize, maxFieldSectionSize);
            }
            else if ((first & 0x10) != 0)
            {
                // Indexed with Post-Base Index (§4.5.3) -- dynamic table only.
                throw new HttpParseException("QPACK post-base indexed field line requires a dynamic table.");
            }
            else
            {
                // Literal with Post-Base Name Reference (§4.5.5) -- dynamic table only.
                throw new HttpParseException("QPACK post-base name reference requires a dynamic table.");
            }
        }

        return fields;
    }

    private static void Account((string Name, string Value) field, ref long sectionSize, long limit)
    {
        sectionSize += field.Name.Length + field.Value.Length + FieldOverhead;
        if (sectionSize > limit)
            throw new Http3ProtocolException(Http3ErrorCode.ExcessiveLoad,
                $"HTTP/3 field section exceeds the {limit}-byte limit once decoded.");
    }

    /// <summary>A prefix integer used as an index or a length. A value that does not fit an
    /// <see cref="int"/> is refused: a plain cast would wrap 2^32 + 2 to the index 2 and decode it
    /// as a different field than the one the peer named.</summary>
    private static int ReadInt(ReadOnlySpan<byte> block, ref int position, int prefixBits)
    {
        var value = PrefixInteger.Read(block, ref position, prefixBits);
        if (value is < 0 or > int.MaxValue) throw new HttpParseException("QPACK integer is out of range.");
        return (int)value;
    }

    private static string ReadString(ReadOnlySpan<byte> block, ref int position, int prefixBits)
    {
        if (position >= block.Length) throw new HttpParseException("Truncated QPACK string literal.");

        var huffman = (block[position] & (1 << prefixBits)) != 0;
        var length = ReadInt(block, ref position, prefixBits);
        if (length > block.Length - position)
            throw new HttpParseException("Truncated QPACK string literal data.");

        var raw = block.Slice(position, length);
        position += length;

        return Encoding.Latin1.GetString(huffman ? Huffman.Decode(raw) : raw);
    }
}
