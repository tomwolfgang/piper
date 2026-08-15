using Piper.Core.Sessions;

namespace Piper.Core.Proxy;

/// <summary>How many times a rule has fired, for the panel's Hits column.</summary>
public readonly record struct AutoResponderRuleStats(long Hits, DateTimeOffset? LastMatched)
{
    public static readonly AutoResponderRuleStats None = new(0, null);
}

/// <summary>What the AutoResponder decided about one request.</summary>
public sealed record AutoResponderDecision(
    AutoResponderOutcome Outcome,
    TimeSpan Delay,
    AutoResponderRule? Rule,
    AutoResponderAction? Action,
    AutoResponderMatchResult Match,
    string Description)
{
    /// <summary>Nothing claimed this request; send it upstream as usual.</summary>
    public static readonly AutoResponderDecision Passthrough = new(
        AutoResponderOutcome.Passthrough, TimeSpan.Zero, null, null, AutoResponderMatchResult.Fail, string.Empty);
}

/// <summary>
/// Ordered request-interception rules, evaluated top to bottom with the first enabled match winning.
/// </summary>
/// <remarks>
/// Thread-safety copies <see cref="HostRemapping"/>: one immutable snapshot swapped atomically, so
/// proxy threads never lock and a request already being evaluated finishes against a consistent rule
/// list. Hit counts sit outside the snapshot, keyed by rule id, so editing one rule does not reset
/// every counter.
///
/// <see cref="Evaluate"/> runs on every single request, so the disabled case costs one volatile read
/// and a branch.
/// </remarks>
public sealed class AutoResponder
{
    private sealed record CompiledRule(
        string Id, bool Enabled, string Description,
        AutoResponderMatch Match, AutoResponderAction Action, AutoResponderRule Source);

    private sealed record Snapshot(
        bool Enabled, bool PassthroughUnmatched, AutoResponderSettings Settings,
        IReadOnlyList<CompiledRule> Rules, IReadOnlyList<string> Warnings,
        AutoResponderDecision Unmatched, long Revision);

    private sealed class Counter
    {
        private long _hits;
        private long _lastMatchedTicks;

        public void Record()
        {
            Interlocked.Increment(ref _hits);
            Interlocked.Exchange(ref _lastMatchedTicks, DateTimeOffset.Now.Ticks);
        }

        public AutoResponderRuleStats Read()
        {
            var ticks = Interlocked.Read(ref _lastMatchedTicks);
            return new AutoResponderRuleStats(Interlocked.Read(ref _hits),
                ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero).ToOffset(DateTimeOffset.Now.Offset));
        }
    }

    private static readonly Snapshot EmptySnapshot = new(
        false, true, new AutoResponderSettings(), [], [], AutoResponderDecision.Passthrough, 0);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Counter> _counters = new();
    private Snapshot _snapshot = EmptySnapshot;

    public bool Enabled => Volatile.Read(ref _snapshot).Enabled;

    public long Revision => Volatile.Read(ref _snapshot).Revision;

    /// <summary>The number of enabled rules, for the status bar.</summary>
    public int ActiveRuleCount => Volatile.Read(ref _snapshot).Rules.Count(rule => rule.Enabled);

    /// <summary>Problems found while compiling the current rule set. Bad rules are skipped, not fatal.</summary>
    public IReadOnlyList<string> Warnings => Volatile.Read(ref _snapshot).Warnings;

    public AutoResponderSettings Export() => Volatile.Read(ref _snapshot).Settings.Clone();

    public void Apply(AutoResponderSettings? settings)
    {
        settings ??= new AutoResponderSettings();

        var rules = new List<CompiledRule>(settings.Rules.Count);
        var warnings = new List<string>();

        foreach (var rule in settings.Rules)
        {
            var match = AutoResponderMatch.Parse(rule.Match);
            var action = AutoResponderAction.Parse(rule.Action);
            var description = Describe(rule);

            if (match.Warning is not null) warnings.Add($"{description}: {match.Warning}");
            if (action.Warning is not null) warnings.Add($"{description}: {action.Warning}");

            rules.Add(new CompiledRule(rule.Id, rule.Enabled, description, match, action, rule.Clone()));
        }

        // Built once here rather than per request: an unmatched 404 is the same answer every time.
        var unmatched = new AutoResponderDecision(
            AutoResponderOutcome.Respond, TimeSpan.Zero,
            new AutoResponderRule { Match = "(unmatched)", Action = "*404" },
            AutoResponderAction.Parse("*404"), AutoResponderMatchResult.Hit,
            "unmatched requests do not pass through");

        var current = Volatile.Read(ref _snapshot);
        Volatile.Write(ref _snapshot, new Snapshot(
            settings.Enabled, settings.PassthroughUnmatched, settings.Clone(),
            rules, warnings, unmatched, current.Revision + 1));

        // Counters for rules that no longer exist would otherwise leak for the life of the process.
        var live = rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _counters.Keys.Where(id => !live.Contains(id))) _counters.TryRemove(id, out _);
    }

    /// <summary>
    /// Decides what to do with one request. <paramref name="session"/> supplies the request; its
    /// response is never consulted, because it has not happened yet.
    /// </summary>
    /// <param name="recordHit">
    /// False for the panel's rule tester, so trying a URL out does not inflate the hit counts that
    /// tell the user which rules real traffic is reaching.
    /// </param>
    public AutoResponderDecision Evaluate(Session session, bool recordHit = true)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        if (!snapshot.Enabled || session?.Request is null) return AutoResponderDecision.Passthrough;

        foreach (var rule in snapshot.Rules)
        {
            if (!rule.Enabled) continue;

            var match = rule.Match.Match(session);
            if (!match.Success) continue;

            if (recordHit) _counters.GetOrAdd(rule.Id, _ => new Counter()).Record();
            return new AutoResponderDecision(
                rule.Action.Outcome, rule.Action.Delay, rule.Source, rule.Action, match, rule.Description);
        }

        return snapshot.PassthroughUnmatched ? AutoResponderDecision.Passthrough : snapshot.Unmatched;
    }

    public AutoResponderRuleStats StatsFor(string? ruleId) =>
        ruleId is not null && _counters.TryGetValue(ruleId, out var counter) ? counter.Read() : AutoResponderRuleStats.None;

    public void ResetStatistics() => _counters.Clear();

    private static string Describe(AutoResponderRule rule)
    {
        var match = string.IsNullOrWhiteSpace(rule.Match) ? "(blank)" : rule.Match.Trim();
        var action = string.IsNullOrWhiteSpace(rule.Action) ? "(pass through)" : rule.Action.Trim();
        return $"{match}  ->  {action}";
    }
}
