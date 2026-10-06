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
                $piperBody = [IO.Path]::GetTempFileName()
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
                Remove-Item -LiteralPath $piperBody
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

            runner.AreEqual("\"a\\\"b\\\\</script>\\u2028\\u0000\"", CopyAs.JsString("a\"b\\</script>\u2028\0"), "JavaScript: quotes, backslashes and invisible characters");
            runner.AreEqual("\"\\ud83d\\ude00\"", CopyAs.JsString("\ud83d\ude00"), "JavaScript: an astral character is a surrogate pair");
            runner.AreEqual("\"\\U0001f600\\ufffd\"", CopyAs.PyString("\ud83d\ude00\ud800"), "Python: an astral character is one \\U escape, a lone surrogate is U+FFFD");
            runner.AreEqual("\"a\\\"b\\\\\\n\\u0000\"", CopyAs.CSharpString("a\"b\\\n\0"), "C#: quotes, backslashes and NUL");
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
        var gitCurl = bash is null ? null : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(bash)!, "..", "..", "mingw64", "bin"));
        var gitCurlMissing = gitCurl is not null && File.Exists(Path.Combine(gitCurl, "curl.exe")) ? null : "Git for Windows' curl.exe is not installed";
        await RoundTripAsync(runner, "cmd.exe with Git for Windows' curl.exe", CopyAsTarget.CurlCmd, gitCurlMissing,
            (text, dir) => RunCmdByLineAsync(Path.Combine(system, "cmd.exe"), "on", text, dir, gitCurl), RoundTripSet.BestFit);
        await RoundTripAsync(runner, "cmd.exe with Git for Windows' curl.exe, typed at the prompt", CopyAsTarget.CurlCmd, gitCurlMissing,
            (text, dir) => ExecuteAsync(Path.Combine(system, "cmd.exe"), "/d /v:on", text + "\r\nexit\r\n", dir, curlDirectory: gitCurl), RoundTripSet.BestFit);

        await CmdMissingFileAsync(runner, Path.Combine(system, "cmd.exe"), File.Exists(curl) ? null : "curl.exe is not installed");

        var curlPowerShell = File.Exists(curl) && File.Exists(powershell) ? null : "curl.exe or powershell.exe is not installed";
        await RoundTripAsync(runner, "PowerShell, curl.exe", CopyAsTarget.CurlPowerShell, curlPowerShell, (text, _) => RunPowerShellAsync(powershell, text));

        // The console re-encodes what PowerShell pipes to curl, so non-ASCII text is not asserted to arrive
        // verbatim here; that it cannot reach curl's argv (no option is added, nothing leaks) still is.
        await RoundTripAsync(runner, "PowerShell, curl.exe", CopyAsTarget.CurlPowerShell, curlPowerShell,
            (text, _) => RunPowerShellAsync(powershell, text), RoundTripSet.BestFit, verbatimNonAscii: false);

        await RoundTripAsync(runner, "PowerShell, Invoke-WebRequest", CopyAsTarget.PowerShellWebRequest,
            File.Exists(powershell) ? null : "powershell.exe is not installed",
            (text, _) => RunPowerShellAsync(powershell, text));

        var bashMissing = bash is null ? "no bash (Git for Windows) is installed" : null;
        await RoundTripAsync(runner, "bash", CopyAsTarget.CurlBash, bashMissing, (text, dir) => ExecuteAsync(bash!, "-l -s", text, dir));
        await RoundTripAsync(runner, "bash", CopyAsTarget.CurlBash, bashMissing, (text, dir) => ExecuteAsync(bash!, "-l -s", text, dir, msysPathConversion: true), RoundTripSet.BestFit);

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

        // A server that answers with a redirect must not be able to move the copied Cookie and
        // Authorization on to another host: the second origin must never be contacted.
        await RedirectRoundTripAsync(runner, "Node.js fetch", CopyAsTarget.JavaScriptFetch, node is null ? "node.exe is not on PATH" : null, RunNodeAsync);
        await RedirectRoundTripAsync(runner, "Python requests", CopyAsTarget.PythonRequests, requests ? null : "python with the requests package is not installed", RunPythonAsync);
        await RedirectRoundTripAsync(runner, "PowerShell, Invoke-WebRequest", CopyAsTarget.PowerShellWebRequest,
            File.Exists(powershell) ? null : "powershell.exe is not installed", (text, _) => RunPowerShellAsync(powershell, text));
        await RedirectRoundTripAsync(runner, "cmd.exe", CopyAsTarget.CurlCmd, File.Exists(curl) ? null : "curl.exe is not installed",
            (text, _) => RunCmdByLineAsync(Path.Combine(system, "cmd.exe"), "off", text));
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
    private enum RoundTripSet { Both, Plain, Bang, Simple, BestFit }

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
            IEnumerable<(string Name, byte[] Bytes, string ContentType)> bodies =
                (plain ? RoundTripBodies : []).Concat(bang ? BangBodies : []).Concat(bestFit ? BestFitBodies : [])
                    .Concat(simple ? RoundTripBodies.Where(b => b.Name is "single-line hostile text" or "every byte value") : []);
            IEnumerable<(string Name, string Value)> headers =
                (plain ? RoundTripHeaders : []).Concat(bang ? BangHeaders : []).Concat(simple ? RoundTripHeaders.Where(h => h.Name != "X-Tab") : []);
            var query = bang ? BangQuery : string.Empty;
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
                if (target == CopyAsTarget.CurlCmd) runner.IsTrue(!left, $"{shell}, {body.Name}: no temporary file is left behind");

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

    private static Task<string> RunPowerShellAsync(string powershell, string script) =>
        ExecuteAsync(powershell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
            + Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'\n" + script)), null);

    private static string? FindOnPath(string file) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => { try { return Path.Combine(directory, file); } catch (ArgumentException) { return null; } })
            .FirstOrDefault(path => path is not null && File.Exists(path));

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
