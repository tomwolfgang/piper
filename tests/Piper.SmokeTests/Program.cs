using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// End-to-end smoke test: a real origin server, the real proxy, a real HttpClient.
// No test framework so this always runs without a NuGet restore.
//
//   Piper.SmokeTests [--filter <substring>] [--timeout <seconds>] [--list]
//
// Every public static `Task Run*Async(TestRunner)` method in this assembly is a test group and runs
// after the inline proxy tests below; nothing needs registering. --filter runs the tests whose name
// contains the text (case-insensitive), or every test of a group whose "Class.Method" does. Tests in
// one group can build on each other, so filter a whole group when in doubt.

if (!TestOptions.TryParse(args, out var testOptions, out var usageError))
{
    Console.Error.WriteLine(usageError);
    return 2;
}

if (testOptions.List)
{
    foreach (var group in TestDiscovery.Groups) Console.WriteLine(group.Id);
    return 0;
}

// Two runs at once (two worktrees, or a run beside CI tooling) must share nothing on disk: the
// suites keep certificate authorities and files under fixed names in the temp folder. Give this
// process its own temp folder, which Path.GetTempPath() picks up from here on, and remove it on exit.
var sandbox = Path.Combine(Path.GetTempPath(), $"{TestOptions.SandboxPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}");
Directory.CreateDirectory(sandbox);
Environment.SetEnvironmentVariable("TMP", sandbox);
Environment.SetEnvironmentVariable("TEMP", sandbox);
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    try { Directory.Delete(sandbox, recursive: true); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // A straggler still holds a file; leave the folder for the OS temp cleanup.
    }
};

var runner = new TestRunner(Console.Out, testOptions.Filter, testOptions.Timeout);

await runner.RunAsync("generated root has an unmistakable Windows certificate name", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"Piper-SmokeTest-RootName-{Guid.NewGuid():N}");
    try
    {
        using var testCa = CertificateAuthority.LoadOrCreate(directory);
        runner.AreEqual(CertificateAuthority.RootCommonName,
            testCa.RootCertificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
            "Issued To name identifies Piper's local interception root");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
});

await runner.RunAsync("legacy root is rotated to the clear certificate name", () =>
{
    var directory = Path.Combine(Path.GetTempPath(), $"Piper-SmokeTest-LegacyRoot-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(directory);
        using (var rsa = System.Security.Cryptography.RSA.Create(2048))
        {
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
                "CN=Piper Root CA, O=Piper", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
                System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            using var legacy = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
            File.WriteAllBytes(Path.Combine(directory, "Piper-Root.pfx"),
                legacy.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "Piper"));
        }

        using var testCa = CertificateAuthority.LoadOrCreate(directory);
        runner.AreEqual(CertificateAuthority.RootCommonName,
            testCa.RootCertificate.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false),
            "legacy root was reissued with the new name");
    }
    finally
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    return Task.CompletedTask;
});

// Both servers take a port the OS hands out, so two runs can overlap.
using var origin = OriginServer.StartOnFreePort();
var originPort = origin.Port;
var originBase = $"http://127.0.0.1:{originPort}";

var store = new SessionStore();
var options = new ProxyOptions { Port = 0, DecryptHttps = false };
using var ca = CertificateAuthority.LoadOrCreate(
    Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Certs"));

await using var proxy = new ProxyServer(options, ca, store);
proxy.Start();
var proxyPort = proxy.Endpoint!.Port;

using var client = new HttpClient(new HttpClientHandler
{
    Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}", BypassOnLocal: false),
    UseProxy = true,
});
client.Timeout = TimeSpan.FromSeconds(20);

// ---------------------------------------------------------------- proxy tests

await runner.RunAsync("GET is proxied and captured", async () =>
{
    var response = await client.GetAsync($"{originBase}/api/orders?id=42");
    var text = await response.Content.ReadAsStringAsync();

    runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "status code reaches the client");
    runner.IsTrue(text.Contains("\"orderId\": 42"), "body reaches the client intact");

    var session = await WaitForAsync(store, s => s.Path == "/api/orders");
    runner.AreEqual("GET", session.Method, "captured method");
    runner.AreEqual(200, session.StatusCode, "captured status");
    runner.AreEqual("127.0.0.1", session.Host, "captured host");
    runner.AreEqual("?id=42", session.Query, "captured query string");
    runner.IsTrue(session.Response!.BodyAsText().Contains("orderId"), "captured response body");
});

