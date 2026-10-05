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

    /// <summary>The most header lines <see cref="Parse"/> accepts; the same cap the stream parser uses.</summary>
    public const int MaxParsedHeaders = HttpSyntax.MaxHeaderCount;

    /// <summary>The longest block, in characters, <see cref="Parse"/> accepts. Matches the largest head
    /// the stream parser reads, so no caller sees a block the proxy itself would have refused.</summary>
    public const int MaxParsedBlockLength = HttpParser.MaxResponseHeadBytes;

    /// <summary>
    /// Parses a "Name: Value" block, tolerating what a hand-edited or archived block contains: blank
    /// lines, lines with no colon (skipped) and obsolete line folding. Throws
    /// <see cref="HttpParseException"/> past <see cref="MaxParsedHeaders"/> headers or
    /// <see cref="MaxParsedBlockLength"/> characters. The input is a SAZ archive, a multipart part, a
    /// file or text the user pasted, so it is bounded like anything else a peer sends.
    /// </summary>
    public static HeaderCollection Parse(string block) =>
        TryParse(block, out var headers, out var error) ? headers : throw new HttpParseException(error);

    /// <summary><see cref="Parse"/> that reports why instead of throwing.</summary>
    public static bool TryParse(string block, out HeaderCollection headers, out string error)
    {
        headers = new HeaderCollection();
        error = string.Empty;

        if (block.Length > MaxParsedBlockLength)
        {
            error = $"The header block is larger than {MaxParsedBlockLength} characters.";
            return false;
        }

        var builder = new HeaderBlockBuilder(lenient: true);
        foreach (var line in block.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Length == 0) continue;
            if (builder.AddLine(line) is { } problem)
            {
                error = problem;
                return false;
            }
        }

        headers = builder.Complete();
        return true;
    }

    public IEnumerator<HttpHeader> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Builds a <see cref="HeaderCollection"/> from header lines one at a time, so the stream parser
/// (which reads lines off a socket) and <see cref="HeaderCollection.Parse"/> (which has a whole
/// block) share one set of rules and one header cap.
/// </summary>
/// <remarks>
/// A header is held back until the next one starts, so that obsolete line folding appends each
/// continuation to a <see cref="StringBuilder"/> rather than rebuilding the value as a new string
/// every time, which took time proportional to the square of the number of folded lines.
/// </remarks>
internal sealed class HeaderBlockBuilder(bool lenient)
{
    private readonly HeaderCollection _headers = new();
    private string? _name;
    private string _value = string.Empty;
    private StringBuilder? _folded;

    /// <summary>Takes one non-blank line. Returns null, or the reason the block must be refused.</summary>
    public string? AddLine(string line)
    {
        if (line[0] is ' ' or '\t' && _name is not null)
        {
            // Obsolete line folding (RFC 9112 5.2): the line continues the previous value.
            _folded ??= new StringBuilder(_value);
            _folded.Append(' ').Append(line.AsSpan().Trim());
            return null;
        }

        if (line[0] is ' ' or '\t' && !lenient) return "Header block starts with a folded line.";

        var colon = line.IndexOf(':');
        if (colon <= 0)
            return lenient ? null : $"Malformed header line: '{HttpParser.Truncate(line)}'";

        Flush();
        if (_headers.Count >= HttpSyntax.MaxHeaderCount) return "Too many headers.";

        _name = lenient ? line[..colon].Trim() : line[..colon].TrimEnd();
        _value = line[(colon + 1)..].Trim();
        return null;
    }

    /// <summary>Ends the block and returns what was read.</summary>
    public HeaderCollection Complete()
    {
        Flush();
        return _headers;
    }

    private void Flush()
    {
        if (_name is null) return;

        _headers.Add(_name, _folded?.ToString() ?? _value);
        _name = null;
        _value = string.Empty;
        _folded = null;
    }
}
