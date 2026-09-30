namespace Piper.Core.Proxy;

/// <summary>
/// One AutoResponder rule: an expression deciding which requests it claims, and an action deciding
/// what they get back instead of the origin's answer.
/// </summary>
/// <remarks>
/// Match and action are kept as the text the user typed rather than a parsed structure. Rules are
/// edited as text, shared as text and copied out of Fiddler as text, so the text is the source of
/// truth; parsing happens when the rule set is applied.
/// </remarks>
public sealed class AutoResponderRule
{
    /// <summary>
    /// Stable across edits so per-rule hit counts survive a change to the rule's text. Generated on
    /// creation; rules restored from disk keep the id they were saved with.
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public bool Enabled { get; set; } = true;

    // A rule file is hostile input: "Match": null deserialises straight into a non-nullable
    // property, so the setters keep the promise the type makes to every reader downstream.
    private string _match = string.Empty;
    private string _action = string.Empty;

    public string Match { get => _match; set => _match = value ?? string.Empty; }

    public string Action { get => _action; set => _action = value ?? string.Empty; }

    /// <summary>Response body for the <c>*inline</c> action, stored with the rule so a rule set is portable.</summary>
    public string? Body { get; set; }

    /// <summary>Content-Type for <see cref="Body"/>. Defaults to text/plain when a rule does not say.</summary>
    public string? ContentType { get; set; }

    /// <summary>Free-text note, shown in the rules list and ignored by the engine.</summary>
    public string? Comment { get; set; }

    public AutoResponderRule Clone() => new()
    {
        Id = Id,
        Enabled = Enabled,
        Match = Match,
        Action = Action,
        Body = Body,
        ContentType = ContentType,
        Comment = Comment,
    };
}

/// <summary>The persisted AutoResponder configuration: the ordered rule list and its two toggles.</summary>
public sealed class AutoResponderSettings
{
    public bool Enabled { get; set; }

    /// <summary>
    /// When false, a request that matches no rule is answered with a 404 instead of being sent to its
    /// origin. This is Fiddler's "Unmatched requests passthrough" checkbox, inverted the same way.
    /// </summary>
    public bool PassthroughUnmatched { get; set; } = true;

    private List<AutoResponderRule> _rules = [];

    /// <summary>
    /// The ordered rules. Assigning null, or a list containing nulls, keeps the rules and drops the
    /// nulls: <c>"Rules": null</c> and <c>[null]</c> both deserialise happily from a hand-edited file
    /// and would otherwise throw on the first read, which for the saved file is the app's constructor.
    /// </summary>
    public List<AutoResponderRule> Rules
    {
        get => _rules;
        set => _rules = value is null ? [] : [.. value.Where(rule => rule is not null)];
    }

    public AutoResponderSettings Clone() => new()
    {
        Enabled = Enabled,
        PassthroughUnmatched = PassthroughUnmatched,
        Rules = [.. Rules.Select(rule => rule.Clone())],
    };

    /// <summary>
    /// A copy of this set with <paramref name="imported"/>'s rules added after its own, for an import
    /// that must not discard what is already here. This set's toggles win. An imported rule whose id
    /// is already in use gets a fresh one, or the two would share one hit counter.
    /// </summary>
    public AutoResponderSettings Appended(AutoResponderSettings imported)
    {
        ArgumentNullException.ThrowIfNull(imported);

        var merged = Clone();
        var used = merged.Rules.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var rule in imported.Rules)
        {
            var copy = rule.Clone();
            if (!used.Add(copy.Id))
            {
                copy.Id = Guid.NewGuid().ToString("N");
                used.Add(copy.Id);
            }

            merged.Rules.Add(copy);
        }

        return merged;
    }
}
