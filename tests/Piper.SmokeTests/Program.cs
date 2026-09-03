using System.IO.Compression;
using System.Net;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// End-to-end smoke test: a real origin server, the real proxy, a real HttpClient.
// No test framework so this always runs without a NuGet restore.

var runner = new TestRunner();

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

const int OriginPort = 19099;
const int ProxyPort = 19088;
var originBase = $"http://127.0.0.1:{OriginPort}";

using var origin = new OriginServer(OriginPort);
origin.Start();

var store = new SessionStore();
var options = new ProxyOptions { Port = ProxyPort, DecryptHttps = false };
using var ca = CertificateAuthority.LoadOrCreate(
    Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Certs"));

await using var proxy = new ProxyServer(options, ca, store);
proxy.Start();

using var client = new HttpClient(new HttpClientHandler
{
    Proxy = new WebProxy($"http://127.0.0.1:{ProxyPort}", BypassOnLocal: false),
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

    // Bad input degrades instead of throwing.
    var bad = SearchQuery.Parse("bogusfield:x");
    runner.IsTrue(bad.Warnings.Count == 1, "unknown field reported as a warning");
    runner.AreEqual(all.Length, bad.Filter(all).Count(), "bad term is ignored, not fatal");

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
        var response = await client.GetAsync($"http://{requestedHost}:{OriginPort}/api/orders?id=remapped");
        runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "mapped request reaches the local origin");
        runner.IsTrue(origin.LastRequestHeaders.Contains($"Host: {requestedHost}:{OriginPort}", StringComparison.OrdinalIgnoreCase),
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
        var response = await client.GetAsync($"http://{requestedHost}:{OriginPort}/api/orders?id=authority-rewrite");
        runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "hostname target reaches the local origin");
        runner.IsTrue(origin.LastRequestHeaders.Contains($"Host: localhost:{OriginPort}", StringComparison.OrdinalIgnoreCase),
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
        Proxy = new WebProxy($"http://127.0.0.1:{ProxyPort}", BypassOnLocal: false),
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

// --------------------------------------------------------------------- HPACK

await HostFilterTests.RunAsync(runner);
await WebFormParserTests.RunAsync(runner);
await TextTransformsTests.RunAsync(runner);
await TextTransformDetectorTests.RunAsync(runner);
await FilterSettingsStoreTests.RunAsync(runner);
await HostFilterHideTests.RunAsync(runner);
await StatusBarSettingsStoreTests.RunAsync(runner);
await ProxyConfigurationSettingsStoreTests.RunAsync(runner);
await ConnectionSettingsBlobTests.RunAsync(runner);
await AutoResponderMatchTests.RunAsync(runner);
await AutoResponderActionTests.RunAsync(runner);
await AutoResponderSettingsStoreTests.RunAsync(runner);
await HttpWireFormatTests.RunAsync(runner);
await JsonEditingTests.RunAsync(runner);
await HostRemappingTests.RunAsync(runner);
await SessionStoreAdmissionTests.RunAsync(runner);
await SazImporterTests.RunAsync(runner);
await SazExporterTests.RunAsync(runner);
await HpackTests.RunAsync(runner);
await Http2FrameTests.RunAsync(runner);
await Http2MessageAdapterTests.RunAsync(runner);
await Http2ConnectionTests.RunAsync(runner);
await Http2Tests.RunAsync(runner);
await Http3CodecTests.RunAsync(runner);
await Http3Tests.RunAsync(runner);

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

sealed class TestRunner
{
    private int _passed;
    private int _failed;
    private readonly List<string> _failures = [];
    private string _currentTest = string.Empty;

    public async Task RunAsync(string name, Func<Task> body)
    {
        _currentTest = name;
        Console.WriteLine($"\n== {name}");
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            _failed++;
            var chain = string.Join(" -> ", InnerMessages(ex));
            var message = $"{name}: threw {ex.GetType().Name}: {chain}";
            _failures.Add(message);
            Console.WriteLine($"   EXCEPTION  {ex.GetType().Name}: {chain}");
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
        if (condition)
        {
            _passed++;
            Console.WriteLine($"   ok    {what}");
        }
        else
        {
            _failed++;
            _failures.Add($"{_currentTest} / {what}");
            Console.WriteLine($"   FAIL  {what}");
        }
    }

    public void AreEqual<T>(T expected, T actual, string what)
    {
        if (EqualityComparer<T>.Default.Equals(expected, actual))
        {
            _passed++;
            Console.WriteLine($"   ok    {what}");
        }
        else
        {
            _failed++;
            _failures.Add($"{_currentTest} / {what}: expected <{expected}>, got <{actual}>");
            Console.WriteLine($"   FAIL  {what}: expected <{expected}>, got <{actual}>");
        }
    }

    public int Summarize()
    {
        Console.WriteLine($"\n{new string('-', 60)}");
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        foreach (var failure in _failures) Console.WriteLine($"  - {failure}");
        return _failed == 0 ? 0 : 1;
    }
}
