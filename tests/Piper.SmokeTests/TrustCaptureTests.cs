using System.Net.Sockets;
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
        await RunUnwritableSettingsAsync(runner);
    }

    private static Task RunStatusTextAsync(TestRunner runner) => runner.RunAsync("the status text only says decryption is on while HTTPS is really decrypted", () =>
    {
        string Text(HttpsState state) => Strings.StatusBar.Listening("127.0.0.1:8888", state);
        runner.IsTrue(Text(HttpsState.NotTrusted).Contains("not trusted", StringComparison.Ordinal) && !Text(HttpsState.NotTrusted).Contains("decryption on", StringComparison.Ordinal),
            $"an untrusted root is reported as such, not as decryption on ({Text(HttpsState.NotTrusted)})");
        runner.IsTrue(Text(HttpsState.Decrypting).Contains("decryption on", StringComparison.Ordinal), "decryption is reported on only when decrypting");
        runner.IsTrue(Text(HttpsState.Off).Contains("decryption off", StringComparison.Ordinal), "a switched-off option is reported as off");
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
}