await runner.RunAsync("POST body survives the round trip", async () =>
{
    var payload = """{"user_id":"tom","items":[1,2,3]}""";
    var response = await client.PostAsync($"{originBase}/api/echo",
        new StringContent(payload, Encoding.UTF8, "application/json"));

    var echoed = await response.Content.ReadAsStringAsync();
    runner.AreEqual(payload, echoed, "origin received the exact body");

    var session = await WaitForAsync(store, s => s.Path == "/api/echo" && s.Method == "POST");
    runner.AreEqual(payload, session.Request!.BodyAsText(), "captured request body");
    runner.AreEqual(payload.Length, (int)session.RequestSize, "captured request size");
});

await runner.RunAsync("chunked responses are de-chunked", async () =>
{
    var response = await client.GetAsync($"{originBase}/chunked");
    var text = await response.Content.ReadAsStringAsync();

    runner.AreEqual("chunk-one|chunk-two|chunk-three", text, "client sees the reassembled body");

    var session = await WaitForAsync(store, s => s.Path == "/chunked");
    runner.AreEqual("chunk-one|chunk-two|chunk-three", session.Response!.BodyAsText(), "captured de-chunked body");
});

await runner.RunAsync("gzip responses are decoded for inspection", async () =>
{
    var response = await client.GetAsync($"{originBase}/gzip");
    var session = await WaitForAsync(store, s => s.Path == "/gzip");

    runner.AreEqual("gzip", session.Response!.ContentEncoding, "Content-Encoding preserved on the wire");
    runner.IsTrue(session.Response.Body.Length < 200, "stored body is still compressed");
    runner.IsTrue(session.Response.BodyAsText().Contains("compressed payload"),
        "decoded body is readable");
});

await runner.RunAsync("404 from origin is captured, not masked", async () =>
{
    var response = await client.GetAsync($"{originBase}/missing");
    runner.AreEqual(HttpStatusCode.NotFound, response.StatusCode, "status passed through");

    var session = await WaitForAsync(store, s => s.Path == "/missing");
    runner.AreEqual(404, session.StatusCode, "captured 404");
});

await runner.RunAsync("unreachable origin yields a captured failure", async () =>
{
    var response = await client.GetAsync("http://127.0.0.1:1/nope");
    runner.AreEqual(HttpStatusCode.BadGateway, response.StatusCode, "proxy reports 502");

    var session = await WaitForAsync(store, s => s.Path == "/nope");
    runner.AreEqual(SessionState.Failed, session.State, "session marked failed");
    runner.IsTrue(!string.IsNullOrEmpty(session.Error), "failure reason recorded");
});

// ------------------------------------------------------------- composer tests

await runner.RunAsync("composer executes a hand-built request", async () =>
{
    var executor = new RequestExecutor(options, store);
    var request = new HttpRequestData
    {
        Method = "POST",
        Url = new Uri($"{originBase}/api/echo"),
        RequestTarget = "/api/echo",
        Body = Encoding.UTF8.GetBytes("""{"composed":true}"""),
    };
    request.Headers.Add("Content-Type", "application/json");
    request.Headers.Add("X-Custom-Header", "kept-verbatim");

    var session = await executor.ExecuteAsync(request);

    runner.AreEqual(SessionState.Complete, session.State, "composed request completed");
    runner.AreEqual(200, session.StatusCode, "composed request got 200");
    runner.IsTrue(session.IsComposed, "flagged as composed");
    runner.AreEqual("""{"composed":true}""", session.Response!.BodyAsText(), "echo matched");
    runner.IsTrue(origin.LastRequestHeaders.Contains("X-Custom-Header: kept-verbatim"),
        "unusual header reached the origin unmodified");
});

await runner.RunAsync("composer parses a pasted raw request", () =>
{
    var raw = "POST http://example.com/v1/items HTTP/1.1\r\n"
            + "Host: example.com\r\n"
            + "Content-Type: application/json\r\n"
            + "\r\n"
            + """{"a":1}""";

    runner.IsTrue(RequestExecutor.TryParseRaw(raw, out var parsed, out var error), $"raw parse succeeded ({error})");
    runner.AreEqual("POST", parsed.Method, "method");
    runner.AreEqual("http://example.com/v1/items", parsed.Url!.ToString(), "url");
    runner.AreEqual("application/json", parsed.Headers["Content-Type"], "header");
    runner.AreEqual("""{"a":1}""", Encoding.UTF8.GetString(parsed.Body), "body");
    return Task.CompletedTask;
});

// --------------------------------------------------------------- search tests

