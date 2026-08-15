using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;

internal static class AutoResponderActionTests
{
    private const long BodyLimit = 128L * 1024 * 1024;

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("AutoResponder actions: statuses, redirects and delays", async () =>
        {
            var notFound = AutoResponderAction.Parse("*404");
            runner.AreEqual(AutoResponderOutcome.Respond, notFound.Outcome, "*404 answers locally");
            var response = await BuildAsync(notFound);
            runner.AreEqual(404, response.StatusCode, "status");
            runner.AreEqual("Not Found", response.ReasonPhrase, "reason phrase filled in");
            runner.AreEqual(0, response.Body.Length, "no body");
            runner.IsTrue(!response.Headers.Contains("Connection"),
                "no Connection header, so the caller decides whether the connection survives");

            runner.AreEqual("I'm a teapot", (await BuildAsync(AutoResponderAction.Parse("*418"))).ReasonPhrase,
                "any three-digit status works, not just Fiddler's fixed list");

            var nonsense = AutoResponderAction.Parse("*999");
            runner.IsTrue(nonsense.Warning is not null, "a status outside 100-599 is reported");
            runner.AreEqual(AutoResponderOutcome.Passthrough, nonsense.Outcome, "and the request passes through");

            var redirect = AutoResponderAction.Parse("*redir:https://example.com/moved");
            var redirectResponse = await BuildAsync(redirect);
            runner.AreEqual(307, redirectResponse.StatusCode, "*redir: is a 307, so a POST stays a POST");
            runner.AreEqual("https://example.com/moved", redirectResponse.Headers["Location"], "Location set");

            // A bare URL is Fiddler's transparent refetch: the client keeps its own URL.
            var refetch = AutoResponderAction.Parse("http://localhost:9000/mock");
            runner.AreEqual(AutoResponderOutcome.Redirect, refetch.Outcome, "a bare URL refetches");
            runner.AreEqual("http://localhost:9000/mock",
                refetch.ResolveTarget(AutoResponderMatchResult.Hit, null)?.ToString(), "target resolves");

            var delayed = AutoResponderAction.Parse("*delay:250");
            runner.AreEqual(250d, delayed.Delay.TotalMilliseconds, "*delay: on its own is a pause");
            runner.AreEqual(AutoResponderOutcome.Passthrough, delayed.Outcome, "then the request continues");

            foreach (var composed in new[] { "*delay:250 *503", "*delay:250;*503" })
            {
                var slowFailure = AutoResponderAction.Parse(composed);
                runner.AreEqual(250d, slowFailure.Delay.TotalMilliseconds, $"{composed} keeps the delay");
                runner.AreEqual(503, (await BuildAsync(slowFailure)).StatusCode, $"{composed} still answers 503");
            }

            runner.AreEqual(AutoResponderOutcome.Drop, AutoResponderAction.Parse("*drop").Outcome, "*drop");
            runner.AreEqual(AutoResponderOutcome.Reset, AutoResponderAction.Parse("*reset").Outcome, "*reset");
            runner.AreEqual(AutoResponderOutcome.Passthrough, AutoResponderAction.Parse("*exit").Outcome,
                "*exit stops rule processing and lets the request go");

            var breakpoint = AutoResponderAction.Parse("*bpu");
            runner.IsTrue(breakpoint.Warning is not null, "a Fiddler breakpoint action is reported, not fatal");
            runner.AreEqual(AutoResponderOutcome.Passthrough, breakpoint.Outcome, "and passes the request through");
        });

