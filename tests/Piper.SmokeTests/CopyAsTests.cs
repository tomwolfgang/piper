using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Sessions;

/// <summary>
/// "Copy as" writes a command that is pasted into a shell, so every captured value is hostile input.
/// The first tests pin the exact text each target produces for a request full of it; the next ones
/// pin each escaper and the rules about what is left out; the last ones run the pasted text in the
/// real shells and runtimes that are installed and check the origin received every value unchanged.
/// Where one is not there the round trip says so and is skipped, never silently passed.
/// </summary>
internal static class CopyAsTests
{
    // What a hostile client could send. Latin-1, as the parser reads headers: the UTF-8 bytes of
    // the non-ASCII text arrive as several characters per letter.
    private static readonly string Latin1Unicode = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes("caf\u00e9 \u2019 \ud83d\ude00"));

    private const string BodyJson = "{\"q\":\"$(id) `x` 'y' %PATH% \\\"z\\\"\",\"u\":\"\u00e9\"}";

    private static readonly CopyAsTarget[] AllTargets = Enum.GetValues<CopyAsTarget>();

    // The curl config the cmd.exe and bash snippets of the hostile request carry: its X-Uni header is not
    // ASCII and X-Cmd holds an exclamation mark, so neither puts the request on the command line. For
    // bash each character stands for one byte, as the snippet writes them.
    private static string HostileConfig(string uni, string body) =>
        "url = " + CopyAs.CurlConfigString("https://api.example.test/v1/it's?q=$(id)&b=%60x%60&p=100%25") + "\n"
        + "globoff\n"
        + "request = \"POST\"\n"
        + "compressed\n"
        + "header = \"Content-Type: application/json\"\n"
        + "header = \"Accept-Encoding: gzip, br\"\n"
        + "header = \"Authorization: Bearer abc$def`x`\"\n"
        + "header = \"X-Dollar: $(touch /tmp/pwn) ${HOME} `id`\"\n"
        + "header = \"X-Quotes: it's \\\"quoted\\\" \\\\ back\\\\\"\n"
        + "header = \"X-Cmd: 100% %PATH% a&b|c<d>e^f!g (h)\"\n"
        + "header = \"X-Empty;\"\n"
        + "header = " + CopyAs.CurlConfigString(uni) + "\n"
        + "header = \"X-Dup: 1\"\n"
        + "header = \"X-Dup: 2\"\n"
        + (body.Length > 0 ? "data-raw = " + CopyAs.CurlConfigString(body) + "\n" : string.Empty);

    private static readonly string HostileCmdConfig = HostileConfig("X-Uni: café ’ 😀", string.Empty);

    private static readonly string HostileBashConfig = HostileConfig("X-Uni: " + Latin1Unicode, Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(BodyJson)));

    // The temporary file names carry a random token; a snapshot compares them with it replaced.
    private static string WithoutToken(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "piper-(args|body)-[0-9a-f]{16}", "piper-$1-TOKEN");

    // The curl config a cmd snippet writes: its base64 line is decoded here instead of by certutil.
    private static string CmdConfigOf(string snippet)
    {
        var echo = snippet.Split("\r\n").Single(l => l.StartsWith("> \"%TEMP%\\piper-args-", StringComparison.Ordinal));
        return Encoding.UTF8.GetString(Convert.FromBase64String(echo[(echo.IndexOf(" echo ", StringComparison.Ordinal) + 6)..]));
    }

    private static readonly Dictionary<CopyAsTarget, string> Snapshots = new()
    {
        [CopyAsTarget.CurlBash] = $"""
                # Headers with a name or value that cannot be written safely were left out.
                printf %s {CopyAs.BashQuote(Encoding.Latin1.GetBytes(HostileBashConfig))} | curl --config -
                """,
        // X-Cmd holds an exclamation mark and X-Uni is not ASCII, so the request travels in a curl config
        // file (written as base64) and the command line is plain ASCII with no "!" in it.
        [CopyAsTarget.CurlCmd] = $"""
                REM The request body is binary or not safe to paste as text, so it is written as base64 and decoded here.
                REM Non-ASCII header text may be re-encoded by the shell it is pasted into, or by the runtime that sends it.
                REM Headers with a name or value that cannot be written safely were left out.
                REM This request has values cmd.exe cannot carry safely (an exclamation mark, or text that is not plain ASCII), so the request, headers and credentials included, is passed to curl in a temporary file in your temp folder as plain text. It is deleted when curl finishes, and stays there if the paste is interrupted: delete the piper-* files in that folder then.
                > "%TEMP%\piper-args-TOKEN.b64" echo {Convert.ToBase64String(Encoding.UTF8.GetBytes(HostileCmdConfig))}
                certutil -f -decode "%TEMP%\piper-args-TOKEN.b64" "%TEMP%\piper-args-TOKEN.cfg" > nul
                del /q "%TEMP%\piper-args-TOKEN.b64"
                > "%TEMP%\piper-body-TOKEN.b64" echo eyJxIjoiJChpZCkgYHhgICd5JyAlUEFUSCUgXCJ6XCIiLCJ1Ijoiw6kifQ==
                certutil -f -decode "%TEMP%\piper-body-TOKEN.b64" "%TEMP%\piper-body-TOKEN.bin" > nul
                del /q "%TEMP%\piper-body-TOKEN.b64"
                curl.exe --config "%TEMP%\piper-args-TOKEN.cfg" ^
                  --data-binary "@%TEMP%\piper-body-TOKEN.bin" & del /q "%TEMP%\piper-args-TOKEN.cfg" "%TEMP%\piper-body-TOKEN.bin"
                """,
        [CopyAsTarget.CurlPowerShell] = """
                # The request body is binary or not safe to paste as text, so it is written as base64 and decoded here.
                # Non-ASCII header text may be re-encoded by the shell it is pasted into, or by the runtime that sends it.
                # Headers with a name or value that cannot be written safely were left out.
                $piperBody = Join-Path ([IO.Path]::GetTempPath()) ('piper-body-' + [IO.Path]::GetRandomFileName())
                try {
                    [IO.File]::WriteAllBytes($piperBody, [Convert]::FromBase64String('eyJxIjoiJChpZCkgYHhgICd5JyAlUEFUSCUgXCJ6XCIiLCJ1Ijoiw6kifQ=='))
                @'
                url = "https://api.example.test/v1/it's?q=$(id)&b=%60x%60&p=100%25"
                globoff
                request = "POST"
                compressed
                header = "Content-Type: application/json"
                header = "Accept-Encoding: gzip, br"
                header = "Authorization: Bearer abc$def`x`"
                header = "X-Dollar: $(touch /tmp/pwn) ${HOME} `id`"
                header = "X-Quotes: it's \"quoted\" \\ back\\"
                header = "X-Cmd: 100% %PATH% a&b|c<d>e^f!g (h)"
                header = "X-Empty;"
                header = "X-Uni: café ’ 😀"
                header = "X-Dup: 1"
                header = "X-Dup: 2"
                '@ | curl.exe --config - --data-binary "@$piperBody"
                } finally {
                    Remove-Item -LiteralPath $piperBody -ErrorAction SilentlyContinue
                }
                """,
        [CopyAsTarget.PowerShellWebRequest] = """
                # Non-ASCII header text may be re-encoded by the shell it is pasted into, or by the runtime that sends it.
                # Headers with a name or value that cannot be written safely were left out.
                # Repeated headers were merged into one value because this tool cannot repeat a header.
                $params = @{
                    Uri = 'https://api.example.test/v1/it''s?q=$(id)&b=%60x%60&p=100%25'
                    Method = 'POST'
                    UseBasicParsing = $true
                    MaximumRedirection = 0
                    ContentType = 'application/json'
                    Headers = @{
                        'Accept-Encoding' = 'gzip, br'
                        'Authorization' = 'Bearer abc$def`x`'
                        'X-Dollar' = '$(touch /tmp/pwn) ${HOME} `id`'
                        'X-Quotes' = 'it''s "quoted" \ back\'
                        'X-Cmd' = '100% %PATH% a&b|c<d>e^f!g (h)'
                        'X-Empty' = ''
                        'X-Uni' = ('cafÃ© â' + [char]0x0080 + [char]0x0099 + ' ð' + [char]0x009f + [char]0x0098 + [char]0x0080)
                        'X-Dup' = '1, 2'
                    }
                    Body = [Text.Encoding]::UTF8.GetBytes('{"q":"$(id) `x` ''y'' %PATH% \"z\"","u":"é"}')
                }
                Invoke-WebRequest @params
                """,
        [CopyAsTarget.JavaScriptFetch] = """
                // Headers with a name or value that cannot be written safely were left out.
                await fetch("https://api.example.test/v1/it's?q=$(id)&b=%60x%60&p=100%25", {
                  method: "POST",
                  redirect: "manual",
                  headers: [
                    ["Content-Type", "application/json"],
                    ["Accept-Encoding", "gzip, br"],
                    ["Authorization", "Bearer abc$def`x`"],
                    ["X-Dollar", "$(touch /tmp/pwn) ${HOME} `id`"],
                    ["X-Quotes", "it's \"quoted\" \\ back\\"],
                    ["X-Cmd", "100% %PATH% a&b|c<d>e^f!g (h)"],
                    ["X-Empty", ""],
                    ["X-Uni", "caf\u00c3\u00a9 \u00e2\u0080\u0099 \u00f0\u009f\u0098\u0080"],
                    ["X-Dup", "1"],
                    ["X-Dup", "2"],
                  ],
                  body: "{\"q\":\"$(id) `x` 'y' %PATH% \\\"z\\\"\",\"u\":\"\u00e9\"}",
                });
                """,
        [CopyAsTarget.PythonRequests] = """
                # Headers with a name or value that cannot be written safely were left out.
                # Repeated headers were merged into one value because this tool cannot repeat a header.
                import requests

                response = requests.request(
                    "POST",
                    "https://api.example.test/v1/it's?q=$(id)&b=%60x%60&p=100%25",
                    allow_redirects=False,
                    headers={
                        "Content-Type": "application/json",
                        "Accept-Encoding": "gzip, br",
                        "Authorization": "Bearer abc$def`x`",
                        "X-Dollar": "$(touch /tmp/pwn) ${HOME} `id`",
                        "X-Quotes": "it's \"quoted\" \\ back\\",
                        "X-Cmd": "100% %PATH% a&b|c<d>e^f!g (h)",
                        "X-Empty": "",
                        "X-Uni": b"caf\xc3\xa9 \xe2\x80\x99 \xf0\x9f\x98\x80",
                        "X-Dup": "1, 2",
                    },
                    data="{\"q\":\"$(id) `x` 'y' %PATH% \\\"z\\\"\",\"u\":\"\u00e9\"}".encode("utf-8"),
                )
                """,
        [CopyAsTarget.CSharpHttpClient] = """
                // Headers with a name or value that cannot be written safely were left out.
                using System.Net;
                using System.Net.Http;
                using System.Text;

                using var client = new HttpClient(new SocketsHttpHandler
                {
                    AutomaticDecompression = DecompressionMethods.All,
                    UseCookies = false,
                    AllowAutoRedirect = false,
                    RequestHeaderEncodingSelector = (_, _) => Encoding.Latin1,
                });
                using var request = new HttpRequestMessage(new HttpMethod("POST"), "https://api.example.test/v1/it's?q=$(id)&b=%60x%60&p=100%25");
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes("{\"q\":\"$(id) `x` 'y' %PATH% \\\"z\\\"\",\"u\":\"\u00e9\"}"));
                request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer abc$def`x`");
                request.Headers.TryAddWithoutValidation("X-Dollar", "$(touch /tmp/pwn) ${HOME} `id`");
                request.Headers.TryAddWithoutValidation("X-Quotes", "it's \"quoted\" \\ back\\");
                request.Headers.TryAddWithoutValidation("X-Cmd", "100% %PATH% a&b|c<d>e^f!g (h)");
                request.Headers.TryAddWithoutValidation("X-Empty", "");
                request.Headers.TryAddWithoutValidation("X-Uni", "caf\u00c3\u00a9 \u00e2\u0080\u0099 \u00f0\u009f\u0098\u0080");
                request.Headers.TryAddWithoutValidation("X-Dup", "1");
                request.Headers.TryAddWithoutValidation("X-Dup", "2");
                using var response = await client.SendAsync(request);
                """,
    };

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("Copy as: Git for Windows curl is found in both install layouts", () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "piper-git-curl-" + Guid.NewGuid().ToString("N"));
            var bash = Path.Combine(root, "usr", "bin", "bash.exe");
            var ucrt = Path.Combine(root, "ucrt64", "bin");
            var mingw = Path.Combine(root, "mingw64", "bin");
            try
            {
                runner.IsTrue(FindGitCurlDirectory(bash) is null, "an install without curl is unavailable");
                Directory.CreateDirectory(mingw);
                File.WriteAllBytes(Path.Combine(mingw, "curl.exe"), []);
                runner.AreEqual(mingw, FindGitCurlDirectory(bash), "the older mingw64 layout is found");
                Directory.CreateDirectory(ucrt);
                File.WriteAllBytes(Path.Combine(ucrt, "curl.exe"), []);
                runner.AreEqual(ucrt, FindGitCurlDirectory(bash), "the newer ucrt64 layout is preferred");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            return Task.CompletedTask;
        });

        foreach (var target in AllTargets)
        {
            await runner.RunAsync($"Copy as: {target} snapshot of a hostile request", () =>
            {
                var result = CopyAs.Build(HostileRequest(), target);
                runner.IsTrue(result is not null, $"{target} builds");
                runner.AreEqual(Snapshots[target].ReplaceLineEndings("\n"), WithoutToken(result!.Text).ReplaceLineEndings("\n"), $"{target} snapshot");
                return Task.CompletedTask;
            });
        }

        await runner.RunAsync("Copy as: bash keeps a text body that holds a slash off the command line", () =>
        {
            string Bash(string body)
            {
                var request = new HttpRequestData { Method = "POST", Url = new Uri("https://api.example.test/p"), Body = Encoding.ASCII.GetBytes(body) };
                request.Headers.Add("Content-Type", "text/plain");
                return CopyAs.Build(request, CopyAsTarget.CurlBash)!.Text;
            }

            runner.IsTrue(Bash("/x").StartsWith("printf %s '/x' | curl ", StringComparison.Ordinal) && Bash("/x").Contains("--data-binary @-", StringComparison.Ordinal), "a body that starts with a slash goes on standard input");
            runner.IsTrue(!Bash("next=/home").Contains("--data-raw", StringComparison.Ordinal), "a body with =/ goes on standard input");
            runner.IsTrue(Bash("plain text").Contains("--data-raw 'plain text'", StringComparison.Ordinal), "a body with no slash stays on the command line");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: every escaper turns hostile text into one literal", () =>
        {
            runner.AreEqual("''", CopyAs.BashQuote(""), "bash: empty is an empty word");
            runner.AreEqual("'a'\\''b'", CopyAs.BashQuote("a'b"), "bash: a single quote closes, escapes and reopens");
            runner.AreEqual("'$(x) `y` ${z} !h \\ \"'", CopyAs.BashQuote("$(x) `y` ${z} !h \\ \""), "bash: nothing inside single quotes is interpreted");
            runner.AreEqual("'a'$'\\x0d''b'", CopyAs.BashQuote("a\rb"), "bash: a carriage return is an ANSI-C escape");
            runner.AreEqual("'a\nb'", CopyAs.BashQuote("a\nb"), "bash: a line feed stays literal inside single quotes");
            runner.AreEqual("$'\\xc3\\xa9'", CopyAs.BashQuote("\u00e9"), "bash: non-ASCII is its UTF-8 bytes, whatever the locale");
            runner.AreEqual("$'\\x00'", CopyAs.BashQuote(new byte[] { 0 }), "bash: even a NUL is written as an escape, never raw");

            runner.AreEqual("^\"^\"", CopyAs.CmdArg(""), "cmd: empty is an empty argument");
            runner.AreEqual("^\"a^&b^|c^<d^>e^^fg^(h^)^\"", CopyAs.CmdArg("a&b|c<d>e^fg(h)"), "cmd: every metacharacter is caret-escaped");
            var refused = false;
            try { CopyAs.CmdArg("a!b"); }
            catch (ArgumentException) { refused = true; }
            runner.IsTrue(refused, "cmd: an exclamation mark has no spelling that is right with delayed expansion on and off, so it is refused");
            foreach (var refusedText in new[] { "a＂b", "café", "a\tb", "a\u007fb" })
            {
                var refusedOther = false;
                try { CopyAs.CmdArg(refusedText); }
                catch (ArgumentException) { refusedOther = true; }
                runner.IsTrue(refusedOther, $"cmd: text outside printable ASCII is refused (U+{(int)refusedText[1]:X4}), because an ANSI argv can turn it into a quote");
            }
            runner.AreEqual("^\"100%^\"", CopyAs.CmdArg("100%"), "cmd: a percent sign is followed by the caret that closing quote brings");
            runner.AreEqual("^\"%^FOO:ZZ=%^\"", CopyAs.CmdArg("%FOO:ZZ=%"), "cmd: a variable name after a percent sign starts with a caret, so nothing expands");
            runner.AreEqual("^\"%^%^\"", CopyAs.CmdArg("%%"), "cmd: so does the second of two percent signs");
            runner.AreEqual("^\"%^&^\"", CopyAs.CmdArg("%&"), "cmd: and a percent sign before a metacharacter shares its caret");
            runner.AreEqual("^\"a%^b^\"", CopyAs.CmdArg("a%b"), "cmd: and one before an ordinary character gets its own");
            runner.AreEqual("^\"say \\^\"hi\\^\"^\"", CopyAs.CmdArg("say \"hi\""), "cmd: a quote is a backslash-escaped quote, caret-escaped");
            runner.AreEqual("^\"C:\\dir\\\\^\"", CopyAs.CmdArg("C:\\dir\\"), "cmd: a trailing backslash is doubled so it cannot escape the closing quote");
            runner.AreEqual("^\"a\\\\\\^\"b^\"", CopyAs.CmdArg("a\\\"b"), "cmd: backslashes before a quote are doubled");

            runner.AreEqual("\"a\\\"b\\\\c\\r\\n\\t\"", CopyAs.CurlConfigString("a\"b\\c\r\n\t"), "curl config: quote, backslash and control escapes");

            runner.AreEqual("''", CopyAs.PsString(""), "PowerShell: empty");
            runner.AreEqual("'it''s'", CopyAs.PsString("it's"), "PowerShell: a single quote is doubled");
            runner.AreEqual("'$(x) `y` \"z\"'", CopyAs.PsString("$(x) `y` \"z\""), "PowerShell: nothing inside single quotes is interpreted");
            runner.AreEqual("[string][char]0x2019", CopyAs.PsString("\u2019"), "PowerShell: a typographic quote, which PowerShell reads as a quote, is a [char]");
            runner.AreEqual("('a' + [char]0x2018 + 'b')", CopyAs.PsString("a\u2018b"), "PowerShell: ... in the middle of text too");
            runner.AreEqual("('a' + [char]0x000a + 'b')", CopyAs.PsString("a\nb"), "PowerShell: a line break is a [char]");
            runner.AreEqual("([string][char]0xd83d + [char]0xde00)", CopyAs.PsString("\ud83d\ude00"), "PowerShell: an astral character is its two UTF-16 units");

            // Past a hundred operands the value is one flat base64 literal, never a long + chain, and it
            // decodes to the exact text.
            var manyLines = string.Concat(Enumerable.Repeat("a\n", 4000)) + "😀";
            var flat = CopyAs.PsString(manyLines);
            const string FlatPrefix = "[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String('";
            runner.IsTrue(flat.StartsWith(FlatPrefix, StringComparison.Ordinal) && flat.EndsWith("'))", StringComparison.Ordinal), "PowerShell: a value with thousands of line breaks is one base64 expression");
            runner.IsTrue(!flat.Contains('+') && !flat.Contains("[char]", StringComparison.Ordinal), "PowerShell: ... with no operand chain in it");
            runner.AreEqual(manyLines, Encoding.Unicode.GetString(Convert.FromBase64String(flat[FlatPrefix.Length..^3])), "PowerShell: ... that decodes to the same text");
            runner.AreEqual(100, CopyAs.PsString(string.Concat(Enumerable.Repeat("\n", 100))).Count(c => c == '+') + 1, "PowerShell: a hundred operands is still a chain");
            runner.IsTrue(CopyAs.PsString(string.Concat(Enumerable.Repeat("\n", 101))).StartsWith(FlatPrefix, StringComparison.Ordinal), "PowerShell: and a hundred and one is flat");

            runner.AreEqual("\"a\\\"b\\\\</script>\\u2028\\u0000\"", CopyAs.JsString("a\"b\\</script>\u2028\0"), "JavaScript: quotes, backslashes and invisible characters");
            runner.AreEqual("\"\\ud83d\\ude00\"", CopyAs.JsString("\ud83d\ude00"), "JavaScript: an astral character is a surrogate pair");
            runner.AreEqual("\"\\U0001f600\\ufffd\"", CopyAs.PyString("\ud83d\ude00\ud800"), "Python: an astral character is one \\U escape, a lone surrogate is U+FFFD");
            runner.AreEqual("\"a\\\"b\\\\\\n\\u0000\"", CopyAs.CSharpString("a\"b\\\n\0"), "C#: quotes, backslashes and NUL");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: cmd.exe keeps every exclamation mark out of the command line", () =>
        {
            var request = Plain("POST", "https://h.test/a!b?x=!y!", Encoding.UTF8.GetBytes("{\"m\":\"Hello!\"}"));
            request.Headers.Add("X-A", "!SECRET!");
            request.Headers.Add("X-B", "plain");
            var result = CopyAs.Build(request, CopyAsTarget.CurlCmd)!;
            runner.IsTrue(!result.Text.Contains('!'), "no exclamation mark is left in the pasted text");
            runner.IsTrue(result.Notes.Contains(CopyAsNote.CmdValuesInFile), "and the note says the values went to a file");
            runner.IsTrue(result.Text.StartsWith("REM This request has values cmd.exe cannot carry safely"), "and so does the REM comment");

            runner.AreEqual("url = \"https://h.test/a!b?x=!y!\"\ngloboff\nrequest = \"POST\"\nheader = \"X-A: !SECRET!\"\nheader = \"X-B: plain\"\nheader = \"Content-Type:\"\ndata-raw = \"{\\\"m\\\":\\\"Hello!\\\"}\"\n",
                CmdConfigOf(result.Text), "the URL, method, every header (in order, so none is reordered) and the body are in the config");
            runner.IsTrue(result.Text.Contains("curl.exe --config \"%TEMP%\\piper-args-") && !result.Text.Contains("--header") && !result.Text.Contains("--data-raw") && !result.Text.Contains("h.test"),
                "the command reads the config and repeats none of them");

            // The same for a value outside printable ASCII: a captured U+FF02 becomes a double quote in the
            // ANSI command line of a curl.exe built with an ANSI main (Git for Windows' is), and would close
            // the quoted argument. The pasted text holds nothing but printable ASCII outside the base64.
            foreach (var odd in new[] { "\uff02", "\uff3c", "\u00a5", "\u201c", "\u2033", "\uff06", "\uff05", "\uff01", "\uff0d", "a\tb", "caf\u00e9" })
            {
                var oddRequest = Plain("POST", "https://h.test/p", Encoding.UTF8.GetBytes("a=b"));
                oddRequest.Headers.Add("X-Odd", "v " + odd + " -o " + odd + "C:\\pwn.txt" + odd);
                oddRequest.Headers.Add("Authorization", "Bearer secret");
                var oddResult = CopyAs.Build(oddRequest, CopyAsTarget.CurlCmd)!;
                var nonAscii = oddResult.Text.Where(c => (c < ' ' && c is not ('\r' or '\n')) || c > '~').ToArray();
                runner.IsTrue(nonAscii.Length == 0, $"U+{(int)odd[0]:X4}: the pasted text is printable ASCII");
                runner.IsTrue(!oddResult.Text.Contains("pwn") && !oddResult.Text.Contains("--header") && !oddResult.Text.Contains("secret"), $"U+{(int)odd[0]:X4}: no header is on the command line");
                runner.IsTrue(CmdConfigOf(oddResult.Text).Contains("header = \"X-Odd: v " + odd.Replace("\t", "\\t") + " -o ", StringComparison.Ordinal), $"U+{(int)odd[0]:X4}: it is in the config");
            }

            var plain = CopyAs.Build(Plain("GET", "https://h.test/p", []), CopyAsTarget.CurlCmd)!;
            runner.IsTrue(!plain.Text.Contains("--config") && !plain.Notes.Contains(CopyAsNote.CmdValuesInFile) && !plain.Text.Contains("del "), "without one, nothing changes");

            // Deletion does not depend on curl succeeding, and the base64 copy goes as soon as it is decoded.
            var cleaned = result.Text.Split("\r\n");
            runner.IsTrue(cleaned.Last().Contains(" & del /q ") && cleaned.Any(l => l.StartsWith("del /q \"%TEMP%\\piper-args-", StringComparison.Ordinal) && l.EndsWith(".b64\"", StringComparison.Ordinal)),
                "the config is deleted after curl whatever its exit, and the base64 file right after certutil");

            // Each copy has its own file names, so a name cannot be predicted and pre-created.
            var again = CopyAs.Build(request, CopyAsTarget.CurlCmd)!.Text;
            runner.IsTrue(again != result.Text && WithoutToken(again) == WithoutToken(result.Text), "the temporary file names change from copy to copy and nothing else does");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: headers a tool or connection owns are not copied, secrets are", () =>
        {
            var request = Plain("POST", "https://h.test/p", Encoding.UTF8.GetBytes("a=b"));
            request.Headers.Add("Host", "h.test");
            request.Headers.Add("Content-Length", "3");
            request.Headers.Add("Connection", "keep-alive");
            request.Headers.Add("Keep-Alive", "timeout=5");
            request.Headers.Add("Proxy-Connection", "keep-alive");
            request.Headers.Add("Proxy-Authorization", "Basic cHJveHk6c2VjcmV0");
            request.Headers.Add("Transfer-Encoding", "chunked");
            request.Headers.Add("TE", "trailers");
            request.Headers.Add("Upgrade", "websocket");
            request.Headers.Add("Cookie", "sid=abc123");
            request.Headers.Add("Authorization", "Bearer s3cr3t");
            foreach (var target in AllTargets)
            {
                var text = CopyAs.Build(request, target)!.Text;
                foreach (var dropped in new[] { "Content-Length", "Connection", "Keep-Alive", "Proxy-Connection", "Proxy-Authorization", "Transfer-Encoding", "TE", "Upgrade", "Host" })
                    runner.IsTrue(!text.Contains(dropped + ":", StringComparison.OrdinalIgnoreCase) && !text.Contains("\"" + dropped + "\"", StringComparison.OrdinalIgnoreCase),
                        $"{target}: {dropped} is not copied");
                runner.IsTrue(text.Contains("sid=abc123") && text.Contains("Bearer s3cr3t"), $"{target}: cookies and Authorization are copied as captured");
            }

            var overridden = Plain("GET", "https://h.test/p", []);
            overridden.Headers.Add("Host", "other.test");
            foreach (var target in AllTargets)
            {
                var result = CopyAs.Build(overridden, target)!;
                var isCurl = target is CopyAsTarget.CurlBash or CopyAsTarget.CurlCmd or CopyAsTarget.CurlPowerShell;
                runner.AreEqual(isCurl, result.Text.Contains("Host: other.test"), $"{target}: a Host that differs from the URL is kept only where it can be sent");
                runner.AreEqual(!isCurl, result.Notes.Contains(CopyAsNote.HostHeaderDropped), $"{target}: and the drop is reported");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: a header that cannot be written safely is left out and reported", () =>
        {
            var request = Plain("GET", "https://h.test/p", []);
            request.Headers.Add("X-Good", "ok");
            request.Headers.Add("X-Cr", "a\rInjected1: x");
            request.Headers.Add("X-Lf", "a\nInjected2: x");
            request.Headers.Add("X-Nul", "a\0Injected3");
            request.Headers.Add("X-Esc", "a\u001b[2JInjected4");
            request.Headers.Add("Bad Name", "Injected5");
            request.Headers.Add("Bad:Name", "Injected6");
            request.Headers.Add("Bad\"Name", "Injected7");
            request.Headers.Add("Bad\nName", "Injected8");
            request.Headers.Add("", "Injected9");
            request.Headers.Add("X-Lone", "a\ud800Injected10");
            foreach (var target in AllTargets)
            {
                var result = CopyAs.Build(request, target)!;
                runner.IsTrue(!result.Text.Contains("Injected"), $"{target}: no value or name that could break out is written");
                runner.IsTrue(result.Text.Contains("X-Good"), $"{target}: the safe header stays");
                runner.IsTrue(result.Notes.Contains(CopyAsNote.HeadersSkipped), $"{target}: the omission is reported");
                runner.IsTrue(result.Text.All(c => c is '\n' or '\r' or '\t' || c >= ' ' && c != '\u007f'), $"{target}: no control character in the output");
            }

            var invisible = Plain("GET", "https://h.test/p", []);
            invisible.Headers.Add("X-Bidi", "a\u202eb");
            runner.IsTrue(CopyAs.Build(invisible, CopyAsTarget.CurlCmd)!.Notes.Contains(CopyAsNote.HeadersSkipped), "cmd: a direction override is left out");
            runner.IsTrue(CopyAs.Build(invisible, CopyAsTarget.CurlPowerShell)!.Notes.Contains(CopyAsNote.HeadersSkipped), "PowerShell curl: a direction override is left out");
            runner.IsTrue(CopyAs.Build(invisible, CopyAsTarget.JavaScriptFetch)!.Text.Contains("\\u00e2\\u0080\\u00ae"), "JavaScript: its three wire bytes are written as escapes instead");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: the decoded body is written, with no Content-Encoding", () =>
        {
            var request = HostileRequest();
            foreach (var target in AllTargets)
            {
                var text = CopyAs.Build(request, target)!.Text;
                runner.IsTrue(!text.Contains("Content-Encoding", StringComparison.OrdinalIgnoreCase), $"{target}: Content-Encoding is not copied");
                runner.IsTrue(!text.Contains(Convert.ToBase64String(request.Body)), $"{target}: the gzip bytes are not written");
            }
            runner.IsTrue(CopyAs.Build(request, CopyAsTarget.PythonRequests)!.Text.Contains("%PATH%"), "the decoded JSON is what is written");

            var unknown = Plain("POST", "https://h.test/p", [1, 2, 3, 4]);
            unknown.Headers.Add("Content-Encoding", "x-weird");
            foreach (var target in AllTargets)
            {
                var text = CopyAs.Build(unknown, target)!.Text;
                runner.IsTrue(text.Contains("Content-Encoding"), $"{target}: an encoding that could not be removed is kept");
                runner.IsTrue(text.Contains("AQIDBA=="), $"{target}: and the original bytes are written as base64");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: binary and control-character bodies are base64, never pasted raw", () =>
        {
            var bodies = new[]
            {
                (Name: "NUL and high bytes", Bytes: new byte[] { 0, 1, 2, 0xff, 0xfe, 0x80 }, Type: "application/octet-stream"),
                (Name: "an escape sequence in text", Bytes: Encoding.UTF8.GetBytes("a\u001b[31mred"), Type: "text/plain"),
                (Name: "text in another charset", Bytes: Encoding.Latin1.GetBytes("caf\u00e9"), Type: "text/plain; charset=iso-8859-1"),
                (Name: "invalid UTF-8 as text", Bytes: new byte[] { 0x61, 0xc3, 0x28 }, Type: "text/plain"),
            };
            foreach (var body in bodies)
            {
                var request = Plain("POST", "https://h.test/p", body.Bytes);
                request.Headers.Add("Content-Type", body.Type);
                var base64 = Convert.ToBase64String(body.Bytes);
                foreach (var target in AllTargets)
                {
                    var result = CopyAs.Build(request, target)!;
                    runner.IsTrue(result.Text.Contains(base64), $"{target}: {body.Name} is written as base64");
                    runner.IsTrue(result.Notes.Contains(CopyAsNote.BodyAsBase64), $"{target}: {body.Name} is reported");
                    runner.IsTrue(!result.Text.Any(c => c < ' ' && c is not ('\n' or '\r' or '\t')), $"{target}: {body.Name} puts no control character in the output");
                }
            }

            runner.IsTrue(CopyAs.Build(Plain("POST", "https://h.test/p", [1, 2]), CopyAsTarget.CurlBash)!.Text.Contains("printf %s 'AQI=' | base64 -d | curl"), "bash decodes it on the way in");
            runner.IsTrue(CopyAs.Build(Plain("POST", "https://h.test/p", [1, 2]), CopyAsTarget.CurlCmd)!.Text.Contains("certutil -f -decode"), "cmd decodes it with certutil");
            runner.IsTrue(CopyAs.Build(Plain("POST", "https://h.test/p", [1, 2]), CopyAsTarget.CurlPowerShell)!.Text.Contains("FromBase64String('AQI=')"), "PowerShell decodes it into a temporary file");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: a body that is cut, incomplete or too large is left out with a comment", () =>
        {
            var incomplete = Plain("POST", "https://h.test/p", Encoding.UTF8.GetBytes("abcde"));
            incomplete.BodyTotalLength = 500;
            var oversized = Plain("POST", "https://h.test/p", Encoding.UTF8.GetBytes(new string('a', CopyAs.MaxInlineBodyBytes + 1)));
            var bomb = Plain("POST", "https://h.test/p", Gzip(new byte[ContentCodec.MaxDecodedBytes + 1024 * 1024]));
            bomb.Headers.Add("Content-Encoding", "gzip");

            foreach (var (name, request, note) in new[]
            {
                ("a prefix of a larger body", incomplete, CopyAsNote.BodyNotCaptured),
                ("a body over the inline limit", oversized, CopyAsNote.BodyTooLarge),
                ("a body whose decoding was cut", bomb, CopyAsNote.BodyDecodeCut),
            })
            {
                foreach (var target in AllTargets)
                {
                    var result = CopyAs.Build(request, target)!;
                    runner.IsTrue(result.Notes.Contains(note), $"{target}: {name} is reported");
                    runner.IsTrue(!result.Text.Contains("abcde") && !result.Text.Contains("aaaaaaaa") && !result.Text.Contains("data") && !result.Text.Contains("body:") && !result.Text.Contains("Body ="),
                        $"{target}: {name} writes no body");
                    runner.IsTrue(result.Text.Contains("The request body is not included"), $"{target}: {name} says so in a comment");
                }
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: a request that cannot be expressed gives nothing", () =>
        {
            foreach (var target in AllTargets)
            {
                runner.IsTrue(CopyAs.Build(new HttpRequestData { Method = "GET" }, target) is null, $"{target}: no URL");
                runner.IsTrue(CopyAs.Build(new HttpRequestData { Method = "GET", Url = new Uri("ftp://h.test/x") }, target) is null, $"{target}: not http or https");
                runner.IsTrue(CopyAs.Build(new HttpRequestData { Method = "GET", Url = new Uri("/relative", UriKind.Relative) }, target) is null, $"{target}: a relative URL");
                foreach (var method in new[] { "", "GE T", "GET;id", "GET\r\nX: y", "$(id)", "GE\"T" })
                    runner.IsTrue(CopyAs.Build(Plain(method, "https://h.test/", []), target) is null, $"{target}: method '{method.Replace("\r", "\\r").Replace("\n", "\\n")}' is not a token");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: methods, empty bodies and the fragment", () =>
        {
            runner.AreEqual("curl 'http://h.test/p?a=1' \\\n  --globoff", CopyAs.Build(Plain("GET", "http://h.test/p?a=1#frag", []), CopyAsTarget.CurlBash)!.Text, "bash: GET with nothing else, and no fragment");
            runner.AreEqual("curl 'http://h.test/' \\\n  --globoff \\\n  --head", CopyAs.Build(Plain("HEAD", "http://h.test/", []), CopyAsTarget.CurlBash)!.Text, "bash: HEAD is --head, not a method that would wait for a body");
            runner.AreEqual("curl 'http://h.test/' \\\n  --globoff \\\n  --request 'DELETE'", CopyAs.Build(Plain("DELETE", "http://h.test/", []), CopyAsTarget.CurlBash)!.Text, "bash: other methods are named");
            var withBody = CopyAs.Build(Plain("GET", "http://h.test/", Encoding.UTF8.GetBytes("x")), CopyAsTarget.CurlBash)!.Text;
            runner.IsTrue(withBody.Contains("--request 'GET'") && withBody.Contains("--header 'Content-Type:'"), "bash: GET with a body is named, and curl's own Content-Type is switched off");
            runner.IsTrue(CopyAs.Build(Plain("GET", "http://h.test/", []), CopyAsTarget.JavaScriptFetch)!.Text == "await fetch(\"http://h.test/\", {\n  redirect: \"manual\",\n});", "fetch: GET has no method line");
            runner.IsTrue(CopyAs.Build(Plain("PUT", "http://[::1]:8080/a b", []), CopyAsTarget.PythonRequests)!.Text.Contains("\"http://[::1]:8080/a%20b\""), "an IPv6 URL with a space is written escaped");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: repeated headers are merged where the target cannot repeat them", () =>
        {
            var request = Plain("GET", "https://h.test/", []);
            request.Headers.Add("Cookie", "a=1");
            request.Headers.Add("cookie", "b=2");
            request.Headers.Add("X-Dup", "1");
            request.Headers.Add("x-dup", "2");
            foreach (var target in AllTargets)
            {
                var all = CopyAs.Build(request, target)!;
                runner.IsTrue(all.Text.Contains("a=1; b=2") && !all.Text.Contains("\"b=2\"") && !all.Text.Contains(": b=2"), $"{target}: repeated Cookie headers are always one, joined with a semicolon");
            }

            var http2 = Plain("GET", "https://h.test/", []);
            http2.Headers.Add("cookie", "a=1");
            http2.Headers.Add("cookie", "b=2");
            http2.Headers.Add("cookie", "c=3");
            foreach (var target in AllTargets)
            {
                var result = CopyAs.Build(http2, target)!;
                runner.IsTrue(result.Text.Contains("a=1; b=2; c=3"), $"{target}: HTTP/2 cookie crumbs are rejoined");
                runner.IsTrue(!result.Notes.Contains(CopyAsNote.DuplicateHeadersMerged), $"{target}: and that is not reported as a lossy merge");
            }

            foreach (var target in new[] { CopyAsTarget.PythonRequests, CopyAsTarget.PowerShellWebRequest })
            {
                var result = CopyAs.Build(request, target)!;
                runner.IsTrue(result.Text.Contains("a=1; b=2") && result.Text.Contains("1, 2"), $"{target}: cookies join with a semicolon, others with a comma");
                runner.IsTrue(result.Notes.Contains(CopyAsNote.DuplicateHeadersMerged), $"{target}: the merge is reported");
            }
            runner.AreEqual(2, System.Text.RegularExpressions.Regex.Matches(CopyAs.Build(request, CopyAsTarget.CurlBash)!.Text, "x-dup:", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count, "curl keeps both");
            runner.IsTrue(!CopyAs.Build(request, CopyAsTarget.CurlBash)!.Notes.Contains(CopyAsNote.DuplicateHeadersMerged), "and says nothing");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: replays do not follow redirects, so credentials stay with the captured host", () =>
        {
            var request = Plain("GET", "https://h.test/", []);
            request.Headers.Add("Cookie", "sid=1");
            runner.IsTrue(CopyAs.Build(request, CopyAsTarget.PythonRequests)!.Text.Contains("allow_redirects=False,"), "requests");
            runner.IsTrue(CopyAs.Build(request, CopyAsTarget.JavaScriptFetch)!.Text.Contains("redirect: \"manual\","), "fetch");
            runner.IsTrue(CopyAs.Build(request, CopyAsTarget.CSharpHttpClient)!.Text.Contains("AllowAutoRedirect = false,"), "HttpClient");
            runner.IsTrue(CopyAs.Build(request, CopyAsTarget.PowerShellWebRequest)!.Text.Contains("MaximumRedirection = 0"), "Invoke-WebRequest");
            foreach (var target in new[] { CopyAsTarget.CurlBash, CopyAsTarget.CurlCmd, CopyAsTarget.CurlPowerShell })
                runner.IsTrue(!CopyAs.Build(request, target)!.Text.Contains("location", StringComparison.OrdinalIgnoreCase), $"{target}: curl follows none unless told to");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: pseudo-headers, upgrades and non-ASCII values", () =>
        {
            var request = Plain("GET", "https://h.test/", []);
            request.Headers.Add(":authority", "h.test");
            request.Headers.Add(":path", "/");
            request.Headers.Add("X-Ok", "1");
            foreach (var target in AllTargets)
            {
                var result = CopyAs.Build(request, target)!;
                runner.IsTrue(!result.Text.Contains(":authority") && !result.Notes.Contains(CopyAsNote.HeadersSkipped), $"{target}: an HTTP/2 pseudo-header is ignored without a complaint");
            }

            var upgrade = Plain("GET", "https://h.test/chat", []);
            upgrade.Headers.Add("Connection", "Upgrade");
            upgrade.Headers.Add("Upgrade", "websocket");
            upgrade.Headers.Add("Sec-WebSocket-Key", "dGhlIHNhbXBsZSBub25jZQ==");
            foreach (var target in AllTargets)
            {
                var result = CopyAs.Build(upgrade, target)!;
                runner.IsTrue(result.Notes.Contains(CopyAsNote.UpgradeDropped) && result.Text.Contains("will not switch protocols"), $"{target}: the dropped upgrade is reported");
                runner.IsTrue(!result.Text.Contains("websocket\"") && !result.Text.Contains("websocket'") && !result.Text.Contains("Upgrade:"), $"{target}: and not copied");
            }

            var unicode = Plain("GET", "https://h.test/", []);
            unicode.Headers.Add("X-Uni", Latin1Unicode);
            runner.IsTrue(CopyAs.Build(unicode, CopyAsTarget.PythonRequests)!.Text.Contains("b\"caf\\xc3\\xa9 \\xe2\\x80\\x99 \\xf0\\x9f\\x98\\x80\""), "requests: a non-ASCII value is the bytes that crossed the wire");
            runner.IsTrue(CopyAs.Build(unicode, CopyAsTarget.JavaScriptFetch)!.Text.Contains("\"caf\\u00c3\\u00a9 \\u00e2\\u0080\\u0099 \\u00f0\\u009f\\u0098\\u0080\""), "fetch: a ByteString, one character per wire byte");
            var csharp = CopyAs.Build(unicode, CopyAsTarget.CSharpHttpClient)!.Text;
            runner.IsTrue(csharp.Contains("RequestHeaderEncodingSelector = (_, _) => Encoding.Latin1,") && csharp.Contains("\"caf\\u00c3\\u00a9"), "HttpClient: Latin-1 encoding and one character per wire byte");
            runner.IsTrue(!CopyAs.Build(Plain("GET", "https://h.test/", []), CopyAsTarget.CSharpHttpClient)!.Text.Contains("RequestHeaderEncodingSelector"), "HttpClient: not asked for when every header is ASCII");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: the cmd temporary files have a random name per copy, one token for both", () =>
        {
            var a = CopyAs.Build(Plain("POST", "https://h.test/", [1, 2, 3]), CopyAsTarget.CurlCmd)!.Text;
            var again = CopyAs.Build(Plain("POST", "https://h.test/", [1, 2, 3]), CopyAsTarget.CurlCmd)!.Text;
            var name = System.Text.RegularExpressions.Regex.Match(a, @"piper-body-[0-9a-f]{16}\.bin").Value;
            var nameAgain = System.Text.RegularExpressions.Regex.Match(again, @"piper-body-[0-9a-f]{16}\.bin").Value;
            runner.IsTrue(name.Length > 0 && nameAgain.Length > 0 && name != nameAgain, "the same body gets a different file name each time, which another user cannot guess");
            runner.AreEqual(WithoutToken(a), WithoutToken(again), "and the text is otherwise the same");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Copy as: a cmd command too long for the prompt is flagged", () =>
        {
            var request = Plain("GET", "https://h.test/", []);
            for (var i = 0; i < 100; i++) request.Headers.Add("X-Long-" + i, new string('v', 100));
            var result = CopyAs.Build(request, CopyAsTarget.CurlCmd)!;
            runner.IsTrue(result.Notes.Contains(CopyAsNote.CommandTooLong), "the note is there");
            runner.IsTrue(result.Text.StartsWith("REM This command is longer than the 8191 characters"), "and so is a REM comment");
            return Task.CompletedTask;
        });

        await RoundTripsAsync(runner);
    }

    // ---------------------------------------------------------------- real shells and runtimes

    private static readonly (string Name, string Value)[] RoundTripHeaders =
    [
        ("X-Dollar", "$(echo pwn) ${HOME} `echo pwn` $HOME $PATH"),
        ("X-Quotes", "it's \"quoted\" \\ back\\"),
        ("X-Cmd", "100% %PATH% %USERNAME% a&b|c<d>e^fg (h) ;,= ^% %^"),
        ("X-Path", "C:\\dir\\sub\\"),
        ("X-Tab", "a\tb"),
        ("X-Empty", ""),
        ("Authorization", "Bearer abc$def`x`"),
        ("Cookie", "a=1; b=\"2\""),

        // Environment references of every shell. The child process holds a secret variable of each
        // name; none of these may be expanded, or the secret would travel to the origin.
        ("X-Env-Cmd", "%PIPER_COPYAS_SECRET% %PIPER_COPYAS_SECRET:ZZ=% %PIPER_COPYAS_SECRET:*x=% %PIPER_COPYAS_SECRET:~0,3% %PIPER_COPYAS_SECRET:s=A% %% %^ ^% %PATH%"),
        ("X-Env-Other", "$PIPER_COPYAS_SECRET ${PIPER_COPYAS_SECRET} $env:PIPER_COPYAS_SECRET ${env:PIPER_COPYAS_SECRET} $(echo $PIPER_COPYAS_SECRET) `$env:PIPER_COPYAS_SECRET"),
        ("X-Env-End", "100%"),
        ("X-Env-Pair", "%PIPER_COPYAS_SECRET:ZZ=%\""),
    ];

    // Values with an exclamation mark, which cmd.exe expands as !NAME! when delayed expansion is on
    // (cmd /v:on, or the DelayedExpansion registry value) and leaves alone when it is off. The cmd
    // snippet must deliver every one of these unchanged in both modes, and never the secret.
    private static readonly (string Name, string Value)[] BangHeaders =
    [
        ("X-Bang-Env", "!PIPER_COPYAS_SECRET!"),
        ("X-Bang-Slice", "!PIPER_COPYAS_SECRET:~0,3! !PIPER_COPYAS_SECRET:s=A! !PIPER_COPYAS_SECRET:*x=!"),
        ("X-Bang-Caret", "^! ^^! ^^^! !^ ^"),
        ("X-Bang-Double", "!! !!! a!b!c Hello!"),
        ("X-Bang-Mixed", "!PIPER_COPYAS_SECRET! %PIPER_COPYAS_SECRET% !%PIPER_COPYAS_SECRET%! %!PIPER_COPYAS_SECRET!% \"!PIPER_COPYAS_SECRET!\" & | < > ( ) ^"),
        ("X-Bang-Trailing", "100!"),
        ("X-Bang-Long", string.Concat(Enumerable.Repeat("ab!c^d%e&", 700))),
    ];

    private static readonly (string Name, byte[] Bytes, string ContentType)[] BangBodies =
    [
        ("JSON text with bangs", Encoding.UTF8.GetBytes("{\"m\":\"Hello!\",\"s\":\"!PIPER_COPYAS_SECRET!\",\"v\":\"!PIPER_COPYAS_SECRET:~0,3!\",\"c\":\"^!\",\"d\":\"!!\"}"), "application/json"),
        ("single-line text with bangs", Encoding.ASCII.GetBytes("x=!PIPER_COPYAS_SECRET! %PIPER_COPYAS_SECRET% !PIPER_COPYAS_SECRET:~1,2! ^! !! \"q\" & echo ran | more < > ( ) ^ !"), "text/plain"),
    ];

    // Added to the request URL when bang values are on: the query holds the same kinds of text.
    private const string BangQuery = "&t=!PIPER_COPYAS_SECRET!&u=!PIPER_COPYAS_SECRET:~0,3!&w=!!&x=a!b!c";

    // Characters that Windows maps to an ASCII quote or backslash when it converts a command line to an
    // ANSI code page (U+FF02 and U+2033 and U+02BA to a double quote on code page 1252 and others,
    // U+FF3C, U+00A5 and U+2216 to a backslash on some), and lookalikes of the other things a shell treats
    // specially. {0} is a file that must never be created: a header that broke out of its argument could
    // add "-o {0}" and have curl write the response there.
    private static readonly (string Name, string Format)[] BestFitHeaders =
    [
        ("X-BF-FF02", "a＂ -o ＂{0}＂ z"),
        ("X-BF-FF3C", "a＼ -o ＼{0}＼ z"),
        ("X-BF-00A5", "a¥ -o ¥{0}¥ z"),
        ("X-BF-201C", "a“ -o “{0}” z"),
        ("X-BF-2033", "a″ -o ″{0}″ z"),
        ("X-BF-02BA", "aʺ -o ʺ{0}ʺ z"),
        ("X-BF-FF07", "a＇ -o ＇{0}＇ z"),
        ("X-BF-2216", "a∖ -o ∖{0}∖ z"),
        ("X-BF-20A9", "a₩ -o ₩{0}₩ z"),
        ("X-BF-Look", "＆ ｜ ＾ ％ ！ －－{0} ％PIPER_COPYAS_SECRET％ ！PIPER_COPYAS_SECRET！ z"),
        ("X-BF-Mixed", "＂ & echo ran> \"{0}\" & ＂ %PIPER_COPYAS_SECRET% !PIPER_COPYAS_SECRET! café z"),
    ];

    private static readonly (string Name, byte[] Bytes, string ContentType)[] BestFitBodies =
    [
        ("best-fit text", Encoding.UTF8.GetBytes("＂ -o ＂pwn.txt＂ ＼＼ ¥ ″ ％PIPER_COPYAS_SECRET％ z"), "text/plain; charset=utf-8"),
        ("every byte value", [.. Enumerable.Range(0, 256).Select(i => (byte)i)], "application/octet-stream"),
    ];

    // Text that starts with a slash or holds "=/": Git for Windows bash turns such a word into a Windows
    // path (/x into X:/, next=/home into next=C:/Program Files/Git/home) before it reaches a native
    // curl.exe, unless the word is kept off the command line. Headers, cookies and the URL query hold the
    // same shapes and must arrive as they are as well.
    private static readonly (string Name, byte[] Bytes, string ContentType)[] PathLikeBodies =
    [
        ("slash text", Encoding.ASCII.GetBytes("/x"), "text/plain"),
        ("path text", Encoding.ASCII.GetBytes("/v1/items"), "text/plain"),
        ("assignment text", Encoding.ASCII.GetBytes("a=/b"), "application/x-www-form-urlencoded"),
        ("form text", Encoding.ASCII.GetBytes("next=/home&from=/a/b"), "application/x-www-form-urlencoded"),
        ("double slash text", Encoding.ASCII.GetBytes("//x"), "text/plain"),
        ("path list text", Encoding.ASCII.GetBytes("/x:/y"), "text/plain"),
        ("option text", Encoding.ASCII.GetBytes("--opt=/x"), "text/plain"),
    ];

    private static readonly (string Name, string Value)[] PathLikeHeaders =
    [
        ("Referer", "/home"),
        ("X-Next", "a=/b"),
        ("X-Path", "/v1/items"),
        ("X-Double", "//x"),
        ("X-List", "/a:/b;/c"),
        ("Cookie", "next=/home; a=/b"),
    ];

    private const string PathLikeQuery = "&next=/home&a=/b&c=//d";

    private const string Secret = "s3cr3t-LEAK-7f3a";

    // Values that would run a second command if they broke out of their argument; each would create
    // the marker file. {0} is the marker path.
    private static readonly string[] BreakOutValues =
    [
        "%\" & echo ran> \"{0}\" & \"",
        "x%\"&echo ran>\"{0}\"&\"",
        "\"; echo ran > '{0}'; \"",
        "'; echo ran > '{0}'; '",
        "$(echo ran > '{0}') `echo ran > '{0}'`",
        "$(Set-Content -LiteralPath '{0}' ran) `$(Set-Content -LiteralPath '{0}' ran)",
        "\"; Set-Content -LiteralPath '{0}' ran; \"",
        "' ; Set-Content -LiteralPath '{0}' ran ; '",
        "^\" & echo ran> \"{0}\"",
    ];

    private static readonly (string Name, byte[] Bytes, string ContentType)[] RoundTripBodies =
    [
        ("hostile text", Encoding.UTF8.GetBytes("line1 \"q\" $(id) `x`\n'x' %PATH% ^& \r\nend\\"), "text/plain"),
        ("single-line hostile text", Encoding.ASCII.GetBytes("x=%PIPER_COPYAS_SECRET% %PIPER_COPYAS_SECRET:ZZ=% %PIPER_COPYAS_SECRET:~0,3% $PIPER_COPYAS_SECRET $env:PIPER_COPYAS_SECRET ${env:PIPER_COPYAS_SECRET} $(echo $PIPER_COPYAS_SECRET) 100% \"q\" & echo ran | more ^"), "text/plain"),
        ("every byte value", [.. Enumerable.Range(0, 256).Select(i => (byte)i)], "application/octet-stream"),
        ("non-ASCII text", Encoding.UTF8.GetBytes("h\u00e9llo \u2019 \ud83d\ude00"), "text/plain; charset=utf-8"),
    ];

    private static async Task RoundTripsAsync(TestRunner runner)
    {
        var system = Environment.SystemDirectory;
        var curl = Path.Combine(system, "curl.exe");
        var bash = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "usr", "bin", "bash.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "usr", "bin", "bash.exe"),
        }.FirstOrDefault(File.Exists);
        var powershell = Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe");
        var node = FindOnPath("node.exe");
        var python = FindOnPath("python.exe");

        // cmd.exe twice, with delayed expansion off (the default) and on (cmd /v:on, or the registry
        // value of the same effect), each with the values that have no exclamation mark and with the
        // ones that do. The snippet has to deliver every value unchanged in all of them.
        foreach (var delayed in new[] { "off", "on" })
        {
            foreach (var set in new[] { RoundTripSet.Simple, RoundTripSet.Plain, RoundTripSet.Bang, RoundTripSet.BestFit })
            {
                var cmd = Path.Combine(system, "cmd.exe");
                var missing = File.Exists(curl) ? null : "curl.exe is not installed";
                await RoundTripAsync(runner, $"cmd.exe /v:{delayed}", CopyAsTarget.CurlCmd, missing, (text, dir) => RunCmdByLineAsync(cmd, delayed, text, dir), set);

                // The same text typed at the prompt: cmd reads it line by line from stdin, which is where a
                // caret at the end of a line continues the command. A quote left open by a bad escape would
                // end that continuation and run the following lines as commands of their own.
                await RoundTripAsync(runner, $"cmd.exe /v:{delayed}, typed at the prompt", CopyAsTarget.CurlCmd, missing,
                    (text, dir) => ExecuteAsync(cmd, "/d /v:" + delayed, text + "\r\nexit\r\n", dir), set);
            }
        }

        // Git for Windows' curl.exe is built with an ANSI main: Windows converts its command line to the
        // ANSI code page, so a U+FF02 on it would become a double quote. With the request in the config
        // file nothing like that reaches it; this run puts that curl first on PATH to prove it.
        var gitCurl = bash is null ? null : FindGitCurlDirectory(bash);
        var gitCurlMissing = gitCurl is null ? "Git for Windows' curl.exe is not installed" : null;
        await RoundTripAsync(runner, "cmd.exe with Git for Windows' curl.exe", CopyAsTarget.CurlCmd, gitCurlMissing,
            (text, dir) => RunCmdByLineAsync(Path.Combine(system, "cmd.exe"), "on", text, dir, gitCurl), RoundTripSet.BestFit);
        await RoundTripAsync(runner, "cmd.exe with Git for Windows' curl.exe, typed at the prompt", CopyAsTarget.CurlCmd, gitCurlMissing,
            (text, dir) => ExecuteAsync(Path.Combine(system, "cmd.exe"), "/d /v:on", text + "\r\nexit\r\n", dir, curlDirectory: gitCurl), RoundTripSet.BestFit);

        await CmdFuzzAsync(runner, Path.Combine(system, "cmd.exe"), File.Exists(curl) ? null : "curl.exe is not installed");
        await CmdMissingFileAsync(runner, Path.Combine(system, "cmd.exe"), File.Exists(curl) ? null : "curl.exe is not installed");

        var curlPowerShell = File.Exists(curl) && File.Exists(powershell) ? null : "curl.exe or powershell.exe is not installed";
        await RoundTripAsync(runner, "PowerShell, curl.exe", CopyAsTarget.CurlPowerShell, curlPowerShell, (text, dir) => RunPowerShellAsync(powershell, text, dir));

        // The console re-encodes what PowerShell pipes to curl, so non-ASCII text is not asserted to arrive
        // verbatim here; that it cannot reach curl's argv (no option is added, nothing leaks) still is.
        await RoundTripAsync(runner, "PowerShell, curl.exe", CopyAsTarget.CurlPowerShell, curlPowerShell,
            (text, dir) => RunPowerShellAsync(powershell, text, dir), RoundTripSet.BestFit, verbatimNonAscii: false);

        await RoundTripAsync(runner, "PowerShell, Invoke-WebRequest", CopyAsTarget.PowerShellWebRequest,
            File.Exists(powershell) ? null : "powershell.exe is not installed",
            (text, _) => RunPowerShellAsync(powershell, text));
        await LargeValuesAsync(runner, powershell);
        await PowerShellCurlCleanupAsync(runner, powershell);

        var bashMissing = bash is null ? "no bash (Git for Windows) is installed" : null;
        await RoundTripAsync(runner, "bash", CopyAsTarget.CurlBash, bashMissing, (text, dir) => ExecuteAsync(bash!, "-l -s", text, dir));
        await RoundTripAsync(runner, "bash", CopyAsTarget.CurlBash, bashMissing, (text, dir) => ExecuteAsync(bash!, "-l -s", text, dir, msysPathConversion: true), RoundTripSet.BestFit);

        // With the default path conversion on, which an interactive Git for Windows bash has, a word such
        // as /x or next=/home must still reach curl.exe as it is.
        await RoundTripAsync(runner, "bash with path conversion", CopyAsTarget.CurlBash, bashMissing, (text, dir) => ExecuteAsync(bash!, "-l -s", text, dir, msysPathConversion: true), RoundTripSet.PathLike);

        // Ctrl+C on a curl that never gets an answer ends curl with SIGINT, which makes bash abandon the
        // command list it is running: a trailing cleanup would never run and the temporary config, which
        // holds the request's credentials, would stay on disk. A curl function stands in for the one that
        // is interrupted: it checks that the config exists, then dies of SIGINT as curl does.
        await runner.RunAsync("Copy as: CurlBash removes its temporary curl config when curl is interrupted", async () =>
        {
            if (bash is null)
            {
                runner.ToolMissing("bash: no bash (Git for Windows) is installed");
                return;
            }

            var dir = Path.Combine(Path.GetTempPath(), "copyas-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var request = new HttpRequestData
                {
                    Method = "POST",
                    Url = new Uri("http://127.0.0.1:9/hang"),
                    Body = [.. Enumerable.Range(0, 256).Select(i => (byte)i)],
                };
                request.Headers.Add("Authorization", "Bearer interrupt-secret");
                request.Headers.Add("X-Uni", Latin1Unicode); // a byte above ASCII and a binary body: the config goes to a temporary file
                var text = CopyAs.Build(request, CopyAsTarget.CurlBash)!.Text;
                var script = "curl() { compgen -G \"${TMPDIR:-/tmp}/piper-*\" > /dev/null && echo CONFIG-PRESENT >&2; kill -INT 0; sleep 5; }\n" + text + "\n";

                var output = await ExecuteAsync(bash, "-l -s", script, dir);
                runner.IsTrue(output.Contains("CONFIG-PRESENT", StringComparison.Ordinal), "the config was on disk when curl was interrupted; output: " + output);
                runner.AreEqual(0, Directory.GetFiles(dir, "piper-*").Length, "no temporary curl config is left behind");
            }
            finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
        });

        await runner.RunAsync("Copy as: bash quoting reproduces every byte it is given", async () =>
        {
            if (bash is null)
            {
                runner.ToolMissing("bash: no bash (Git for Windows) is installed");
                return;
            }

            // Every byte but NUL (which bash cannot hold), the same bytes the other way round, then text.
            byte[][] samples =
            [
                [.. Enumerable.Range(1, 255).Select(i => (byte)i)],
                [.. Enumerable.Range(1, 255).Reverse().Select(i => (byte)i)],
                Encoding.UTF8.GetBytes("it's $(id) `x` \\ \"q\" caf\u00e9 \ud83d\ude00\n\r\t'"),
                Encoding.UTF8.GetBytes("'''"),
            ];
            foreach (var sample in samples)
            {
                var script = "printf %s " + CopyAs.BashQuote(sample) + " | od -An -v -tx1";
                var output = await ExecuteAsync(bash, "-l -s", script);
                var hex = string.Concat(output[(output.IndexOf(':') + 1)..output.IndexOf("--- stderr ---", StringComparison.Ordinal)].Where(Uri.IsHexDigit));
                runner.AreEqual(Convert.ToHexString(sample).ToLowerInvariant(), hex, $"bash prints back all {sample.Length} bytes");
            }
        });

        async Task<string> RunNodeAsync(string text, string dir)
        {
            var file = Path.Combine(dir, "snippet.mjs");
            await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
            return await ExecuteAsync(node!, "\"" + file + "\"", null);
        }

        await RoundTripAsync(runner, "Node.js fetch", CopyAsTarget.JavaScriptFetch, node is null ? "node.exe is not on PATH" : null, RunNodeAsync);

        var requests = python is not null && (await ExecuteAsync(python, "-c \"import requests\"", null)).StartsWith("exit 0", StringComparison.Ordinal);
        async Task<string> RunPythonAsync(string text, string dir)
        {
            var file = Path.Combine(dir, "snippet.py");
            await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
            return await ExecuteAsync(python!, "\"" + file + "\"", null);
        }

        await RoundTripAsync(runner, "Python requests", CopyAsTarget.PythonRequests, requests ? null : "python with the requests package is not installed", RunPythonAsync);

        await CSharpCompilesAsync(runner);

        // A server that answers with a redirect must not be able to move the copied Cookie and
        // Authorization on to another host: the second origin must never be contacted.
        await RedirectRoundTripAsync(runner, "Node.js fetch", CopyAsTarget.JavaScriptFetch, node is null ? "node.exe is not on PATH" : null, RunNodeAsync);
        await RedirectRoundTripAsync(runner, "Python requests", CopyAsTarget.PythonRequests, requests ? null : "python with the requests package is not installed", RunPythonAsync);
        await RedirectRoundTripAsync(runner, "PowerShell, Invoke-WebRequest", CopyAsTarget.PowerShellWebRequest,
            File.Exists(powershell) ? null : "powershell.exe is not installed", (text, _) => RunPowerShellAsync(powershell, text));
        await RedirectRoundTripAsync(runner, "cmd.exe", CopyAsTarget.CurlCmd, File.Exists(curl) ? null : "curl.exe is not installed",
            (text, _) => RunCmdByLineAsync(Path.Combine(system, "cmd.exe"), "off", text));
    }

    private static readonly string[] FuzzPieces =
    [
        "!", "^", "%", "&", "|", "<", ">", "(", ")", "\"", "'", "\\", " ", "a", "B1", ":", "~0,3", "=", "s=A", "*", ";", ",", "$", "`", "/", "-",
        "PIPER_COPYAS_SECRET", "!PIPER_COPYAS_SECRET!", "%PIPER_COPYAS_SECRET%", "!PIPER_COPYAS_SECRET:~0,3!", "%PIPER_COPYAS_SECRET:~0,3%", "!NOPE!", "!!", "^!", "^^!", "%%", "%^",
    ];

    // Header values also draw on characters an ANSI code page maps to ASCII quotes and backslashes.
    private static readonly string[] FuzzHeaderPieces = [.. FuzzPieces, "＂", "＼", "¥", "“", "″", "é", "＆", "％"];

    private static readonly string[] FuzzUrlPieces =
    [
        "!", "&", "=", "a", "'", "(", ")", "$", "x", "~", "*", "+", ",", ";", "@", ":", "%25", "!PIPER_COPYAS_SECRET!", "%25PIPER_COPYAS_SECRET%25", "!!", "!PIPER_COPYAS_SECRET:~0,3!",
    ];

    private static string FuzzText(Random random, string[] pieces, int maxPieces)
    {
        var sb = new StringBuilder();
        for (var n = random.Next(1, maxPieces + 1); n > 0; n--) sb.Append(pieces[random.Next(pieces.Length)]);
        return sb.ToString();
    }

    /// <summary>
    /// Random text drawn from the pieces cmd.exe cares about, pasted into cmd.exe with delayed expansion
    /// off and on, as a command and typed at the prompt: every value must arrive as it was, and the
    /// secret variable never. PIPER_COPYAS_FUZZ sets how many requests each of the four runs sends.
    /// </summary>
    private static async Task CmdFuzzAsync(TestRunner runner, string cmd, string? unavailable)
    {
        await runner.RunAsync("Copy as: cmd.exe delivers random hostile text with delayed expansion off and on", async () =>
        {
            if (unavailable is not null)
            {
                runner.ToolMissing($"cmd.exe: {unavailable}");
                return;
            }

            var requests = int.TryParse(Environment.GetEnvironmentVariable("PIPER_COPYAS_FUZZ"), out var configured) && configured > 0 ? configured : 4;
            foreach (var delayed in new[] { "off", "on" })
            {
                foreach (var typed in new[] { false, true })
                {
                    var cases = 0;
                    var failures = new List<string>();
                    for (var r = 0; r < requests; r++)
                    {
                        var random = new Random(8300 + r);
                        using var origin = new CaptureOrigin();
                        var hasBody = random.Next(2) == 0;
                        var request = new HttpRequestData
                        {
                            Method = hasBody ? "POST" : "GET",
                            Url = new Uri(origin.Url + "f?q=" + FuzzText(random, FuzzUrlPieces, 12)),
                            Body = hasBody ? Encoding.ASCII.GetBytes(FuzzText(random, FuzzPieces, 40)) : [],
                        };
                        var expected = new List<(string Name, string Value)>();
                        for (var h = 0; h < 8; h++)
                        {
                            // Header values are bytes: the Latin-1 reading of the UTF-8 of the text.
                            var value = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(FuzzText(random, FuzzHeaderPieces, 14).Trim(' ')));
                            expected.Add(("X-F" + h, value));
                            request.Headers.Add("X-F" + h, value);
                        }
                        if (hasBody)
                        {
                            expected.Add(("Content-Type", "text/plain"));
                            request.Headers.Add("Content-Type", "text/plain");
                        }

                        var text = CopyAs.Build(request, CopyAsTarget.CurlCmd)!.Text;
                        if (text.Contains('!')) failures.Add($"request {r}: the pasted text holds an exclamation mark");
                        var diagnostics = typed
                            ? await ExecuteAsync(cmd, "/d /v:" + delayed, text + "\r\nexit\r\n")
                            : await RunCmdByLineAsync(cmd, delayed, text);
                        var seen = await origin.WaitAsync(TimeSpan.FromSeconds(20));
                        if (seen is null) { failures.Add($"request {r}: nothing arrived; output: {diagnostics}"); continue; }

                        cases += 1 + expected.Count + (hasBody ? 1 : 0);
                        if (seen.RequestLine != (hasBody ? "POST " : "GET ") + request.Url!.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped))
                            failures.Add($"request {r}: the URL arrived as {seen.RequestLine}");
                        foreach (var (name, value) in expected)
                            if (!seen.Headers.Any(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && h.Value == value))
                                failures.Add($"request {r}: {name} <{value}> arrived as <{string.Join(" | ", seen.Headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value))}>");
                        if (hasBody && !request.Body.SequenceEqual(seen.Body))
                            failures.Add($"request {r}: the body <{Encoding.ASCII.GetString(request.Body)}> arrived as <{Encoding.Latin1.GetString(seen.Body)}>");
                        var everything = seen.RequestLine + "\n" + string.Join("\n", seen.Headers.Select(h => h.Name + ": " + h.Value)) + "\n" + Encoding.Latin1.GetString(seen.Body);
                        if (everything.Contains(Secret)) failures.Add($"request {r}: the secret reached the origin");
                    }

                    Console.WriteLine($"   cmd.exe /v:{delayed}{(typed ? ", typed" : string.Empty)}: {cases} values in {requests} requests, {failures.Count} failed");
                    runner.IsTrue(failures.Count == 0, $"cmd.exe /v:{delayed}{(typed ? ", typed at the prompt" : string.Empty)}: {cases} random values arrive unchanged and no secret does"
                        + (failures.Count == 0 ? string.Empty : "; first failures: " + string.Join(" || ", failures.Take(3))));
                }
            }
        });
    }

    /// <summary>
    /// When certutil fails (or the temporary file is gone) curl must send nothing at all, never a request
    /// without the headers that were in the file, and the files still get deleted.
    /// </summary>
    private static async Task CmdMissingFileAsync(TestRunner runner, string cmd, string? unavailable)
    {
        await runner.RunAsync("Copy as: cmd.exe sends nothing when the temporary file was not written", async () =>
        {
            if (unavailable is not null)
            {
                runner.ToolMissing($"cmd.exe: {unavailable}");
                return;
            }

            foreach (var broken in new[] { "piper-args-", "piper-body-" })
            {
                using var origin = new CaptureOrigin();
                var dir = Path.Combine(Path.GetTempPath(), "copyas-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var request = new HttpRequestData { Method = "POST", Url = new Uri(origin.Url + "x"), Body = [1, 2, 3] };
                request.Headers.Add("Authorization", "Bearer must-not-be-sent-alone");
                request.Headers.Add("X-Bang", "Hello!");

                // certutil never ran: drop its line for one of the two files.
                var lines = CopyAs.Build(request, CopyAsTarget.CurlCmd)!.Text.Split("\r\n")
                    .Where(l => !(l.StartsWith("certutil ", StringComparison.Ordinal) && l.Contains(broken, StringComparison.Ordinal))).ToList();
                string diagnostics;
                bool left;
                try
                {
                    diagnostics = await RunCmdByLineAsync(cmd, "off", string.Join("\r\n", lines), dir);
                    left = Directory.GetFiles(dir, "piper-*").Length > 0;
                }
                finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }

                runner.IsTrue(await origin.WaitAsync(TimeSpan.FromSeconds(3)) is null, $"{broken}: curl sent no request (output: {diagnostics})");
                runner.IsTrue(diagnostics.Contains("curl:", StringComparison.Ordinal), $"{broken}: and said why (output: {diagnostics})");
                runner.IsTrue(!left, $"{broken}: the files that did exist were still deleted");
            }
        });
    }

    private static async Task RedirectRoundTripAsync(TestRunner runner, string shell, CopyAsTarget target, string? unavailable, Func<string, string, Task<string>> run)
    {
        await runner.RunAsync($"Copy as: {target} pasted into {shell} does not follow a redirect", async () =>
        {
            if (unavailable is not null)
            {
                runner.ToolMissing($"{shell}: {unavailable}");
                return;
            }

            using var elsewhere = new CaptureOrigin();
            using var origin = new CaptureOrigin(elsewhere.Url + "moved");
            var dir = Path.Combine(Path.GetTempPath(), "copyas-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var request = new HttpRequestData { Method = "GET", Url = new Uri(origin.Url + "start") };
            request.Headers.Add("Authorization", "Bearer redirect-secret");
            request.Headers.Add("Cookie", "sid=redirect-secret");

            string diagnostics;
            try { diagnostics = await run(CopyAs.Build(request, target)!.Text, dir); }
            finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }

            var first = await origin.WaitAsync(TimeSpan.FromSeconds(20));
            runner.IsTrue(first is not null, $"{shell}: the captured host received the request" + (first is null ? "; output: " + diagnostics : string.Empty));
            var second = await elsewhere.WaitAsync(TimeSpan.FromSeconds(1));
            runner.IsTrue(second is null, $"{shell}: the host the redirect named received nothing (it got: {second?.RequestLine})");
        });
    }

    // Both is Plain and Bang together, for the targets that need no separate treatment. Simple holds only
    // printable ASCII with no exclamation mark, which the cmd snippet writes on its command line.
    private enum RoundTripSet { Both, Plain, Bang, Simple, BestFit, PathLike }

    /// <summary>Runs the pasted text of every cmd.exe line as its own <c>cmd /c</c>; a trailing caret continues a line.</summary>
    private static async Task<string> RunCmdByLineAsync(string cmd, string delayed, string text, string? temp = null, string? curlDirectory = null)
    {
        var output = new StringBuilder();
        var logical = new StringBuilder();
        foreach (var line in text.Split("\r\n"))
        {
            if (line.EndsWith('^')) { logical.Append(line[..^1]); continue; }
            logical.Append(line);
            output.AppendLine(await ExecuteAsync(cmd, "/d /v:" + delayed + " /s /c \"" + logical + "\"", null, temp, curlDirectory: curlDirectory));
            logical.Clear();
        }
        return output.ToString();
    }

    private static async Task RoundTripAsync(TestRunner runner, string shell, CopyAsTarget target, string? unavailable, Func<string, string, Task<string>> run, RoundTripSet set = RoundTripSet.Both, bool verbatimNonAscii = true)
    {
        var label = set switch
        {
            RoundTripSet.Bang => "values with an exclamation mark",
            RoundTripSet.Simple => "plain ASCII values",
            RoundTripSet.BestFit => "best-fit characters",
            RoundTripSet.PathLike => "values that look like paths",
            _ => "every value",
        };
        await runner.RunAsync($"Copy as: {target} pasted into {shell} delivers {label} unchanged", async () =>
        {
            if (unavailable is not null)
            {
                // Counted apart from the passes, and a failure under CI, where every tool is expected.
                runner.ToolMissing($"{shell}: {unavailable}");
                return;
            }

            var plain = set is RoundTripSet.Both or RoundTripSet.Plain;
            var bang = set is RoundTripSet.Both or RoundTripSet.Bang;
            var simple = set == RoundTripSet.Simple;
            var bestFit = set == RoundTripSet.BestFit;
            var pathLike = set == RoundTripSet.PathLike;
            IEnumerable<(string Name, byte[] Bytes, string ContentType)> bodies =
                (plain ? RoundTripBodies : []).Concat(bang ? BangBodies : []).Concat(bestFit ? BestFitBodies : [])
                    .Concat(simple ? RoundTripBodies.Where(b => b.Name is "single-line hostile text" or "every byte value") : [])
                    .Concat(pathLike ? PathLikeBodies : []);
            IEnumerable<(string Name, string Value)> headers =
                (plain ? RoundTripHeaders : []).Concat(bang ? BangHeaders : []).Concat(simple ? RoundTripHeaders.Where(h => h.Name != "X-Tab") : [])
                    .Concat(pathLike ? PathLikeHeaders : []);
            var query = bang ? BangQuery : pathLike ? PathLikeQuery : string.Empty;
            foreach (var body in bodies)
            {
                using var origin = new CaptureOrigin();
                var dir = Path.Combine(Path.GetTempPath(), "copyas-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var marker = Path.Combine(dir, "ran.txt");
                var pwn = Path.Combine(dir, "pwn.txt");

                var request = new HttpRequestData
                {
                    Method = "POST",
                    Url = new Uri(origin.Url + "p/it's?q=$(id)&r=%25&s=`x`" + query),
                    Body = body.Bytes,
                };
                var expected = headers.Append(("Content-Type", body.ContentType)).ToList();
                for (var i = 0; i < BreakOutValues.Length; i++)
                    expected.Add(("X-Break-" + i, string.Format(BreakOutValues[i], marker)));

                // A header value is bytes: its characters here are the Latin-1 reading of the UTF-8 bytes.
                var bestFitExpected = new List<(string Name, string Value)>();
                if (bestFit)
                    foreach (var (name, format) in BestFitHeaders)
                        bestFitExpected.Add((name, Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(string.Format(format, pwn)))));
                expected.AddRange(bestFitExpected);

                // The runtimes that can carry bytes above 127 in a header must deliver them exactly.
                if (target is CopyAsTarget.PythonRequests or CopyAsTarget.JavaScriptFetch or CopyAsTarget.PowerShellWebRequest)
                    expected.Add(("X-Uni", Latin1Unicode));
                foreach (var (name, value) in expected) request.Headers.Add(name, value);

                // Windows PowerShell 5.1 does not send a Cookie header given to Invoke-WebRequest; the
                // snippet says so, and this pins that it does.
                if (target == CopyAsTarget.PowerShellWebRequest)
                {
                    expected.RemoveAll(h => h.Item1 == "Cookie");
                    runner.IsTrue(CopyAs.Build(request, target)!.Notes.Contains(CopyAsNote.CookieNeedsPowerShell7), $"{shell}: the Cookie limitation is reported");
                }

                var text = CopyAs.Build(request, target)!.Text;

                // Delayed expansion reads every exclamation mark in the line, so none may be there at all.
                if (target == CopyAsTarget.CurlCmd)
                {
                    runner.IsTrue(!text.Contains('!'), $"{shell}, {body.Name}: the pasted cmd text holds no exclamation mark");

                    // An ANSI command line can turn a character above ASCII into a quote, so none may be on it.
                    runner.IsTrue(!text.Any(c => (c < ' ' && c is not ('\r' or '\n')) || c > '~'), $"{shell}, {body.Name}: the pasted cmd text is printable ASCII");
                    if (simple) runner.IsTrue(!text.Contains("--config"), $"{shell}, {body.Name}: plain ASCII values stay on the command line");
                    else if (bestFit || bang) runner.IsTrue(text.Contains("--config") && !text.Contains("--header"), $"{shell}, {body.Name}: the headers are in the config, not on the command line");
                }

                string diagnostics;
                bool ran, injected, left;
                try
                {
                    diagnostics = await run(text, dir);
                    ran = File.Exists(marker);
                    injected = File.Exists(pwn);
                    left = Directory.GetFiles(dir, "piper-*").Length > 0;
                }
                finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
                runner.IsTrue(!ran, $"{shell}, {body.Name}: no second command ran");
                runner.IsTrue(!injected, $"{shell}, {body.Name}: no extra curl option wrote a file");
                if (target is CopyAsTarget.CurlCmd or CopyAsTarget.CurlPowerShell) runner.IsTrue(!left, $"{shell}, {body.Name}: no temporary file is left behind");

                var seen = await origin.WaitAsync(TimeSpan.FromSeconds(20));
                runner.IsTrue(seen is not null, $"{shell}, {body.Name}: the origin received a request" + (seen is null ? "; output: " + diagnostics : string.Empty));
                if (seen is null) continue;

                runner.AreEqual("POST " + request.Url!.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped), seen.RequestLine, $"{shell}, {body.Name}: the URL arrives unchanged");
                foreach (var (name, value) in expected)
                {
                    if (!verbatimNonAscii && bestFitExpected.Any(b => b.Name == name)) continue;
                    var got = seen.Headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
                    runner.IsTrue(got.Contains(value), $"{shell}, {body.Name}: {name} arrives verbatim (got: {string.Join(" | ", got)})");
                }
                runner.IsTrue(!seen.Headers.Any(h => h.Name.Equals("pwn", StringComparison.OrdinalIgnoreCase)), $"{shell}, {body.Name}: nothing was injected");
                var everything = seen.RequestLine + "\n" + string.Join("\n", seen.Headers.Select(h => h.Name + ": " + h.Value)) + "\n" + Encoding.Latin1.GetString(seen.Body);
                runner.IsTrue(!everything.Contains(Secret), $"{shell}, {body.Name}: an environment variable's value never reaches the origin");
                if (verbatimNonAscii || body.Bytes.All(b => b < 0x80))
                    runner.IsTrue(body.Bytes.SequenceEqual(seen.Body),
                        $"{shell}, {body.Name}: the body arrives byte for byte ({seen.Body.Length} of {body.Bytes.Length} bytes)");
            }
        });
    }

    /// <summary>
    /// Windows PowerShell 5.1 has a fixed parser stack: a value written as one long chain of operands
    /// ("a" + [char]10 + "b" + ...) overflows it and kills the whole process, which is what a pasted
    /// multi-line body of a few thousand lines did. The script goes through a file because the
    /// command line cannot hold a body of the size cap.
    /// </summary>
    private static async Task LargeValuesAsync(TestRunner runner, string powershell)
    {
        var emoji = Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("😀", 1500))));
        var cases = new (string Name, byte[] Body, string ContentType)[]
        {
            ("8,000 bytes of short lines", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("a\n", 4000))), "text/plain"),
            ("a body of the size cap, all line breaks", Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("a\r\n", CopyAs.MaxInlineBodyBytes / 3)).PadRight(CopyAs.MaxInlineBodyBytes, '\n')), "text/plain"),
            ("non-ASCII lines", Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("hé😀\t\r\n", 3000))), "text/plain; charset=utf-8"),
            ("no body", [], "text/plain"),
        };

        foreach (var (name, body, contentType) in cases)
        {
            await runner.RunAsync($"Copy as: PowerShell, Invoke-WebRequest delivers {name} and long header values unchanged", async () =>
            {
                if (!File.Exists(powershell))
                {
                    runner.ToolMissing("PowerShell, Invoke-WebRequest: powershell.exe is not installed");
                    return;
                }

                using var origin = new CaptureOrigin();
                var request = new HttpRequestData { Method = "POST", Url = new Uri(origin.Url + "large"), Body = body };
                request.Headers.Add("Content-Type", contentType);

                // A header value has no line break (such a value is left out), but it can hold thousands of
                // characters that are not printable: tabs, and the C1 bytes inside a UTF-8 emoji.
                var tabs = string.Concat(Enumerable.Repeat("a\t", 4000)) + "z";
                request.Headers.Add("X-Tabs", tabs);
                request.Headers.Add("X-Emoji", emoji);
                request.Headers.Add("User-Agent", emoji);

                var dir = Path.Combine(Path.GetTempPath(), "copyas-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    var file = Path.Combine(dir, "snippet.ps1");
                    await File.WriteAllTextAsync(file, "$ErrorActionPreference = 'Stop'\n" + CopyAs.Build(request, CopyAsTarget.PowerShellWebRequest)!.Text, new UTF8Encoding(true));
                    var output = await ExecuteAsync(powershell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + file + "\"", null);

                    var seen = await origin.WaitAsync(TimeSpan.FromSeconds(20));
                    runner.IsTrue(seen is not null, $"{name}: the origin received a request" + (seen is null ? "; output: " + Truncate(output) : string.Empty));
                    if (seen is null) return;

                    runner.IsTrue(body.SequenceEqual(seen.Body), $"{name}: the body arrives byte for byte ({seen.Body.Length} of {body.Length} bytes)");
                    string Header(string header) => seen.Headers.FirstOrDefault(h => h.Name.Equals(header, StringComparison.OrdinalIgnoreCase)).Value ?? "(missing)";
                    runner.IsTrue(Header("X-Tabs") == tabs, $"{name}: the tab-filled header arrives");
                    runner.IsTrue(Header("X-Emoji") == emoji, $"{name}: the emoji header arrives byte for byte");
                    runner.IsTrue(Header("User-Agent") == emoji, $"{name}: the emoji User-Agent arrives byte for byte");
                }
                finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
            });
        }
    }

    /// <summary>
    /// A binary body goes through a temporary file that holds the request's credentials. Ctrl+C on a curl
    /// that never gets an answer stops the pipeline, so a Remove-Item written after it would never run.
    /// A real console Ctrl+C cannot be sent from a test without a console of its own; what Ctrl+C does
    /// to a running pipeline is PowerShell.Stop(), which this uses, with a curl.exe function that finds
    /// the file on disk and then hangs (or fails) in place of the real one.
    /// </summary>
    private static async Task PowerShellCurlCleanupAsync(TestRunner runner, string powershell)
    {
        foreach (var mode in new[] { "interrupted", "failing" })
        {
            await runner.RunAsync($"Copy as: PowerShell, curl.exe removes its temporary body file when curl is {mode}", async () =>
            {
                if (!File.Exists(powershell))
                {
                    runner.ToolMissing("PowerShell, curl.exe: powershell.exe is not installed");
                    return;
                }

                var dir = Path.Combine(Path.GetTempPath(), "copyas-" + Guid.NewGuid().ToString("N"));
                var temp = Path.Combine(dir, "temp");
                Directory.CreateDirectory(temp);
                try
                {
                    var request = new HttpRequestData
                    {
                        Method = "POST",
                        Url = new Uri("http://127.0.0.1:9/hang"),
                        Body = [.. Enumerable.Range(0, 256).Select(i => (byte)i)],
                    };
                    request.Headers.Add("Authorization", "Bearer interrupt-secret");
                    var snippet = Path.Combine(dir, "snippet.ps1");
                    var marker = Path.Combine(dir, "present.txt");
                    await File.WriteAllTextAsync(snippet, CopyAs.Build(request, CopyAsTarget.CurlPowerShell)!.Text, new UTF8Encoding(true));

                    // Stands in for curl.exe: records that the body file was on disk, then hangs or fails.
                    var standIn = Path.Combine(dir, "standin.ps1");
                    await File.WriteAllTextAsync(standIn, $$"""
                        function curl.exe {
                            $null = $input
                            if (@(Get-ChildItem -LiteralPath ([IO.Path]::GetTempPath()) -File | Where-Object Length -gt 0).Count -gt 0) { Set-Content -LiteralPath '{{marker}}' -Value present }
                            {{(mode == "interrupted" ? "Start-Sleep -Seconds 60" : "throw 'curl failed'")}}
                        }
                        """, new UTF8Encoding(true));
                    var script = mode == "interrupted"
                        ? $$"""
                            $ErrorActionPreference = 'Stop'
                            $ps = [powershell]::Create()
                            $null = $ps.AddScript(". '{{standIn}}'").AddStatement().AddScript("& '{{snippet}}'")
                            $handle = $ps.BeginInvoke()
                            $deadline = (Get-Date).AddSeconds(30)
                            while (-not (Test-Path -LiteralPath '{{marker}}') -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 100 }
                            $ps.Stop()
                            try { $null = $ps.EndInvoke($handle) } catch { }
                            'stopped'
                            """
                        : $$"""
                            $ErrorActionPreference = 'Stop'
                            . '{{standIn}}'
                            try { & '{{snippet}}' } catch { 'caught' }
                            """;
                    var driver = Path.Combine(dir, "driver.ps1");
                    await File.WriteAllTextAsync(driver, script, new UTF8Encoding(true));

                    var output = await ExecuteAsync(powershell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + driver + "\"", null, temp);
                    runner.IsTrue(File.Exists(marker), "the body file was on disk when curl ran; output: " + Truncate(output));
                    runner.AreEqual(0, Directory.GetFileSystemEntries(temp).Length, $"no temporary file is left behind when curl is {mode}; output: " + Truncate(output));
                }
                finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
            });
        }
    }

    private static string Truncate(string text) => text.Length <= 600 ? text : text[..600] + "...";

    private static Task<string> RunPowerShellAsync(string powershell, string script, string? temp = null) =>
        ExecuteAsync(powershell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'\n" + script)), null, temp);

    /// <summary>
    /// The generated C# is pasted into a project that may have implicit usings off, where Convert is not
    /// in scope without a using System. It is built here as a top-level program of exactly that kind.
    /// </summary>
    private static async Task CSharpCompilesAsync(TestRunner runner)
    {
        var dotnet = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
            FindOnPath("dotnet.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
        }.FirstOrDefault(path => !string.IsNullOrEmpty(path) && File.Exists(path));

        var cases = new (string Name, byte[] Body)[]
        {
            ("a binary body", [.. Enumerable.Range(0, 256).Select(i => (byte)i)]),
            ("a text body", Encoding.UTF8.GetBytes("{\"q\":\"hé\"}")),
            ("no body", []),
        };
        foreach (var (name, body) in cases)
        {
            await runner.RunAsync($"Copy as: the C# snippet for {name} compiles without implicit usings", async () =>
            {
                if (dotnet is null)
                {
                    runner.ToolMissing("C# compile: dotnet.exe was not found");
                    return;
                }

                var request = new HttpRequestData { Method = "POST", Url = new Uri("https://api.example.test/x"), Body = body };
                request.Headers.Add("Content-Type", "application/octet-stream");
                request.Headers.Add("X-Uni", Latin1Unicode); // makes the snippet use Encoding.Latin1
                var dir = Path.Combine(Path.GetTempPath(), "copyas-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                try
                {
                    await File.WriteAllTextAsync(Path.Combine(dir, "Snippet.csproj"),
                        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>"
                        + "<ImplicitUsings>disable</ImplicitUsings><Nullable>disable</Nullable><UseAppHost>false</UseAppHost></PropertyGroup></Project>");
                    await File.WriteAllTextAsync(Path.Combine(dir, "Program.cs"), CopyAs.Build(request, CopyAsTarget.CSharpHttpClient)!.Text, new UTF8Encoding(false));

                    var output = await ExecuteAsync(dotnet, "build \"" + Path.Combine(dir, "Snippet.csproj") + "\" -nologo -v:minimal -nodeReuse:false -p:UseSharedCompilation=false", null);
                    runner.IsTrue(output.StartsWith("exit 0", StringComparison.Ordinal) && !output.Contains("error CS", StringComparison.Ordinal),
                        $"the snippet for {name} compiles; output: " + (output.Length <= 800 ? output : output[..800]));
                }
                finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
            });
        }
    }

    private static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => { try { return Path.Combine(directory, file); } catch (ArgumentException) { return null; } })
            .FirstOrDefault(path => path is not null && File.Exists(path));

    private static string? FindGitCurlDirectory(string bash)
    {
        var gitRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(bash)!, "..", ".."));
        // Git for Windows 2.56 moved native tools from mingw64 to ucrt64.
        return new[] { "ucrt64", "mingw64" }
            .Select(variant => Path.Combine(gitRoot, variant, "bin"))
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "curl.exe")));
    }

    /// <summary>Runs a program to completion and returns its exit code and output, or kills it after a minute.</summary>
    private static async Task<string> ExecuteAsync(string file, string arguments, string? stdin, string? temp = null, bool msysPathConversion = false, string? curlDirectory = null)
    {
        var start = new ProcessStartInfo(file, arguments)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // A proxy in the environment (Piper itself, while it is capturing) must not catch the test traffic.
        foreach (var name in new[] { "http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY", "all_proxy", "ALL_PROXY" })
            start.Environment.Remove(name);
        start.Environment["NO_PROXY"] = "*";
        start.Environment["no_proxy"] = "*";
        if (msysPathConversion) start.Environment.Remove("MSYS_NO_PATHCONV");
        else start.Environment["MSYS_NO_PATHCONV"] = "1";

        // Another curl.exe first on PATH, e.g. Git for Windows', which is built with an ANSI main.
        if (curlDirectory is not null)
            start.Environment["PATH"] = curlDirectory + Path.PathSeparator + start.Environment["PATH"];

        // A private temporary directory, so the test can see which files a snippet leaves behind.
        if (temp is not null)
        {
            start.Environment["TEMP"] = temp;
            start.Environment["TMP"] = temp;
        }

        // A secret every shell above could expand if a pasted value were let through.
        start.Environment["PIPER_COPYAS_SECRET"] = Secret;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (stdin is not null) await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return "timed out";
        }
        return $"exit {process.ExitCode}: {await output}\n--- stderr ---\n{await error}";
    }

    private sealed record Seen(string RequestLine, List<(string Name, string Value)> Headers, byte[] Body);

    /// <summary>A server that records the first request it is sent, byte for byte, and answers 200.</summary>
    private sealed class CaptureOrigin : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly TaskCompletionSource<Seen> _seen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();

        private readonly string? _redirectTo;

        /// <param name="redirectTo">When given, the origin answers 302 with this Location instead of 200.</param>
        public CaptureOrigin(string? redirectTo = null)
        {
            _redirectTo = redirectTo;
            _listener.Start();
            _ = AcceptAsync();
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

        public async Task<Seen?> WaitAsync(TimeSpan timeout)
        {
            try { return await _seen.Task.WaitAsync(timeout); }
            catch (TimeoutException) { return null; }
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = ServeAsync(client);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var buffer = new List<byte>();
                    var one = new byte[4096];
                    int headEnd;
                    while ((headEnd = IndexOfHeadEnd(buffer)) < 0)
                    {
                        var n = await stream.ReadAsync(one, _cts.Token);
                        if (n == 0) return;
                        buffer.AddRange(one.AsSpan(0, n).ToArray());
                    }

                    var lines = Encoding.Latin1.GetString(buffer.GetRange(0, headEnd).ToArray()).Split("\r\n");
                    // A header line without a colon is not a header; skipping it keeps a malformed request
                    // from throwing here instead of failing the assertion that notices what is missing.
                    var headers = lines.Skip(1).Where(l => l.Contains(':')).Select(l => (Name: l[..l.IndexOf(':')], Value: l[(l.IndexOf(':') + 1)..].Trim(' ', '\t'))).ToList();
                    var rest = buffer.GetRange(headEnd + 4, buffer.Count - headEnd - 4);
                    var lengthHeader = headers.FirstOrDefault(h => h.Name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).Value;
                    var body = new List<byte>();
                    if (lengthHeader is not null)
                    {
                        var length = int.Parse(lengthHeader);
                        body.AddRange(rest);
                        while (body.Count < length)
                        {
                            var n = await stream.ReadAsync(one, _cts.Token);
                            if (n == 0) break;
                            body.AddRange(one.AsSpan(0, n).ToArray());
                        }
                    }
                    else if (headers.Any(h => h.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Chunked: read until the terminating zero chunk, then decode.
                        var raw = new List<byte>(rest);
                        while (!EndsChunked(raw))
                        {
                            var n = await stream.ReadAsync(one, _cts.Token);
                            if (n == 0) break;
                            raw.AddRange(one.AsSpan(0, n).ToArray());
                        }
                        body.AddRange(DecodeChunked(raw));
                    }

                    _seen.TrySetResult(new Seen(lines[0][..lines[0].LastIndexOf(' ')], headers, body.ToArray()));
                    var status = _redirectTo is null ? "200 OK" : "302 Found\r\nLocation: " + _redirectTo;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _cts.Token);
                }
                catch (InvalidDataException ex)
                {
                    // A client that sent a broken chunked body: fail the test that waits, with the reason.
                    _seen.TrySetException(ex);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            }
        }

        private static int IndexOfHeadEnd(List<byte> bytes)
        {
            for (var i = 0; i + 3 < bytes.Count; i++)
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n') return i;
            return -1;
        }

        private static bool EndsChunked(List<byte> raw) =>
            raw.Count >= 5 && Encoding.ASCII.GetString(raw.GetRange(raw.Count - 5, 5).ToArray()) == "0\r\n\r\n";

        private static IEnumerable<byte> DecodeChunked(List<byte> raw)
        {
            var position = 0;
            while (position < raw.Count)
            {
                var lineEnd = raw.FindIndex(position, b => b == '\r');
                if (lineEnd < 0) throw new InvalidDataException($"malformed chunked body: no chunk-size line ends after byte {position} of {raw.Count}");
                int size;
                try { size = Convert.ToInt32(Encoding.ASCII.GetString(raw.GetRange(position, lineEnd - position).ToArray()).Split(';')[0], 16); }
                catch (FormatException) { throw new InvalidDataException($"malformed chunked body: the chunk size at byte {position} is not hexadecimal"); }
                if (size < 0 || lineEnd + 2 + size > raw.Count) throw new InvalidDataException($"malformed chunked body: the chunk at byte {position} is cut short");
                if (size == 0) yield break;
                for (var i = 0; i < size; i++) yield return raw[lineEnd + 2 + i];
                position = lineEnd + 2 + size + 2;
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    // ---------------------------------------------------------------- fixtures

    private static HttpRequestData Plain(string method, string url, byte[] body) => new()
    {
        Method = method,
        Url = url.StartsWith("http", StringComparison.Ordinal) ? new Uri(url) : null,
        Body = body,
    };

    private static HttpRequestData HostileRequest()
    {
        var request = new HttpRequestData
        {
            Method = "POST",
            Url = new Uri("https://api.example.test/v1/it's?q=$(id)&b=`x`&p=100%25"),
            RequestTarget = "/v1/it's",
            Body = Gzip(Encoding.UTF8.GetBytes(BodyJson)),
        };
        request.Headers.Add("Host", "api.example.test");
        request.Headers.Add("Content-Type", "application/json");
        request.Headers.Add("Content-Length", "999");
        request.Headers.Add("Connection", "keep-alive");
        request.Headers.Add("Content-Encoding", "gzip");
        request.Headers.Add("Accept-Encoding", "gzip, br");
        request.Headers.Add("Authorization", "Bearer abc$def`x`");
        request.Headers.Add("X-Dollar", "$(touch /tmp/pwn) ${HOME} `id`");
        request.Headers.Add("X-Quotes", "it's \"quoted\" \\ back\\");
        request.Headers.Add("X-Cmd", "100% %PATH% a&b|c<d>e^f!g (h)");
        request.Headers.Add("X-Empty", "");
        request.Headers.Add("X-Uni", Latin1Unicode);
        request.Headers.Add("X-Inject", "a\r\nInjected: 1");
        request.Headers.Add("Bad Name", "v");
        request.Headers.Add("X-Dup", "1");
        request.Headers.Add("X-Dup", "2");
        return request;
    }

    private static byte[] Gzip(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
}
