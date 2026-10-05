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

    private static readonly Dictionary<CopyAsTarget, string> Snapshots = new()
    {
        [CopyAsTarget.CurlBash] = """
                # Headers with a name or value that cannot be written safely were left out.
                curl 'https://api.example.test/v1/it'\''s?q=$(id)&b=%60x%60&p=100%25' \
                  --globoff \
                  --request 'POST' \
                  --compressed \
                  --header 'Content-Type: application/json' \
                  --header 'Accept-Encoding: gzip, br' \
                  --header 'Authorization: Bearer abc$def`x`' \
                  --header 'X-Dollar: $(touch /tmp/pwn) ${HOME} `id`' \
                  --header 'X-Quotes: it'\''s "quoted" \ back\' \
                  --header 'X-Cmd: 100% %PATH% a&b|c<d>e^f!g (h)' \
                  --header 'X-Empty;' \
                  --header 'X-Uni: caf'$'\xc3\xa9'' '$'\xe2\x80\x99'' '$'\xf0\x9f\x98\x80' \
                  --header 'X-Dup: 1' \
                  --header 'X-Dup: 2' \
                  --data-raw '{"q":"$(id) `x` '\''y'\'' %PATH% \"z\"","u":"'$'\xc3\xa9''"}'
                """,
        [CopyAsTarget.CurlCmd] = """
                REM The request body is binary or not safe to paste as text, so it is written as base64 and decoded here.
                REM Non-ASCII header text may be re-encoded by the shell it is pasted into, or by the runtime that sends it.
                REM Headers with a name or value that cannot be written safely were left out.
                > "%TEMP%\piper-body-1bcf5579bf16b3da.b64" echo eyJxIjoiJChpZCkgYHhgICd5JyAlUEFUSCUgXCJ6XCIiLCJ1Ijoiw6kifQ==
                certutil -f -decode "%TEMP%\piper-body-1bcf5579bf16b3da.b64" "%TEMP%\piper-body-1bcf5579bf16b3da.bin" > nul
                curl.exe ^"https://api.example.test/v1/it's?q=$^(id^)^&b=%^60x%^60^&p=100%^25^" ^
                  --globoff ^
                  --request ^"POST^" ^
                  --compressed ^
                  --header ^"Content-Type: application/json^" ^
                  --header ^"Accept-Encoding: gzip, br^" ^
                  --header ^"Authorization: Bearer abc$def`x`^" ^
                  --header ^"X-Dollar: $^(touch /tmp/pwn^) ${HOME} `id`^" ^
                  --header ^"X-Quotes: it's \^"quoted\^" \ back\\^" ^
                  --header ^"X-Cmd: 100%^ %^PATH%^ a^&b^|c^<d^>e^^f^!g ^(h^)^" ^
                  --header ^"X-Empty;^" ^
                  --header ^"X-Uni: café ’ 😀^" ^
                  --header ^"X-Dup: 1^" ^
                  --header ^"X-Dup: 2^" ^
                  --data-binary "@%TEMP%\piper-body-1bcf5579bf16b3da.bin"
                del /q "%TEMP%\piper-body-1bcf5579bf16b3da.b64" "%TEMP%\piper-body-1bcf5579bf16b3da.bin"
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
                runner.AreEqual(Snapshots[target].ReplaceLineEndings("\n"), result!.Text.ReplaceLineEndings("\n"), $"{target} snapshot");
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
            runner.AreEqual("^\"a^&b^|c^<d^>e^^f^!g^(h^)^\"", CopyAs.CmdArg("a&b|c<d>e^f!g(h)"), "cmd: every metacharacter is caret-escaped");
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

        await runner.RunAsync("Copy as: the cmd temporary file is named after its body", () =>
        {
            var a = CopyAs.Build(Plain("POST", "https://h.test/", [1, 2, 3]), CopyAsTarget.CurlCmd)!.Text;
            var b = CopyAs.Build(Plain("POST", "https://h.test/", [1, 2, 4]), CopyAsTarget.CurlCmd)!.Text;
            var again = CopyAs.Build(Plain("POST", "https://h.test/", [1, 2, 3]), CopyAsTarget.CurlCmd)!.Text;
            var name = System.Text.RegularExpressions.Regex.Match(a, @"piper-body-[0-9a-f]{16}\.bin").Value;
            runner.IsTrue(name.Length > 0 && !b.Contains(name), "a different body gets a different file");
            runner.AreEqual(a, again, "and the same body the same text");
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
        ("X-Cmd", "100% %PATH% %USERNAME% a&b|c<d>e^f!g (h) ;,= ^% %^"),
        ("X-Path", "C:\\dir\\sub\\"),
        ("X-Tab", "a\tb"),
        ("X-Empty", ""),
        ("Authorization", "Bearer abc$def`x`"),
        ("Cookie", "a=1; b=\"2\""),

        // Environment references of every shell. The child process holds a secret variable of each
        // name; none of these may be expanded, or the secret would travel to the origin.
        ("X-Env-Cmd", "%PIPER_COPYAS_SECRET% %PIPER_COPYAS_SECRET:ZZ=% %PIPER_COPYAS_SECRET:*x=% %PIPER_COPYAS_SECRET:~0,3% %PIPER_COPYAS_SECRET:s=A% %% %^ ^% %PATH%"),
        ("X-Env-Other", "$PIPER_COPYAS_SECRET ${PIPER_COPYAS_SECRET} $env:PIPER_COPYAS_SECRET ${env:PIPER_COPYAS_SECRET} $(echo $PIPER_COPYAS_SECRET) !PIPER_COPYAS_SECRET! `$env:PIPER_COPYAS_SECRET"),
        ("X-Env-End", "100%"),
        ("X-Env-Pair", "%PIPER_COPYAS_SECRET:ZZ=%\""),
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
        ("hostile text", Encoding.UTF8.GetBytes("line1 \"q\" $(id) `x`\n'x' %PATH% ^& !\r\nend\\"), "text/plain"),
        ("single-line hostile text", Encoding.ASCII.GetBytes("x=%PIPER_COPYAS_SECRET% %PIPER_COPYAS_SECRET:ZZ=% %PIPER_COPYAS_SECRET:~0,3% $PIPER_COPYAS_SECRET $env:PIPER_COPYAS_SECRET ${env:PIPER_COPYAS_SECRET} $(echo $PIPER_COPYAS_SECRET) 100% \"q\" & echo ran | more ^ !"), "text/plain"),
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

        await RoundTripAsync(runner, "cmd.exe", CopyAsTarget.CurlCmd, File.Exists(curl) ? null : "curl.exe is not installed", async (text, _) =>
        {
            // Each logical line is its own cmd /c; a trailing caret is the line continuation.
            var output = new StringBuilder();
            var logical = new StringBuilder();
            foreach (var line in text.Split("\r\n"))
            {
                if (line.EndsWith('^')) { logical.Append(line[..^1]); continue; }
                logical.Append(line);
                output.AppendLine(await ExecuteAsync(Path.Combine(system, "cmd.exe"), "/d /s /c \"" + logical + "\"", null));
                logical.Clear();
            }
            return output.ToString();
        });

        // The same text typed at the prompt: cmd reads it line by line from stdin, which is where a
        // caret at the end of a line continues the command. A quote left open by a bad escape would
        // end that continuation and run the following lines as commands of their own.
        await RoundTripAsync(runner, "cmd.exe, typed at the prompt", CopyAsTarget.CurlCmd, File.Exists(curl) ? null : "curl.exe is not installed",
            (text, _) => ExecuteAsync(Path.Combine(system, "cmd.exe"), "/d", text + "\r\nexit\r\n"));

        await RoundTripAsync(runner, "PowerShell, curl.exe", CopyAsTarget.CurlPowerShell,
            File.Exists(curl) && File.Exists(powershell) ? null : "curl.exe or powershell.exe is not installed",
            (text, _) => RunPowerShellAsync(powershell, text));

        await RoundTripAsync(runner, "PowerShell, Invoke-WebRequest", CopyAsTarget.PowerShellWebRequest,
            File.Exists(powershell) ? null : "powershell.exe is not installed",
            (text, _) => RunPowerShellAsync(powershell, text));

        await RoundTripAsync(runner, "bash", CopyAsTarget.CurlBash, bash is null ? "no bash (Git for Windows) is installed" : null,
            (text, _) => ExecuteAsync(bash!, "-l -s", text));

        await runner.RunAsync("Copy as: bash quoting reproduces every byte it is given", async () =>
        {
            if (bash is null)
            {
                Console.WriteLine("   SKIPPED  bash: no bash (Git for Windows) is installed");
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

        await RoundTripAsync(runner, "Node.js fetch", CopyAsTarget.JavaScriptFetch, node is null ? "node.exe is not on PATH" : null, async (text, dir) =>
        {
            var file = Path.Combine(dir, "snippet.mjs");
            await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
            return await ExecuteAsync(node!, "\"" + file + "\"", null);
        });

        var requests = python is not null && (await ExecuteAsync(python, "-c \"import requests\"", null)).StartsWith("exit 0", StringComparison.Ordinal);
        await RoundTripAsync(runner, "Python requests", CopyAsTarget.PythonRequests, requests ? null : "python with the requests package is not installed", async (text, dir) =>
        {
            var file = Path.Combine(dir, "snippet.py");
            await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
            return await ExecuteAsync(python!, "\"" + file + "\"", null);
        });
    }

    private static async Task RoundTripAsync(TestRunner runner, string shell, CopyAsTarget target, string? unavailable, Func<string, string, Task<string>> run)
    {
        await runner.RunAsync($"Copy as: {target} pasted into {shell} delivers every value unchanged", async () =>
        {
            if (unavailable is not null)
            {
                // Visible in the log, and not counted as a pass or a failure of the escaper.
                Console.WriteLine($"   SKIPPED  {shell}: {unavailable}");
                return;
            }

            foreach (var body in RoundTripBodies)
            {
                // Git for Windows runs native curl.exe, and Windows narrows its arguments to the ANSI
                // code page, so non-ASCII text cannot arrive intact through it. The bash quoting is
                // checked byte for byte by its own test instead.
                if (target == CopyAsTarget.CurlBash && body.Name == "non-ASCII text") continue;

                using var origin = new CaptureOrigin();
                var dir = Path.Combine(Path.GetTempPath(), "copyas-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                var marker = Path.Combine(dir, "ran.txt");

                var request = new HttpRequestData
                {
                    Method = "POST",
                    Url = new Uri(origin.Url + "p/it's?q=$(id)&r=%25&s=`x`"),
                    Body = body.Bytes,
                };
                var expected = RoundTripHeaders.Append(("Content-Type", body.ContentType)).ToList();
                for (var i = 0; i < BreakOutValues.Length; i++)
                    expected.Add(("X-Break-" + i, string.Format(BreakOutValues[i], marker)));

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
                string diagnostics;
                bool ran;
                try
                {
                    diagnostics = await run(text, dir);
                    ran = File.Exists(marker);
                }
                finally { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } }
                runner.IsTrue(!ran, $"{shell}, {body.Name}: no second command ran");

                var seen = await origin.WaitAsync(TimeSpan.FromSeconds(20));
                runner.IsTrue(seen is not null, $"{shell}, {body.Name}: the origin received a request" + (seen is null ? "; output: " + diagnostics : string.Empty));
                if (seen is null) continue;

                runner.AreEqual("POST " + request.Url!.GetComponents(UriComponents.PathAndQuery, UriFormat.UriEscaped), seen.RequestLine, $"{shell}, {body.Name}: the URL arrives unchanged");
                foreach (var (name, value) in expected)
                {
                    var got = seen.Headers.Where(h => h.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(h => h.Value).ToList();
                    runner.IsTrue(got.Contains(value), $"{shell}, {body.Name}: {name} arrives verbatim (got: {string.Join(" | ", got)})");
                }
                runner.IsTrue(!seen.Headers.Any(h => h.Name.Equals("pwn", StringComparison.OrdinalIgnoreCase)), $"{shell}, {body.Name}: nothing was injected");
                var everything = seen.RequestLine + "\n" + string.Join("\n", seen.Headers.Select(h => h.Name + ": " + h.Value)) + "\n" + Encoding.Latin1.GetString(seen.Body);
                runner.IsTrue(!everything.Contains(Secret), $"{shell}, {body.Name}: an environment variable's value never reaches the origin");
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
    private static async Task<string> ExecuteAsync(string file, string arguments, string? stdin)
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
        start.Environment["MSYS_NO_PATHCONV"] = "1";
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

        public CaptureOrigin()
        {
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
                    var headers = lines.Skip(1).Select(l => l.IndexOf(':')).Zip(lines.Skip(1), (i, l) => (Name: l[..i], Value: l[(i + 1)..].Trim())).ToList();
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
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _cts.Token);
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
                var size = Convert.ToInt32(Encoding.ASCII.GetString(raw.GetRange(position, lineEnd - position).ToArray()).Split(';')[0], 16);
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
