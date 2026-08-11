using Piper.Core.Http;

namespace Piper.Core.Http3;

/// <summary>
/// QUIC variable-length integers (RFC 9000 §16), the primitive HTTP/3 uses for every frame type,
/// frame length and setting identifier. The top two bits of the first byte give the base-2 log of
/// the total byte count (1, 2, 4 or 8), and the value occupies the remaining bits.
/// </summary>
public static class VarInt
{
    public const long MaxValue = 4_611_686_018_427_387_903; // 2^62 - 1

    public static int EncodedLength(long value) => value switch
    {
        < 0 => throw new ArgumentOutOfRangeException(nameof(value), "Variable-length integers are non-negative."),
        < 64 => 1,
        < 16_384 => 2,
        < 1_073_741_824 => 4,
        <= MaxValue => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(value), "Value exceeds the 62-bit variable-length integer range."),
    };

    public static void Write(List<byte> output, long value)
    {
        switch (EncodedLength(value))
        {
            case 1:
                output.Add((byte)value);
                break;
            case 2:
                output.Add((byte)(0x40 | (value >> 8)));
                output.Add((byte)value);
                break;
            case 4:
                output.Add((byte)(0x80 | (value >> 24)));
                output.Add((byte)(value >> 16));
                output.Add((byte)(value >> 8));
                output.Add((byte)value);
                break;
            default:
                output.Add((byte)(0xc0 | (value >> 56)));
                output.Add((byte)(value >> 48));
                output.Add((byte)(value >> 40));
                output.Add((byte)(value >> 32));
                output.Add((byte)(value >> 24));
                output.Add((byte)(value >> 16));
                output.Add((byte)(value >> 8));
                output.Add((byte)value);
                break;
        }
    }

    public static byte[] Encode(long value)
    {
        var output = new List<byte>(8);
        Write(output, value);
        return output.ToArray();
    }

    /// <summary>Reads one variable-length integer, advancing <paramref name="position"/>.</summary>
    public static long Read(ReadOnlySpan<byte> data, ref int position)
    {
        if (position >= data.Length) throw new HttpParseException("Truncated variable-length integer.");

        var first = data[position];
        var length = 1 << (first >> 6);
        if (position + length > data.Length) throw new HttpParseException("Truncated variable-length integer.");

        long value = first & 0x3f;
        for (var i = 1; i < length; i++) value = (value << 8) + data[position + i];

        position += length;
        return value;
    }

    /// <summary>How many bytes follow the first one, given that first byte. Lets a reader pull the
    /// leading octet, size the rest, and read exactly that much off a stream.</summary>
    public static int TrailingBytes(byte firstByte) => (1 << (firstByte >> 6)) - 1;

    /// <summary>Rebuilds a value from its first byte plus the trailing bytes read separately.</summary>
    public static long Combine(byte firstByte, ReadOnlySpan<byte> trailing)
    {
        long value = firstByte & 0x3f;
        foreach (var b in trailing) value = (value << 8) + b;
        return value;
    }
}
