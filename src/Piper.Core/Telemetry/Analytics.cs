using System.Diagnostics;
using System.Text;

namespace Piper.Core.Telemetry;

/// <summary>
/// Process-wide entry point for reporting. A static façade rather than an injected service because
/// Piper has no container and the crash handler in <c>Program</c> needs to reach this from a context
/// that owns nothing; matching the shape of the existing settings stores keeps call sites one line.
///
/// Every method is a no-op until <see cref="Initialize"/> runs, so reporting can never be the reason
/// something fails.
/// </summary>
public static class Analytics
{
    private static AnalyticsClient? _client;

    /// <summary>Installs the client and starts its background flush loop.</summary>
    public static void Initialize(AnalyticsClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        client.Start();
    }

    /// <summary>Records an event. Safe on the UI thread and safe before initialisation.</summary>
    public static void Track(string name, params (string Key, string Value)[] properties) =>
        _client?.Track(name, properties);

    /// <summary>
    /// Records an exception as its type and top frames. Never records the message: exception text
    /// routinely carries URLs, hostnames, and file paths containing the user's account name.
    /// </summary>
    public static void TrackError(string action, Exception exception, bool fatal = false)
    {
        if (exception is null) return;

        // Subsystem and type together, matching how the CurseForge analytics service keys errors on
        // a single error_id alongside the action in progress.
        _client?.Track(
            AnalyticsEvents.Error,
            (AnalyticsProperties.ErrorId, $"{action}.{exception.GetType().Name}"),
            (AnalyticsProperties.Action, action),
            (AnalyticsProperties.Frames, DescribeFrames(exception)),
            (AnalyticsProperties.Fatal, fatal ? "true" : "false"));
    }

    /// <summary>Writes queued events to disk without waiting for the timer. Used by the crash path.</summary>
    public static void FlushToDisk() => _client?.FlushToDisk();

    /// <summary>Whether events are being collected at all.</summary>
    public static bool IsEnabled => _client?.Settings.Enabled ?? false;

    /// <summary>
    /// Whether to ask for consent on this launch: never asked, or opted out and since updated. False
    /// when reporting failed to start, since there is then nothing to ask about.
    /// </summary>
    public static bool ShouldAskConsent(string currentVersion) =>
        _client?.ShouldAskConsent(currentVersion) ?? false;

    /// <summary>
    /// Applies the user's choice. Turning it off also discards anything not yet delivered. Returns
    /// false if switching off could not erase the stored identifier, so a caller does not claim it
    /// did.
    /// </summary>
    public static bool SetEnabled(bool enabled) => _client?.SetEnabled(enabled) ?? true;

    /// <summary>Records that the notice has been shown, which is what unblocks uploading.</summary>
    public static void RecordNoticeShown(string version) => _client?.RecordNoticeShown(version);

    /// <summary>The file holding events awaiting delivery, so the user can read what will be sent.</summary>
    public static string? SpoolPath => _client?.SpoolPath;

    public static void Shutdown()
    {
        _client?.Dispose();
        _client = null;
    }

    /// <summary>
    /// Renders the top frames as <c>Type.Method</c>, joined by hyphens. Method and type names come
    /// from assembly metadata, not from anything Piper captured, so they carry diagnostic value
    /// without carrying user data - unlike file paths and line numbers, which are omitted.
    /// </summary>
    public static string DescribeFrames(Exception exception, int maxFrames = 3)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var parts = new List<string>(maxFrames);
        foreach (var frame in new StackTrace(exception, fNeedFileInfo: false).GetFrames())
        {
            var method = frame.GetMethod();
            if (method is null) continue;

            var cleaned = KeepAcceptable($"{method.DeclaringType?.Name}.{method.Name}");
            if (cleaned.Length == 0) continue;

            parts.Add(cleaned);
            if (parts.Count == maxFrames) break;
        }

        if (parts.Count == 0) return "none";

        var joined = string.Join('-', parts);
        return joined.Length > AnalyticsSchema.MaxValueLength
            ? joined[..AnalyticsSchema.MaxValueLength]
            : joined;
    }

    /// <summary>
    /// Buckets a count. An exact figure is a behavioural fingerprint and nothing is decided by the
    /// difference between 41 and 42; the bucket answers "how big do real captures get".
    /// </summary>
    public static string CountBucket(int count) => count switch
    {
        <= 0 => "0",
        < 10 => "1-9",
        < 100 => "10-99",
        < 1000 => "100-999",
        _ => "gt999",
    };

    /// <summary>
    /// Drops anything the schema would reject, so a generic or compiler-generated member name
    /// (<c>&lt;Main&gt;b__0</c>, <c>List`1</c>) degrades to a readable frame instead of discarding
    /// the whole value.
    /// </summary>
    private static string KeepAcceptable(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            var acceptable = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
                or '.' or '_';
            if (acceptable) builder.Append(c);
        }

        return builder.ToString();
    }
}
