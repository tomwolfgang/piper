using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Piper.Core.Http;
using Piper.Core.Http2;

// A raw TcpListener + SslStream origin double that speaks HTTP/2 only (ApplicationProtocols =
// [Http2], no http/1.1 fallback, forcing deterministic ALPN negotiation) by self-hosting Piper's
// own Http2Connection server-role machinery. HttpListener can't do ALPN at all, and pulling in
// Kestrel to get an independent h2 server would break this project's zero-NuGet posture -- this
// is the pragmatic middle ground for testing the *client* role (Http2ClientConnection /
// UpstreamConnection's ALPN) against something that isn't literally the code under test's own
// client-side pairing partner. Testing the client against Piper's own server is still weaker
// evidence for symmetric bugs than an independent stack would be, which is why Http2Tests'
// end-to-end assertions additionally check exact captured header/body values, not just "it didn't
// throw".
internal sealed class TestHttp2Origin : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _certificate;
    private readonly Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> _handler;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = [];
    private readonly Lock _gate = new();
    private readonly Task _acceptLoop;

    public TestHttp2Origin(X509Certificate2 certificate, Func<HttpRequestData, CancellationToken, Task<HttpResponseData>> handler)
    {
        _certificate = certificate;
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _acceptLoop = Task.Run(AcceptLoopAsync);
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
        var ssl = new SslStream(c.GetStream(), leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = _certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ApplicationProtocols = [SslApplicationProtocol.Http2], // h2-only: no h1.1 fallback
            }, _cts.Token).ConfigureAwait(false);
        }
        catch { await ssl.DisposeAsync().ConfigureAwait(false); return; }

        var connection = new Http2Connection(ssl, _handler);
        try { await connection.RunAsync(_cts.Token).ConfigureAwait(false); }
        catch { /* the test asserts on the client side */ }
        finally { await ssl.DisposeAsync().ConfigureAwait(false); }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener.Stop(); } catch (SocketException) { }
        try { await _acceptLoop.ConfigureAwait(false); } catch { }

        Task[] pending;
        lock (_gate) pending = _connections.ToArray();
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch { }
    }
}
