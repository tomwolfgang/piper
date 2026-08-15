using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Piper.Core.Http;

/// <summary>
/// Mutations behind an editable JSON tree: retype a value, rename a property, write the document
/// back out.
/// </summary>
/// <remarks>
/// Built on the mutable <see cref="JsonNode"/> DOM rather than the read-only
/// <see cref="JsonDocument"/> the inspector uses, because the point here is to change the payload
/// and hand it back. Kept out of the UI so the fiddly parts - preserving a property's type when its
/// text changes, and preserving property order across a rename - are testable.
/// </remarks>
public static class JsonEditing
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // The inspector's pretty-printer relaxes escaping for the same reason: a response body full
        // of & instead of & is unreadable, and this text is meant to be edited by a person.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static bool TryParse(string? text, out JsonNode? root, out string error)
    {
        root = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "the body is empty";
            return false;
        }

        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static string Serialize(JsonNode? root) =>
        root is null ? "null" : root.ToJsonString(WriteOptions);

    /// <summary>
    /// Builds the replacement for <paramref name="existing"/> from typed text.
    /// </summary>
    /// <remarks>
    /// A property that was a string stays a string, so typing 123 into an id that was "123" does not
    /// silently change its type and break whatever is being stubbed. Anything else is inferred, which
    /// is what lets a number stay a number and true stay a boolean.
    /// </remarks>
    public static JsonNode? ValueFrom(JsonNode? existing, string? text)
    {
        text ??= string.Empty;

        if (existing is JsonValue value && value.GetValueKind() == JsonValueKind.String)
            return JsonValue.Create(text);

        return Infer(text);
    }

    private static JsonNode? Infer(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)) return null;
        if (trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);

        if (long.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var whole))
            return JsonValue.Create(whole);

        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var real))
            return JsonValue.Create(real);

        return JsonValue.Create(text);
    }

    /// <summary>Puts <paramref name="replacement"/> where <paramref name="target"/> sits in its parent.</summary>
    public static bool TryReplace(JsonNode target, JsonNode? replacement, out string error)
    {
        ArgumentNullException.ThrowIfNull(target);
        error = string.Empty;

        switch (target.Parent)
        {
            case JsonObject parent:
                parent[target.GetPropertyName()] = replacement;
                return true;

            case JsonArray parent:
                parent[target.GetElementIndex()] = replacement;
                return true;

            default:
                error = "the root of the document cannot be replaced this way";
                return false;
        }
    }

    /// <summary>
    /// Renames the property holding <paramref name="target"/>, keeping every property in its
    /// original position - a stub whose keys reshuffle on edit is hard to read against the original.
    /// </summary>
    public static bool TryRename(JsonNode target, string newName, out string error)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.Parent is not JsonObject parent)
        {
            error = "only a property of an object can be renamed";
            return false;
        }

        return TryRenameProperty(parent, target.GetPropertyName(), newName, out error);
    }

    /// <summary>
    /// Renames by container and key rather than by node, so a property whose value is null - and
    /// which therefore has no node to point at - can still be renamed.
    /// </summary>
    public static bool TryRenameProperty(JsonObject parent, string oldName, string newName, out string error)
    {
        ArgumentNullException.ThrowIfNull(parent);
        error = string.Empty;

        if (string.Equals(oldName, newName, StringComparison.Ordinal)) return true;

        if (string.IsNullOrEmpty(newName))
        {
            error = "a property needs a name";
            return false;
        }

        if (parent.ContainsKey(newName))
        {
            error = $"there is already a property called '{newName}'";
            return false;
        }

        // Rebuild in place: JsonObject keeps insertion order, so renaming by remove-and-add would
        // move the property to the end.
        var entries = parent.ToList();
        parent.Clear();
        foreach (var (key, value) in entries)
            parent[string.Equals(key, oldName, StringComparison.Ordinal) ? newName : key] = value;

        return true;
    }

    /// <summary>How one node reads in a tree: "name: value" for a leaf, a count for a container.</summary>
    public static string Describe(string? name, JsonNode? node)
    {
        var label = name is null ? string.Empty : $"{name}: ";
        return node switch
        {
            null => $"{label}null",
            JsonObject o => $"{label}{{{o.Count} propert{(o.Count == 1 ? "y" : "ies")}}}",
            JsonArray a => $"{label}[{a.Count} item{(a.Count == 1 ? string.Empty : "s")}]",
            JsonValue v when v.GetValueKind() == JsonValueKind.String => $"{label}\"{v}\"",
            _ => $"{label}{node}",
        };
    }

    /// <summary>The editable text of a leaf: a string without its quotes, anything else verbatim.</summary>
    public static string EditableText(JsonNode? node) => node switch
    {
        null => "null",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => node.ToJsonString(),
    };

    /// <summary>True for nodes whose value can be typed into a single box.</summary>
    public static bool IsLeaf(JsonNode? node) => node is null or JsonValue;
}