await runner.RunAsync("search query grammar", () =>
{
    var all = store.Snapshot();
    runner.IsTrue(all.Length >= 6, $"have sessions to search ({all.Length})");

    int Count(string query) => SearchQuery.Parse(query).Filter(all).Count();

    runner.IsTrue(Count("method:POST") >= 2, "method:POST");
    runner.IsTrue(Count("status:404") == 1, "status:404");
    runner.IsTrue(Count("status:4xx") == 1, "status:4xx class shorthand");
    runner.IsTrue(Count("status:>=400") == 1, "status:>=400 comparison");
    runner.IsTrue(Count("status:200..299") >= 4, "status range");
    runner.IsTrue(Count("host:127.0.0.1") >= 6, "host field");
    runner.IsTrue(Count("path:/api/echo") >= 2, "path field");
    runner.IsTrue(Count("is:composed") == 1, "is:composed");
    runner.IsTrue(Count("is:captured") >= 5, "is:captured");
    runner.IsTrue(Count("is:json") >= 2, "is:json");
    runner.IsTrue(Count("body:user_id") == 1, "body substring");
    runner.IsTrue(Count("req:composed") == 1, "request-body-only search");
    runner.IsTrue(Count("resp:orderId") == 1, "response-body-only search");
    runner.IsTrue(Count("header:X-Custom-Header") == 1, "header name search");
    runner.IsTrue(Count("/orders|echo/") >= 3, "regex over the whole index");
    runner.IsTrue(Count("method:GET|POST") >= 6, "alternatives with |");
    runner.IsTrue(Count("dur:>=0") >= 6, "duration comparison");
    runner.IsTrue(Count("size:>0") >= 4, "size comparison");

    // Negation and conjunction.
    var posts = Count("method:POST");
    runner.AreEqual(all.Length - posts, Count("-method:POST"), "negation is the complement");
    runner.IsTrue(Count("method:POST path:/api/echo") <= posts, "terms are ANDed");
    runner.AreEqual(0, Count("method:POST method:GET"), "contradictory terms match nothing");

    // Quoted phrases keep their spaces.
    runner.AreEqual(0, Count("\"no such phrase anywhere\""), "quoted phrase that matches nothing");

    // Bad input degrades instead of throwing. An unrecognised field is searched literally rather
    // than dropped, so a typo narrows the list instead of quietly matching every session.
    var bad = SearchQuery.Parse("bogusfield:x");
    runner.AreEqual(0, bad.Warnings.Count, "unknown field is not reported as a broken query");
    runner.AreEqual(0, bad.Filter(all).Count(), "unknown field is searched literally, matching nothing here");

    // A malformed value on a known field is still a warning, and that term is dropped.
    var malformed = SearchQuery.Parse("status:abc");
    runner.IsTrue(malformed.Warnings.Count == 1, "malformed value reported as a warning");
    runner.AreEqual(all.Length, malformed.Filter(all).Count(), "bad term is ignored, not fatal");

    return Task.CompletedTask;
});

await runner.RunAsync("host remapping redirects the origin connection without changing Host", async () =>
{
    const string requestedHost = "remapped.piper.test";
    options.HostRemapping.Apply(new HostRemappingSettings
    {
        Enabled = true,
        Mappings = $"127.0.0.1 {requestedHost}",
    });
    try
    {
        var response = await client.GetAsync($"http://{requestedHost}:{originPort}/api/orders?id=remapped");
        runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "mapped request reaches the local origin");
        runner.IsTrue(origin.LastRequestHeaders.Contains($"Host: {requestedHost}:{originPort}", StringComparison.OrdinalIgnoreCase),
            "original Host header reaches the remapped origin");

        var session = await WaitForAsync(store, s => s.Host == requestedHost && s.Query == "?id=remapped");
        runner.AreEqual(requestedHost, session.Host, "captured request retains the original URL host");
    }
    finally
    {
        options.HostRemapping.Apply(new HostRemappingSettings());
    }
});

await runner.RunAsync("hostname remapping rewrites the outbound Host authority", async () =>
{
    const string requestedHost = "remapped-authority.piper.test";
    options.HostRemapping.Apply(new HostRemappingSettings
    {
        Enabled = true,
        Mappings = $"localhost {requestedHost}",
    });
    try
    {
        var response = await client.GetAsync($"http://{requestedHost}:{originPort}/api/orders?id=authority-rewrite");
        runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "hostname target reaches the local origin");
        runner.IsTrue(origin.LastRequestHeaders.Contains($"Host: localhost:{originPort}", StringComparison.OrdinalIgnoreCase),
            "hostname target replaces the outbound Host header");

        var session = await WaitForAsync(store, s => s.Host == requestedHost && s.Query == "?id=authority-rewrite");
        runner.AreEqual(requestedHost, session.Host, "captured request still identifies the browser URL host");
    }
    finally
    {
        options.HostRemapping.Apply(new HostRemappingSettings());
    }
});

