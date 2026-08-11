using System.IO.Compression;
using System.Net;
using System.Text;

// Sends varied traffic through a proxy so the UI can be exercised without internet access.
//
//   trafficgen [proxyPort] [originPort]
//
// Must be .NET (not Windows PowerShell): .NET Framework's WebProxy unconditionally
// bypasses the proxy for loopback targets, so requests would never reach Piper.

var proxyPort = args.Length > 0 && int.TryParse(args[0], out var pp) ? pp : 8888;
var originPort = args.Length > 1 && int.TryParse(args[1], out var op) ? op : 19200;

using var origin = new Origin(originPort);
origin.Start();
Console.WriteLine($"origin listening on http://127.0.0.1:{originPort}");

using var handler = new HttpClientHandler
{
    // BypassOnLocal must be false or loopback requests skip the proxy entirely.
    Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}", BypassOnLocal: false),
    UseProxy = true,
};
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
client.DefaultRequestHeaders.Add("User-Agent", "Piper-TrafficGen/1.0");

var baseUrl = $"http://127.0.0.1:{originPort}";
var sent = 0;
var failed = 0;

async Task GetAsync(string path, string? accept = null)
{
    try
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        if (accept is not null) request.Headers.Add("Accept", accept);
        using var response = await client.SendAsync(request);
        await response.Content.ReadAsByteArrayAsync();
        Console.WriteLine($"  GET  {path} -> {(int)response.StatusCode}");
        sent++;
    }
    catch (Exception ex) { Console.WriteLine($"  GET  {path} -> FAILED {ex.Message}"); failed++; }
}

async Task PostAsync(string path, string json)
{
    try
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(baseUrl + path, content);
        await response.Content.ReadAsByteArrayAsync();
        Console.WriteLine($"  POST {path} -> {(int)response.StatusCode}");
        sent++;
    }
    catch (Exception ex) { Console.WriteLine($"  POST {path} -> FAILED {ex.Message}"); failed++; }
}

await GetAsync("/index.html", "text/html");
await GetAsync("/static/app.js");
await GetAsync("/static/styles.css");
await GetAsync("/static/logo.png");
await GetAsync("/api/orders?id=42");
await GetAsync("/api/orders?id=7&expand=items");
await GetAsync("/api/products?category=widgets&page=2");
await GetAsync("/api/slow");
await GetAsync("/api/large");
await GetAsync("/api/missing");
await GetAsync("/api/secret");

await PostAsync("/api/login", """{"user_id":"tom","password":"hunter2"}""");
await PostAsync("/api/checkout", """{"order_id":8871,"coupon":"SPRING25","items":[1,2,3]}""");
await PostAsync("/api/orders", """{"customer":"acme","total":129.95,"currency":"GBP"}""");
await PostAsync("/api/events", """{"event":"page_view","path":"/checkout","session":"abc123"}""");

Console.WriteLine($"\n{sent} sent, {failed} failed");
return failed == 0 ? 0 : 1;

/// <summary>Serves a spread of status codes, content types and body shapes.</summary>
sealed class Origin(int port) : IDisposable
{
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;

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
            _ = Task.Run(async () => { try { await HandleAsync(context); } catch { /* client gone */ } }, ct);
        }
    }

    private static async Task HandleAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var response = context.Response;

        switch (path)
        {
            case "/index.html":
                await WriteAsync(response, 200, "text/html; charset=utf-8",
                    "<html><head><title>Widget Store</title></head><body><h1>Widgets</h1>"
                    + "<p>Everything you need, and several things you do not.</p></body></html>");
                return;

            case "/static/app.js":
                await WriteAsync(response, 200, "application/javascript",
                    "(function(){console.log('app booted');window.__ready=true;})();");
                return;

            case "/static/styles.css":
                await WriteAsync(response, 200, "text/css", "body{margin:0;font-family:system-ui}h1{color:#333}");
                return;

            case "/static/logo.png":
            {
                // A tiny real PNG, so the binary/hex path gets exercised.
                var png = Convert.FromBase64String(
                    "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
                response.AddHeader("Cache-Control", "public, max-age=86400");
                await WriteBytesAsync(response, 200, "image/png", png);
                return;
            }

            case "/api/orders" when context.Request.HttpMethod == "GET":
                await WriteAsync(response, 200, "application/json",
                    """{"orderId":42,"customer":"acme","status":"shipped","total":129.95,"items":[{"sku":"W-1","qty":2}]}""");
                return;

            case "/api/orders":
                response.AddHeader("Location", "/api/orders/9001");
                await WriteAsync(response, 201, "application/json", """{"orderId":9001,"status":"created"}""");
                return;

            case "/api/products":
                await WriteAsync(response, 200, "application/json",
                    """{"page":2,"category":"widgets","results":[{"sku":"W-1"},{"sku":"W-2"},{"sku":"W-3"}]}""");
                return;

            case "/api/login":
                response.AddHeader("Set-Cookie", "session=abc123; HttpOnly; Path=/");
                await WriteAsync(response, 200, "application/json",
                    """{"token":"eyJhbGciOiJIUzI1NiJ9.payload.sig","user_id":"tom","expires_in":3600}""");
                return;

            case "/api/checkout":
                await WriteAsync(response, 402, "application/json",
                    """{"error":"payment_declined","order_id":8871,"retryable":false}""");
                return;

            case "/api/events":
                response.StatusCode = 204;
                response.Close();
                return;

            case "/api/secret":
                response.AddHeader("WWW-Authenticate", "Bearer realm=\"api\"");
                await WriteAsync(response, 401, "application/json", """{"error":"unauthorized"}""");
                return;

            case "/api/slow":
                await Task.Delay(1200);
                await WriteAsync(response, 200, "application/json", """{"slow":true,"waited_ms":1200}""");
                return;

            case "/api/large":
            {
                // gzip-encoded so the decode path shows a readable body in the inspector.
                var payload = Encoding.UTF8.GetBytes(
                    "{\"rows\":[" + string.Join(",", Enumerable.Range(0, 400)
                        .Select(i => $"{{\"id\":{i},\"name\":\"row-{i}\",\"value\":{i * 7}}}")) + "]}");

                using var buffer = new MemoryStream();
                using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                    gzip.Write(payload);

                response.AddHeader("Content-Encoding", "gzip");
                await WriteBytesAsync(response, 200, "application/json", buffer.ToArray());
                return;
            }

            default:
                await WriteAsync(response, 404, "application/json", """{"error":"not_found"}""");
                return;
        }
    }

    private static Task WriteAsync(HttpListenerResponse response, int status, string contentType, string body) =>
        WriteBytesAsync(response, status, contentType, Encoding.UTF8.GetBytes(body));

    private static async Task WriteBytesAsync(HttpListenerResponse response, int status, string contentType, byte[] body)
    {
        response.StatusCode = status;
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
    }
}