        await runner.RunAsync("AutoResponder actions: bodies from disk and inline", async () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), $"piper-autoresponder-{Guid.NewGuid():N}");
            Directory.CreateDirectory(directory);
            try
            {
                var jsonPath = Path.Combine(directory, "orders.json");
                await File.WriteAllTextAsync(jsonPath, """{"orderId":7}""");

                var served = await BuildAsync(AutoResponderAction.Parse(jsonPath));
                runner.AreEqual(200, served.StatusCode, "a bare path serves the file");
                runner.AreEqual("application/json; charset=utf-8", served.ContentType, "Content-Type from the extension");
                runner.AreEqual("""{"orderId":7}""", Encoding.UTF8.GetString(served.Body), "body is the file");
                runner.AreEqual(served.Body.Length.ToString(), served.Headers["Content-Length"], "Content-Length matches");

                // Binary bodies must survive untouched -- NUL bytes are what break a text-only path.
                var binaryPath = Path.Combine(directory, "logo.png");
                byte[] binary = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x00, 0xFF, 0xFE];
                await File.WriteAllBytesAsync(binaryPath, binary);

                var binaryServed = await BuildAsync(AutoResponderAction.Parse($"*file:{binaryPath}"));
                runner.AreEqual("image/png", binaryServed.ContentType, "binary Content-Type has no charset");
                runner.AreEqual(Convert.ToHexString(binary), Convert.ToHexString(binaryServed.Body),
                    "binary body is byte-exact");

                // A capture substituted into the path is how one regex rule serves a whole folder.
                var captured = new AutoResponderMatchResult(true,
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["name"] = "orders" });
                var byCapture = await BuildAsync(
                    AutoResponderAction.Parse(Path.Combine(directory, "${name}.json")), match: captured);
                runner.AreEqual("""{"orderId":7}""", Encoding.UTF8.GetString(byCapture.Body), "${name} resolved in the path");

                var missing = await BuildAsync(AutoResponderAction.Parse(Path.Combine(directory, "nope.json")));
                runner.AreEqual(502, missing.StatusCode, "a missing file is a diagnosable 502, not an exception");
                runner.IsTrue(Encoding.UTF8.GetString(missing.Body).Contains("nope.json"), "and it names the path");

                // *raw: replays a complete captured response, framing headers stripped.
                var rawPath = Path.Combine(directory, "captured.txt");
                await File.WriteAllTextAsync(rawPath,
                    "HTTP/1.1 201 Created\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n"
                    + "Set-Cookie: a=1\r\nSet-Cookie: b=2\r\n\r\n{\"id\":1}");

                var replayed = await BuildAsync(AutoResponderAction.Parse($"*raw:{rawPath}"));
                runner.AreEqual(201, replayed.StatusCode, "*raw: keeps the captured status");
                runner.AreEqual("Created", replayed.ReasonPhrase, "and its reason phrase");
                runner.AreEqual("application/json", replayed.ContentType, "and its headers");
                runner.AreEqual(2, replayed.Headers.GetValues("Set-Cookie").Count(), "duplicate headers survive");
                runner.IsTrue(!replayed.Headers.Contains("Transfer-Encoding"),
                    "stale framing from the original exchange is dropped");
                runner.AreEqual("8", replayed.Headers["Content-Length"], "Content-Length is restated for the new body");
                runner.AreEqual("{\"id\":1}", Encoding.UTF8.GetString(replayed.Body), "body replayed");

                await File.WriteAllTextAsync(rawPath, "not an http response at all");
                var broken = await BuildAsync(AutoResponderAction.Parse($"*raw:{rawPath}"));
                runner.AreEqual(502, broken.StatusCode, "a file that is not a raw response is reported");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        });

        await runner.RunAsync("AutoResponder actions: inline bodies and CORS preflight", async () =>
        {
            var rule = new AutoResponderRule
            {
                Match = "example.com",
                Action = "*inline",
                Body = """{"stubbed":true}""",
                ContentType = "application/json",
            };

            var inline = await AutoResponderAction.Parse(rule.Action)
                .BuildResponseAsync(rule, AutoResponderMatchResult.Hit, RequestFor(), BodyLimit, default);
            runner.AreEqual(200, inline.StatusCode, "*inline answers 200");
            runner.AreEqual("application/json", inline.ContentType, "with the rule's content type");
            runner.AreEqual("""{"stubbed":true}""", Encoding.UTF8.GetString(inline.Body), "and the rule's body");

            var preflight = RequestFor("OPTIONS");
            preflight.Headers.Add("Origin", "https://app.example.com");
            preflight.Headers.Add("Access-Control-Request-Method", "PATCH");
            preflight.Headers.Add("Access-Control-Request-Headers", "authorization, x-trace");

            var cors = await AutoResponderAction.Parse("*CORSPreflightAllow")
                .BuildResponseAsync(new AutoResponderRule(), AutoResponderMatchResult.Hit, preflight, BodyLimit, default);
            runner.AreEqual(200, cors.StatusCode, "preflight is allowed");
            runner.AreEqual("https://app.example.com", cors.Headers["Access-Control-Allow-Origin"],
                "the origin is echoed, not wildcarded, so credentialed requests work");
            runner.AreEqual("PATCH", cors.Headers["Access-Control-Allow-Methods"], "requested method echoed");
            runner.AreEqual("authorization, x-trace", cors.Headers["Access-Control-Allow-Headers"], "requested headers echoed");
            runner.AreEqual("true", cors.Headers["Access-Control-Allow-Credentials"], "credentials allowed");
        });
    }

    private static Task<HttpResponseData> BuildAsync(AutoResponderAction action, AutoResponderMatchResult? match = null) =>
        action.BuildResponseAsync(new AutoResponderRule(), match ?? AutoResponderMatchResult.Hit,
            RequestFor(), BodyLimit, default);

    private static HttpRequestData RequestFor(string method = "GET") => new()
    {
        Method = method,
        Url = new Uri("http://example.com/api/orders"),
        RequestTarget = "/api/orders",
    };
}
