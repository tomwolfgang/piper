using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

// The filter grammar for failed client TLS handshakes (X2 slice 3): is:tlsfail and handshake:<kind>, case
// insensitive, negatable, a warning for an unknown value, refused by AutoResponder rules, and documented
// in the README table.
internal static class TlsFailureGrammarTests
{
    private static Session Row(TlsFailureKind kind, string host = "host.test", bool overflow = false) => new()
    {
        Request = new HttpRequestData { Method = "CONNECT", RequestTarget = $"{host}:443", Url = host.Length == 0 ? null : new Uri($"https://{host}") },
        IsTunnel = true,
        IsHttps = true,
        State = SessionState.Failed,
        ProcessName = "launcher.exe",
        Error = "TLS handshake with client failed: boom",
        TlsFailure = new TlsFailureInfo(kind, TlsFailurePhase.InHandshake) { IsOverflow = overflow },
    };

    private static Session Plain(string host = "host.test", bool failed = false) => new()
    {
        Request = new HttpRequestData { Method = "GET", Url = new Uri($"https://{host}/x") },
        Response = failed ? null : new HttpResponseData { StatusCode = 200 },
        State = failed ? SessionState.Failed : SessionState.Complete,
        Error = failed ? "Could not connect" : null,
    };

    private static bool Hits(string query, Session session) => SearchQuery.Parse(query).Matches(session);

    public static Task RunTlsFailureGrammarAsync(TestRunner runner) => runner.RunAsync("filter grammar: is:tlsfail and handshake:<kind>", () =>
    {
        var rejected = Row(TlsFailureKind.Rejected);
        var closed = Row(TlsFailureKind.ClosedInHandshake);
        var overflow = Row(TlsFailureKind.Other, "", overflow: true);
        var ok = Plain();
        var otherFailure = Plain(failed: true);
        Session[] all = [rejected, closed, overflow, ok, otherFailure];

        runner.IsTrue(all.Where(s => Hits("is:tlsfail", s)).SequenceEqual([rejected, closed, overflow]),
            "is:tlsfail matches exactly the sessions that have a TlsFailure");
        runner.IsTrue(all.Where(s => Hits("-is:tlsfail", s)).SequenceEqual([ok, otherFailure]), "-is:tlsfail is the rest");
        runner.IsTrue(all.Where(s => Hits("!is:tlsfail", s)).SequenceEqual([ok, otherFailure]), "and so is !is:tlsfail");
        runner.IsTrue(all.All(s => Hits("is:TLSFAIL", s) == Hits("is:tlsfail", s) && Hits("IS:tlsFail", s) == Hits("is:tlsfail", s)), "the value and the field ignore case");
        runner.IsTrue(all.All(s => Hits("is:handshake", s) == Hits("is:tlsfail", s) && Hits("has:tlsfail", s) == Hits("is:tlsfail", s)), "is:handshake and has:tlsfail are the same");
        runner.IsTrue(all.Where(s => Hits("is:error", s)).SequenceEqual([rejected, closed, overflow, otherFailure]),
            "is:error keeps its meaning: every failed row, TLS or not");
        runner.IsTrue(Hits("is:failed", rejected) && Hits("is:tunnel", rejected) && Hits("is:https", rejected), "the other tokens still describe a TLS failure row");
        runner.IsTrue(Hits("error:handshake", rejected) && !Hits("error:handshake", otherFailure), "error: still searches the error text of a TLS failure row");
        runner.IsTrue(Hits("is:tlsfail -is:error", rejected) == false && Hits("is:tlsfail is:error", rejected), "tokens combine with AND");

        runner.IsTrue(all.Where(s => Hits("handshake:rejected", s)).SequenceEqual([rejected]), "handshake:rejected matches that kind only");
        runner.IsTrue(all.Where(s => Hits("handshake:closed", s)).SequenceEqual([closed]), "handshake:closed matches the in-handshake close only");
        runner.IsTrue(all.Where(s => Hits("handshake:rejected|closed", s)).SequenceEqual([rejected, closed]), "alternatives with |");
        runner.IsTrue(all.Where(s => Hits("handshake:Rejected|CLOSED", s)).SequenceEqual([rejected, closed]), "values ignore case");
        runner.IsTrue(all.Where(s => Hits("HANDSHAKE:rejected", s)).SequenceEqual([rejected]), "the field ignores case");
        runner.IsTrue(all.Where(s => Hits("handshake:\"closed\"", s)).SequenceEqual([closed]), "a quoted value works");
        runner.IsTrue(all.Where(s => Hits("handshake: rejected", s)).Count() == 0 && SearchQuery.Parse("handshake: rejected").Warnings.Count > 0,
            "a space after the colon is an empty value, which is reported");
        runner.IsTrue(all.Where(s => Hits("-handshake:rejected", s)).SequenceEqual([closed, overflow, ok, otherFailure]),
            "-handshake:rejected is everything else, as a negated field is for every other field");
        runner.IsTrue(!Hits("handshake:other", overflow), "no kind selects the overflow row, which stands for many");
        runner.IsTrue(Hits("handshake:other", Row(TlsFailureKind.Other)), "other matches the kind Other");

        foreach (var kind in Enum.GetValues<TlsFailureKind>())
        {
            var key = SearchQuery.HandshakeValues.Single(v => Hits(v, Row(kind)));
            runner.IsTrue(SearchQuery.Parse(key).Warnings.Count == 0 && Enum.GetValues<TlsFailureKind>().Select(k => Row(k)).Count(s => Hits(key, s)) == 1,
                $"{kind} has exactly one filter value, {key}");
        }
        runner.AreEqual(Enum.GetValues<TlsFailureKind>().Length, SearchQuery.HandshakeValues.Length, "and no value is without a kind");
        runner.IsTrue(!SearchQuery.HandshakeValues.Any(v => v.Contains("pin", StringComparison.OrdinalIgnoreCase)), "there is no pinning value");

        runner.IsTrue(Hits("host:host.test is:tlsfail", rejected) && !Hits("host:other.test is:tlsfail", rejected), "it combines with host:");
        runner.IsTrue(Hits("host:host.test handshake:rejected", rejected) && !Hits("host:host.test handshake:closed", rejected), "and handshake: with host:");
        runner.IsTrue(Hits("domain:host.test -handshake:closed is:tlsfail", rejected) && !Hits("domain:host.test -handshake:closed is:tlsfail", closed), "and with domain: and a negation");
        runner.IsTrue(Hits("method:CONNECT handshake:rejected", rejected), "and with method:");

        foreach (var bad in new[] { "handshake:pinning", "handshake:rejected|pinning", "handshake:", "handshake:/rej/", "handshake:||", "is:tlsfails" })
        {
            var query = SearchQuery.Parse(bad);
            runner.IsTrue(query.Warnings.Count == 1, $"{bad} is reported as a warning ({string.Join("; ", query.Warnings)})");
        }
        runner.IsTrue(SearchQuery.Parse("handshake:pinning").Warnings[0].Contains("'pinning'"), "the warning names the value that was not understood");
        runner.IsTrue(SearchQuery.Parse("handshake:pinning").IsEmpty && SearchQuery.Parse("handshake:pinning").Matches(rejected),
            "an unknown value is ignored like any other bad term, so the rest of the query still runs");

        var used = SearchQuery.Parse("handshake:closed is:tlsfail");
        runner.IsTrue(used.Fields.Contains("handshake") && used.IsValuesUsed.Contains("tlsfail"), "the query reports the field and the value it used");
        runner.IsTrue(SearchQuery.FieldNames.Contains("handshake:") && SearchQuery.IsValues.Contains("is:tlsfail"), "both are offered by autocomplete");
        return Task.CompletedTask;
    });

