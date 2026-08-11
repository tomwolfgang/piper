using System.Text;
using Piper.Core.Http;

namespace Piper.Core.Http2.Hpack;

/// <summary>
/// RFC 7541 HPACK decoder. One instance per connection direction (each side of an HTTP/2
/// connection keeps two independent dynamic tables — see RFC 7541 §2.2). Fully spec-compliant,
/// including dynamic table insertion/eviction and size updates, because real browsers rely on
/// dynamic-table indexing for repeated headers and this side must decode whatever they send.
/// </summary>
public sealed class HpackDecoder
{
    private readonly List<(string Name, string Value)> _dynamicTable = [];
    private int _dynamicTableSize;
    private readonly int _advertisedMaxSize;
    private int _maxSize;

    /// <param name="advertisedMaxSize">The SETTINGS_HEADER_TABLE_SIZE this side advertised to the
    /// peer. A dynamic table size update that exceeds it is a decoding error (RFC 7541 §6.3).</param>
    public HpackDecoder(int advertisedMaxSize = 4096)
    {
        _advertisedMaxSize = advertisedMaxSize;
        _maxSize = advertisedMaxSize;
    }

    /// <summary>Decodes one header block into an ordered, duplicate-preserving field list.</summary>
    public List<(string Name, string Value)> Decode(ReadOnlySpan<byte> block)
    {
        var result = new List<(string Name, string Value)>();
        var pos = 0;

        while (pos < block.Length)
        {
            var first = block[pos];

            if ((first & 0x80) != 0)
            {
                // Indexed Header Field: 1xxxxxxx (RFC 7541 §6.1).
                var index = (int)DecodeInteger(block, ref pos, 7);
                if (index == 0) throw new HttpParseException("HPACK indexed header field index 0 is invalid.");
                result.Add(GetIndexed(index));
            }
            else if ((first & 0x40) != 0)
            {
                // Literal Header Field with Incremental Indexing: 01xxxxxx (§6.2.1).
                var index = (int)DecodeInteger(block, ref pos, 6);
                var name = index == 0 ? DecodeString(block, ref pos) : GetIndexed(index).Name;
                var value = DecodeString(block, ref pos);
                result.Add((name, value));
                InsertDynamic(name, value);
            }
            else if ((first & 0x20) != 0)
            {
                // Dynamic Table Size Update: 001xxxxx (§6.3). No header field emitted.
                var newSize = (int)DecodeInteger(block, ref pos, 5);
                if (newSize > _advertisedMaxSize)
                    throw new HttpParseException($"HPACK dynamic table size update {newSize} exceeds the advertised limit {_advertisedMaxSize}.");
                _maxSize = newSize;
                EvictTo(_maxSize);
            }
            else
            {
                // Literal Header Field without Indexing (0000xxxx, §6.2.2) or Never Indexed
                // (0001xxxx, §6.2.3) -- identical wire shape; a decoder treats both the same way.
                var index = (int)DecodeInteger(block, ref pos, 4);
                var name = index == 0 ? DecodeString(block, ref pos) : GetIndexed(index).Name;
                var value = DecodeString(block, ref pos);
                result.Add((name, value));
            }
        }

        return result;
    }

    private (string Name, string Value) GetIndexed(int index)
    {
        if (index <= HpackStaticTable.Count) return HpackStaticTable.Get(index);

        var dynIndex = index - HpackStaticTable.Count - 1;
        if (dynIndex < 0 || dynIndex >= _dynamicTable.Count)
            throw new HttpParseException($"HPACK index {index} is out of range.");
        return _dynamicTable[dynIndex];
    }

    private void InsertDynamic(string name, string value)
    {
        var entrySize = EntrySize(name, value);
        EvictTo(Math.Max(0, _maxSize - entrySize));
        if (entrySize > _maxSize) return; // too big to ever fit; table stays empty (RFC 7541 §4.4)

        _dynamicTable.Insert(0, (name, value));
        _dynamicTableSize += entrySize;
    }

    private void EvictTo(int targetSize)
    {
        while (_dynamicTable.Count > 0 && _dynamicTableSize > targetSize)
        {
            var oldest = _dynamicTable[^1];
            _dynamicTable.RemoveAt(_dynamicTable.Count - 1);
            _dynamicTableSize -= EntrySize(oldest.Name, oldest.Value);
        }
    }

    /// <summary>RFC 7541 §4.1: name length + value length + 32 octets of estimated overhead.</summary>
    private static int EntrySize(string name, string value) => name.Length + value.Length + 32;

    private static ulong DecodeInteger(ReadOnlySpan<byte> block, ref int pos, int prefixBits)
    {
        if (pos >= block.Length) throw new HttpParseException("Truncated HPACK integer.");
        var prefixMax = (1 << prefixBits) - 1;
        var value = (ulong)(block[pos] & prefixMax);
        pos++;
        if (value < (ulong)prefixMax) return value;

        var shift = 0;
        byte b;
        do
        {
            if (pos >= block.Length) throw new HttpParseException("Truncated HPACK integer.");
            b = block[pos++];
            value += (ulong)(b & 0x7f) << shift;
            shift += 7;
            if (shift > 63) throw new HttpParseException("HPACK integer too large.");
        } while ((b & 0x80) != 0);

        return value;
    }

    private static string DecodeString(ReadOnlySpan<byte> block, ref int pos)
    {
        if (pos >= block.Length) throw new HttpParseException("Truncated HPACK string.");
        var huffman = (block[pos] & 0x80) != 0;
        var length = (int)DecodeInteger(block, ref pos, 7);
        if (length < 0 || pos + length > block.Length) throw new HttpParseException("Truncated HPACK string data.");

        var raw = block.Slice(pos, length);
        pos += length;

        var bytes = huffman ? Huffman.Decode(raw) : raw.ToArray();
        return Encoding.Latin1.GetString(bytes);
    }
}
