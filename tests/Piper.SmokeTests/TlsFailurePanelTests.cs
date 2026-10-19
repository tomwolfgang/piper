using System.Globalization;
using Piper.App;
using Piper.App.Controls;
using Piper.Core.Sessions;

// What the inspector says about a failed client TLS handshake (X2 slice 3): the wording is the observed fact
// only and never claims pinning, the panel rows are what Piper saw and could not tell, and the strings the
// client controls (host, app, SNI) are clipped and cleaned and never take part in a template.
internal static class TlsFailurePanelTests
{
    private static TlsFailureInfo Info(TlsFailureKind kind, int attempts = 1, string? alert = null, uint? code = null, string? sni = null,
        TlsFailurePhase phase = TlsFailurePhase.InHandshake, bool overflow = false) =>
        TlsFailureTextTests.Info(kind, attempts, alert, code, sni, phase, overflow);

    private static string Text(IReadOnlyList<(string Name, string Value)> rows) => string.Join(" ", rows.Select(r => r.Value));

    private static string Chars(params int[] codes) => string.Concat(codes.Select(code => (char)code));

    public static Task RunTlsFailureWordingAsync(TestRunner runner) => runner.RunAsync("TLS failure text: honest wording, never a claim of pinning", () =>
    {
        var hinting = new[] { TlsFailureKind.Rejected, TlsFailureKind.ClosedInHandshake, TlsFailureKind.ClosedAfterHandshake };
        var gridMentions = new List<string>();
        var panelMentions = new List<string>();
        foreach (var kind in Enum.GetValues<TlsFailureKind>())
        foreach (var overflow in new[] { false, true })
        foreach (var attempts in new[] { 1, 9 })
        foreach (var alert in new string?[] { null, "UnknownCA" })
        {
            var info = Info(kind, attempts, alert, overflow: overflow);
            var grid = TlsFailureText.PathLabel(info) + " " + TlsFailureText.Summary(info) + " " + TlsFailureText.Reason(info);
            if (grid.Contains("pin", StringComparison.OrdinalIgnoreCase)) gridMentions.Add($"{kind}/{overflow}");

            var panel = Text(TlsFailureText.Panel(info, "host.test", "app.exe"));
            if (panel.Contains("pin", StringComparison.OrdinalIgnoreCase) && !(hinting.Contains(kind) && !overflow)) panelMentions.Add($"{kind}/{overflow}");
        }
        runner.AreEqual(0, gridMentions.Count, "a grid or summary text never mentions pinning: " + string.Join(", ", gridMentions));
        runner.AreEqual(0, panelMentions.Count, "only the kinds with a certificate in play carry the pinning hint: " + string.Join(", ", panelMentions));

        var rejected = Text(TlsFailureText.Panel(Info(TlsFailureKind.Rejected, alert: "UnknownCA"), "host.test", "app.exe"));
        runner.IsTrue(rejected.Contains("refused it"), "Rejected says the client refused the certificate");
        runner.IsTrue(rejected.Contains("look the same to Piper") && rejected.Contains("may be pinning"), "and that its cause is unknown, pinning being a hint");
        var closed = Text(TlsFailureText.Panel(Info(TlsFailureKind.ClosedInHandshake), "host.test", "app.exe"));
        runner.IsTrue(!closed.Contains("refused it") && closed.Contains("no alert that Piper could read"), "ClosedInHandshake claims no refusal");
        var afterHandshake = Text(TlsFailureText.Panel(Info(TlsFailureKind.ClosedAfterHandshake, phase: TlsFailurePhase.AfterHandshake), "host.test", "app.exe"));
        runner.IsTrue(afterHandshake.Contains("low confidence") && afterHandshake.Contains("speculative"), "ClosedAfterHandshake says it is low confidence and may be a browser preconnect");
        var early = Text(TlsFailureText.Panel(Info(TlsFailureKind.ClosedEarly, phase: TlsFailurePhase.BeforeHello), "host.test", "app.exe"));
        runner.IsTrue(early.Contains("never saw Piper's certificate") && early.Contains("Usually nothing"), "ClosedEarly says the certificate was never seen and is usually harmless");
        var overflow2 = Text(TlsFailureText.Panel(Info(TlsFailureKind.Other, 40, overflow: true), null, "app.exe"));
        runner.IsTrue(overflow2.Contains("only counts the rest here"), "the overflow row says the rest are only counted");
        return Task.CompletedTask;
    });