    public static Task RunTlsFailureGrammarRulesAsync(TestRunner runner) => runner.RunAsync("filter grammar: AutoResponder rules refuse the handshake tokens, the README table lists them", () =>
    {
        // A rule is matched before any response exists, and a handshake failure never reaches a rule at all.
        foreach (var rule in new[] { "Q:is:tlsfail", "Q:handshake:rejected", "Q:is:handshake", "Q:method:CONNECT handshake:closed" })
        {
            var match = AutoResponderMatch.Parse(rule);
            runner.IsTrue(match.Warning is not null, $"{rule} is refused when the rule is parsed");
            runner.IsTrue(!match.Match(Row(TlsFailureKind.Rejected)).Success, $"{rule} never matches");
        }

        var readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));
        var start = readme.IndexOf("## Search grammar", StringComparison.Ordinal);
        var end = readme.IndexOf("\n## ", start + 1, StringComparison.Ordinal);
        runner.IsTrue(start >= 0 && end > start, "setup: the README has a search grammar section");
        var grammar = readme[start..end];
        var tableRows = grammar.Split('\n').Where(l => l.StartsWith('|')).ToArray();

        runner.IsTrue(SearchQuery.IsValues.Contains("is:tlsfail") && tableRows.Any(row => row.Contains("`is:tlsfail`", StringComparison.Ordinal)),
            "the README table lists is:tlsfail");
        var handshakeRow = tableRows.SingleOrDefault(row => row.StartsWith("| `handshake:`", StringComparison.Ordinal)) ?? "";
        runner.IsTrue(handshakeRow.Length > 0, "the README table has a handshake: row");
        foreach (var value in SearchQuery.HandshakeValues)
            runner.IsTrue(handshakeRow.Contains($"`{value["handshake:".Length..]}`", StringComparison.Ordinal), $"and that row lists the value {value}");
        runner.IsTrue(!grammar.Contains("handshake:pinning", StringComparison.Ordinal), "the README does not offer a pinning value");
        return Task.CompletedTask;
    });

    internal static string RepositoryRoot()
    {
        for (var directory = AppContext.BaseDirectory; directory is not null;
             directory = Path.GetDirectoryName(directory.TrimEnd(Path.DirectorySeparatorChar)))
        {
            if (File.Exists(Path.Combine(directory, "Piper.slnx"))) return directory;
        }

        throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory + ".");
    }
}
