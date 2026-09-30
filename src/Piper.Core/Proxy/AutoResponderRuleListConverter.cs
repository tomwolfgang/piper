using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piper.Core.Proxy;

/// <summary>
/// Raised from inside the read, the moment a rule set turns out to have too many rules. Deliberately
/// not a <see cref="System.Text.Json.JsonException"/>: System.Text.Json lets other exception types
/// from a converter through unwrapped, and the store's <c>Load</c> relies on that to tell "too many
/// rules" from "malformed" (a smoke test pins it).
/// </summary>
internal sealed class RuleCountExceededException : Exception
{
}

/// <summary>
/// Reads the rule array element by element and gives up at the first one past
/// <see cref="AutoResponderSettingsStore.MaxRules"/>, so the cap bounds what is allocated rather than
/// only what is kept: an 8 MB array of <c>{}</c> is millions of elements.
/// </summary>
internal sealed class AutoResponderRuleListConverter : JsonConverter<List<AutoResponderRule>>
{
    public override List<AutoResponderRule>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Rules must be an array.");

        var rules = new List<AutoResponderRule>();
        var seen = 0;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            // Counts nulls too: they cost a token each, and the array is not a rule set past the cap.
            if (++seen > AutoResponderSettingsStore.MaxRules) throw new RuleCountExceededException();

            var rule = JsonSerializer.Deserialize<AutoResponderRule>(ref reader, options);
            if (rule is not null) rules.Add(rule);
        }

        return rules;
    }

    public override void Write(Utf8JsonWriter writer, List<AutoResponderRule> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var rule in value) JsonSerializer.Serialize(writer, rule, options);
        writer.WriteEndArray();
    }
}