await runner.RunAsync("global User-Agent rule overrides proxied requests", async () =>
{
    const string userAgent = "PiperSmokeTest/1.0";
    options.GlobalUserAgent = userAgent;
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{originBase}/api/orders");
        request.Headers.TryAddWithoutValidation("User-Agent", "OriginalClient/1.0");
        using var response = await client.SendAsync(request);

        runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "request completed");
        runner.IsTrue(origin.LastRequestHeaders.Contains($"User-Agent: {userAgent}", StringComparison.Ordinal),
            "origin receives the configured User-Agent");
        runner.IsTrue(!origin.LastRequestHeaders.Contains("OriginalClient/1.0", StringComparison.Ordinal),
            "original User-Agent is replaced");
    }
    finally
    {
        options.GlobalUserAgent = null;
    }
});

await runner.RunAsync("header collection semantics", () =>
{
    var headers = new HeaderCollection();
    headers.Add("Set-Cookie", "a=1");
    headers.Add("Set-Cookie", "b=2");
    headers.Add("Content-Type", "text/plain");

    runner.AreEqual(2, headers.GetValues("Set-Cookie").Count(), "duplicates preserved");
    runner.AreEqual("a=1", headers["set-cookie"], "lookup is case-insensitive");

    headers.Set("Set-Cookie", "c=3");
    runner.AreEqual(1, headers.GetValues("Set-Cookie").Count(), "Set collapses duplicates");
    runner.AreEqual(0, headers.ToRawString().IndexOf("Set-Cookie", StringComparison.Ordinal), "order preserved");

    var parsed = HeaderCollection.Parse("A: 1\r\nB: 2\r\n  continued\r\n");
    runner.AreEqual("2 continued", parsed["B"], "obs-fold continuation appended");

    runner.IsTrue(new HeaderCollection([new HttpHeader("Connection", "keep-alive, Upgrade")])
        .HasToken("Connection", "upgrade"), "comma-list token match");

    return Task.CompletedTask;
});

await runner.RunAsync("content codec round trips", () =>
{
    var original = Encoding.UTF8.GetBytes("hello compressed world");

    using var gzipped = new MemoryStream();
    using (var gzip = new GZipStream(gzipped, CompressionLevel.Optimal, leaveOpen: true))
        gzip.Write(original);

    runner.AreEqual("hello compressed world",
        Encoding.UTF8.GetString(ContentCodec.Decode(gzipped.ToArray(), "gzip")), "gzip decode");

    runner.AreEqual("hello compressed world",
        Encoding.UTF8.GetString(ContentCodec.Decode(original, "zstd")),
        "unknown encoding returns the body untouched");

    runner.AreEqual("hello compressed world",
        Encoding.UTF8.GetString(ContentCodec.Decode(original, null)), "no encoding is a no-op");

    runner.IsTrue(ContentCodec.LooksTextual("application/json", original), "json is textual");
    runner.IsTrue(!ContentCodec.LooksTextual("image/png", [0, 1, 2, 0]), "png is binary");
    runner.AreEqual("utf-8", ContentCodec.CharsetFor("text/html; charset=utf-8").WebName, "charset parsed");
    runner.AreEqual("utf-8", ContentCodec.CharsetFor("text/html; charset=nonsense").WebName, "bad charset falls back");

    return Task.CompletedTask;
});

// --------------------------------------------------------- AutoResponder (end to end)

// Every case restores an empty rule set in a finally: a rule left enabled would silently claim
// every later test's traffic.
static AutoResponderSettings RuleSet(params AutoResponderRule[] rules) =>
    new() { Enabled = true, Rules = [.. rules] };

await runner.RunAsync("AutoResponder answers without contacting the origin", async () =>
{
    var before = origin.RequestCount;
    options.AutoResponder.Apply(RuleSet(new AutoResponderRule { Match = "/api/orders", Action = "*503" }));
    try
    {
        var response = await client.GetAsync($"{originBase}/api/orders?id=faked");
        runner.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode, "the client gets the rule's status");
        runner.AreEqual(before, origin.RequestCount, "and the origin was never contacted");

        var session = await WaitForAsync(store, s => s.Query == "?id=faked");
        runner.IsTrue(session.IsAutoResponded, "the session records that a rule answered it");
        runner.IsTrue(SearchQuery.Parse("is:auto").Matches(session), "is:auto finds it");
        runner.AreEqual(503, session.StatusCode, "the captured response is the faked one");
    }
    finally
    {
        options.AutoResponder.Apply(new AutoResponderSettings());
    }
});

