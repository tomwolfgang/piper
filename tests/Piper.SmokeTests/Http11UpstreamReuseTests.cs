using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// ProxyOptions.PreferHttp11UpstreamForHttp11Clients: an HTTP/1.1 client whose origin offers h2 used
// to get a fresh (one-shot) h2 upstream connection for every request. With the option on (the
// default) it asks the origin for HTTP/1.1 and keeps one upstream connection per client connection.
internal static class Http11UpstreamReuseTests
{
    private const int Requests = 5;

    public static async Task RunAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Http11UpstreamReuse-Certs"));
        var root = ca.RootCertificate;

        await runner.RunAsync("an HTTP/1.1 client reuses one HTTP/1.1 upstream connection when the origin also offers h2", async () =>
        {
            await using var origin = new DualProtocolOrigin(ca.GetCertificateFor("127.0.0.1"));
            await using var proxy = StartProxy(ca, preferHttp11: true, out var store);
            using var client = CreateClient(proxy.Endpoint!.Port, root, HttpVersion.Version11);

            await SendSequentialAsync(runner, client, origin.Port, "on");

            runner.AreEqual(1, origin.Protocols.Count, $"one upstream connection for {Requests} requests");
            runner.IsTrue(origin.Protocols.All(p => p != "h2"), "the upstream connection is not h2");
            var session = await WaitForSessionAsync(store, s => s.Path == $"/on/{Requests - 1}");
            runner.AreEqual(TransportProtocol.Http1_1, session.ResponseProtocol, "the session records an HTTP/1.1 upstream");
        });

        await runner.RunAsync("with the option off an HTTP/1.1 client gets a new h2 upstream connection per request", async () =>
        {
            await using var origin = new DualProtocolOrigin(ca.GetCertificateFor("127.0.0.1"));
            await using var proxy = StartProxy(ca, preferHttp11: false, out var store);
            using var client = CreateClient(proxy.Endpoint!.Port, root, HttpVersion.Version11);

            await SendSequentialAsync(runner, client, origin.Port, "off");

            runner.AreEqual(Requests, origin.Protocols.Count, "one upstream connection per request (the old behaviour)");
            runner.IsTrue(origin.Protocols.All(p => p == "h2"), "every upstream connection is h2");
            var session = await WaitForSessionAsync(store, s => s.Path == $"/off/{Requests - 1}");
            runner.AreEqual(TransportProtocol.Http2, session.ResponseProtocol, "the session records an h2 upstream");
        });

        await runner.RunAsync("the option leaves an HTTP/2 client on an h2 upstream", async () =>
        {
            await using var origin = new DualProtocolOrigin(ca.GetCertificateFor("127.0.0.1"));
            await using var proxy = StartProxy(ca, preferHttp11: true, out var store);
            using var client = CreateClient(proxy.Endpoint!.Port, root, HttpVersion.Version20);

            await SendSequentialAsync(runner, client, origin.Port, "h2client");

            runner.IsTrue(origin.Protocols.Count > 0 && origin.Protocols.All(p => p == "h2"), "the upstream is still h2");
            var session = await WaitForSessionAsync(store, s => s.Path == $"/h2client/{Requests - 1}");
            runner.AreEqual(TransportProtocol.Http2, session.RequestProtocol, "the client spoke h2");
            runner.AreEqual(TransportProtocol.Http2, session.ResponseProtocol, "the origin was reached over h2");
        });

