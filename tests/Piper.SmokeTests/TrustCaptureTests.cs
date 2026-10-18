using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Piper.App;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// C1: with the root untrusted the proxy still captures and tunnels HTTPS unread, the status text never
// claims decryption, and an unsaved Don't ask again is detectable. No test touches the Windows store.
internal static class TrustCaptureTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    public static async Task RunAsync(TestRunner runner)
    {
        await RunStatusTextAsync(runner);
        await RunSuspendedProxyAsync(runner);
        await RunDisplayReadAsync(runner);
        await RunConcurrentCacheAsync(runner);
        await RunUnwritableSettingsAsync(runner);
    }

    private static Task RunStatusTextAsync(TestRunner runner) => runner.RunAsync("the status text only says decryption is on while HTTPS is really decrypted", () =>
    {
        string Text(HttpsState state) => Strings.StatusBar.Listening("127.0.0.1:8888", state);
        runner.IsTrue(Text(HttpsState.NotTrusted).Contains("not trusted", StringComparison.Ordinal) && !Text(HttpsState.NotTrusted).Contains("decryption on", StringComparison.Ordinal),
            $"an untrusted root is reported as such, not as decryption on ({Text(HttpsState.NotTrusted)})");
        runner.IsTrue(Text(HttpsState.Decrypting).Contains("decryption on", StringComparison.Ordinal), "decryption is reported on only when decrypting");
        runner.IsTrue(Text(HttpsState.DecryptingUntrusted).Contains("clients must trust it themselves", StringComparison.Ordinal),
            "decrypting without Windows trust says the clients carry the trust");
        runner.IsTrue(Text(HttpsState.Off).Contains("decryption off", StringComparison.Ordinal), "a switched-off option is reported as off");

        var labels = Enum.GetValues<HttpsState>().Select(Strings.StatusBar.HttpsLabel).ToList();
        runner.AreEqual(4, labels.Distinct().Count(), "the four HTTPS states read differently in the status bar");
        runner.IsTrue(Strings.StatusBar.HttpsLabel(HttpsState.NotTrusted).Contains("Not decrypted", StringComparison.Ordinal), "the untrusted state says HTTPS is not decrypted");
        runner.IsTrue(labels.Where((_, i) => (HttpsState)i is HttpsState.NotTrusted or HttpsState.Off).All(l => !l.Contains("Decrypting", StringComparison.Ordinal)),
            "a state that tunnels HTTPS unread never says Decrypting");
        runner.IsTrue(Strings.StatusBar.HttpsLabel(HttpsState.DecryptingUntrusted).Contains("clients must trust", StringComparison.Ordinal),
            "decrypting without Windows trust says the clients carry the trust");
        return Task.CompletedTask;
    });

    private static Task RunSuspendedProxyAsync(TestRunner runner) => runner.RunAsync("a proxy with decryption suspended still captures HTTP and tunnels HTTPS unread", async () =>
    {
        // The test CA's private key is unencrypted, so its folder is removed after the proxy is disposed.
        var directory = Path.Combine(Path.GetTempPath(), $"Piper-SmokeTest-Trust-{Guid.NewGuid():N}");
        try { await SuspendedProxyAsync(runner, directory); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    });

    private static async Task SuspendedProxyAsync(TestRunner runner, string directory)
    {
        using var ca = CertificateAuthority.LoadOrCreate(directory);
        await using var origin = new TestRawOrigin(async (head, stream, ct) =>
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), ct);
            return false;
        });
        var options = new ProxyOptions { Port = 0, DecryptionSuspended = true };
        var store = new SessionStore();
        await using var proxy = new ProxyServer(options, ca, store);
        proxy.Start();
        var port = proxy.Endpoint!.Port;
        var host = $"127.0.0.1:{origin.Port}";

        var http = await ExchangeAsync(port, $"GET http://{host}/plain HTTP/1.1\r\nHost: {host}\r\nConnection: close\r\n\r\n");
        runner.IsTrue(http.Contains("200 OK", StringComparison.Ordinal), "plain HTTP is served while decryption is suspended");
        runner.IsTrue(await Poll.UntilAsync(() => store.Snapshot().Any(s => s.Request?.RequestTarget.Contains("/plain", StringComparison.Ordinal) == true)),
            "and is captured as a session");

        // The tunnel carries plain HTTP, not TLS: only an unread tunnel passes it to the origin.
        const string FollowUp = "GET /through HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n";
        var connect = $"CONNECT {host} HTTP/1.1\r\nHost: {host}\r\n\r\n";
        var tunnelled = await ExchangeAsync(port, connect, FollowUp);
        runner.IsTrue(tunnelled.Contains("Connection Established", StringComparison.Ordinal) && tunnelled.Contains("HTTP/1.1 200 OK", StringComparison.Ordinal),
            "a suspended proxy tunnels the CONNECT unread, so the origin's own bytes come back");

        options.DecryptionSuspended = false;
        runner.IsTrue(!(await ExchangeAsync(port, connect, FollowUp)).Contains("HTTP/1.1 200 OK", StringComparison.Ordinal),
            "control: with decryption active the same CONNECT expects TLS, so the origin never answers");

        // The root is untrusted by Windows here. A client that trusts the CA itself completes the TLS
        // handshake only when the opt-in is on; with it off the CONNECT is tunnelled unread.
        foreach (var optIn in new[] { false, true })
        {
            options.DecryptionSuspended = TrustPolicy.IsSuspended(trusted: false, decryptWhenUntrusted: optIn);
            var decrypted = await TlsHandshakeSucceedsAsync(port, host, ca.RootCertificate);
            runner.AreEqual(optIn, decrypted, optIn
                ? "opt-in on, root untrusted: a client that trusts the CA is decrypted"
                : "opt-in off, root untrusted: the same client is tunnelled unread (no handshake)");
        }
    }

    private static async Task<bool> TlsHandshakeSucceedsAsync(int port, string host, X509Certificate2 root)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); // a tunnelled connection never answers
        await client.ConnectAsync("127.0.0.1", port, cts.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"CONNECT {host} HTTP/1.1\r\nHost: {host}\r\n\r\n"), cts.Token);
        var head = new byte[256];
        _ = await stream.ReadAsync(head, cts.Token); // "200 Connection Established"

        using var tls = new SslStream(stream, leaveInnerStreamOpen: true, (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(new X509Certificate2(certificate));
        });
        try
        {
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "example.test" }, cts.Token);
            return true;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException)
        {
            return false; // plain bytes from the origin where a ServerHello should be, or no answer at all
        }
    }

    private static async Task<string> ExchangeAsync(int port, string request, string? afterConnect = null)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(Patience);
        await client.ConnectAsync("127.0.0.1", port, cts.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);

        var text = new StringBuilder();
        var buffer = new byte[4096];
        var followUp = afterConnect;
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (followUp is not null && text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(followUp), cts.Token);
                    followUp = null;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The proxy closed or the wait ran out; what arrived so far is the answer.
        }

        return text.ToString();
    }

    private static Task RunDisplayReadAsync(TestRunner runner) => runner.RunAsync("a repaint after the cache expired never reads the store on the calling thread", () =>
    {
        var reads = 0;
        var trusted = true;
        var clock = new FakeClock();
        var cache = new TrustStateCache(() => { reads++; return trusted; }, TimeSpan.FromSeconds(30), clock);
        var queued = 0;

        runner.IsTrue(cache.ForDisplay(() => queued++) && reads == 1, "the first answer has to be read");
        clock.Advance(TimeSpan.FromSeconds(31));
        trusted = false;
        runner.IsTrue(cache.ForDisplay(() => queued++) && reads == 1 && queued == 1,
            "after expiry a repaint shows the last answer, reads nothing, and asks for a refresh elsewhere");
        runner.IsTrue(cache.TryRefresh() && !cache.ForDisplay(() => queued++) && queued == 1, "the worker's read is then shown, and nothing more is queued");

        // A failed worker read keeps the last answer, reports failure, and does not throw.
        var fails = false;
        var flaky = new TrustStateCache(() => fails ? throw new UnauthorizedAccessException() : true, TimeSpan.FromSeconds(30), clock);
        runner.IsTrue(flaky.TryRefresh() && flaky.Last == true, "a good read is kept");
        clock.Advance(TimeSpan.FromSeconds(31));
        fails = true;
        runner.IsTrue(!flaky.TryRefresh() && flaky.Last == true && flaky.NeedsRead, "a failing read keeps the last answer and a retry is still due");
        var broken = new TrustStateCache(() => throw new CryptographicException("store unavailable"), TimeSpan.FromSeconds(30), clock);
        runner.IsTrue(!broken.TryRefresh() && broken.Last is null, "a failing first read leaves no answer");
        return Task.CompletedTask;
    });

    private static Task RunConcurrentCacheAsync(TestRunner runner) => runner.RunAsync("the trust cache is safe when a worker reads while the UI thread invalidates", async () =>
    {
        var trusted = true;
        var cache = new TrustStateCache(() => Volatile.Read(ref trusted), TimeSpan.FromMilliseconds(1));
        using var stop = new CancellationTokenSource();
        var failures = 0;
        Task Loop(Action body) => Task.Run(() =>
        {
            try { while (!stop.IsCancellationRequested) body(); }
            catch (Exception) { Interlocked.Increment(ref failures); }
        });

        var workers = new[] { Loop(() => _ = cache.IsTrusted), Loop(() => _ = cache.IsTrusted), Loop(cache.Invalidate), Loop(() => { _ = cache.Last; _ = cache.NeedsRead; }) };
        await Task.Delay(300);
        stop.Cancel();
        await Task.WhenAll(workers);
        runner.AreEqual(0, failures, "no thread failed while reading, invalidating and peeking concurrently");

        Volatile.Write(ref trusted, false);
        cache.Invalidate();
        runner.IsTrue(!cache.IsTrusted, "after the storm an invalidate still makes the next read see the change");
    });

    private static Task RunUnwritableSettingsAsync(TestRunner runner) => runner.RunAsync("a settings file that cannot be written is skipped, not thrown, and reads back as not saved", () =>
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"piper-trust-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "a file where the settings folder should be");
        try
        {
            var path = Path.Combine(blocker, "configuration.json");
            ProxyConfigurationSettingsStore.Save(new ProxyConfigurationSettings { AskToTrustCertificate = false }, path);
            runner.IsTrue(ProxyConfigurationSettingsStore.Load(path) is null, "Save swallows the failure, so the caller reads back to know it did not stick");
        }
        finally
        {
            File.Delete(blocker);
        }

        return Task.CompletedTask;
    });

    private sealed class FakeClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
