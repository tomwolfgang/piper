using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// A WebSocket upgrade through a decrypted tunnel to an origin that offers h2 in ALPN: the upstream
// leg must stay on HTTP/1.1, because an h2 request carries no Connection/Upgrade.
internal static class UpgradeOverH2Tests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    private const string UpgradeRequest =
        "GET /chat HTTP/1.1\r\nHost: {0}\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n"
        + "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";

    public static async Task RunAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-UpgradeOverH2-Certs"));

        await runner.RunAsync("a WebSocket upgrade reaches an origin that offers h2 in ALPN and gets its 101", async () =>
        {
            // The origin prefers h2 whenever it is offered. Piper used to offer it on an upgrade too,
            // and an h2 request has no Connection/Upgrade, so the origin answered a plain 200 and
            // the handshake could not complete.
            await using var origin = new AlpnUpgradeOrigin(ca.GetCertificateFor("127.0.0.1"));
            using var harness = new Harness(ca);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, harness.Port);
            var authority = $"127.0.0.1:{origin.Port}";
            await using var ssl = await OpenTunnelAsync(runner, tcp, authority, ca.RootCertificate);

            await WriteAsync(ssl, string.Format(UpgradeRequest, authority));
            var head = await ReadAsync(ssl, "\r\n\r\n", Patience);
            runner.IsTrue(head.StartsWith("HTTP/1.1 101", StringComparison.Ordinal),
                $"the client gets the 101, not an h2 origin's plain answer (got: {FirstLine(head)})");

            await WriteAsync(ssl, "ping");
            var echoed = await ReadAsync(ssl, "echo:ping", Patience);
            runner.IsTrue(echoed.Contains("echo:ping", StringComparison.Ordinal), "and bytes flow both ways afterwards");

            runner.AreEqual(0, origin.H2Connections, "the origin was never offered h2 for the upgrade");
        });

        await runner.RunAsync("an upgrade after an ordinary request on the same client connection still gets its 101", async () =>
        {
            // The reuse path: the per-connection upstream slot is consulted before a new connection is
            // made, and UpstreamConnection.Matches does not look at IsHttp2. The ordinary request is
            // served over h2 (so it does reach the h2 branch), and the upgrade behind it must not be
            // handed that upstream.
            await using var origin = new AlpnUpgradeOrigin(ca.GetCertificateFor("127.0.0.1"));
            using var harness = new Harness(ca);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, harness.Port);
            var authority = $"127.0.0.1:{origin.Port}";
            await using var ssl = await OpenTunnelAsync(runner, tcp, authority, ca.RootCertificate);

            await WriteAsync(ssl, $"GET /plain HTTP/1.1\r\nHost: {authority}\r\n\r\n");
            var plain = await ReadAsync(ssl, "no upgrade was possible", Patience);
            runner.IsTrue(plain.Contains("h2 origin answered", StringComparison.Ordinal),
                $"the ordinary request is served over h2 (got: {FirstLine(plain)})");
            runner.AreEqual(1, origin.H2Connections, "one h2 connection upstream so far");

            await WriteAsync(ssl, string.Format(UpgradeRequest, authority));
            var head = await ReadAsync(ssl, "\r\n\r\n", Patience);
            runner.IsTrue(head.StartsWith("HTTP/1.1 101", StringComparison.Ordinal),
                $"the upgrade on the same client connection gets the 101 (got: {FirstLine(head)})");

            await WriteAsync(ssl, "ping");
            var echoed = await ReadAsync(ssl, "echo:ping", Patience);
            runner.IsTrue(echoed.Contains("echo:ping", StringComparison.Ordinal), "and bytes flow both ways afterwards");
            runner.AreEqual(1, origin.H2Connections, "and the upgrade was not given a second h2 connection");
        });

        await runner.RunAsync("an ordinary request to the same origin still negotiates h2 upstream", async () =>
        {
            await using var origin = new AlpnUpgradeOrigin(ca.GetCertificateFor("127.0.0.1"));
            using var harness = new Harness(ca);
            using var client = harness.CreateTlsClient(ca.RootCertificate);

            var response = await client.GetAsync($"https://127.0.0.1:{origin.Port}/plain");
            var body = await response.Content.ReadAsStringAsync();
            runner.IsTrue(body.Contains("h2 origin answered", StringComparison.Ordinal), $"served over h2 (got: {body})");
            runner.AreEqual(1, origin.H2Connections, "one h2 connection upstream");
        });
    }

    private sealed class Harness : IDisposable
    {
        public Harness(CertificateAuthority ca)
        {
            Store = new SessionStore();
            Proxy = new ProxyServer(
                new ProxyOptions { Port = 0, DecryptHttps = true, ValidateUpstreamCertificates = false }, ca, Store);
            Proxy.Start();
            Port = Proxy.Endpoint!.Port;
        }

        public ProxyServer Proxy { get; }
        public int Port { get; }
        public SessionStore Store { get; }

        public HttpClient CreateTlsClient(X509Certificate2 trustedRoot) => new(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{Port}", BypassOnLocal: false),
            UseProxy = true,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) => TrustsRoot(trustedRoot, cert),
            },
        })
        { Timeout = TimeSpan.FromSeconds(20) };

        public void Dispose() => Proxy.StopAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// An HTTPS origin that picks h2 whenever the client offers it and speaks HTTP/1.1 otherwise,
    /// as a real server behind a CDN does. Over h2 it answers every request with a plain 200; over
    /// HTTP/1.1 it completes a WebSocket-style upgrade and then echoes what it is sent.
    /// </summary>
    private sealed class AlpnUpgradeOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _cts = new();
        private readonly List<Task> _connections = [];
        private readonly Lock _gate = new();
        private readonly Task _acceptLoop;
        private int _h2Connections;

        public AlpnUpgradeOrigin(X509Certificate2 certificate)
        {
            _certificate = certificate;
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int H2Connections => Volatile.Read(ref _h2Connections);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }

                var task = Task.Run(() => ServeAsync(client));
                lock (_gate) _connections.Add(task);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var c = client;
            c.NoDelay = true;
            var ssl = new SslStream(c.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                    ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
                }, _cts.Token).ConfigureAwait(false);

                if (ssl.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
                {
                    Interlocked.Increment(ref _h2Connections);
                    var connection = new Http2Connection(ssl, (_, _) => Task.FromResult(
                        (Http2StreamResponse)HttpResponseData.Simple(200, "OK", "h2 origin answered; no upgrade was possible")));
                    await connection.RunAsync(_cts.Token).ConfigureAwait(false);
                    return;
                }

                using var reader = new HttpStreamReader(ssl);
                var request = await HttpParser.ReadRequestAsync(reader, _cts.Token).ConfigureAwait(false);
                if (request is null) return;

                if (request.Headers.HasToken("Connection", "Upgrade") && request.Headers.Contains("Upgrade"))
                {
                    await WriteAsync(ssl, "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n\r\n");
                    var buffer = new byte[256];
                    var n = await reader.ReadAsync(buffer, _cts.Token).ConfigureAwait(false);
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes("echo:" + Encoding.ASCII.GetString(buffer, 0, n)), _cts.Token)
                        .ConfigureAwait(false);
                    await ssl.FlushAsync(_cts.Token).ConfigureAwait(false);
                    await Task.Delay(200, _cts.Token).ConfigureAwait(false);
                    return;
                }

                await ssl.WriteAsync(HttpResponseData.Simple(200, "OK", "h1 origin answered").ToBytes(), _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or AuthenticationException
                                           or ObjectDisposedException or Http2ProtocolException or HttpParseException)
            {
                // The test asserts on the client side; a connection that ended early is not news.
            }
            finally
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync().ConfigureAwait(false);
            try { _listener.Stop(); } catch (SocketException) { }
            try { await _acceptLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }

            Task[] pending;
            lock (_gate) pending = [.. _connections];
            await Task.WhenAll(pending).ConfigureAwait(false);
            _cts.Dispose();
        }
    }

    /// <summary>CONNECTs through the proxy and completes the TLS handshake with its minted leaf.</summary>
    private static async Task<SslStream> OpenTunnelAsync(TestRunner runner, TcpClient client, string authority, X509Certificate2 root)
    {
        var raw = client.GetStream();
        await WriteAsync(raw, $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");
        var connected = await ReadAsync(raw, "\r\n\r\n", Patience);
        runner.IsTrue(connected.Contains(" 200 ", StringComparison.Ordinal), "CONNECT is accepted");

        var ssl = new SslStream(raw, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "127.0.0.1",
            ApplicationProtocols = [SslApplicationProtocol.Http11],
            RemoteCertificateValidationCallback = (_, cert, _, _) => TrustsRoot(root, cert),
        });
        return ssl;
    }

    private static Task WriteAsync(Stream stream, string text) =>
        stream.WriteAsync(Encoding.Latin1.GetBytes(text)).AsTask();

    /// <summary>Reads until <paramref name="until"/> has arrived, the limit passes, or the connection
    /// ends. Never throws on a closed peer.</summary>
    private static async Task<string> ReadAsync(Stream stream, string until, TimeSpan limit)
    {
        using var cts = new CancellationTokenSource(limit);
        var text = new StringBuilder();
        var buffer = new byte[4096];
        while (!text.ToString().Contains(until, StringComparison.Ordinal))
        {
            int n;
            try { n = await stream.ReadAsync(buffer, cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (IOException) { break; } // a reset is as closed as a FIN
            if (n == 0) break;
            text.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }
        return text.ToString();
    }

    private static string FirstLine(string text)
    {
        var end = text.IndexOf('\r');
        return end < 0 ? text : text[..end];
    }

    private static bool TrustsRoot(X509Certificate2 root, X509Certificate? presented)
    {
        if (presented is null) return false;
        using var leaf = new X509Certificate2(presented);
        using var chain = new X509Chain();
        chain.ChainPolicy.ExtraStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        return chain.Build(leaf)
               && chain.ChainElements.Cast<X509ChainElement>().Any(e => e.Certificate.Thumbprint == root.Thumbprint);
    }
}