        await runner.RunAsync("an h2-only origin fails an HTTP/1.1 client cleanly with the option on", async () =>
        {
            await using var origin = new TestHttp2Origin(ca.GetCertificateFor("127.0.0.1"),
                (_, _) => Task.FromResult(HttpResponseData.Simple(200, "OK", "h2-only")));
            await using var proxy = StartProxy(ca, preferHttp11: true, out var store);
            using var client = CreateClient(proxy.Endpoint!.Port, root, HttpVersion.Version11);

            var response = await client.GetAsync($"https://127.0.0.1:{origin.Port}/h2only");
            runner.AreEqual(HttpStatusCode.BadGateway, response.StatusCode, "the client gets a 502, not a hang");
            var session = await WaitForSessionAsync(store, s => s.Path == "/h2only");
            runner.AreEqual(SessionState.Failed, session.State, "the session is marked failed");
            runner.IsTrue(!string.IsNullOrEmpty(session.Error), $"the session carries the error ({session.Error})");
        });
    }

    private static async Task SendSequentialAsync(TestRunner runner, HttpClient client, int originPort, string prefix)
    {
        for (var i = 0; i < Requests; i++)
        {
            var response = await client.GetAsync($"https://127.0.0.1:{originPort}/{prefix}/{i}");
            runner.AreEqual(HttpStatusCode.OK, response.StatusCode, $"request {i} succeeds");
            runner.AreEqual($"GET /{prefix}/{i}", await response.Content.ReadAsStringAsync(), $"request {i} gets its own body");
        }
    }

    private static ProxyServer StartProxy(CertificateAuthority ca, bool preferHttp11, out SessionStore store)
    {
        store = new SessionStore();
        var proxy = new ProxyServer(new ProxyOptions
        {
            Port = 0,
            DecryptHttps = true,
            ValidateUpstreamCertificates = false, // the origin's leaf is from the test CA, trusted nowhere
            EnableHttp2Downstream = true,
            EnableHttp2Upstream = true,
            PreferHttp11UpstreamForHttp11Clients = preferHttp11,
        }, ca, store);
        proxy.Start();
        return proxy;
    }

    private static HttpClient CreateClient(int proxyPort, X509Certificate2 root, Version version) =>
        new(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
            MaxConnectionsPerServer = 1,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) => TrustsRoot(root, cert),
            },
        })
        {
            DefaultRequestVersion = version,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(20),
        };

    /// <summary>Accepts only Piper's MITM leaf signed by this run's throwaway root.</summary>
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

    /// <summary>A TLS origin that offers h2 and http/1.1 in ALPN, like most real HTTPS sites, and
    /// records the protocol each accepted connection negotiated ("h2", "http/1.1" or "" for none).
    /// h2 is served by Piper's own server-role Http2Connection, HTTP/1.1 by a keep-alive loop.</summary>
    private sealed class DualProtocolOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Task _acceptLoop;

        public DualProtocolOrigin(X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public ConcurrentQueue<string> Protocols { get; } = new();

        private static HttpResponseData Respond(HttpRequestData request)
        {
            var response = HttpResponseData.Simple(200, "OK", $"{request.Method} {request.Url?.AbsolutePath ?? request.RequestTarget}");
            response.Headers.Set("Connection", "keep-alive");
            return response;
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                _connections.Add(Task.Run(() => HandleClientAsync(client)));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using var c = client;
            c.NoDelay = true;
            await using var ssl = new SslStream(c.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                }, _cts.Token).ConfigureAwait(false);
            }
            catch { return; }

            Protocols.Enqueue(ssl.NegotiatedApplicationProtocol.ToString());
            try
            {
                if (ssl.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
                {
                    var h2 = new Http2Connection(ssl, (request, _) => Task.FromResult<Http2StreamResponse>(Respond(request)));
                    await h2.RunAsync(_cts.Token).ConfigureAwait(false);
                    return;
                }

                using var reader = new HttpStreamReader(ssl);
                while (!_cts.IsCancellationRequested)
                {
                    var request = await HttpParser.ReadRequestAsync(reader, _cts.Token).ConfigureAwait(false);
                    if (request is null) break;
                    await ssl.WriteAsync(Respond(request).ToBytes(), _cts.Token).ConfigureAwait(false);
                    await ssl.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
            }
            catch { /* the tests assert on the client side */ }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { _listener.Stop(); } catch (SocketException) { }
            try { await _acceptLoop.ConfigureAwait(false); } catch { }
            try { await Task.WhenAll(_connections).ConfigureAwait(false); } catch { }
            _cts.Dispose();
        }
    }
}
