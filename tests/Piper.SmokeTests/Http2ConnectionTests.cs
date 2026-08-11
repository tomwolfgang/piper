using System.Net;
using System.Net.Sockets;
using Piper.Core.Http;
using Piper.Core.Http2;

// Http2Connection wired to a stubbed handler over a bare cleartext (h2c) TCP loopback socket --
// no TLS, no upstream. Proves the frame demux / per-stream concurrency / single-writer outbox
// design in isolation, before any of it is wired into ProxyServer's real TLS-terminated path.
internal static class Http2ConnectionTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

        await runner.RunAsync("Http2Connection serves a single GET", async () =>
        {
            await using var harness = await Harness.StartAsync(StubHandler);
            using var client = harness.CreateClient();

            var response = await client.GetAsync($"{harness.BaseUrl}/hello");
            var body = await response.Content.ReadAsStringAsync();

            runner.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, "status");
            runner.AreEqual("2.0", response.Version.ToString(), "client negotiated h2");
            runner.IsTrue(body.Contains("GET /hello"), "body reflects the request");
        });

        await runner.RunAsync("Http2Connection serves a POST body round trip", async () =>
        {
            await using var harness = await Harness.StartAsync(StubHandler);
            using var client = harness.CreateClient();

            var response = await client.PostAsync($"{harness.BaseUrl}/echo", new StringContent("payload-data"));
            var body = await response.Content.ReadAsStringAsync();

            runner.AreEqual(System.Net.HttpStatusCode.OK, response.StatusCode, "status");
            runner.IsTrue(body.Contains("POST /echo"), "method and path");
            runner.IsTrue(body.Contains("payload-data"), "request body reached the handler");
        });

        await runner.RunAsync("Http2Connection multiplexes many concurrent streams without cross-talk", async () =>
        {
            await using var harness = await Harness.StartAsync(StubHandler);
            using var client = harness.CreateClient();

            const int count = 20;
            var tasks = Enumerable.Range(0, count).Select(async i =>
            {
                var path = i % 3 == 0 ? $"/slow/{i}" : $"/fast/{i}";
                var response = await client.GetAsync($"{harness.BaseUrl}{path}");
                var body = await response.Content.ReadAsStringAsync();
                return (i, path, body);
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            foreach (var (i, path, body) in results)
                runner.AreEqual($"GET {path}", body, $"stream {i} got exactly its own response body, not another stream's");
        });

        await runner.RunAsync("Http2Connection returns a distinct status per stream", async () =>
        {
            await using var harness = await Harness.StartAsync(StubHandler);
            using var client = harness.CreateClient();

            var ok = await client.GetAsync($"{harness.BaseUrl}/hello");
            var notFound = await client.GetAsync($"{harness.BaseUrl}/status/404");

            runner.AreEqual(System.Net.HttpStatusCode.OK, ok.StatusCode, "first stream status");
            runner.AreEqual(System.Net.HttpStatusCode.NotFound, notFound.StatusCode, "second stream status, independent of the first");
        });

        await runner.RunAsync("many concurrent large bodies don't overrun the shared connection flow-control window", async () =>
        {
            // Regression test for a real bug: sending each stream's body against the *shared*
            // connection-level window via separate read-then-subtract steps let concurrent
            // streams each act on the same stale balance and, combined, send more than the peer
            // had granted -- a real HTTP/2 client (browser or HttpClient) enforces flow control
            // strictly and resets the connection when that happens. Each body here is larger than
            // the 64KB RFC-default initial window, and there are enough of them running at once
            // that their combined size can only fit if the shared window is spent atomically.
            await using var harness = await Harness.StartAsync(StubHandler);
            using var client = harness.CreateClient();

            const int count = 12;
            const int bodySize = 300_000;

            var tasks = Enumerable.Range(0, count).Select(async i =>
            {
                var response = await client.GetAsync($"{harness.BaseUrl}/big/{i}?size={bodySize}");
                var body = await response.Content.ReadAsByteArrayAsync();
                return (i, response.StatusCode, body);
            }).ToArray();

            var results = await Task.WhenAll(tasks);

            foreach (var (i, status, body) in results)
            {
                runner.AreEqual(System.Net.HttpStatusCode.OK, status, $"stream {i} completed instead of being reset");
                runner.AreEqual(bodySize, body.Length, $"stream {i} body arrived complete, not truncated");
                runner.IsTrue(BigBodyPattern(i, bodySize).AsSpan().SequenceEqual(body), $"stream {i} body matches its own pattern, not another stream's");
            }
        });
    }

    /// <summary>Deterministic per-stream fill so cross-talk between concurrently-sent large
    /// bodies would show up as a content mismatch, not just a length mismatch.</summary>
    private static byte[] BigBodyPattern(int streamIndex, int size)
    {
        var body = new byte[size];
        for (var i = 0; i < size; i++) body[i] = (byte)(streamIndex + i);
        return body;
    }

    private static Task<HttpResponseData> StubHandler(HttpRequestData request, CancellationToken ct)
    {
        var path = request.Url!.PathAndQuery;
        if (path.StartsWith("/status/", StringComparison.Ordinal))
        {
            var code = int.Parse(path["/status/".Length..]);
            return Task.FromResult(HttpResponseData.Simple(code, "Custom", $"{request.Method} {path}"));
        }

        if (path.StartsWith("/big/", StringComparison.Ordinal))
        {
            var afterPrefix = path["/big/".Length..];
            var queryStart = afterPrefix.IndexOf('?');
            var streamIndex = int.Parse(queryStart >= 0 ? afterPrefix[..queryStart] : afterPrefix);
            var size = int.Parse(request.Url!.Query[(request.Url.Query.IndexOf("size=", StringComparison.Ordinal) + 5)..]);

            var response = new HttpResponseData { StatusCode = 200, ReasonPhrase = "OK", Body = BigBodyPattern(streamIndex, size) };
            response.Headers.Set("Content-Type", "application/octet-stream");
            return Task.FromResult(response);
        }

        return SlowOrFastAsync(request, path, ct);
    }

    private static async Task<HttpResponseData> SlowOrFastAsync(HttpRequestData request, string path, CancellationToken ct)
    {
        if (path.StartsWith("/slow/", StringComparison.Ordinal))
            await Task.Delay(80, ct).ConfigureAwait(false);

        var bodyText = request.Body.Length > 0
            ? $"{request.Method} {path} body={request.BodyAsText()}"
            : $"{request.Method} {path}";
        return HttpResponseData.Simple(200, "OK", bodyText);
    }

    /// <summary>Bare TCP loopback listener speaking cleartext HTTP/2, so the demux/concurrency
    /// design can be exercised with a real <see cref="HttpClient"/> without any TLS involved.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly Task _acceptLoop;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _connections = [];
        private readonly Lock _gate = new();

        private Harness(TcpListener listener, Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
        {
            _listener = listener;
            _acceptLoop = AcceptLoopAsync(handler);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
        public string BaseUrl => $"http://127.0.0.1:{Port}";

        public static Task<Harness> StartAsync(Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new Harness(listener, handler));
        }

        public HttpClient CreateClient() => new(new SocketsHttpHandler
        {
            // Cleartext h2 ("h2c"): no ALPN/TLS negotiation, so this must be forced explicitly.
        })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(10),
        };

        private async Task AcceptLoopAsync(Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                var task = Task.Run(async () =>
                {
                    using var c = client;
                    c.NoDelay = true;
                    var connection = new Http2Connection(c.GetStream(), handler);
                    try { await connection.RunAsync(_cts.Token).ConfigureAwait(false); }
                    catch { /* test asserts on the client side */ }
                }, _cts.Token);

                lock (_gate) _connections.Add(task);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            _listener.Stop();
            try { await _acceptLoop.ConfigureAwait(false); } catch { }

            Task[] pending;
            lock (_gate) pending = _connections.ToArray();
            try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
        }
    }
}
