using System.Globalization;
using Piper.App;
using Piper.App.Controls;
using Piper.Core.Sessions;

// What the grid says about a failed client TLS handshake (X2 slice 3): the wording is the observed fact only
// and never claims pinning, the colour tone follows the kind, and the one string a cell takes from the client
// (an alert name) is cleaned like everything else that comes from it.
internal static class TlsFailureTextTests
{
    internal static TlsFailureInfo Info(TlsFailureKind kind, int attempts = 1, string? alert = null, uint? code = null, string? sni = null,
        TlsFailurePhase phase = TlsFailurePhase.InHandshake, bool overflow = false) =>
        new(kind, phase, alert, code) { Attempts = attempts, Sni = sni, IsOverflow = overflow, LastSeen = new DateTimeOffset(2026, 10, 19, 12, 34, 56, TimeSpan.Zero) };

    public static Task RunTlsFailureTextAsync(TestRunner runner) => runner.RunAsync("TLS failure text: Result, Path label, plurals and tone", () =>
    {
        runner.AreEqual("TLS ERR", TlsFailureText.Result, "the Result cell says TLS ERR, not ERR");
        runner.IsTrue(Format.WidestResultTexts().Contains("TLS ERR") && "TLS ERR".Length <= Format.WidestResultTexts().Max(t => t.Length),
            "the Result column is sized for it");

        runner.AreEqual("client refused Piper's certificate", TlsFailureText.PathLabel(Info(TlsFailureKind.Rejected)), "Rejected, one attempt");
        runner.AreEqual("client refused Piper's certificate (12 attempts)", TlsFailureText.PathLabel(Info(TlsFailureKind.Rejected, 12)), "Rejected, the other plural form");
        runner.AreEqual("client refused Piper's certificate (2 attempts)", TlsFailureText.PathLabel(Info(TlsFailureKind.Rejected, 2)), "two is already plural");
        runner.AreEqual("client refused Piper's certificate: UnknownCA (3 attempts)", TlsFailureText.PathLabel(Info(TlsFailureKind.Rejected, 3, alert: "UnknownCA")),
            "a rejection names the alert it was read from");
        runner.AreEqual($"client refused Piper's certificate ({1234.ToString("N0", CultureInfo.CurrentCulture)} attempts)",
            TlsFailureText.PathLabel(Info(TlsFailureKind.Rejected, 1234)), "the count is formatted with thousands separators");
        runner.AreEqual("client closed the connection during the handshake", TlsFailureText.PathLabel(Info(TlsFailureKind.ClosedInHandshake)), "ClosedInHandshake");
        runner.AreEqual("client closed right after the handshake, no request sent", TlsFailureText.PathLabel(Info(TlsFailureKind.ClosedAfterHandshake)),
            "ClosedAfterHandshake says what happened, not why");
        runner.AreEqual("client closed the tunnel before TLS started", TlsFailureText.PathLabel(Info(TlsFailureKind.ClosedEarly)), "ClosedEarly");
        runner.AreEqual("no TLS handshake completed in time", TlsFailureText.PathLabel(Info(TlsFailureKind.Timeout)), "Timeout");
        runner.AreEqual("no shared TLS version or cipher (Piper offers TLS 1.2 and 1.3)", TlsFailureText.PathLabel(Info(TlsFailureKind.NoCommonProtocol)), "NoCommonProtocol");
        runner.AreEqual("client sent non-TLS data after CONNECT", TlsFailureText.PathLabel(Info(TlsFailureKind.NotTls)), "NotTls");
        runner.AreEqual("TLS handshake failed (5 attempts)", TlsFailureText.PathLabel(Info(TlsFailureKind.Other, 5)), "Other claims nothing more");
        runner.AreEqual("1 more TLS handshake failure is not shown individually", TlsFailureText.PathLabel(Info(TlsFailureKind.Other, 1, overflow: true)),
            "the overflow row, singular");
        runner.AreEqual("40 more TLS handshake failures are not shown individually", TlsFailureText.PathLabel(Info(TlsFailureKind.Other, 40, overflow: true)),
            "the overflow row says the failures are not shown individually");
        runner.AreEqual("client refused Piper's certificate", TlsFailureText.PathLabel(Info(TlsFailureKind.Rejected, 0)), "a count under one reads as one");

        foreach (var kind in new[] { TlsFailureKind.Rejected, TlsFailureKind.ClosedInHandshake, TlsFailureKind.ClosedAfterHandshake, TlsFailureKind.ClosedEarly })
            runner.IsTrue(TlsFailureText.Tone(Info(kind)) == TlsFailureTone.Refusal, $"{kind}: the client declined or left, so amber");
        foreach (var kind in new[] { TlsFailureKind.Timeout, TlsFailureKind.NoCommonProtocol, TlsFailureKind.NotTls, TlsFailureKind.Other })
            runner.IsTrue(TlsFailureText.Tone(Info(kind)) == TlsFailureTone.Fault, $"{kind}: not the client's decision, so red");
        runner.IsTrue(TlsFailureText.Tone(Info(TlsFailureKind.Rejected, overflow: true)) == TlsFailureTone.Fault, "the overflow row stands for no one decision, so red");

        // A kind added later has to get words, or it would read as the generic Other.
        foreach (var kind in Enum.GetValues<TlsFailureKind>().Where(k => k != TlsFailureKind.Other))
            runner.IsTrue(TlsFailureText.Reason(Info(kind)) != TlsFailureText.Reason(Info(TlsFailureKind.Other)), $"{kind} has a reason of its own");
        return Task.CompletedTask;
    });

