using System.Collections.Frozen;
using System.Text.Json.Serialization;

namespace Piper.Core.Telemetry;

/// <summary>
/// One recorded analytics event. Every instance is produced by <see cref="AnalyticsSchema.Create"/>,
/// which is the only way properties get in, so a value that reached this type has already passed the
/// sanitiser.
/// </summary>
public sealed class AnalyticsEvent
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("time")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// Identifies one run of the application. Ordering the events that share a run by timestamp is
    /// what turns them into a funnel; there is no separate funnel machinery.
    /// </summary>
    [JsonPropertyName("run")]
    public required string RunId { get; init; }

    /// <summary>
    /// Position within the run, from 1. Retries can deliver an event twice and the collector keeps
    /// no identifier of its own, so this is the deduplication key alongside the run; a gap is an
    /// event lost to the queue or spool caps.
    /// </summary>
    [JsonPropertyName("seq")]
    public int Sequence { get; init; }

    [JsonPropertyName("props")]
    public IReadOnlyDictionary<string, string> Properties { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// The complete set of event names Piper may report. Nothing outside this list is sent.
///
/// Deliberately six. Each one answers a question that changes what gets built, and anything that
/// could only be read as a number on a dashboard was left out: session length says nothing about a
/// debugging tool, a stop event is the inverse of a start, and release adoption already falls out of
/// <see cref="AnalyticsProperties.AppVersion"/> riding on every event. Four of the six form the
/// activation funnel - started, trusted, capturing, seeing traffic - because for a proxy the
/// question that matters most is how far a new user gets before giving up.
/// </summary>
public static class AnalyticsEvents
{
    /// <summary>One run began. With the machine identifier this yields actives and retention.</summary>
    public const string AppStarted = "piper_usage_app_started";

    /// <summary>
    /// The root-certificate step - the one most likely to lose a new user.
    /// <see cref="AnalyticsProperties.Result"/> is "ok", "declined", "failed", or "already" when a
    /// run starts with the root trusted, so returning users are not read as having skipped it.
    /// </summary>
    public const string CertTrusted = "piper_usage_cert_trusted";

    /// <summary>
    /// Capture was attempted; <see cref="AnalyticsProperties.Result"/> is "ok", "port_in_use" or
    /// "failed".
    /// </summary>
    public const string CaptureStarted = "piper_usage_capture_started";

    /// <summary>Traffic actually appeared. The end of the funnel: Piper works for this user.</summary>
    public const string FirstSessionCaptured = "piper_usage_first_session_captured";

    /// <summary>A feature was used. Tells maintenance apart from dead weight.</summary>
    public const string FeatureUsed = "piper_usage_feature_used";

    /// <summary>Something failed. Crash rate is this over <see cref="AppStarted"/>.</summary>
    public const string Error = "piper_usage_error";
}

/// <summary>
/// The complete set of property keys carried in Extra. Nothing outside this list is sent.
///
/// The first three are attached to every event, matching the keys the CurseForge analytics service
/// merges in automatically, so the same dashboards read both. Piper reports directly rather than
/// through that service, so it has to add them itself. Both send them in snake_case; the collector's
/// pipeline indexes every Extra key in camelCase (app_ver as appVer), so they must stay snake_case.
/// </summary>
public static class AnalyticsProperties
{
    /// <summary>Piper's version. On every event.</summary>
    public const string AppVersion = "app_ver";

    /// <summary>Short operating system name. On every event.</summary>
    public const string OsVersion = "os_ver";

    /// <summary>Which application is reporting. On every event.</summary>
    public const string AppType = "app_type";

    /// <summary>Outcome of an attempted action, for example "ok", "port_in_use".</summary>
    public const string Result = "result";

    /// <summary>Where an action was started from, for example "startup", "manual".</summary>
    public const string Source = "source";

    /// <summary>Which feature was used, for example "composer", "saz_export".</summary>
    public const string Feature = "feature";

    /// <summary>Archive or payload format, for example "saz", "raz".</summary>
    public const string Format = "format";

    /// <summary>Coarse count bucket, for example "10-99". Never an exact count.</summary>
    public const string Count = "count";

    /// <summary>Subsystem and exception type, for example "saz_import.IOException".</summary>
    public const string ErrorId = "error_id";

    /// <summary>What the user was doing, for example "unhandled", "capture_start".</summary>
    public const string Action = "action";

    /// <summary>Top stack frames as Type.Method, joined by "-". Never file paths or line numbers.</summary>
    public const string Frames = "frames";

    /// <summary>"true" when the error terminated the process.</summary>
    public const string Fatal = "fatal";

    /// <summary>Identifies one run, so ordered steps can be read as a funnel. On every event.</summary>
    public const string Run = "run";

    /// <summary>When the event happened, as Unix seconds - not when it was received.</summary>
    public const string Timestamp = "ts";

    /// <summary>The event's position within its run. On every event.</summary>
    public const string Sequence = "seq";
}

/// <summary>
/// The single trust boundary between Piper and anything it reports.
///
/// Piper decrypts other people's HTTPS traffic, so the rule that has to survive every future edit is
/// that captured data cannot leave. Enforcing that at each call site would mean trusting every author
/// of every future call site. Instead the vocabulary is closed: event names and property keys must
/// appear in the lists above, and a property value must be a short run of [A-Za-z0-9._-]. A URL,
/// header, hostname, cookie, path or body fragment fails that test by construction - it will contain
/// a colon, slash, at sign, space or non-ASCII character - and is replaced with
/// <see cref="InvalidValue"/> before it can reach the queue.
/// </summary>
public static class AnalyticsSchema
{
    /// <summary>Stands in for a value that failed validation. Keeps the event, discards the payload.</summary>
    public const string InvalidValue = "invalid";

    /// <summary>
    /// Long enough for an exception type name or a short frame list, short enough that no realistic
    /// URL, token or body fragment fits.
    /// </summary>
    public const int MaxValueLength = 64;

    /// <summary>Guards against a call site looping over attacker-influenced data to build properties.</summary>
    public const int MaxProperties = 8;

    public static readonly FrozenSet<string> EventNames = new[]
    {
        AnalyticsEvents.AppStarted,
        AnalyticsEvents.CertTrusted,
        AnalyticsEvents.CaptureStarted,
        AnalyticsEvents.FirstSessionCaptured,
        AnalyticsEvents.FeatureUsed,
        AnalyticsEvents.Error,
    }.ToFrozenSet(StringComparer.Ordinal);

    public static readonly FrozenSet<string> PropertyKeys = new[]
    {
        AnalyticsProperties.AppVersion,
        AnalyticsProperties.OsVersion,
        AnalyticsProperties.AppType,
        AnalyticsProperties.Result,
        AnalyticsProperties.Source,
        AnalyticsProperties.Feature,
        AnalyticsProperties.Format,
        AnalyticsProperties.Count,
        AnalyticsProperties.ErrorId,
        AnalyticsProperties.Action,
        AnalyticsProperties.Frames,
        AnalyticsProperties.Fatal,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Builds an event, or returns <see langword="null"/> if the name is not one Piper reports.
    /// Unknown property keys are dropped and unacceptable values are replaced; neither loses the
    /// event, because a partially redacted event is still worth more than none.
    /// </summary>
    public static AnalyticsEvent? Create(
        string? name,
        IReadOnlyList<(string Key, string Value)>? properties,
        DateTimeOffset timestamp,
        string runId,
        int sequence = 0)
    {
        if (name is null || !EventNames.Contains(name)) return null;

        var sanitised = new Dictionary<string, string>(StringComparer.Ordinal);
        if (properties is not null)
        {
            foreach (var (key, value) in properties)
            {
                if (sanitised.Count >= MaxProperties) break;
                if (key is null || !PropertyKeys.Contains(key)) continue;
                sanitised[key] = SanitiseValue(value);
            }
        }

        return new AnalyticsEvent
        {
            Name = name,
            Timestamp = timestamp,

            // Sanitised here rather than relying on callers. The run identifier is not carried in
            // the property bag - it deliberately has no entry in PropertyKeys - so this is the one
            // string that would otherwise reach the query string without passing the boundary this
            // method is documented to be.
            RunId = SanitiseValue(runId),

            // Read back from a user-editable spool, so it is bounded here like everything else.
            Sequence = Math.Max(sequence, 0),
            Properties = sanitised,
        };
    }

    /// <summary>
    /// Returns the value unchanged when it is a short run of letters, digits, dot, underscore or
    /// hyphen; otherwise <see cref="InvalidValue"/>. Deliberately a character scan rather than a
    /// regular expression: no backtracking, and nothing to get subtly wrong in a future edit.
    /// </summary>
    public static string SanitiseValue(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxValueLength) return InvalidValue;

        foreach (var c in value)
        {
            var acceptable = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '.' or '_' or '-';
            if (!acceptable) return InvalidValue;
        }

        return value;
    }
}
