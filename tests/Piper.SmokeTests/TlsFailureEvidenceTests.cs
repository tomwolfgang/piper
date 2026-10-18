using System.ComponentModel;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Failed client TLS handshakes (X2 slice 1): the classifier on fixture exceptions, then the real proxy
// against the failures .NET can produce on loopback (no node, no openssl), replaying a recorded ClientHello.
internal static class TlsFailureEvidenceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public static async Task RunTlsFailureEvidenceAsync(TestRunner runner)
    {
        await runner.RunAsync("classifier: closes, timeouts, non-TLS bytes and unknown text", () =>
        {
            var eof = new IOException("Received an unexpected EOF or 0 bytes from the transport stream.");
            Check(runner, eof, false, false, TlsFailureKind.ClosedEarly, TlsFailurePhase.BeforeHello);
            Check(runner, eof, true, false, TlsFailureKind.ClosedInHandshake, TlsFailurePhase.InHandshake);
            var cancelled = new OperationCanceledException();
            Check(runner, cancelled, false, true, TlsFailureKind.Timeout, TlsFailurePhase.BeforeHello);
            Check(runner, cancelled, false, false, TlsFailureKind.Other, TlsFailurePhase.BeforeHello);
            Check(runner, new AuthenticationException("Cannot determine the frame size or a corrupted frame was received."),
                false, false, TlsFailureKind.NotTls, TlsFailurePhase.BeforeHello);
            Check(runner, new AuthenticationException("something new in .NET 12"), true, false, TlsFailureKind.Other, TlsFailurePhase.InHandshake);

            Exception deep = new Win32Exception(unchecked((int)TlsFailureClassifier.SecUntrustedRoot));
            for (var i = 0; i < 100; i++) deep = new IOException("wrap", deep);
            runner.IsTrue(TlsFailureClassifier.Classify(deep, true, false).SecStatus is null, "a status deeper than the walk goes is not trusted");
            return Task.CompletedTask;
        });

        await runner.RunAsync("classifier: SChannel codes and TLS 1.3 alert names", () =>
        {
            foreach (var code in new[] { TlsFailureClassifier.SecUntrustedRoot, TlsFailureClassifier.SecCertUnknown })
            {
                var failure = TlsFailureClassifier.Classify(Sspi(code), true, false);
                runner.IsTrue(failure.Kind == TlsFailureKind.Rejected && failure.SecStatus == code, $"0x{code:X} is a rejection, and is recorded");
            }
            Check(runner, Sspi(TlsFailureClassifier.SecNoCommonAlgorithm), true, false, TlsFailureKind.NoCommonProtocol, TlsFailurePhase.InHandshake);
            // What a .NET client's first read throws after a handshake it did not like (probe).
            Check(runner, new IOException("The decryption operation failed.", new Win32Exception(unchecked((int)TlsFailureClassifier.SecUntrustedRoot))),
                true, false, TlsFailureKind.Rejected, TlsFailurePhase.InHandshake);
            var unknown = TlsFailureClassifier.Classify(Sspi(0x80090326), true, false);
            runner.IsTrue(unknown.Kind == TlsFailureKind.Other && unknown.SecStatus == 0x80090326, "an unknown status is kept and claims nothing");

            foreach (var alert in new[] { "UnknownCA", "BadCertificate" })
                runner.IsTrue(TlsFailureClassifier.Classify(Alert(alert), true, false) is { Kind: TlsFailureKind.Rejected } r && r.Alert == alert, $"{alert} is a rejection");
            var userCancelled = TlsFailureClassifier.Classify(Alert("UserCanceled"), true, false);
            runner.IsTrue(userCancelled.Alert == "UserCanceled" && userCancelled.Kind == TlsFailureKind.Other, "a non-rejecting alert is kept and claims nothing");

            foreach (var text in new[]
                     {
                         "a TLS alert: 'Pinned'.", "a TLS alert: 'UnknownCA", "a TLS alert: '" + new string('A', 5000) + "'.",
                         "Der Vorgang ist fehlgeschlagen: UnknownCA",
                     })
                runner.IsTrue(TlsFailureClassifier.Classify(new AuthenticationException(text), true, false) is { Alert: null, Kind: TlsFailureKind.Other },
                    $"no alert evidence from: {text[..Math.Min(text.Length, 30)]}");
            return Task.CompletedTask;
        });

        await runner.RunAsync("SNI sanitising keeps plain host names and drops everything else whole", () =>
        {
            foreach (var good in new[] { "Example.COM", "a_b-c.d.", "xn--bcher-kva.example", new string('a', 253) })
                runner.AreEqual(good, TlsFailureClassifier.SanitizeSni(good), $"kept: {good[..Math.Min(good.Length, 30)]}");
            foreach (var bad in new[] { "", "a b", "a\r\nb", "a\0b", "bücher.example", "例え.jp", "[::1]", new string('a', 254), new string('a', 100_000) })
                runner.IsTrue(TlsFailureClassifier.SanitizeSni(bad) is null, $"dropped: {bad[..Math.Min(bad.Length, 12)].Replace("\0", "NUL")}");
            runner.IsTrue(TlsFailureClassifier.SanitizeSni(null) is null, "null stays null");
            return Task.CompletedTask;
        });

        await runner.RunAsync("a refused handshake, like an update check, does not show that capture works", () =>
        {
            runner.IsTrue(new Session().ProvesCapture, "an ordinary session does");
            runner.IsTrue(!new Session { IsUpdateCheck = true }.ProvesCapture, "an update check does not");
            runner.IsTrue(!new Session { TlsFailure = new TlsFailureInfo(TlsFailureKind.Rejected, TlsFailurePhase.InHandshake) }.ProvesCapture, "a failed handshake does not");
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------ the real proxy, real loopback

        using var ca = CertificateAuthority.LoadOrCreate(Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-TlsFailure-Certs"));
        var hello = await RecordClientHelloAsync();
        var hostile = (byte[])hello.Clone();
        hostile[hello.AsSpan().IndexOf("capture.test"u8) + "capture".Length] = 0x01; // a control character where the dot was
        runner.IsTrue(hello.Length > 100, $"a ClientHello was recorded ({hello.Length} bytes)");

        async Task Fails(string name, Func<TcpClient, Task> act, TlsFailureKind kind, TlsFailurePhase phase, string? sni, TimeSpan? budget = null) =>
            await runner.RunAsync(name, () => WithProxyAsync(ca, budget, async (port, store) =>
            {
                using var client = await OpenTunnelAsync(port);
                await act(client);
                Session? row = null;
                await Poll.UntilAsync(() => (row = store.Snapshot().FirstOrDefault(s => s.TlsFailure is not null)) is not null);
                if (row?.TlsFailure is not { } failure) { runner.IsTrue(false, "a row is recorded"); return; }
                runner.IsTrue(failure.Kind == kind && failure.Phase == phase, $"{kind}/{phase} (got {failure.Kind}/{failure.Phase})");
                runner.AreEqual(sni, failure.Sni, "SNI");
                runner.IsTrue(row.State == SessionState.Failed && row.IsTunnel && failure.Attempts == 1 && failure.LastSeen == row.Completed, "a failed tunnel, one attempt");
                runner.IsTrue(row.Error?.StartsWith("TLS handshake with client failed", StringComparison.Ordinal) == true, $"Error keeps its prefix ({row.Error})");
                if (kind == TlsFailureKind.Timeout)
                    runner.IsTrue(row.Duration >= TimeSpan.FromMilliseconds(1300) && row.Duration < TimeSpan.FromSeconds(10),
                        $"Time is the wait, not the 3 ms it was ({row.Duration.TotalMilliseconds:0} ms for 1500)");
            }));

        const TlsFailureKind Early = TlsFailureKind.ClosedEarly;
        const TlsFailurePhase Before = TlsFailurePhase.BeforeHello;
        await Fails("zero bytes then FIN is closed early", c => { c.Client.Shutdown(SocketShutdown.Send); return Task.CompletedTask; }, Early, Before, null);
        await Fails("zero bytes then RST is closed early", c => { Reset(c); return Task.CompletedTask; }, Early, Before, null);
        await Fails("10 bytes of a ClientHello then FIN is closed early, with no SNI",
            async c => { await c.GetStream().WriteAsync(hello.AsMemory(0, 10)); c.Client.Shutdown(SocketShutdown.Send); }, Early, Before, null);
        await Fails("100 bytes of a ClientHello then RST is closed early, with no SNI",
            async c => { await c.GetStream().WriteAsync(hello.AsMemory(0, 100)); Reset(c); }, Early, Before, null);
        await Fails("a full ClientHello then FIN is closed in handshake, with the SNI",
            async c => { await c.GetStream().WriteAsync(hello); c.Client.Shutdown(SocketShutdown.Send); },
            TlsFailureKind.ClosedInHandshake, TlsFailurePhase.InHandshake, "capture.test");
        await Fails("a full ClientHello, the server's flight, then RST is closed in handshake",
            async c =>
            {
                await c.GetStream().WriteAsync(hello);
                // Reading the answer proves the hello was processed; an RST may discard unread bytes.
                runner.AreEqual(1, await c.GetStream().ReadAsync(new byte[1]).AsTask().WaitAsync(Patience), "the proxy answers the hello");
                Reset(c);
            }, TlsFailureKind.ClosedInHandshake, TlsFailurePhase.InHandshake, "capture.test");
        // The certificate lookup refuses the name (as before this change), so the handshake dies of that, not of a close.
        await Fails("a control character in the SNI is recorded as 'other' and costs the whole SNI",
            async c => { await c.GetStream().WriteAsync(hostile); c.Client.Shutdown(SocketShutdown.Send); },
            TlsFailureKind.Other, TlsFailurePhase.InHandshake, null);
        await Fails("non-TLS bytes after the CONNECT are not TLS",
            c => c.GetStream().WriteAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x\r\n\r\n")).AsTask(), TlsFailureKind.NotTls, Before, null);
        await Fails("a silent client is a timeout, and Time is the wait", _ => Task.CompletedTask,
            TlsFailureKind.Timeout, Before, null, TimeSpan.FromMilliseconds(1500));

        await runner.RunAsync("a .NET client that rejects the certificate (TLS 1.2 and 1.3) is never mislabelled", async () =>
        {
            foreach (var protocol in new[] { SslProtocols.Tls12, SslProtocols.Tls13 })
            {
                await WithProxyAsync(ca, null, async (port, store) =>
                {
                    using var client = await OpenTunnelAsync(port);
                    using var ssl = new SslStream(client.GetStream());
                    var refused = false;
                    try { await ssl.AuthenticateAsClientAsync(Client("reject.test", protocol, accept: false)); }
                    catch (Exception ex) when (ex is AuthenticationException or IOException) { refused = true; }
                    client.Dispose();
                    runner.IsTrue(refused, $"{protocol}: the client refused the certificate");

                    // The server may see this in the handshake or only on the first read (a later change
                    // records that); whatever is recorded now must say "rejected".
                    await Poll.UntilAsync(() => store.Snapshot().Any(s => s.TlsFailure is not null), 1500);
                    foreach (var row in store.Snapshot().Where(s => s.TlsFailure is not null))
                        runner.AreEqual(TlsFailureKind.Rejected, row.TlsFailure!.Kind, $"{protocol}: a recorded row says rejected");
                });
            }
        });

        await runner.RunAsync("a client that completes the handshake and sends a request leaves no failure evidence", () =>
            WithProxyAsync(ca, null, async (port, store) =>
            {
                using var client = await OpenTunnelAsync(port);
                using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsClientAsync(Client("127.0.0.1", SslProtocols.None, accept: true));
                await ssl.WriteAsync(Encoding.ASCII.GetBytes("GET /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\nConnection: close\r\n\r\n"));
                runner.IsTrue(await Poll.UntilAsync(() => store.Snapshot().Any(s => s.Method == "GET")), "the request is recorded");
                runner.IsTrue(store.Snapshot().All(s => s.TlsFailure is null), "and nothing carries TLS-failure evidence");
            }));

        await runner.RunAsync("stopping the proxy during in-flight handshakes records no rows", async () =>
        {
            var store = new SessionStore();
            var proxy = new ProxyServer(new ProxyOptions { Port = 0, DecryptHttps = true }, ca, store);
            proxy.Start();
            var clients = new List<TcpClient>();
            try
            {
                for (var i = 0; i < 5; i++) clients.Add(await OpenTunnelAsync(proxy.Endpoint!.Port));
                await clients[4].GetStream().WriteAsync(hello.AsMemory(0, 100)); // one has begun a hello; none finishes
                runner.IsTrue(await Poll.UntilAsync(() => proxy.ActiveConnections == 5), "five handshakes are in flight");
                await proxy.StopAsync();
                runner.IsTrue(await Poll.UntilAsync(() => proxy.ActiveConnections == 0), "they all end");
                runner.AreEqual(0, store.Snapshot().Count(s => s.TlsFailure is not null || s.IsTunnel), "and none is recorded as a failed handshake");
            }
            finally
            {
                foreach (var client in clients) client.Dispose();
                await proxy.StopAsync();
            }
        });
    }

    // -------------------------------------------------------------------------- helpers

    private static void Check(TestRunner runner, Exception ex, bool helloSeen, bool expired, TlsFailureKind kind, TlsFailurePhase phase)
    {
        var failure = TlsFailureClassifier.Classify(ex, helloSeen, expired);
        runner.IsTrue(failure.Kind == kind && failure.Phase == phase, $"{ex.GetType().Name} is {kind}/{phase} (got {failure.Kind}/{failure.Phase})");
    }

    private static AuthenticationException Sspi(uint status) =>
        new("A call to SSPI failed, see inner exception.", new Win32Exception(unchecked((int)status)));

    // The text .NET gives on the TLS 1.3 path, over the inner SChannel error seen with it (probe).
    private static AuthenticationException Alert(string name) =>
        new($"Authentication failed because the remote party sent a TLS alert: '{name}'.", new Win32Exception(unchecked((int)0x80090326)));

    private static SslClientAuthenticationOptions Client(string host, SslProtocols protocols, bool accept) => new()
    {
        TargetHost = host,
        EnabledSslProtocols = protocols,
        ApplicationProtocols = [SslApplicationProtocol.Http11],
        RemoteCertificateValidationCallback = (_, _, _, _) => accept,
    };

    private static void Reset(TcpClient client)
    {
        client.Client.LingerState = new LingerOption(true, 0);
        client.Dispose();
    }

    private static async Task WithProxyAsync(CertificateAuthority ca, TimeSpan? handshakeBudget, Func<int, SessionStore, Task> body)
    {
        var store = new SessionStore();
        var options = new ProxyOptions { Port = 0, DecryptHttps = true };
        if (handshakeBudget is { } budget) options.TlsHandshakeTimeout = budget;
        var proxy = new ProxyServer(options, ca, store);
        proxy.Start();
        try
        {
            await body(proxy.Endpoint!.Port, store);
            await Poll.UntilAsync(() => proxy.ActiveConnections == 0);
        }
        finally { await proxy.StopAsync(); }
    }

    /// <summary>Opens a tunnel through the proxy and returns the connection once the 200 has arrived.</summary>
    private static async Task<TcpClient> OpenTunnelAsync(int port)
    {
        var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.Latin1.GetBytes("CONNECT 127.0.0.1:9 HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n"));
        var text = new StringBuilder();
        var buffer = new byte[256];
        using var limit = new CancellationTokenSource(Patience);
        while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await stream.ReadAsync(buffer, limit.Token);
            if (n == 0) throw new IOException("the proxy closed before answering the CONNECT");
            text.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }
        return client;
    }

    /// <summary>The bytes a real <see cref="SslStream"/> client opens with, taken by running it over a
    /// stream that keeps what is written and fails the first read.</summary>
    private static async Task<byte[]> RecordClientHelloAsync()
    {
        var recorder = new HelloRecorder();
        using var ssl = new SslStream(recorder);
        try { await ssl.AuthenticateAsClientAsync(Client("capture.test", SslProtocols.None, accept: true)); }
        catch (IOException) { /* the recorder ends the handshake on the first read, as intended */ }
        return recorder.Written.ToArray();
    }

    private sealed class HelloRecorder : Stream
    {
        public MemoryStream Written { get; } = new();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Written.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new IOException("recorded");
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("recorded");
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
