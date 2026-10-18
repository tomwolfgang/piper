using System.Text.Json;

namespace Piper.App.Controls;

/// <summary>
/// What one inspector JSON tree may cost, whatever the document. Every figure bounds work or memory a
/// hostile body could otherwise choose: the body is captured traffic, so its size, depth and shape are
/// the sender's.
/// </summary>
internal sealed record JsonTreeLimits
{
    public static JsonTreeLimits Default { get; } = new();

    /// <summary>
    /// Characters parsed. A token costs 12 bytes of parser metadata, so 8 Mi characters of "1," is about 50 MB.
    /// A body never has fewer bytes than characters, so a caller can refuse by byte count before decoding to text.
    /// </summary>
    public int MaxChars { get; init; } = 8 * 1024 * 1024;

    /// <summary>Nesting parsed, and so the depth of any walk. A deeper document is refused as invalid.</summary>
    public int MaxDepth { get; init; } = 64;

    /// <summary>Children shown under one node; one expansion makes at most this many tree nodes.</summary>
    public int MaxChildren { get; init; } = 5_000;

    /// <summary>Nodes made for one document across every expansion, so expanding everything cannot exhaust memory.</summary>
    public int MaxNodes { get; init; } = 50_000;

    /// <summary>Characters of one node's text; a longer value is clipped and says how long it is.</summary>
    public int MaxDisplayChars { get; init; } = 1_000;

    /// <summary>Nodes one search looks at.</summary>
    public int MaxSearchVisits { get; init; } = 200_000;

    /// <summary>Matches one search keeps.</summary>
    public int MaxMatches { get; init; } = 10_000;
}

/// <summary>One node to show: its label, the value behind it, and whether it has children to expand.</summary>
internal sealed record JsonTreeChild(string Name, JsonElement Value, string Text, bool HasChildren);

/// <summary>The children of a node that may be shown, and how many more exist but are not shown.</summary>
internal sealed record JsonTreeLevel(IReadOnlyList<JsonTreeChild> Children, int Omitted);

/// <summary>
/// A parsed JSON document the inspector's tree reads on demand. Nothing is built for a node until it
/// is expanded, raw text is taken only when asked for, and the document is released once
/// <see cref="Retire"/> has been called and the last reader (a search on a worker thread, say) is done.
/// </summary>
internal sealed class JsonTreeSource
{
    private readonly JsonDocument _document;
    private readonly object _gate = new();
    private int _readers;
    private int _shown;
    private bool _retired;

    private JsonTreeSource(JsonDocument document, JsonTreeLimits limits)
    {
        _document = document;
        Limits = limits;
    }

    public JsonTreeLimits Limits { get; }