    public static Task RunTlsFailurePanelAsync(TestRunner runner) => runner.RunAsync("TLS failure text: the inspector panel rows", () =>
    {
        runner.AreEqual("Response   TLS ERR - client refused Piper's certificate (4 attempts)", TlsFailureText.Summary(Info(TlsFailureKind.Rejected, 4)),
            "the inspector's summary line");
        var info = Info(TlsFailureKind.Rejected, 12, "UnknownCA", 0x80090325, "other.test");
        var rows = TlsFailureText.Panel(info, "host.test", "launcher.exe");
        var names = rows.Select(r => r.Name).Where(n => n.Length > 0).ToArray();
        runner.IsTrue(string.Join(",", names) == "App,Host,SNI,Attempts,Last attempt,Phase,TLS alert,SChannel code,Piper saw,Piper cannot tell,What to do",
            "the rows run from who and where, to what was seen, to what to do: " + string.Join(", ", names));
        runner.AreEqual("launcher.exe", rows.Single(r => r.Name == "App").Value, "the app");
        runner.AreEqual("host.test", rows.Single(r => r.Name == "Host").Value, "the host");
        runner.AreEqual("other.test", rows.Single(r => r.Name == "SNI").Value, "an SNI that differs from the host is shown");
        runner.AreEqual("12", rows.Single(r => r.Name == "Attempts").Value, "the count");
        runner.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(rows.Single(r => r.Name == "Last attempt").Value, @"^\d\d:\d\d:\d\d$"),
            "the last attempt is a time of day");
        runner.AreEqual("during the handshake", rows.Single(r => r.Name == "Phase").Value, "the phase");
        runner.AreEqual("UnknownCA", rows.Single(r => r.Name == "TLS alert").Value, "the alert name");
        runner.AreEqual("0x80090325", rows.Single(r => r.Name == "SChannel code").Value, "the SChannel status as a code, not as OS text");
        runner.IsTrue(rows.All(r => r.Value.Length <= TlsFailureText.PanelWidth), "no row is longer than the panel breaks at");
        runner.IsTrue(rows.Count > names.Length && rows.Where(r => r.Name.Length == 0).All(r => r.Value.Length > 0), "a long sentence continues on rows with no name");

        var sameSni = TlsFailureText.Panel(Info(TlsFailureKind.ClosedInHandshake, sni: "HOST.test"), "host.test", "a.exe");
        runner.IsTrue(sameSni.All(r => r.Name != "SNI"), "an SNI that is the host (in any case) is not repeated");
        var single = TlsFailureText.Panel(Info(TlsFailureKind.ClosedEarly, phase: TlsFailurePhase.BeforeHello), "host.test", "");
        runner.IsTrue(single.All(r => r.Name is not ("Attempts" or "Last attempt" or "App" or "TLS alert" or "SChannel code")),
            "one attempt, no app, no alert and no code add no rows");
        runner.AreEqual("before a complete ClientHello arrived", single.Single(r => r.Name == "Phase").Value, "the phase before the hello");
        var overflow = TlsFailureText.Panel(Info(TlsFailureKind.Other, 7, overflow: true), "", "app.exe");
        runner.IsTrue(overflow.All(r => r.Name is not ("Host" or "Phase")), "the overflow row has no host and no phase to report");
        runner.IsTrue(TlsFailureText.Panel(Info(TlsFailureKind.Other), null, null).Count > 0, "a missing host and app do not throw");

        var panelStrings = I18n.Keys.Where(k => k.StartsWith("tlsFailure.", StringComparison.Ordinal)
            || k == "inspector.responseTlsFailed").ToArray();
        runner.IsTrue(panelStrings.Length > 40, "setup: the new strings are in the catalogue");
        var withControls = panelStrings.Where(k => I18n.T(k).Any(c => char.IsControl(c) && c is not ('\r' or '\n'))).ToArray();
        runner.AreEqual(0, withControls.Length, "no new string carries a tab or another control character: " + string.Join(", ", withControls));
        return Task.CompletedTask;
    });

    public static Task RunTlsFailureHostileTextAsync(TestRunner runner) => runner.RunAsync("TLS failure text: hostile host, app and SNI are data, never a template", () =>
    {
        var hostile = new[]
        {
            "evil\r\nHost: x\tTAB\0NUL",
            "{{count}}{{reason}}{{alert, X8}}$t(tlsFailure.result)",
            "{{",
            "}}{{0}}{0}%s%n",
            Chars(0x202E) + "gnp.exe" + Chars(0x202C) + " " + Chars(0x200F, 0x2066) + "mixed" + Chars(0x2069),
            "line" + Chars(0x2028) + "para" + Chars(0x2029) + "sep" + Chars(0x85) + "next",
            new string('a', 5000),
            new string(Chars(0x05D0)[0], 400),
            new string('x', TlsFailureText.MaxShown - 1) + "\U0001F600tail",
            "\uD800 lone high",
            "",
        };
        var unsafeRows = new List<string>();
        var longRows = new List<string>();
        foreach (var text in hostile)
        {
            foreach (var (name, value) in TlsFailureText.Panel(Info(TlsFailureKind.Rejected, 3, "UnknownCA", sni: text), text, text))
            {
                if (value.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
                    unsafeRows.Add($"{name} for input starting {Escape(text)}");
                if (value.Length > Math.Max(TlsFailureText.MaxShown + 3, TlsFailureText.PanelWidth)) longRows.Add($"{name} ({value.Length} characters)");
            }
        }
        runner.AreEqual(0, unsafeRows.Count, "no row holds a control, line-break or direction character: " + string.Join("; ", unsafeRows));
        runner.AreEqual(0, longRows.Count, "no row is longer than the clip: " + string.Join("; ", longRows));

        // Interpolation works on the catalogue's templates; a value is put in as text.
        var interpolating = TlsFailureText.Panel(Info(TlsFailureKind.Rejected, 3, "UnknownCA", sni: "{{count}}"), "{{reason}}{{count}}", "{{alert}}");
        runner.AreEqual("{{alert}}", interpolating.Single(r => r.Name == "App").Value, "an app named like a placeholder is shown as typed");
        runner.AreEqual("{{reason}}{{count}}", interpolating.Single(r => r.Name == "Host").Value, "so is a host");
        runner.AreEqual("{{count}}", interpolating.Single(r => r.Name == "SNI").Value, "and an SNI");
        runner.AreEqual("3", interpolating.Single(r => r.Name == "Attempts").Value, "while the real count is untouched");

        // Wrapping: boundaries.
        runner.AreEqual(1, TlsFailureText.Wrap("", 10).Count, "empty text is one empty line");
        runner.AreEqual("abc def", TlsFailureText.Wrap("abc def", 7).Single(), "a line of exactly the width stays whole");
        runner.IsTrue(TlsFailureText.Wrap("abc defg", 7) is ["abc", "defg"], "one more character breaks at the space");
        runner.IsTrue(TlsFailureText.Wrap(new string('z', 25), 10) is [{ Length: 10 }, { Length: 10 }, { Length: 5 }], "a word longer than the width is cut");
        runner.IsTrue(TlsFailureText.Wrap(string.Join(' ', Enumerable.Repeat("word", 400)), 64).All(l => l.Length <= 64), "no wrapped line is over the width");
        return Task.CompletedTask;
    });

    private static string Escape(string text) => text.Length > 16 ? text[..16].Replace("\0", "\\0") + "..." : text.Replace("\0", "\\0");
}
