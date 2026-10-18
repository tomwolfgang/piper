using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// Failed client TLS handshakes (X2 slice 2): a client that finishes the handshake and then leaves is recorded,
// for HTTP/1.1 and HTTP/2, only when it made no request, and a refusal is one row however often it repeats.
// Real loopback, no node or openssl.
internal static class TlsAfterHandshakeTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public static async Task RunTlsAfterHandshakeAsync(TestRunner runner)
    {
        await runner.RunAsync("classifier: after the handshake, a refusal is rejected and anything else is a plain close", () =>
        {
            var closed = TlsFailureClassifier.ClassifyAfterHandshake(null);
            runner.IsTrue(closed is { Kind: TlsFailureKind.ClosedAfterHandshake, Phase: TlsFailurePhase.AfterHandshake, SecStatus: null }, "a clean close");
            var reset = TlsFailureClassifier.ClassifyAfterHandshake(new IOException("reset", new SocketException((int)SocketError.ConnectionReset)));
            runner.AreEqual(TlsFailureKind.ClosedAfterHandshake, reset.Kind, "a reset claims no more");
            var refused = TlsFailureClassifier.ClassifyAfterHandshake(
                new IOException("The decryption operation failed.", new System.ComponentModel.Win32Exception(unchecked((int)TlsFailureClassifier.SecUntrustedRoot))));
            runner.IsTrue(refused is { Kind: TlsFailureKind.Rejected, SecStatus: TlsFailureClassifier.SecUntrustedRoot }, "the first read's SChannel status is a refusal");
            var other = TlsFailureClassifier.ClassifyAfterHandshake(new IOException("x", new System.ComponentModel.Win32Exception(unchecked((int)0x80090326))));
            runner.AreEqual(TlsFailureKind.ClosedAfterHandshake, other.Kind, "an unknown status is not");
            return Task.CompletedTask;
        });

        // ------------------------------------------------------------ the real proxy, real loopback

        using var ca = CertificateAuthority.LoadOrCreate(Path.Combine(Path.GetTempPath(), "Piper-SmokeTest-TlsCollapse-Certs"));
        var protocols = new[] { SslProtocols.Tls12, SslProtocols.Tls13 };

        await runner.RunAsync("a client that finishes the handshake and closes with no request is recorded once, after the handshake (TLS 1.2, 1.3, RST)", async () =>
        {
            foreach (var how in new[] { "FIN 1.2", "FIN 1.3", "RST 1.2", "RST 1.3" })
            {
                await TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
                {
                    using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                    using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsClientAsync(TlsFailureEvidenceTests.Client("close.test", how.EndsWith("1.2") ? SslProtocols.Tls12 : SslProtocols.Tls13, accept: true));
                    if (how.StartsWith("RST")) TlsFailureEvidenceTests.Reset(client); else client.Dispose();

                    runner.IsTrue(await Poll.UntilAsync(() => store.Snapshot().Any(s => s.TlsFailure is not null)), $"{how}: a row is recorded");
                    await Task.Delay(300);
                    var rows = Rows(store);
                    runner.AreEqual(1, rows.Length, $"{how}: once");
                    var failure = rows[0].TlsFailure!;
                    runner.IsTrue(failure is { Kind: TlsFailureKind.ClosedAfterHandshake, Phase: TlsFailurePhase.AfterHandshake, Attempts: 1, IsOverflow: false },
                        $"{how}: closed after the handshake ({failure.Kind}/{failure.Phase})");
                    runner.AreEqual("close.test", failure.Sni, $"{how}: with the SNI");
                    runner.IsTrue(rows[0] is { State: SessionState.Failed, IsTunnel: true, IsHttps: true, ServerEndpoint: "127.0.0.1:9" }
                                  && rows[0].Completed is not null && failure.LastSeen == rows[0].Completed, $"{how}: a failed tunnel row");
                });
            }
        });

        await runner.RunAsync("a client that finishes the handshake, sends a request and then closes leaves no TLS failure row", async () =>
        {
            foreach (var protocol in protocols)
            {
                SessionStore? seen = null;
                // Asserted once the proxy has finished with the connection, which is after it has read the close.
                await TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
                {
                    seen = store;
                    using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                    using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsClientAsync(TlsFailureEvidenceTests.Client("served.test", protocol, accept: true));
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes("GET /x HTTP/1.1\r\nHost: 127.0.0.1:9\r\n\r\n"));
                    runner.IsTrue(await Poll.UntilAsync(() => store.Snapshot().Any(Answered)), $"{protocol}: the request is answered and the connection is kept");
                    await ssl.ShutdownAsync(); // a TLS close_notify, so 1.2 ends in a clean EOF; 1.3 may still end in a reset
                    client.Dispose();
                }, WindowOpenWide);
                runner.AreEqual(0, Rows(seen!).Length, $"{protocol}: the close after the request is not a failure");
            }
        });

        await runner.RunAsync("a .NET client that rejects the certificate over HTTP/2 (TLS 1.2 and 1.3) is recorded once, as rejected (1.3: or a bare close)", async () =>
        {
            foreach (var protocol in protocols)
            {
                await TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
                {
                    using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                    using var ssl = new SslStream(client.GetStream());
                    try { await ssl.AuthenticateAsClientAsync(TlsFailureEvidenceTests.Client("h2reject.test", protocol, accept: false, SslApplicationProtocol.Http2)); }
                    catch (Exception ex) when (ex is AuthenticationException or IOException) { }
                    await TlsFailureEvidenceTests.CloseGracefullyAsync(client);
                    runner.IsTrue(await Poll.UntilAsync(() => store.Snapshot().Any(s => s.TlsFailure is not null)), $"{protocol}: a row is recorded");
                    await Task.Delay(300);
                    var rows = Rows(store);
                    runner.AreEqual(1, rows.Length, $"{protocol}: once");
                    var kind = rows[0].TlsFailure!.Kind; // TLS 1.3: the alert races the client's reset, see TlsFailureEvidenceTests
                    runner.IsTrue(kind == TlsFailureKind.Rejected || (protocol == SslProtocols.Tls13 && kind == TlsFailureKind.ClosedAfterHandshake),
                        $"{protocol}: rejected, or on 1.3 a reset with no evidence (got {kind})");
                });
            }
        });

        await runner.RunAsync("an HTTP/2 client that completes the handshake and closes: before the preface, or after SETTINGS with no stream", async () =>
        {
            foreach (var preface in new[] { false, true })
            {
                await TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
                {
                    using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                    using var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsClientAsync(TlsFailureEvidenceTests.Client("h2close.test", SslProtocols.None, accept: true, SslApplicationProtocol.Http2));
                    runner.AreEqual("h2", ssl.NegotiatedApplicationProtocol.ToString(), "h2 was negotiated");
                    if (preface) await ssl.WriteAsync(H2Preface());
                    client.Dispose();
                    runner.IsTrue(await Poll.UntilAsync(() => store.Snapshot().Any(s => s.TlsFailure is not null)), $"preface={preface}: a row is recorded");
                    await Task.Delay(300);
                    var rows = Rows(store);
                    runner.AreEqual(1, rows.Length, $"preface={preface}: once");
                    runner.IsTrue(rows[0].TlsFailure is { Kind: TlsFailureKind.ClosedAfterHandshake, Phase: TlsFailurePhase.AfterHandshake },
                        $"preface={preface}: closed after the handshake ({rows[0].TlsFailure!.Kind})");
                    runner.AreEqual("h2close.test", rows[0].TlsFailure!.Sni, $"preface={preface}: with the SNI");
                });
            }
        });

        await runner.RunAsync("an HTTP/2 client that sends a request and then closes leaves no TLS failure row", async () =>
        {
            SessionStore? seen = null;
            await TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
            {
                seen = store;
                using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsClientAsync(TlsFailureEvidenceTests.Client("h2served.test", SslProtocols.None, accept: true, SslApplicationProtocol.Http2));
                // GET https://127.0.0.1:9/x on stream 1: indexed :method GET and :scheme https, :path and :authority as literals.
                byte[] headers = [0x82, 0x87, 0x44, 0x02, .. "/x"u8, 0x41, 0x0b, .. "127.0.0.1:9"u8];
                byte[] frame = [0, 0, (byte)headers.Length, 0x01, 0x05, 0, 0, 0, 1, .. headers];
                await ssl.WriteAsync(H2Preface().Concat(frame).ToArray());
                runner.IsTrue(await Poll.UntilAsync(() => store.Snapshot().Any(Answered)), "the request is answered");
                client.Dispose();
            }, WindowOpenWide);
            runner.AreEqual(0, Rows(seen!).Length, "the close after the request is not a failure");
        });

        await runner.RunAsync("a close within the window is switchable off, and one after it is not recorded; a refusal is recorded either way", async () =>
        {
            await TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
            {
                using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsClientAsync(TlsFailureEvidenceTests.Client("off.test", SslProtocols.None, accept: true));
                client.Dispose();
                await Task.Delay(500);
                runner.AreEqual(0, Rows(store).Length, "switched off: a plain close is not recorded");
            }, o => o.RecordClosedAfterHandshake = false);

            await TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
            {
                using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                using var ssl = new SslStream(client.GetStream());
                try { await ssl.AuthenticateAsClientAsync(TlsFailureEvidenceTests.Client("off.test", SslProtocols.Tls12, accept: false)); }
                catch (Exception ex) when (ex is AuthenticationException or IOException) { }
                await TlsFailureEvidenceTests.CloseGracefullyAsync(client);
                runner.IsTrue(await Poll.UntilAsync(() => store.Snapshot().Any(s => s.TlsFailure is not null)), "switched off: a refusal is still recorded");
            }, o => o.RecordClosedAfterHandshake = false);

            await TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
            {
                using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsClientAsync(TlsFailureEvidenceTests.Client("slow.test", SslProtocols.None, accept: true));
                await Task.Delay(700);
                client.Dispose();
                await Task.Delay(300);
                runner.AreEqual(0, Rows(store).Length, "a client that held the connection past the window is not recorded");
            }, o => o.ClosedAfterHandshakeWindow = TimeSpan.FromMilliseconds(300));
        });

        await runner.RunAsync("a failure before the handshake finishes is recorded once, not again by the after-handshake check", () =>
            TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
            {
                using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                client.Client.Shutdown(SocketShutdown.Send);
                runner.IsTrue(await Poll.UntilAsync(() => store.Snapshot().Any(s => s.TlsFailure is not null)), "a row");
                await Task.Delay(300);
                runner.AreEqual(1, Rows(store).Length, "one");
                runner.AreEqual(TlsFailureKind.ClosedEarly, Rows(store)[0].TlsFailure!.Kind, "closed early");
            }));

        await runner.RunAsync("twelve rejections of one host by one process are one row with twelve attempts", () =>
            TlsFailureEvidenceTests.WithProxyAsync(ca, null, async (port, store) =>
            {
                for (var i = 0; i < 12; i++)
                {
                    using var client = await TlsFailureEvidenceTests.OpenTunnelAsync(port);
                    using var ssl = new SslStream(client.GetStream());
                    try { await ssl.AuthenticateAsClientAsync(TlsFailureEvidenceTests.Client("retry.test", SslProtocols.Tls12, accept: false)); }
                    catch (Exception ex) when (ex is AuthenticationException or IOException) { }
                    await TlsFailureEvidenceTests.CloseGracefullyAsync(client);
                }
                runner.IsTrue(await Poll.UntilAsync(() => Rows(store) is [{ TlsFailure.Attempts: 12 }]), $"one row, twelve attempts (got {string.Join(",", Rows(store).Select(r => r.TlsFailure!.Attempts))})");
                runner.AreEqual(1, store.Count, "and nothing else in the store");
            }));

        await runner.RunAsync("stopping the proxy while clients sit idle after the handshake records nothing", async () =>
        {
            var store = new SessionStore();
            var proxy = new ProxyServer(new ProxyOptions { Port = 0, DecryptHttps = true }, ca, store);
            proxy.Start();
            var clients = new List<(TcpClient, SslStream)>();
            try
            {
                for (var i = 0; i < 3; i++)
                {
                    var client = await TlsFailureEvidenceTests.OpenTunnelAsync(proxy.Endpoint!.Port);
                    var ssl = new SslStream(client.GetStream());
                    await ssl.AuthenticateAsClientAsync(TlsFailureEvidenceTests.Client("idle.test", SslProtocols.None, accept: true));
                    clients.Add((client, ssl));
                }
                await proxy.StopAsync();
                runner.IsTrue(await Poll.UntilAsync(() => proxy.ActiveConnections == 0), "the connections end");
                await Task.Delay(300);
                runner.AreEqual(0, Rows(store).Length, "and none is a failure");
            }
            finally
            {
                foreach (var (client, ssl) in clients) { ssl.Dispose(); client.Dispose(); }
                await proxy.StopAsync();
            }
        });
    }

    // -------------------------------------------------------------------------- helpers

    /// <summary>A window no test outlasts, so that only "a request was served" can keep a close from being recorded,
    /// and an AutoResponder rule that answers /x from the proxy, so the connection is kept alive with no origin.</summary>
    private static void WindowOpenWide(ProxyOptions options)
    {
        options.ClosedAfterHandshakeWindow = TimeSpan.FromMinutes(1);
        options.AutoResponder.Apply(new AutoResponderSettings { Enabled = true, Rules = [new AutoResponderRule { Match = "/x", Action = "*503" }] });
    }

    private static bool Answered(Session session) => session is { IsAutoResponded: true, State: SessionState.Complete };

    private static Session[] Rows(SessionStore store) => store.Snapshot().Where(s => s.TlsFailure is not null).ToArray();

    /// <summary>The connection preface and an empty SETTINGS frame.</summary>
    private static byte[] H2Preface() => [.. "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8, 0, 0, 0, 0x04, 0, 0, 0, 0, 0];
}
