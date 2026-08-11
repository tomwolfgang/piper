using System.Text;

namespace Piper.Core.Http2.Hpack;

/// <summary>
/// RFC 7541 HPACK encoder, deliberately simplified: every field is sent as "Literal Header
/// Field without Indexing" (§6.2.2), using a static-table index for the name when one exists
/// and a literal name otherwise. The dynamic table is never written to, so there is no
/// send-side eviction or peer-desync bookkeeping to get wrong — legal per RFC 7541 (using the
/// dynamic table is optional for a sender) at the cost of a few extra bytes per header, which
/// is irrelevant for a MITM debugging proxy. Outbound strings are literal ASCII, not
/// Huffman-encoded, in phase 1 -- a future optimization, not a correctness gap (the decoder
/// above already fully supports Huffman-coded input from real peers).
/// </summary>
public static class HpackEncoder
{
    public static byte[] Encode(IReadOnlyList<(string Name, string Value)> fields)
    {
        var output = new List<byte>(fields.Count * 24);
        foreach (var (name, value) in fields) EncodeField(output, name, value);
        return output.ToArray();
    }

    private static void EncodeField(List<byte> output, string name, string value)
    {
        if (HpackStaticTable.TryFindName(name, out var index))
            EncodeInteger(output, 0x00, 4, index);
        else
        {
            EncodeInteger(output, 0x00, 4, 0);
            EncodeString(output, name);
        }
        EncodeString(output, value);
    }

    private static void EncodeInteger(List<byte> output, byte patternBits, int prefixBits, int value)
    {
        var prefixMax = (1 << prefixBits) - 1;
        if (value < prefixMax)
        {
            output.Add((byte)(patternBits | value));
            return;
        }

        output.Add((byte)(patternBits | prefixMax));
        value -= prefixMax;
        while (value >= 128)
        {
            output.Add((byte)((value % 128) + 128));
            value /= 128;
        }
        output.Add((byte)value);
    }

    private static void EncodeString(List<byte> output, string value)
    {
        var bytes = Encoding.Latin1.GetBytes(value);
        EncodeInteger(output, 0x00, 7, bytes.Length); // H = 0: raw octets, no Huffman
        output.AddRange(bytes);
    }
}