    public static Task RunTlsFailureCleanAsync(TestRunner runner) => runner.RunAsync("TLS failure text: text from the client is cleaned and clipped", () =>
    {
        runner.AreEqual("evil??Host: x?TAB?NUL", TlsFailureText.Clean("evil\r\nHost: x\tTAB\0NUL", 120), "control characters become ?");
        runner.AreEqual("?gnp.exe? ??mixed?", TlsFailureText.Clean(Chars(0x202E) + "gnp.exe" + Chars(0x202C) + " " + Chars(0x200F, 0x2066) + "mixed" + Chars(0x2069), 120),
            "direction overrides, isolates and marks become ?");
        runner.AreEqual("a?b?c?d", TlsFailureText.Clean("a" + Chars(0x2028) + "b" + Chars(0x2029) + "c" + Chars(0x85) + "d", 120), "line and paragraph separators and NEL become ?");
        runner.AreEqual(string.Empty, TlsFailureText.Clean(null, 120), "null is empty");
        runner.AreEqual("abc...", TlsFailureText.Clean("abcdef", 3), "a long value is cut and says so");
        runner.AreEqual("abc", TlsFailureText.Clean("abc", 3), "one that fits is left alone");
        runner.AreEqual("xxxxx...", TlsFailureText.Clean(new string('x', 5) + "\U0001F600", 6), "a surrogate pair is not cut in half");
        runner.AreEqual("a\U0001F600b", TlsFailureText.Clean("a\U0001F600b", 6), "a whole pair is kept");
        runner.AreEqual("a?b", TlsFailureText.Clean("a\uD800b", 6), "a lone surrogate becomes ?");
        runner.AreEqual(123, TlsFailureText.Clean(new string('a', 5000), 120).Length, "5,000 characters are cut to the clip and the three dots");

        // The only client-derived text in a cell is the alert name; the classifier keeps a closed list, and this is the second line.
        var label = TlsFailureText.PathLabel(Info(TlsFailureKind.Rejected, 2, alert: "{{count}}" + Chars(0x0D, 0x0A, 0x202E)));
        runner.IsTrue(!label.Contains((char)0x0D) && !label.Contains((char)0x202E) && label.StartsWith("client refused Piper's certificate: {{count}}??"), "an unexpected alert name is cleaned before it reaches a cell: " + label);
        return Task.CompletedTask;
    });

    private static string Chars(params int[] codes) => string.Concat(codes.Select(code => (char)code));
}