await runner.RunAsync("a faked response keeps the connection alive", async () =>
{
    options.AutoResponder.Apply(RuleSet(new AutoResponderRule { Match = "/keepalive", Action = "*404" }));
    try
    {
        // Two requests in a row: if the first faked response closed the connection or mis-framed its
        // body, the second either fails or hangs.
        for (var i = 1; i <= 2; i++)
        {
            var response = await client.GetAsync($"{originBase}/keepalive?n={i}");
            runner.AreEqual(HttpStatusCode.NotFound, response.StatusCode, $"request {i} answered");
            runner.IsTrue(response.Headers.ConnectionClose != true, $"request {i} did not force a close");
        }

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, $"{originBase}/keepalive"));
        runner.AreEqual(0, (await head.Content.ReadAsByteArrayAsync()).Length, "HEAD gets no body");
    }
    finally
    {
        options.AutoResponder.Apply(new AutoResponderSettings());
    }
});

await runner.RunAsync("AutoResponder serves a file and honours rule order", async () =>
{
    var path = Path.Combine(Path.GetTempPath(), $"piper-autoresponder-{Guid.NewGuid():N}.json");
    await File.WriteAllTextAsync(path, """{"served":"from disk"}""");
    options.AutoResponder.Apply(RuleSet(
        new AutoResponderRule { Enabled = false, Match = "/from-disk", Action = "*500" },
        new AutoResponderRule { Match = "/from-disk", Action = path },
        new AutoResponderRule { Match = "/from-disk", Action = "*418" }));
    try
    {
        var response = await client.GetAsync($"{originBase}/from-disk");
        runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "the first enabled match wins");
        runner.AreEqual("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString(),
            "Content-Type comes from the file extension");
        runner.AreEqual("""{"served":"from disk"}""", await response.Content.ReadAsStringAsync(), "file body served");
    }
    finally
    {
        options.AutoResponder.Apply(new AutoResponderSettings());
        if (File.Exists(path)) File.Delete(path);
    }
});

await runner.RunAsync("AutoResponder redirects and refetches", async () =>
{
    options.AutoResponder.Apply(RuleSet(
        new AutoResponderRule { Match = @"REGEX:/redirect/(?<id>\d+)", Action = $"*redir:{originBase}/api/orders?id=${{id}}" },
        new AutoResponderRule { Match = "/refetch", Action = $"{originBase}/api/orders?id=refetched" }));
    try
    {
        // *redir: is a real 307 the client follows, so it ends up at the origin itself.
        var redirected = await client.GetAsync($"{originBase}/redirect/77");
        runner.AreEqual(HttpStatusCode.OK, redirected.StatusCode, "the client followed the redirect to the origin");

        var redirectSession = await WaitForAsync(store, s => s.Path == "/redirect/77");
        runner.AreEqual(307, redirectSession.StatusCode, "the rule answered with a 307");
        runner.IsTrue(redirectSession.IsAutoResponded, "and it is marked as auto-responded");

        // The followed request landing on ?id=77 is the proof that ${id} expanded from the regex.
        var followed = await WaitForAsync(store, s => s.Query == "?id=77");
        runner.AreEqual(200, followed.StatusCode, "the redirect target carried the captured id to the origin");

        // A bare URL refetches transparently: the client never learns another address was used.
        var refetched = await client.GetAsync($"{originBase}/refetch");
        runner.AreEqual(HttpStatusCode.OK, refetched.StatusCode, "the refetch succeeded");
        runner.IsTrue((await refetched.Content.ReadAsStringAsync()).Contains("orderId"), "with the other URL's content");

        var session = await WaitForAsync(store, s => s.Path == "/refetch");
        runner.AreEqual("/refetch", session.Path, "the session still shows the URL the client asked for");
        runner.IsTrue(session.IsAutoResponded, "and records the rule that redirected it");
    }
    finally
    {
        options.AutoResponder.Apply(new AutoResponderSettings());
    }
});

await runner.RunAsync("AutoResponder drops a connection on demand", async () =>
{
    // A dedicated client: SocketsHttpHandler silently retries once on a reused connection, which
    // would hide the drop on the shared client.
    using var dropClient = new HttpClient(new HttpClientHandler
    {
        Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}", BypassOnLocal: false),
        UseProxy = true,
    })
    { Timeout = TimeSpan.FromSeconds(10) };

    options.AutoResponder.Apply(RuleSet(new AutoResponderRule { Match = "/dropped", Action = "*drop" }));
    try
    {
        var failed = false;
        try { await dropClient.GetAsync($"{originBase}/dropped"); }
        catch (HttpRequestException) { failed = true; }
        runner.IsTrue(failed, "the request fails because the connection went away");

        var session = await WaitForAsync(store, s => s.Path == "/dropped");
        runner.AreEqual(SessionState.Failed, session.State, "the session is recorded as failed");
        runner.IsTrue(session.IsAutoResponded, "and names the rule that dropped it");
    }
    finally
    {
        options.AutoResponder.Apply(new AutoResponderSettings());
    }
});

