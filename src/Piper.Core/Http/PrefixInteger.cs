namespace Piper.Core.Http;

/// <summary>
/// The N-bit prefix integer encoding shared by HPACK (RFC 7541 §5.1) and QPACK
/// (RFC 9204 §4.1.1, which defers to the same algorithm): small values live in the low bits of
/// the current octet, larger ones fill the prefix and continue across following octets with the
/// high bit as a continuation flag.
/// </summary>
/// <remarks>
/// QPACK needs this with several different prefix sizes (3-, 4-, 5-, 6- and 7-bit) depending on
/// the representation, which is why it lives here rather than inline in one codec.
/// </remarks>
public static class PrefixInteger
{
    public static void Write(List<byte> output, byte patternBits, int prefixBits, long value)
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

    public static long Read(ReadOnlySpan<byte> data, ref int position, int prefixBits)
    {
        if (position >= data.Length) throw new HttpParseException("Truncated prefix integer.");

        var prefixMax = (1 << prefixBits) - 1;
        long value = data[position] & prefixMax;
        position++;
        if (value < prefixMax) return value;

        var shift = 0;
        byte b;
        do
        {
            if (position >= data.Length) throw new HttpParseException("Truncated prefix integer.");
            b = data[position++];
            value += (long)(b & 0x7f) << shift;
            shift += 7;
            if (shift > 62) throw new HttpParseException("Prefix integer too large.");
        } while ((b & 0x80) != 0);

        return value;
    }
}
