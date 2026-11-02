using System.Collections.Concurrent;
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

// P6c: the upstream connection a client connection keeps between requests (on by default since
// P6a) has to cope with an origin that closes it while it sits idle. A connection the origin has
// already closed must not be reused, a reused one that dies before answering is retried once on
// a fresh connection when that cannot repeat a delivered request, and any other failure is a 502
// and a Failed row rather than a bare close.
internal static class UpstreamReuseSafetyTests
{
    public static async Task RunAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(
            Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-UpstreamReuseSafety-Certs"));
        var root = ca.RootCertificate;

        await runner.RunAsync("a kept upstream connection the origin closed with close_notify is not reused", async () =>
        {
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var requestsOnClosed = 0;
            await using var origin = new ScriptedOrigin(ca.GetCertificateFor("127.0.0.1"), async (index, stream, reader, ct) =>
            {
                if (index > 0)
                {
                    await ServeKeepAliveAsync(stream, reader, ct);
                    return;
                }

                await AnswerOneAsync(stream, reader, ct);

                // The idle timeout: TLS close_notify, with the TCP connection left open so the
                // record is all the proxy can see. Anything that arrives afterwards was sent into
                // a connection the origin had already closed.
                await ((SslStream)stream).ShutdownAsync();
                closed.TrySetResult();
                if (await HttpParser.ReadRequestAsync(reader, ct) is not null) Interlocked.Increment(ref requestsOnClosed);
            });
            await using var proxy = StartProxy(ca, out _);
            using var client = CreateClient(proxy.Endpoint!.Port, root);

            await ExpectOkAsync(runner, client, $"https://127.0.0.1:{origin.Port}/notify/0", "the first request");
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(200); // let the close_notify record reach the proxy's socket
            await ExpectOkAsync(runner, client, $"https://127.0.0.1:{origin.Port}/notify/1", "the request after close_notify");

            runner.AreEqual(0, Volatile.Read(ref requestsOnClosed), "nothing was sent into the closed connection");
            runner.AreEqual(2, origin.Connections, "the second request went on a fresh connection");
        });

        await runner.RunAsync("a kept upstream connection closed with FIN as a GET arrives is retried once on a fresh one", async () =>
        {
            await using var origin = new ScriptedOrigin(null, async (index, stream, reader, ct) =>
            {
                if (index > 0)
                {
                    await ServeKeepAliveAsync(stream, reader, ct);
                    return;
                }

                // The race no idle check can see: the origin's keep-alive timer fires just as the
                // next request reaches it, so it closes (FIN) without answering.
                await AnswerOneAsync(stream, reader, ct);
                _ = await HttpParser.ReadRequestAsync(reader, ct);
            });
            await using var proxy = StartProxy(ca, out var store);
            using var client = CreateClient(proxy.Endpoint!.Port, root);

            await ExpectOkAsync(runner, client, $"http://127.0.0.1:{origin.Port}/fin/0", "the first request");
            await ExpectOkAsync(runner, client, $"http://127.0.0.1:{origin.Port}/fin/1", "the request the origin dropped");

            runner.AreEqual(2, origin.Connections, "exactly one retry connection");
            var session = await WaitForSessionAsync(store, s => s.Path == "/fin/1");
            runner.AreEqual(SessionState.Complete, session.State, "the retried request is recorded complete");
        });

        await runner.RunAsync("a POST written to a kept upstream connection that then dies is not retried", async () =>
        {
            // Two ways to die once the POST is on the wire: before any byte of the answer, and in
            // the middle of it. Either way the origin may have acted on the request.
            foreach (var (name, answer, expected) in new (string, string, HttpStatusCode?)[]
                     {
                         ("silent", "", HttpStatusCode.BadGateway),
                         ("midresponse", "HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\npartial", null),
                     })
            {
                var posts = 0;
                await using var origin = new ScriptedOrigin(null, async (index, stream, reader, ct) =>
                {
                    if (index == 0) await AnswerOneAsync(stream, reader, ct);
                    while (await HttpParser.ReadRequestAsync(reader, ct) is { } request)
                    {
                        if (request.Method == "POST")
                        {
                            Interlocked.Increment(ref posts);
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), ct);
                            await stream.FlushAsync(ct);
                            return; // and close
                        }
                        await stream.WriteAsync(KeepAliveResponse(request).ToBytes(), ct);
                    }
                });
                await using var proxy = StartProxy(ca, out var store);
                using var client = CreateClient(proxy.Endpoint!.Port, root);

                await ExpectOkAsync(runner, client, $"http://127.0.0.1:{origin.Port}/{name}/0", $"{name}: the first request");

                HttpStatusCode? status = null;
                try
                {
                    using var response = await client.PostAsync($"http://127.0.0.1:{origin.Port}/{name}/1",
                        new StringContent("charge the card once"));
                    _ = await response.Content.ReadAsByteArrayAsync();
                    status = response.StatusCode;
                }
                catch (HttpRequestException) when (expected is null) { /* the truncated body is reset */ }
                catch (IOException) when (expected is null) { }

                if (expected is not null) runner.AreEqual(expected, status, $"{name}: the client is told the request failed");
                var session = await WaitForSessionAsync(store, s => s.Path == $"/{name}/1");
                runner.AreEqual(SessionState.Failed, session.State, $"{name}: the session is Failed");
                runner.AreEqual(1, Volatile.Read(ref posts), $"{name}: the origin received the POST exactly once");
                runner.AreEqual(1, origin.Connections, $"{name}: no retry connection was opened");
            }
        });

        await runner.RunAsync("a GET whose answer had started on a kept upstream connection is not retried", async () =>
        {
            await using var origin = new ScriptedOrigin(null, async (index, stream, reader, ct) =>
            {
                if (index > 0)
                {
                    await ServeKeepAliveAsync(stream, reader, ct);
                    return;
                }

                await AnswerOneAsync(stream, reader, ct);
                _ = await HttpParser.ReadRequestAsync(reader, ct);
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-"u8.ToArray(), ct); // and close
            });
            await using var proxy = StartProxy(ca, out _);
            using var client = CreateClient(proxy.Endpoint!.Port, root);

            await ExpectOkAsync(runner, client, $"http://127.0.0.1:{origin.Port}/partial/0", "the first request");
            using var response = await client.GetAsync($"http://127.0.0.1:{origin.Port}/partial/1");

            runner.AreEqual(HttpStatusCode.BadGateway, response.StatusCode, "a half-sent head is a 502");
            runner.AreEqual(1, origin.Connections, "no retry once a byte of the answer arrived");
        });

        await runner.RunAsync("a host name over 255 characters gets a 502 and a Failed row, not a bare close", async () =>
        {
            await using var proxy = StartProxy(ca, out var store);
            var label = new string('a', 63);
            var host = $"{label}.{label}.{label}.{label}.test"; // 260 characters, every label legal

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, proxy.Endpoint!.Port);
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"GET http://{host}/long HTTP/1.1\r\nHost: {host}\r\n\r\n"));

            var answer = await ReadUntilCloseAsync(stream, TimeSpan.FromSeconds(15));
            runner.IsTrue(answer.StartsWith("HTTP/1.1 502", StringComparison.Ordinal),
                $"the client is answered 502 (got '{answer.Split('\r')[0]}')");
            var session = await WaitForSessionAsync(store, s => s.Path == "/long");
            runner.AreEqual(SessionState.Failed, session.State, "the session is Failed");
            runner.IsTrue(!string.IsNullOrEmpty(session.Error), $"the session carries the error ({session.Error})");
        });
    }

    private static HttpResponseData KeepAliveResponse(HttpRequestData request)
    {
        var response = HttpResponseData.Simple(200, "OK", $"{request.Method} {request.Url?.AbsolutePath ?? request.RequestTarget}");
        response.Headers.Set("Connection", "keep-alive");
        return response;
    }

    private static async Task AnswerOneAsync(Stream stream, HttpStreamReader reader, CancellationToken ct)
    {
        var request = await HttpParser.ReadRequestAsync(reader, ct) ?? throw new IOException("no first request");
        await stream.WriteAsync(KeepAliveResponse(request).ToBytes(), ct);
        await stream.FlushAsync(ct);
    }

    private static async Task ServeKeepAliveAsync(Stream stream, HttpStreamReader reader, CancellationToken ct)
    {
        while (await HttpParser.ReadRequestAsync(reader, ct) is { } request)
        {
            await stream.WriteAsync(KeepAliveResponse(request).ToBytes(), ct);
            await stream.FlushAsync(ct);
        }
    }

    private static async Task ExpectOkAsync(TestRunner runner, HttpClient client, string url, string what)
    {
        using var response = await client.GetAsync(url);
        runner.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{what} succeeds");
        runner.AreEqual($"GET {new Uri(url).AbsolutePath}", await response.Content.ReadAsStringAsync(), $"{what} gets its own body");
    }

    private static async Task<string> ReadUntilCloseAsync(Stream stream, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var received = new MemoryStream();
        var buffer = new byte[4096];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer, cts.Token)) > 0) received.Write(buffer, 0, n);
        }
        catch (IOException) { /* reset: whatever arrived is the answer */ }
        catch (OperationCanceledException) { }
        return Encoding.Latin1.GetString(received.ToArray());
    }

    private static ProxyServer StartProxy(CertificateAuthority ca, out SessionStore store)
    {
        store = new SessionStore();
        var proxy = new ProxyServer(new ProxyOptions
        {
            Port = 0,
            DecryptHttps = true,
            ValidateUpstreamCertificates = false, // the origin's leaf is from the test CA, trusted nowhere
            EnableHttp2Upstream = true,
        }, ca, store);
        proxy.Start();
        return proxy;
    }

    private static HttpClient CreateClient(int proxyPort, X509Certificate2 root) =>
        new(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{proxyPort}"),
            UseProxy = true,
            MaxConnectionsPerServer = 1, // both requests share one client connection, so one upstream slot
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, cert, _, _) => TrustsRoot(root, cert),
            },
        })
        {
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Timeout = TimeSpan.FromSeconds(20),
        };

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

    private static async Task<Session> WaitForSessionAsync(SessionStore store, Func<Session, bool> predicate, int timeoutMs = 10000)
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

    /// <summary>An HTTP/1.1 origin (TLS when given a certificate) that runs a script per accepted
    /// connection, numbered from 0 in accept order. The connection closes when the script returns.</summary>
    private sealed class ScriptedOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2? _certificate;
        private readonly Func<int, Stream, HttpStreamReader, CancellationToken, Task> _script;
        private readonly CancellationTokenSource _cts = new();
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Task _acceptLoop;
        private int _accepted;

        public ScriptedOrigin(X509Certificate2? certificate, Func<int, Stream, HttpStreamReader, CancellationToken, Task> script)
        {
            _certificate = certificate;
            _script = script;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Connections => Volatile.Read(ref _accepted);

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                var index = Interlocked.Increment(ref _accepted) - 1;
                _connections.Add(Task.Run(() => HandleClientAsync(client, index)));
            }
        }

        private async Task HandleClientAsync(TcpClient client, int index)
        {
            using var c = client;
            c.NoDelay = true;
            Stream stream = c.GetStream();
            try
            {
                if (_certificate is not null)
                {
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                        ApplicationProtocols = [SslApplicationProtocol.Http11],
                    }, _cts.Token).ConfigureAwait(false);
                    stream = ssl;
                }

                using var reader = new HttpStreamReader(stream);
                await _script(index, stream, reader, _cts.Token).ConfigureAwait(false);
            }
            catch { /* the tests assert on the client side */ }
            finally
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }
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