await runner.RunAsync("AutoResponder toggles gate the whole rule set", async () =>
{
    var rules = RuleSet(new AutoResponderRule { Match = "/api/orders", Action = "*418" });

    // Disabled: rules exist but must not touch traffic.
    rules.Enabled = false;
    options.AutoResponder.Apply(rules);
    try
    {
        runner.AreEqual(HttpStatusCode.OK, (await client.GetAsync($"{originBase}/api/orders")).StatusCode,
            "with the master toggle off the request reaches the origin");

        // Passthrough off: anything unmatched is refused instead of being sent upstream.
        rules.Enabled = true;
        rules.PassthroughUnmatched = false;
        options.AutoResponder.Apply(rules);

        var before = origin.RequestCount;
        var unmatched = await client.GetAsync($"{originBase}/chunked");
        runner.AreEqual(HttpStatusCode.NotFound, unmatched.StatusCode, "an unmatched request is refused");
        runner.AreEqual(before, origin.RequestCount, "and never reaches the origin");
    }
    finally
    {
        options.AutoResponder.Apply(new AutoResponderSettings());
    }
});

await proxy.StopAsync();
origin.Stop();

// ------------------------------------------------- discovered test groups (name order)

foreach (var group in TestDiscovery.Groups)
    await runner.RunGroupAsync(group.Id, r => group.Run(r));

return runner.Summarize();

// ------------------------------------------------------------------- helpers

static async Task<Session> WaitForAsync(SessionStore store, Func<Session, bool> predicate, int timeoutMs = 5000)
{
    var deadline = Environment.TickCount64 + timeoutMs;
    while (Environment.TickCount64 < deadline)
    {
        var match = store.Snapshot().LastOrDefault(s => predicate(s) && s.Completed is not null);
        if (match is not null) return match;
        await Task.Delay(25);
    }
    throw new TimeoutException("No session matched within the timeout.");
}

/// <summary>Minimal origin server exercising the response shapes the proxy has to handle.</summary>
sealed class OriginServer(int port) : IDisposable
{
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;

    public string LastRequestHeaders { get; private set; } = string.Empty;

    /// <summary>Requests actually served. Proving the origin was *never* contacted needs a count,
    /// not a last-write-wins snapshot of the headers.</summary>
    public int RequestCount { get; private set; }

    public int Port => port;

