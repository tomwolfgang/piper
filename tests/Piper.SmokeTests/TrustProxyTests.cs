using System.Net.Sockets;
using System.Text;
using Piper.Core.Proxy;
using Piper.Core.Security;
using Piper.Core.Sessions;

// C1: a proxy whose decryption is suspended (root not trusted) still captures HTTP and tunnels HTTPS
// unread; the trust cache holds up under concurrent use; and a settings file that cannot be written
// is detectable. The Windows certificate store is never touched.
internal static class TrustProxyTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(8);

    public static async Task RunAsync(TestRunner runner)
    {
        await RunSuspendedProxyAsync(runner);
        await RunConcurrentCacheAsync(runner);
        await RunUnwritableSettingsAsync(runner);
    }

    private static Task RunSuspendedProxyAsync(TestRunner runner) => runner.RunAsync("a proxy with decryption suspended still captures HTTP and tunnels HTTPS unread", async () =>
    {
        var directory = Path.Combine(Path.GetTempPath(), $"Piper-SmokeTest-Trust-{Guid.NewGuid():N}");
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

        var http = await ExchangeAsync(port,
            $"GET http://127.0.0.1:{origin.Port}/plain HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\nConnection: close\r\n\r\n");
        runner.IsTrue(http.Contains("200 OK", StringComparison.Ordinal), "plain HTTP is served while decryption is suspended");
        runner.IsTrue(await Poll.UntilAsync(() => store.Snapshot().Any(s => s.Request?.RequestTarget.Contains("/plain", StringComparison.Ordinal) == true)),
            "and is captured as a session");

        // A CONNECT whose tunnel is not TLS: only an unread tunnel passes these bytes to the origin.
        var tunnelled = await ExchangeAsync(port,
            $"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n",
            afterConnect: "GET /through HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        runner.IsTrue(tunnelled.Contains("Connection Established", StringComparison.Ordinal)
            && tunnelled.Contains("HTTP/1.1 200 OK", StringComparison.Ordinal),
            "a suspended proxy tunnels the CONNECT unread, so the origin's own bytes come back");

        options.DecryptionSuspended = false;
        var decrypting = await ExchangeAsync(port,
            $"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n",
            afterConnect: "GET /through HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
        runner.IsTrue(!decrypting.Contains("HTTP/1.1 200 OK", StringComparison.Ordinal),
            "control: the same CONNECT with decryption active expects TLS, so the origin never answers");
    });

    private static async Task<string> ExchangeAsync(int port, string request, string? afterConnect = null)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(Patience);
        await client.ConnectAsync("127.0.0.1", port, cts.Token);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cts.Token);

        var text = new StringBuilder();
        var buffer = new byte[4096];
        var sentFollowUp = afterConnect is null;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0) break;
                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (!sentFollowUp && text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    sentFollowUp = true;
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(afterConnect!), cts.Token);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The proxy closed or the wait ran out; what arrived so far is the answer.
        }

        return text.ToString();
    }

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

        var workers = new[]
        {
            Loop(() => _ = cache.IsTrusted), Loop(() => _ = cache.IsTrusted),
            Loop(cache.Invalidate), Loop(() => { _ = cache.Last; _ = cache.NeedsRead; }),
        };
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
            runner.IsTrue(ProxyConfigurationSettingsStore.Load(path) is null,
                "Save swallowed the failure, so the caller has to read back to know it did not stick");
        }
        finally
        {
            File.Delete(blocker);
        }

        return Task.CompletedTask;
    });
}
