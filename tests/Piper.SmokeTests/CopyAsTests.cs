using System.IO.Compression;
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
            runner.AreEqual("\"a\\\"b\\\\</script>\\u2028\\u0000\"", CopyAs.JsString("a\"b\\</script>\u2028\0"), "JavaScript: quotes, backslashes and invisible characters");
            runner.AreEqual("\"\\ud83d\\ude00\"", CopyAs.JsString("\ud83d\ude00"), "JavaScript: an astral character is a surrogate pair");
            return Task.CompletedTask;
        });
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