    /// <summary>
    /// <c>HttpListener</c> cannot bind port 0, so ask the OS for a free port and bind that. Another
    /// process can take it in between (or hold an HTTP.sys reservation on it, which the TCP probe
    /// cannot see), so a refused bind retries on a fresh port.
    /// </summary>
    public static OriginServer StartOnFreePort()
    {
        for (var attempt = 1; ; attempt++)
        {
            var server = new OriginServer(FreePort());
            try
            {
                server.Start();
                return server;
            }
            catch (HttpListenerException) when (attempt < 10)
            {
                server.Dispose();
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = Task.Run(async () =>
            {
                try { await HandleAsync(context); }
                catch (Exception) { /* the test asserts on the client side */ }
            }, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;

        LastRequestHeaders = string.Join("\r\n",
            request.Headers.AllKeys.Select(k => $"{k}: {request.Headers[k]}"));
        RequestCount++;

        switch (request.Url?.AbsolutePath)
        {
            case "/api/orders":
                await WriteAsync(response, 200, "application/json",
                    Encoding.UTF8.GetBytes("""{ "orderId": 42, "status": "shipped" }"""));
                break;

            case "/api/echo":
            {
                using var reader = new StreamReader(request.InputStream);
                var body = await reader.ReadToEndAsync();
                await WriteAsync(response, 200, "application/json", Encoding.UTF8.GetBytes(body));
                break;
            }

            case "/chunked":
                response.StatusCode = 200;
                response.SendChunked = true;
                response.ContentType = "text/plain";
                foreach (var part in new[] { "chunk-one|", "chunk-two|", "chunk-three" })
                {
                    var bytes = Encoding.UTF8.GetBytes(part);
                    await response.OutputStream.WriteAsync(bytes);
                    await response.OutputStream.FlushAsync();
                }
                response.Close();
                break;

            case "/gzip":
            {
                var payload = Encoding.UTF8.GetBytes("this is a compressed payload for the codec test");
                using var buffer = new MemoryStream();
                using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                    gzip.Write(payload);

                response.AddHeader("Content-Encoding", "gzip");
                await WriteAsync(response, 200, "text/plain", buffer.ToArray());
                break;
            }

            default:
                await WriteAsync(response, 404, "text/plain", Encoding.UTF8.GetBytes("not found"));
                break;
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string contentType, byte[] body)
    {
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
    }
}

/// <summary>Command-line options of the smoke-test runner.</summary>
sealed record TestOptions(string? Filter, TimeSpan Timeout, bool List)
{
    /// <summary>Prefix of the per-process temp folder, which the runner deletes on exit.</summary>
    public const string SandboxPrefix = "Piper-SmokeTests-";

    /// <summary>The slowest test today takes a few seconds; this leaves room for a loaded machine.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static bool TryParse(string[] args, out TestOptions options, out string error)
    {
        string? filter = null;
        var timeout = DefaultTimeout;
        var list = false;
        options = new TestOptions(null, DefaultTimeout, false);
        error = "usage: Piper.SmokeTests [--filter <substring>] [--timeout <seconds>] [--list]";

        for (var i = 0; i < args.Length; i++)
        {
            // "--name value" or "--name=value".
            var equals = args[i].IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? args[i] : args[i][..equals];
            var value = equals < 0 ? (i + 1 < args.Length && name != "--list" ? args[i + 1] : null) : args[i][(equals + 1)..];
            if (equals < 0 && value is not null) i++;

            switch (name)
            {
                case "--filter":
                    if (string.IsNullOrEmpty(value)) return false;
                    filter = value;
                    break;
                case "--timeout":
                    if (!int.TryParse(value, out var seconds) || seconds is < 1 or > 3600) return false;
                    timeout = TimeSpan.FromSeconds(seconds);
                    break;
                case "--list" when equals < 0:
                    list = true;
                    break;
                default:
                    return false;
            }
        }

        options = new TestOptions(filter, timeout, list);
        error = string.Empty;
        return true;
    }
}

/// <summary>One discoverable test group: a public static <c>Task Run*Async(TestRunner)</c> method.</summary>
sealed record TestGroup(string Id, MethodInfo Method)
{
    public Task Run(TestRunner runner) => (Task)Method.Invoke(null, [runner])!;
}

static class TestDiscovery
{
    /// <summary>
    /// Every test group in this assembly, in ordinal "Class.Method" order, so a run is the same on
    /// every machine and adding a group needs no registration. Sorting the names (rather than trusting
    /// reflection order, which the runtime does not promise) is what makes the order stable.
    /// </summary>
    public static IReadOnlyList<TestGroup> Groups { get; } = Discover(typeof(TestRunner).Assembly);

    public static IReadOnlyList<TestGroup> Discover(Assembly assembly) =>
    [
        .. assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(IsGroup)
                .Select(method => new TestGroup($"{type.FullName}.{method.Name}", method)))
            .OrderBy(group => group.Id, StringComparer.Ordinal),
    ];

    public static bool IsGroup(MethodInfo method) =>
        method is { IsPublic: true, IsStatic: true }
        && method.Name.StartsWith("Run", StringComparison.Ordinal)
        && method.Name.EndsWith("Async", StringComparison.Ordinal)
        && method.ReturnType == typeof(Task)
        && method.GetParameters() is [{ ParameterType: var parameter }]
        && parameter == typeof(TestRunner);
}

sealed class TestRunner(TextWriter output, string? filter = null, TimeSpan? testTimeout = null, bool? underCi = null)
{
    private readonly bool _underCi = underCi
        ?? (Environment.GetEnvironmentVariable("CI") is { Length: > 0 } || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is { Length: > 0 });

    /// <summary>The state of one running test; assertions find it through <see cref="_current"/>.</summary>
    private sealed class TestContext(string name)
    {
        public string Name { get; } = name;

        /// <summary>Set once the runner has given up on the test; its late assertions are dropped.</summary>
        public volatile bool Abandoned;
    }

    private readonly object _gate = new();
    private readonly AsyncLocal<TestContext?> _current = new();
    private readonly TimeSpan _testTimeout = testTimeout ?? TestOptions.DefaultTimeout;
    private readonly List<string> _failures = [];
    private readonly List<(string Name, TimeSpan Elapsed)> _timings = [];
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private bool _groupSelected;
    private int _passed;
    private int _failed;
    private int _skipped;
    private int _unavailable;

    public int Passed => _passed;
    public int Failed => _failed;
    public int Skipped => _skipped;
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>Elapsed time of each test that ran, in run order.</summary>
    public IReadOnlyList<(string Name, TimeSpan Elapsed)> Timings => _timings;

    /// <summary>Runs one test group, which selects all of its tests when the filter matches its id.</summary>
    public async Task RunGroupAsync(string id, Func<TestRunner, Task> group)
    {
        _groupSelected = filter is not null && id.Contains(filter, StringComparison.OrdinalIgnoreCase);
        try
        {
            await group(this);
        }
        catch (Exception ex)
        {
            // Setup that fails outside any test; report it as this group's failure and go on.
            Fail($"{id}: threw {ex.GetType().Name}: {string.Join(" -> ", InnerMessages(ex))}");
        }
        finally
        {
            _groupSelected = false;
        }
    }

    public async Task RunAsync(string name, Func<Task> body)
    {
        if (filter is not null && !_groupSelected && !name.Contains(filter, StringComparison.OrdinalIgnoreCase))
        {
            _skipped++;
            return;
        }

        var test = new TestContext(name);
        _current.Value = test;
        output.WriteLine($"\n== {name}");
        var stopwatch = Stopwatch.StartNew();

        // Task.Run so a test that blocks synchronously (before its first await) is also caught by the timeout.
        var running = Task.Run(body);
        try
        {
            await running.WaitAsync(_testTimeout);
        }
        catch (TimeoutException) when (!running.IsCompleted)
        {
            test.Abandoned = true;
            _ = running.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            var message = $"{name}: timed out after {_testTimeout.TotalSeconds:0.##} s (hung or too slow)";
            Fail(message);
            output.WriteLine($"   TIMEOUT  {message}");
        }
        catch (Exception ex)
        {
            var chain = string.Join(" -> ", InnerMessages(ex));
            Fail($"{name}: threw {ex.GetType().Name}: {chain}");
            output.WriteLine($"   EXCEPTION  {ex.GetType().Name}: {chain}");
        }
        finally
        {
            _current.Value = null;
        }

        lock (_gate) _timings.Add((name, stopwatch.Elapsed));
        output.WriteLine($"   ({stopwatch.Elapsed.TotalMilliseconds:0} ms)");
    }

    private void Fail(string message)
    {
        lock (_gate)
        {
            _failed++;
            _failures.Add(message);
        }
    }

    private static IEnumerable<string> InnerMessages(Exception ex)
    {
        var current = (Exception?)ex;
        while (current is not null)
        {
            yield return current.Message;
            current = current.InnerException;
        }
    }

    public void IsTrue(bool condition, string what)
    {
        var test = _current.Value;
        if (test is { Abandoned: true }) return;

        if (condition)
        {
            Interlocked.Increment(ref _passed);
            output.WriteLine($"   ok    {what}");
        }
        else
        {
            Fail($"{test?.Name ?? OutsideTest} / {what}");
            output.WriteLine($"   FAIL  {what}");
        }
    }

    public void AreEqual<T>(T expected, T actual, string what)
    {
        var test = _current.Value;
        if (test is { Abandoned: true }) return;

        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Interlocked.Increment(ref _passed);
            output.WriteLine($"   ok    {what}");
        }
        else
        {
            Fail($"{test?.Name ?? OutsideTest} / {what}: expected <{expected}>, got <{actual}>");
            output.WriteLine($"   FAIL  {what}: expected <{expected}>, got <{actual}>");
        }
    }

    private const string OutsideTest = "(outside a test)";

    /// <summary>Tests that could not check anything because a tool they drive is not installed.</summary>
    public int NotRun => _unavailable;

    /// <summary>
    /// Reports that the running test cannot check anything here because a tool it drives is missing. That
    /// is counted apart from the passes and shown in the summary, so a machine without the tool is never
    /// read as having verified it. Under CI (the CI or GITHUB_ACTIONS environment variable is set) every
    /// such tool is expected, so the same call fails the test instead.
    /// </summary>
    public void ToolMissing(string what)
    {
        var test = _current.Value;
        if (test is { Abandoned: true }) return;

        if (_underCi)
        {
            Fail($"{test?.Name ?? OutsideTest} / {what} (required under CI)");
            output.WriteLine($"   FAIL  {what} (required under CI)");
            return;
        }

        Interlocked.Increment(ref _unavailable);
        output.WriteLine($"   SKIPPED  {what}");
    }

    public int Summarize()
    {
        output.WriteLine($"\n{new string('-', 60)}");
        output.WriteLine($"{_passed} passed, {_failed} failed" + (_unavailable > 0 ? $", {_unavailable} not run (tool not installed)" : string.Empty));
        output.WriteLine($"{_timings.Count} tests run, {_skipped} skipped by --filter, {_total.Elapsed.TotalSeconds:0.0} s");

        output.WriteLine("Slowest tests:");
        foreach (var (name, elapsed) in _timings.OrderByDescending(t => t.Elapsed).Take(10))
            output.WriteLine($"  {elapsed.TotalMilliseconds,7:0} ms  {name}");

        foreach (var failure in _failures) output.WriteLine($"  - {failure}");

        if (filter is not null && _timings.Count == 0)
        {
            output.WriteLine($"No test matched --filter \"{filter}\". Use --list for the group names.");
            return 1;
        }

        return _failed == 0 ? 0 : 1;
    }
}
