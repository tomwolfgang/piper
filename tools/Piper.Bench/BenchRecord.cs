using System.Text.Json;
using System.Text.Json.Serialization;

namespace Piper.Bench;

/// <summary>One scenario, one run, one build: a line of the JSONL output. Metric names carry their
/// unit as a suffix (<c>_ms</c>, <c>_us</c>, <c>_mb</c>, <c>_bytes</c>, <c>rps</c>, <c>mbps</c>).</summary>
internal sealed class BenchRecord
{
    /// <summary>2: the env record names hosts by label and file name only (snake_case keys), and bg_top names only known noisy processes. Version 1 files still load.</summary>
    public const int SchemaVersion = 2;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [JsonPropertyName("schema")] public int Schema { get; set; } = SchemaVersion;
    [JsonPropertyName("kind")] public string Kind { get; set; } = "run";
    [JsonPropertyName("scenario")] public string Scenario { get; set; } = "";
    [JsonPropertyName("run")] public int Run { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("utc")] public string Utc { get; set; } = "";
    [JsonPropertyName("bg_cpu_pct")] public double? BackgroundCpuPercent { get; set; }
    [JsonPropertyName("bg_top")] public string? BackgroundTop { get; set; }
    [JsonPropertyName("eset")] public bool EsetRunning { get; set; }
    [JsonPropertyName("metrics")] public Dictionary<string, double> Metrics { get; set; } = [];
    [JsonPropertyName("error")] public string? Error { get; set; }

    public string ToLine() => JsonSerializer.Serialize(this, Json);
}
