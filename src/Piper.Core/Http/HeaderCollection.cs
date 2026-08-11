using System.Collections;
using System.Text;

namespace Piper.Core.Http;

/// <summary>A single header line. Name casing is preserved exactly as it appeared on the wire.</summary>
public readonly record struct HttpHeader(string Name, string Value)
{
    public override string ToString() => $"{Name}: {Value}";
}

/// <summary>
/// Ordered, duplicate-preserving header list with case-insensitive lookup.
/// Order matters for fingerprinting and for byte-accurate replay, so we never
/// collapse into a dictionary.
/// </summary>
public sealed class HeaderCollection : IEnumerable<HttpHeader>
{
    private readonly List<HttpHeader> _items;

    public HeaderCollection() => _items = new List<HttpHeader>(16);

    public HeaderCollection(IEnumerable<HttpHeader> items) => _items = new List<HttpHeader>(items);

    public int Count => _items.Count;

    public HttpHeader this[int index]
    {
        get => _items[index];
        set => _items[index] = value;
    }

    /// <summary>First value for <paramref name="name"/>, or null.</summary>
    public string? this[string name]
    {
        get
        {
            foreach (var h in _items)
                if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
                    return h.Value;
            return null;
        }
    }

    public void Add(string name, string value) => _items.Add(new HttpHeader(name, value));

    /// <summary>Replaces every existing occurrence with a single header, keeping the original position.</summary>
    public void Set(string name, string value)
    {
        var idx = IndexOf(name);
        if (idx < 0)
        {
            _items.Add(new HttpHeader(name, value));
            return;
        }
        _items[idx] = new HttpHeader(name, value);
        for (var i = _items.Count - 1; i > idx; i--)
            if (string.Equals(_items[i].Name, name, StringComparison.OrdinalIgnoreCase))
                _items.RemoveAt(i);
    }

    public bool Remove(string name)
    {
        var removed = false;
        for (var i = _items.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(_items[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            _items.RemoveAt(i);
            removed = true;
        }
        return removed;
    }

    public bool Contains(string name) => IndexOf(name) >= 0;

    public IEnumerable<string> GetValues(string name)
    {
        foreach (var h in _items)
            if (string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))
                yield return h.Value;
    }

    private int IndexOf(string name)
    {
        for (var i = 0; i < _items.Count; i++)
            if (string.Equals(_items[i].Name, name, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>True when <paramref name="name"/> exists and any of its values contains <paramref name="token"/> (comma-list aware).</summary>
    public bool HasToken(string name, string token)
    {
        foreach (var value in GetValues(name))
        {
            foreach (var part in value.Split(','))
                if (part.Trim().Equals(token, StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    public HeaderCollection Clone() => new(_items);

    public void Clear() => _items.Clear();

    /// <summary>Renders the block including the trailing blank line, as it goes on the wire.</summary>
    public string ToRawString()
    {
        var sb = new StringBuilder(Count * 40);
        foreach (var h in _items) sb.Append(h.Name).Append(": ").Append(h.Value).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Parses a "Name: Value" block. Handles obs-fold continuation lines.</summary>
    public static HeaderCollection Parse(string block)
    {
        var result = new HeaderCollection();
        var lines = block.Replace("\r\n", "\n").Split('\n');
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if ((line[0] == ' ' || line[0] == '\t') && result.Count > 0)
            {
                // Obsolete line folding: append to the previous value.
                var prev = result._items[^1];
                result._items[^1] = prev with { Value = prev.Value + " " + line.Trim() };
                continue;
            }
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            result.Add(line[..colon].Trim(), line[(colon + 1)..].Trim());
        }
        return result;
    }

    public IEnumerator<HttpHeader> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
