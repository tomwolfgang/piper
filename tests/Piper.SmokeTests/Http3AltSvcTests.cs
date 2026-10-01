using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Http3;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// The Alt-Svc cache: what it takes from a header, whose header it takes it from, and how much of it
// it keeps. Assertions go through TryGetRecorded, which is everything TryGetEndpoint decides except
// "can this machine run QUIC at all", so they mean the same thing on a machine without it.
internal static class Http3AltSvcTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static bool Applies(AltSvcCache cache, string host, int originPort) =>
        cache.TryGetRecorded(host, originPort, out _);

    private static int? PortFor(AltSvcCache cache, string host, int originPort) =>
        cache.TryGetRecorded(host, originPort, out var port) ? port : null;

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("Alt-Svc: the ma lifetime is honoured, defaults to 24 hours and is clamped", () =>
        {
            var time = new ManualTime();
            var cache = new AltSvcCache(time);

            cache.RecordAltSvc("short.example", 443, "h3=\":443\"; ma=60");
            cache.RecordAltSvc("default.example", 443, "h3=\":443\"");
            cache.RecordAltSvc("quoted.example", 443, "h3=\":443\"; ma=\"120\"");
            cache.RecordAltSvc("huge.example", 443, "h3=\":443\"; ma=99999999999999999999999999");

            time.Now += TimeSpan.FromSeconds(59);
            runner.IsTrue(Applies(cache, "short.example", 443), "fresh just inside ma");
            time.Now += TimeSpan.FromSeconds(2);
            runner.IsTrue(!Applies(cache, "short.example", 443), "stale just past ma=60");
            runner.IsTrue(Applies(cache, "quoted.example", 443), "a quoted ma=\"120\" is read too: still fresh at 61 s");

            time.Now = new ManualTime().Now + TimeSpan.FromHours(23);
            runner.IsTrue(Applies(cache, "default.example", 443), "no ma means 24 hours: fresh at 23 h");
            time.Now += TimeSpan.FromHours(2);
            runner.IsTrue(!Applies(cache, "default.example", 443), "no ma means 24 hours: stale at 25 h");

            time.Now = new ManualTime().Now + TimeSpan.FromDays(29);
            runner.IsTrue(Applies(cache, "huge.example", 443), "an enormous ma is clamped, not overflowed: fresh at 29 d");
            time.Now += TimeSpan.FromDays(2);
            runner.IsTrue(!Applies(cache, "huge.example", 443), "an enormous ma is clamped to 30 days");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Alt-Svc: ma=0 withdraws only its own alternative, a malformed ma makes it unusable", () =>
        {
            var cache = new AltSvcCache(new ManualTime());

            cache.RecordAltSvc("a.example", 443, "h3=\":443\"; ma=3600");
            runner.IsTrue(Applies(cache, "a.example", 443), "advertised");
            cache.RecordAltSvc("a.example", 443, "h3=\":443\"; ma=0");
            runner.IsTrue(!Applies(cache, "a.example", 443), "ma=0 withdraws it");

            // The scan goes on past a withdrawn alternative to the next usable one.
            cache.RecordAltSvc("a.example", 443, "h3=\":443\"; ma=0, h3=\":8443\"; ma=60");
            runner.AreEqual((int?)8443, PortFor(cache, "a.example", 443), "ma=0 then a valid alternative: the valid one is used");
            cache.RecordAltSvc("a.example", 443, "h3=\":9443\"; ma=60, h3=\":443\"; ma=0");
            runner.AreEqual((int?)9443, PortFor(cache, "a.example", 443), "a valid alternative then ma=0: the valid one stays");

            foreach (var bad in new[] { "ma=-5", "ma=abc", "ma=", "ma=1e9", "ma=1 000", "ma=+60", "ma=6.5" })
            {
                var fresh = new AltSvcCache(new ManualTime());
                fresh.RecordAltSvc("b.example", 443, $"h3=\":443\"; {bad}");
                runner.IsTrue(!Applies(fresh, "b.example", 443), $"'{bad}' is not a lifetime, so the alternative is not used");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("Alt-Svc: an advertisement belongs to the origin (host and port) that made it", () =>
        {
            var cache = new AltSvcCache(new ManualTime());

            cache.RecordAltSvc("dev.local", 8443, "h3=\":9443\"");
            runner.AreEqual((int?)9443, PortFor(cache, "dev.local", 8443), "the UDP port comes from the authority");
            runner.IsTrue(!Applies(cache, "dev.local", 443), "an advertisement from :8443 never applies to :443");
            runner.IsTrue(!Applies(cache, "dev.local", 9443), "nor to the port it names");

            // The reverse, and the case where the alternative's port equals another origin's port.
            cache.RecordAltSvc("dev.local", 443, "h3=\":8443\"");
            runner.AreEqual((int?)8443, PortFor(cache, "dev.local", 443), "an advertisement from :443 is its own entry");
            runner.AreEqual((int?)9443, PortFor(cache, "dev.local", 8443), "and leaves the other untouched");

            cache.RecordAltSvc("same.example", 443, "h3=\":443\"");
            runner.IsTrue(Applies(cache, "SAME.example", 443), "the common case, host compared without case");
            runner.IsTrue(!Applies(cache, "same.example", 444), "and it does not leak to another port of the host");

            runner.IsTrue(!Applies(cache, "never.example", 443), "an origin that said nothing is never attempted");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Alt-Svc: an alternative on another host or with a bad port is ignored", () =>
        {
            var cache = new AltSvcCache(new ManualTime());

            cache.RecordAltSvc("example.com", 443, "h3=\"EXAMPLE.com:8443\"");
            runner.IsTrue(Applies(cache, "example.com", 443), "the origin's own host, any case, is accepted");

            foreach (var header in new[]
            {
                "h3=\"other.example:443\"", "h3=\"evil.example:443\"; ma=86400", "h3=\"127.0.0.1:443\"", "h3=\"[::1]:443\"",
                "h3=\":0\"", "h3=\":65536\"", "h3=\":-1\"", "h3=\":abc\"", "h3=\":\"", "h3=\"\"", "h3=", "h3=\":99999999999999999999\"",
                "h3=\":4 43\"", "h3=\"example.com\"", "h3-29=\":443\"", "h2=\":443\"", "=:443",
            })
            {
                var fresh = new AltSvcCache(new ManualTime());
                fresh.RecordAltSvc("example.com", 443, header);
                runner.IsTrue(!Applies(fresh, "example.com", 443), $"not used: {header}");
            }

            var mixed = new AltSvcCache(new ManualTime());
            mixed.RecordAltSvc("example.com", 443, "h3=\"cdn.example:443\", h3-29=\":443\", h3=\":8443\"; ma=60");
            runner.AreEqual((int?)8443, PortFor(mixed, "example.com", 443), "the off-host and draft alternatives are skipped, the first usable wins");

            var ipv6 = new AltSvcCache(new ManualTime());
            ipv6.RecordAltSvc("::1", 443, "h3=\"[::1]:8443\"");
            runner.IsTrue(Applies(ipv6, "::1", 443), "an IPv6 literal origin matches its bracketed form");

            runner.IsTrue(AltSvcCache.AdvertisesHttp3("h3=\":443\"; ma=86400"), "plain h3");
            runner.IsTrue(!AltSvcCache.AdvertisesHttp3("h3-29=\":443\"; ma=86400"), "draft-only does not qualify");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Alt-Svc: the cache is bounded in origins and in the header it will read", () =>
        {
            var time = new ManualTime();
            var cache = new AltSvcCache(time);

            for (var i = 0; i < 5_000; i++) cache.RecordAltSvc($"host{i}.example", 443, "h3=\":443\"; ma=86400");
            runner.IsTrue(cache.Count <= AltSvcCache.MaxEntries, $"advertisements are capped ({cache.Count} <= {AltSvcCache.MaxEntries})");
            runner.IsTrue(Applies(cache, "host4999.example", 443), "the newest origin is kept");
            runner.IsTrue(!Applies(cache, "host0.example", 443), "the oldest origin was dropped");

            // Ports multiply origins, so they count against the same bound.
            var ports = new AltSvcCache(time);
            for (var port = 1; port <= 5_000; port++) ports.RecordAltSvc("one.example", port, "h3=\":443\"");
            runner.IsTrue(ports.Count <= AltSvcCache.MaxEntries, $"one host on many ports is capped ({ports.Count})");

            var failures = new AltSvcCache(time);
            for (var i = 0; i < 5_000; i++) failures.RecordFailure($"down{i}.example", 443);
            runner.IsTrue(failures.Count <= AltSvcCache.MaxEntries, $"failures are capped too ({failures.Count})");

            var tooLong = new AltSvcCache(time);
            tooLong.RecordAltSvc("long.example", 443, "h3=\":443\", " + new string('x', AltSvcCache.MaxHeaderLength));
            runner.IsTrue(!Applies(tooLong, "long.example", 443), "a header past the length limit is ignored whole");
            tooLong.RecordAltSvc(new string('h', 300) + ".example", 443, "h3=\":443\"");
            runner.AreEqual(0, tooLong.Count, "a host name longer than any host name is not stored");
            tooLong.RecordAltSvc("port.example", 0, "h3=\":443\"");
            tooLong.RecordAltSvc("port.example", 70_000, "h3=\":443\"");
            runner.AreEqual(0, tooLong.Count, "an origin port outside 1-65535 is not stored");

            var many = new AltSvcCache(time);
            many.RecordAltSvc("many.example", 443, string.Join(", ", Enumerable.Repeat("h2=\":443\"", 20)) + ", h3=\":443\"");
            runner.IsTrue(!Applies(many, "many.example", 443), "the h3 entry after 16 alternatives is not looked for");
            many.RecordAltSvc("few.example", 443, "h2=\":443\", h3=\":443\"");
            runner.IsTrue(Applies(many, "few.example", 443), "within the first 16 it is found");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Alt-Svc: expired entries are dropped before live ones when the table is full", () =>
        {
            var time = new ManualTime();
            var cache = new AltSvcCache(time);

            cache.RecordAltSvc("keeper.example", 443, "h3=\":443\"; ma=86400");
            for (var i = 0; i < AltSvcCache.MaxEntries - 1; i++) cache.RecordAltSvc($"brief{i}.example", 443, "h3=\":443\"; ma=1");
            runner.AreEqual(AltSvcCache.MaxEntries, cache.Count, "the table is full");

            time.Now += TimeSpan.FromSeconds(30);
            cache.RecordAltSvc("newcomer.example", 443, "h3=\":443\"; ma=60");

            runner.IsTrue(Applies(cache, "keeper.example", 443), "the live entry survived the clean-up");
            runner.IsTrue(Applies(cache, "newcomer.example", 443), "and the new one was added");
            runner.IsTrue(cache.Count < 10, $"the expired ones went ({cache.Count} left)");
            return Task.CompletedTask;
        });

        await runner.RunAsync("Alt-Svc: cool-downs expire, a success lifts them, and withdrawing does not erase them", () =>
        {
            var time = new ManualTime();
            var cache = new AltSvcCache(time) { FailureCooldown = TimeSpan.FromMinutes(5), SoftCooldown = TimeSpan.FromMinutes(1) };

            cache.RecordAltSvc("f.example", 443, "h3=\":443\"; ma=86400");
            cache.RecordFailure("f.example", 443);
            runner.IsTrue(!Applies(cache, "f.example", 443), "barred during the cool-down");
            time.Now += TimeSpan.FromMinutes(6);
            runner.IsTrue(Applies(cache, "f.example", 443), "eligible again after it");

            cache.RecordFailure("f.example", 443);
            cache.RecordSuccess("f.example", 443);
            runner.IsTrue(Applies(cache, "f.example", 443), "a success clears the failure");

            // A soft failure (GOAWAY, too large for the buffered path) is short and does not bar for long.
            cache.RecordSoftFailure("f.example", 443);
            runner.IsTrue(!Applies(cache, "f.example", 443), "left on TCP right after a soft failure");
            time.Now += TimeSpan.FromSeconds(61);
            runner.IsTrue(Applies(cache, "f.example", 443), "but only for the soft cool-down");
            cache.RecordSoftFailure("f.example", 443);
            cache.RecordSuccess("f.example", 443);
            runner.IsTrue(Applies(cache, "f.example", 443), "and a success lifts it");

            // The cool-down belongs to the origin: another port is unaffected.
            cache.RecordAltSvc("f.example", 8443, "h3=\":8443\"");
            cache.RecordFailure("f.example", 443);
            runner.IsTrue(Applies(cache, "f.example", 8443), "a bar on :443 does not touch :8443");

            // Withdrawing the alternative (clear, or ma=0) must not end a bar the origin has earned.
            foreach (var withdrawal in new[] { "clear", "h3=\":443\"; ma=0" })
            {
                var barred = new AltSvcCache(time) { FailureCooldown = TimeSpan.FromMinutes(5) };
                barred.RecordAltSvc("g.example", 443, "h3=\":443\"");
                barred.RecordFailure("g.example", 443);
                barred.RecordAltSvc("g.example", 443, withdrawal);
                barred.RecordAltSvc("g.example", 443, "h3=\":443\"");
                runner.IsTrue(!Applies(barred, "g.example", 443), $"'{withdrawal}' then a fresh advertisement: still barred");
                time.Now += TimeSpan.FromMinutes(6);
                runner.IsTrue(Applies(barred, "g.example", 443), $"'{withdrawal}': eligible once the cool-down is over");
            }
            return Task.CompletedTask;
        });

        await runner.RunAsync("Alt-Svc: TryGetEndpoint adds only the QUIC-support check to the cache's answer", () =>
        {
            var cache = new AltSvcCache(new ManualTime());
            cache.RecordAltSvc("e.example", 443, "h3=\":8443\"");

            var ok = cache.TryGetEndpoint("e.example", 443, out var port);
            runner.AreEqual(Http3ClientConnection.IsSupported, ok, "eligible exactly when QUIC can run");
            if (ok) runner.AreEqual(8443, port, "on the advertised port");
            runner.IsTrue(!cache.TryGetEndpoint("e.example", 444, out _), "never for another origin port");
            return Task.CompletedTask;
        });

        await RunCallSiteTestsAsync(runner);
    }

    // ------------------------------------------------------------------ where the proxy records it

    private static async Task RunCallSiteTestsAsync(TestRunner runner)
    {
        using var ca = CertificateAuthority.LoadOrCreate(Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-Http3AltSvc-Certs"));

        await runner.RunAsync("Alt-Svc call sites: a plain-HTTP response advertising h3 is ignored", async () =>
        {
            await using var origin = new TestRawOrigin(async (_, stream, ct) =>
            {
                await TestRawOrigin.WriteAsync(stream,
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nAlt-Svc: h3=\":443\"; ma=86400\r\n\r\nok", ct);
                return false;
            });

            await using var proxy = new ProxyServer(new ProxyOptions { Port = 0 }, ca, new SessionStore());
            proxy.Start();
            using var client = ClientVia(proxy);

            var body = await client.GetStringAsync($"http://127.0.0.1:{origin.Port}/plain");
            runner.AreEqual("ok", body, "the response itself reaches the client");
            runner.AreEqual(0, proxy.AltSvc.Count, "and nothing about h3 was remembered: anything on the path could have written that header");
            runner.IsTrue(!Applies(proxy.AltSvc, "127.0.0.1", origin.Port), "so the origin is not eligible");
        });

        await runner.RunAsync("Alt-Svc call sites: over TLS the header is recorded for the origin port it arrived on", async () =>
        {
            using var origin = new TlsAltSvcOrigin(ca.GetCertificateFor("127.0.0.1"), "h3=\":9443\"; ma=3600");
            var options = new ProxyOptions { Port = 0, DecryptHttps = true, ValidateUpstreamCertificates = false };

            await using var proxy = new ProxyServer(options, ca, new SessionStore());
            proxy.Start();
            using var client = ClientVia(proxy);

            var body = await client.GetStringAsync($"https://127.0.0.1:{origin.Port}/tls");
            runner.AreEqual("ok", body, "the response reaches the client through the decrypting path");
            runner.AreEqual((int?)9443, PortFor(proxy.AltSvc, "127.0.0.1", origin.Port), "recorded for host:port, with the advertised UDP port");
            runner.IsTrue(!Applies(proxy.AltSvc, "127.0.0.1", 443), "and an advertisement from this port never applies to :443");
            runner.IsTrue(!Applies(proxy.AltSvc, "127.0.0.1", 9443), "nor to the port it names");
        });

        await runner.RunAsync("Alt-Svc call sites: the HTTP/2 forwarder records it the same way", async () =>
        {
            using var origin = new TlsAltSvcOrigin(ca.GetCertificateFor("127.0.0.1"), "h3=\":9443\"; ma=3600");
            var options = new ProxyOptions { Port = 0, ValidateUpstreamCertificates = false };
            var request = new HttpRequestData
            {
                Method = "GET",
                RequestTarget = "/fwd",
                HttpVersion = "HTTP/2",
                Url = new Uri($"https://127.0.0.1:{origin.Port}/fwd"),
            };
            request.Headers.Set("Host", $"127.0.0.1:{origin.Port}");

            var altSvc = new AltSvcCache();
            var response = await Http2RequestForwarder.ForwardAsync(
                request, options, new SessionStore(), altSvc, "test", "test", CancellationToken.None);

            runner.AreEqual(200, response.Head.StatusCode, "the forwarded request succeeded");
            runner.AreEqual((int?)9443, PortFor(altSvc, "127.0.0.1", origin.Port), "recorded for the origin's own port");
            runner.IsTrue(!Applies(altSvc, "127.0.0.1", 443), "and not for :443");
        });
    }

    private static HttpClient ClientVia(ProxyServer proxy) => new(new HttpClientHandler
    {
        Proxy = new WebProxy($"http://127.0.0.1:{proxy.Endpoint!.Port}", BypassOnLocal: false),
        UseProxy = true,
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // the proxy's own CA stands in for the origin's
    })
    { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>A TLS HTTP/1.1 origin whose every response carries one Alt-Svc header.</summary>
    private sealed class TlsAltSvcOrigin : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly X509Certificate2 _certificate;
        private readonly string _altSvc;
        private readonly CancellationTokenSource _cts = new();

        public TlsAltSvcOrigin(X509Certificate2 certificate, string altSvc)
        {
            _certificate = certificate;
            _altSvc = altSvc;
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var c = client;
            await using var ssl = new SslStream(c.GetStream(), leaveInnerStreamOpen: false);
            try
            {
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, _cts.Token);

                // Read to the end of the request head, then answer once and close.
                var seen = new StringBuilder();
                var buffer = new byte[2048];
                while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await ssl.ReadAsync(buffer, _cts.Token);
                    if (read <= 0) return;
                    seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                var reply = $"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nAlt-Svc: {_altSvc}\r\nConnection: close\r\n\r\nok";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(reply), _cts.Token);
                await ssl.FlushAsync(_cts.Token);
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException)
            {
                // The test asserts on the client side.
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
