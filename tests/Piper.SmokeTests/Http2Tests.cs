using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// End-to-end HTTP/2 downstream tests: a real HttpClient, the real ProxyServer with HTTPS
// decryption and ALPN both turned on, and a real (self-signed) TLS origin. This exercises the
// actual MITM path -- SNI leaf minting, ALPN negotiation, Http2Connection wired through
// Http2RequestForwarder to a real upstream connection -- not just the isolated h2c harness in
// Http2ConnectionTests.
internal static class Http2Tests
{
    public static async Task RunAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Http2-Certs"));

        using var origin = new TlsOriginServer(ca.GetCertificateFor("127.0.0.1"));
        var originBase = $"https://127.0.0.1:{origin.Port}";

        var store = new SessionStore();
        var options = new ProxyOptions
        {
            Port = 0,
            DecryptHttps = true,
            ValidateUpstreamCertificates = false, // the origin's leaf isn't independently trusted; MITM re-encryption is
            EnableHttp2Downstream = true,
        };

        await using var proxy = new ProxyServer(options, ca, store);
        proxy.Start();
        var proxyPort = proxy.Endpoint!.Port;

        using var client = CreateClient(proxyPort, ca.RootCertificate);

        await runner.RunAsync("HTTP/2 GET through the real MITM path negotiates h2 and is captured", async () =>
        {
            var response = await client.GetAsync($"{originBase}/hello");
            var body = await response.Content.ReadAsStringAsync();

            runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "status reaches the client");
            runner.AreEqual("2.0", response.Version.ToString(), "client actually negotiated h2, not a silent 1.1 fallback");
            runner.IsTrue(body.Contains("GET /hello"), "body reaches the client");

            var session = await WaitForSessionAsync(store, s => s.Path == "/hello");
            runner.AreEqual("HTTP/2", session.Request!.HttpVersion, "captured request tagged as the browser's actual protocol");
            runner.AreEqual(200, session.StatusCode, "captured status");
            runner.AreEqual(TransportProtocol.Http2, session.RequestProtocol, "Session.RequestProtocol computed from HttpVersion");
            runner.AreEqual(TransportProtocol.Http1_1, session.ResponseProtocol, "Session.ResponseProtocol: origin was plain h1.1 here");
        });

        await runner.RunAsync("HTTP/2 POST body round-trips through the real MITM path", async () =>
        {
            var response = await client.PostAsync($"{originBase}/echo", new StringContent("h2-payload"));
            var body = await response.Content.ReadAsStringAsync();

            runner.IsTrue(body.Contains("h2-payload"), "body reached the origin and came back");

            var session = await WaitForSessionAsync(store, s => s.Path == "/echo");
            runner.AreEqual("h2-payload", session.Request!.BodyAsText(), "captured request body");
        });

        await runner.RunAsync("concurrent HTTP/2 requests through the real MITM path don't cross-talk", async () =>
        {
            var tasks = Enumerable.Range(0, 8).Select(async i =>
            {
                var response = await client.GetAsync($"{originBase}/item/{i}");
                return (i, body: await response.Content.ReadAsStringAsync());
            }).ToArray();

            var results = await Task.WhenAll(tasks);
            foreach (var (i, body) in results)
                runner.AreEqual($"GET /item/{i}", body, $"request {i} got exactly its own response");
        });

        // ---------------------------------------------------- upstream leg negotiates h2 too

        await using var h2Origin = new TestHttp2Origin(ca.GetCertificateFor("127.0.0.1"), EchoHandler);
        var h2OriginBase = $"https://127.0.0.1:{h2Origin.Port}";

        await runner.RunAsync("browser speaks h1.1, Piper negotiates h2 upstream: both legs captured independently", async () =>
        {
            using var h1Client = new HttpClient(new HttpClientHandler
            {
                Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
                UseProxy = true,
                ServerCertificateCustomValidationCallback = (_, cert, chain, _) => TrustsRoot(ca.RootCertificate, cert),
            })
            { DefaultRequestVersion = HttpVersion.Version11, DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact };

            var response = await h1Client.GetAsync($"{h2OriginBase}/upstream-h2");
            var body = await response.Content.ReadAsStringAsync();

            runner.AreEqual("1.1", response.Version.ToString(), "browser leg stayed h1.1");
            runner.IsTrue(body.Contains("GET /upstream-h2"), "body reached the client");

            var session = await WaitForSessionAsync(store, s => s.Path == "/upstream-h2");
            runner.AreEqual("HTTP/1.1", session.Request!.HttpVersion, "downstream leg recorded as h1.1 (the browser's actual choice)");
            runner.AreEqual("HTTP/2", session.Response!.HttpVersion, "upstream leg recorded as h2 (the origin's actual choice)");
            runner.IsTrue(!session.Response.Headers.Contains("Connection"), "no Connection header leaks into an h2-sourced response");
        });

        await runner.RunAsync("both legs negotiate h2: full translation matrix corner", async () =>
        {
            var response = await client.PostAsync($"{h2OriginBase}/both-h2", new StringContent("both-h2-body"));
            var body = await response.Content.ReadAsStringAsync();

            runner.AreEqual("2.0", response.Version.ToString(), "browser leg negotiated h2");
            runner.IsTrue(body.Contains("both-h2-body"), "body round-tripped through two independent h2 connections");

            var session = await WaitForSessionAsync(store, s => s.Path == "/both-h2");
            runner.AreEqual("HTTP/2", session.Request!.HttpVersion, "downstream leg h2");
            runner.AreEqual("HTTP/2", session.Response!.HttpVersion, "upstream leg h2");
        });

        await runner.RunAsync("a response larger than the 64KB connection window survives both h2 legs", async () =>
        {
            // Regression test for the bug that broke real browsing: the connection-level
            // flow-control window always starts at 65,535 and grows ONLY via WINDOW_UPDATE
            // (RFC 9113 6.9.2 -- SETTINGS_INITIAL_WINDOW_SIZE sizes stream windows only).
            // Piper's h2 client role never sent WINDOW_UPDATE, so any origin response past 64KB
            // stalled forever: small pages loaded fine, every real one hung. 512KB is chosen to
            // need several rounds of credit, not just one.
            const int size = 512 * 1024;
            var response = await client.GetAsync($"{h2OriginBase}/large?size={size}");
            var body = await response.Content.ReadAsByteArrayAsync();

            runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "large response completed instead of stalling");
            runner.AreEqual(size, body.Length, "whole body arrived, not just the first window");
            runner.IsTrue(LargePattern(size).AsSpan().SequenceEqual(body), "body bytes are intact end to end");
        });

        await runner.RunAsync("a request body larger than the 64KB connection window survives both h2 legs", async () =>
        {
            // The mirror case: Piper's h2 *server* role receiving a large upload. It had the same
            // defect from the other direction -- it assumed the connection window started at the
            // 1MB it advertised in SETTINGS rather than the RFC-mandated 65,535.
            const int size = 512 * 1024;
            var payload = LargePattern(size);
            var response = await client.PostAsync($"{h2OriginBase}/upload", new ByteArrayContent(payload));
            var echoed = await response.Content.ReadAsStringAsync();

            runner.AreEqual(HttpStatusCode.OK, response.StatusCode, "large upload completed instead of stalling");
            runner.AreEqual($"received {size}", echoed, "origin received every byte of the upload");
        });

        await proxy.StopAsync();
        origin.Dispose();
    }

    /// <summary>Deterministic fill so a truncated or interleaved body fails on content, not just length.</summary>
    private static byte[] LargePattern(int size)
    {
        var body = new byte[size];
        for (var i = 0; i < size; i++) body[i] = (byte)(i * 31);
        return body;
    }

    private static Task<HttpResponseData> EchoHandler(HttpRequestData request, CancellationToken ct)
    {
        var path = request.Url!.PathAndQuery;

        if (path.StartsWith("/large", StringComparison.Ordinal))
        {
            var size = int.Parse(path[(path.IndexOf("size=", StringComparison.Ordinal) + 5)..]);
            var large = new HttpResponseData { StatusCode = 200, ReasonPhrase = "OK", Body = LargePattern(size) };
            large.Headers.Set("Content-Type", "application/octet-stream");
            return Task.FromResult(large);
        }

        if (path.StartsWith("/upload", StringComparison.Ordinal))
            return Task.FromResult(HttpResponseData.Simple(200, "OK", $"received {request.Body.Length}"));

        var body = request.Body.Length > 0
            ? $"{request.Method} {path} body={Encoding.UTF8.GetString(request.Body)}"
            : $"{request.Method} {path}";
        return Task.FromResult(HttpResponseData.Simple(200, "OK", body));
    }

    private static HttpClient CreateClient(int proxyPort, X509Certificate2 trustedRoot)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) => TrustsRoot(trustedRoot, cert),
            },
        };
        return new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    /// <summary>The test's HttpClient only ever sees Piper's MITM leaf, minted on the fly and
    /// signed by this run's own throwaway root -- never installed into any real trust store.</summary>
    private static bool TrustsRoot(X509Certificate2 root, X509Certificate? presented)
    {
        if (presented is null) return false;
        using var leaf = new X509Certificate2(presented);
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        if (!chain.Build(leaf)) return false;
        return chain.ChainElements.Cast<X509ChainElement>().Any(e => e.Certificate.Thumbprint == root.Thumbprint);
    }

    private static async Task<Session> WaitForSessionAsync(SessionStore store, Func<Session, bool> predicate, int timeoutMs = 5000)
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

    /// <summary>A minimal self-signed-TLS, HTTP/1.1-only origin -- stands in for "a real HTTPS
    /// website" so the proxy's upstream leg has something to actually TLS-handshake with. Not h2
    /// (that origin double, needed once the upstream leg negotiates h2 too, is TestHttp2Origin).</summary>
    private sealed class TlsOriginServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _connections = [];
        private readonly Lock _gate = new();

        public TlsOriginServer(X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                var task = Task.Run(() => HandleClientAsync(client));
                lock (_gate) _connections.Add(task);
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var c = client;
            c.NoDelay = true;
            using var ssl = new SslStream(c.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, _cts.Token).ConfigureAwait(false);
            }
            catch { return; }

            using var reader = new HttpStreamReader(ssl);
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var request = await HttpParser.ReadRequestAsync(reader, _cts.Token).ConfigureAwait(false);
                    if (request is null) break;

                    var response = BuildResponse(request);
                    await ssl.WriteAsync(response.ToBytes(), _cts.Token).ConfigureAwait(false);
                    await ssl.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
            }
            catch { /* test asserts on the client side */ }
        }

        private static HttpResponseData BuildResponse(HttpRequestData request)
        {
            var path = request.Url?.PathAndQuery ?? request.RequestTarget;
            var body = request.Body.Length > 0
                ? $"{request.Method} {path} body={Encoding.UTF8.GetString(request.Body)}"
                : $"{request.Method} {path}";

            var response = HttpResponseData.Simple(200, "OK", body);
            response.Headers.Set("Connection", "keep-alive");
            return response;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch (SocketException) { }
        }
    }
}
