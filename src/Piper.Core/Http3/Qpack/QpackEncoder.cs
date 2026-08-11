using System.Text;
using Piper.Core.Http;

namespace Piper.Core.Http3.Qpack;

/// <summary>
/// QPACK encoder (RFC 9204) restricted to the static table: an exact static hit becomes a single
/// indexed byte, a name-only hit becomes "literal with name reference", anything else becomes a
/// fully literal field line. The dynamic table is never used.
/// </summary>
/// <remarks>
/// This is explicitly permitted -- RFC 9204 §2.1 lets an encoder decline the dynamic table
/// entirely -- and it removes the single nastiest part of QPACK: the encoder/decoder instruction
/// streams and the insert-count accounting that lets a decoder become "blocked" waiting on state
/// it has not received. Piper is only issuing its own requests, so the few bytes saved by dynamic
/// compression are worth far less than not having that failure mode at all.
/// </remarks>
public static class QpackEncoder
{
    public static byte[] Encode(IReadOnlyList<(string Name, string Value)> fields)
    {
        var output = new List<byte>(fields.Count * 24);

        // Field section prefix (RFC 9204 §4.5.1): Required Insert Count then Delta Base. With no
        // dynamic table references both are zero, which is also the smallest legal prefix.
        PrefixInteger.Write(output, 0x00, 8, 0); // Required Insert Count = 0
        PrefixInteger.Write(output, 0x00, 7, 0); // S = 0, Delta Base = 0

        foreach (var (name, value) in fields)
        {
            var lower = name.ToLowerInvariant();

            if (QpackStaticTable.TryFindExact(lower, value, out var exact))
            {
                // Indexed Field Line, static (§4.5.2): '1' then T=1 then a 6-bit index.
                PrefixInteger.Write(output, 0xc0, 6, exact);
                continue;
            }

            if (QpackStaticTable.TryFindName(lower, out var nameIndex))
            {
                // Literal with Name Reference (§4.5.4): '01', N=0, T=1, 4-bit name index.
                PrefixInteger.Write(output, 0x50, 4, nameIndex);
                WriteString(output, value, prefixBits: 7, patternBits: 0x00);
                continue;
            }

            // Literal with Literal Name (§4.5.6): '001', N=0, then H + a 3-bit name length.
            WriteString(output, lower, prefixBits: 3, patternBits: 0x20);
            WriteString(output, value, prefixBits: 7, patternBits: 0x00);
        }

        return output.ToArray();
    }

    /// <summary>Writes a string literal, Huffman-coding it only when that is actually smaller.
    /// The H bit is the top bit of the length's prefix octet.</summary>
    private static void WriteString(List<byte> output, string value, int prefixBits, byte patternBits)
    {
        var raw = Encoding.Latin1.GetBytes(value);
        var huffmanLength = Huffman.EncodedLength(raw);

        if (huffmanLength < raw.Length)
        {
            var huffmanBit = (byte)(1 << prefixBits);
            PrefixInteger.Write(output, (byte)(patternBits | huffmanBit), prefixBits, huffmanLength);
            output.AddRange(Huffman.Encode(raw));
        }
        else
        {
            PrefixInteger.Write(output, patternBits, prefixBits, raw.Length);
            output.AddRange(raw);
        }
    }
}
