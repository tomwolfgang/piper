using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piper.Bench;

/// <summary>One scenario, one run, one build: a line of the JSONL output. Metric names carry their
/// unit as a suffix (<c>_ms</c>, <c>_us</c>, <c>_mb</c>, <c>_bytes</c>, <c>rps</c>, <c>mbps</c>).</summary>
internal sealed class BenchRecord
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [JsonPropertyName("schema")] public int Schema { get; set; } = SchemaVersion;
    [JsonPropertyName("kind")] public string Kind { get; set; } = "run";
    [JsonPropertyName("scenario")] public string Scenario { get; set; } = "";
    [JsonPropertyName("run")] public int Run { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("utc")] public string Utc { get; set; } = "";
    [JsonPropertyName("metrics")] public Dictionary<string, double> Metrics { get; set; } = [];
    [JsonPropertyName("error")] public string? Error { get; set; }

    public string ToLine() => JsonSerializer.Serialize(this, Json);
}