    /// <summary>Parses the text. Throws <see cref="JsonException"/> for invalid JSON, including text or nesting beyond the limits.</summary>
    public static JsonTreeSource Parse(string text, JsonTreeLimits? limits = null)
    {
        limits ??= JsonTreeLimits.Default;
        if (text.Length > limits.MaxChars) throw new JsonException($"The text is longer than {limits.MaxChars} characters.");
        return new JsonTreeSource(JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = limits.MaxDepth }), limits);
    }

    /// <summary>The top node, or null once retired.</summary>
    public JsonTreeChild? Root() => Read(() =>
    {
        lock (_gate) _shown++;
        return Describe("root", _document.RootElement);
    });

    /// <summary>The first children of a node, within the per-node and per-document limits; null once retired.</summary>
    public JsonTreeLevel? Children(JsonElement parent) => Read(() =>
    {
        var total = ChildCount(parent);
        int take;
        lock (_gate) // reserved up front, so concurrent expansions cannot overrun the node cap together
        {
            take = Math.Min(total, Math.Min(Limits.MaxChildren, Math.Max(0, Limits.MaxNodes - _shown)));
            _shown += take;
        }

        var children = new List<JsonTreeChild>(take);
        foreach (var (name, value) in Enumerate(parent, take))
            children.Add(Describe(name, value));
        return new JsonTreeLevel(children, total - children.Count);
    });

    /// <summary>The value's JSON text, taken now rather than for every node up front; null once retired.</summary>
    public string? RawText(JsonElement value) => Read(() => value.GetRawText());

    /// <summary>A string value's characters, or null for any other kind of value, for text that is not valid, or once retired.</summary>
    public string? StringValue(JsonElement value) => Read(() =>
    {
        if (value.ValueKind != JsonValueKind.String) return null;
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null; // a lone surrogate escape
        }
    });

    /// <summary>The node's text unclipped, for copying; null once retired.</summary>
    public string? FullText(string name, JsonElement value) =>
        Read(() => JsonTreeText.Format(name, value, int.MaxValue));

    /// <summary>
    /// The nodes whose text contains the query, in tree order, each as the child index to take at every
    /// level from the top (empty for the top node itself). Looks only at what the tree can show, and
    /// stops at <see cref="JsonTreeLimits.MaxSearchVisits"/> nodes or <see cref="JsonTreeLimits.MaxMatches"/>
    /// matches. Null once retired; throws <see cref="OperationCanceledException"/> when cancelled.
    /// </summary>
    public IReadOnlyList<int[]>? Search(string query, CancellationToken cancellationToken) => Read(() =>
    {
        var matches = new List<int[]>();
        var path = new List<int>();
        var visits = 0;
        Visit("root", _document.RootElement);
        return matches;

        bool Done() => visits >= Limits.MaxSearchVisits || matches.Count >= Limits.MaxMatches;

        void Visit(string name, JsonElement value)
        {
            if (Done()) return;
            if ((++visits & 0x3FF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (JsonTreeText.Format(name, value, int.MaxValue).Contains(query, StringComparison.OrdinalIgnoreCase))
                matches.Add([.. path]);

            var index = 0;
            foreach (var (childName, child) in Enumerate(value, Limits.MaxChildren))
            {
                path.Add(index++);
                Visit(childName, child);
                path.RemoveAt(path.Count - 1);
                if (Done()) return;
            }
        }
    });

    /// <summary>Lets go of the document: now, or when the last reader finishes.</summary>
    public void Retire()
    {
        lock (_gate)
        {
            if (_retired) return;
            _retired = true;
            if (_readers == 0) _document.Dispose();
        }
    }

    private JsonTreeChild Describe(string name, JsonElement value) =>
        new(name, value, JsonTreeText.Format(name, value, Limits.MaxDisplayChars), ChildCount(value) > 0);

    private static int ChildCount(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.GetPropertyCount(),
        JsonValueKind.Array => value.GetArrayLength(),
        _ => 0,
    };

    // Only ever the first few: indexing into the middle of an array is not constant time.
    private static IEnumerable<(string Name, JsonElement Value)> Enumerate(JsonElement parent, int take)
    {
        var index = 0;
        if (parent.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in parent.EnumerateObject())
            {
                if (index++ >= take) yield break;
                yield return (property.Name, property.Value);
            }
        }
        else if (parent.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in parent.EnumerateArray())
            {
                if (index >= take) yield break;
                yield return ($"[{index++}]", item);
            }
        }
    }

    private T? Read<T>(Func<T?> read) where T : class
    {
        lock (_gate)
        {
            if (_retired) return null;
            _readers++;
        }

        try
        {
            return read();
        }
        finally
        {
            lock (_gate)
            {
                if (--_readers == 0 && _retired) _document.Dispose();
            }
        }
    }
}

/// <summary>A tree node's value, kept beside its display text and read from the document only when a command needs it.</summary>
internal sealed class JsonNodeRef(JsonTreeSource source, string key, JsonElement value)
{
    public JsonTreeSource Source { get; } = source;

    public JsonElement Value { get; } = value;

    public string Key { get; } = key;

    public string? RawValue => Source.RawText(Value);

    public string? StringValue => Source.StringValue(Value);

    public string? PairText => Source.FullText(Key, Value);
}

/// <summary>The one-line text of a JSON node.</summary>
internal static class JsonTreeText
{
    /// <summary>
    /// "name: value" with the value in JSON form. A line longer than <paramref name="maxChars"/> is cut
    /// there and says how long it was, because one multi-megabyte line is slow to measure and paint.
    /// </summary>
    public static string Format(string name, JsonElement value, int maxChars)
    {
        var line = value.ValueKind switch
        {
            JsonValueKind.Object => $"{name}: {{ }}",
            JsonValueKind.Array => $"{name}: [ ]",
            JsonValueKind.String => $"{name}: {QuotedString(value)}",
            JsonValueKind.Null => $"{name}: null",
            _ => $"{name}: {value.GetRawText()}",
        };
        if (line.Length <= maxChars) return line;

        var cut = maxChars > 0 && char.IsHighSurrogate(line[maxChars - 1]) ? maxChars - 1 : maxChars;
        return Strings.Inspector.JsonClipped(line[..cut], line.Length);
    }

    // This is display, search and copy text, not HTML: only what JSON itself requires is escaped (quote,
    // backslash, control characters), so "José", "<b>", "a&b" and emoji read, and are found, as they were
    // sent. Any JsonSerializer encoder writes every non-ASCII or non-BMP character as \uXXXX.
    // A "\ud800" escape on its own parses but is not text: the string is shown as it was written.
    private static string QuotedString(JsonElement value)
    {
        string text;
        try
        {
            text = value.GetString() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return value.GetRawText();
        }

        var quoted = new System.Text.StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': quoted.Append("\\\""); break;
                case '\\': quoted.Append("\\\\"); break;
                case '\b': quoted.Append("\\b"); break;
                case '\f': quoted.Append("\\f"); break;
                case '\n': quoted.Append("\\n"); break;
                case '\r': quoted.Append("\\r"); break;
                case '\u0009': quoted.Append('\\').Append('t'); break; // spelled out: the UI-string lint scans for the tab escape
                case < ' ': quoted.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture)); break;
                default: quoted.Append(c); break;
            }
        }

        return quoted.Append('"').ToString();
    }
}
