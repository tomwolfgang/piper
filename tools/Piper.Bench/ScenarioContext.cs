using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Piper.Bench;

/// <summary>Everything one scenario run needs, and the owner of the proxy host it starts. The runner
/// disposes it, so a scenario that throws or is cancelled still ends its host process.</summary>
internal sealed class ScenarioContext(BenchOptions options, Origin origin, string hostPath, string caDirectory, CancellationToken token) : IAsyncDisposable
{
    private HostClient? _host;
    private long _clientConnections;

    public BenchOptions Options { get; } = options;
    public Origin Origin { get; } = origin;
    public CancellationToken Token { get; } = token;

    public string OriginUrl => $"http://127.0.0.1:{Origin.HttpPort}";

    public HostClient Host => _host ?? throw new InvalidOperationException("The proxy host has not been started.");

    public long ClientConnections => Interlocked.Read(ref _clientConnections);

    public async Task<HostClient> StartHostAsync(params string[] hostArguments) =>
        _host ??= await HostClient.StartAsync(hostPath, caDirectory, Token, hostArguments).ConfigureAwait(false);

    /// <summary>Clears what a scenario counts per measurement: the connections the load generator
    /// opened, and the ones the origin accepted. Equal to the concurrency when keep-alive works.</summary>
    public void ResetConnectionCounts()
    {
        Interlocked.Exchange(ref _clientConnections, 0);
        Origin.ResetConnectionCount();
    }

    /// <summary>A client that reaches the origin only through the proxy (loopback is not bypassed).</summary>
    public HttpClient NewClient(int maxConnections, TimeSpan? timeout = null) =>
        new(NewHandler(maxConnections)) { Timeout = timeout ?? TimeSpan.FromSeconds(10) };

    /// <summary>A client for HTTPS through a decrypting proxy. It trusts the throwaway CA the host made, and
    /// only that, through a callback of its own: nothing is added to any certificate store. With
    /// <paramref name="http2"/> every request must be HTTP/2 and share one connection.</summary>
    public HttpClient NewTlsClient(int maxConnections, bool http2)
    {
        var handler = NewHandler(maxConnections);
        var root = X509Certificate2.CreateFromPem(File.ReadAllText(Path.Combine(caDirectory, "Piper-Root.cer")));
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is not null && ChainsTo(root, certificate);
        handler.EnableMultipleHttp2Connections = !http2;
        var client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) };
        if (http2)
        {
            client.DefaultRequestVersion = HttpVersion.Version20;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }
        return client;
    }

    private static bool ChainsTo(X509Certificate2 root, X509Certificate leaf)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var certificate = new X509Certificate2(leaf);
        return chain.Build(certificate) && chain.ChainElements[^1].Certificate.Thumbprint == root.Thumbprint;
    }

    private SocketsHttpHandler NewHandler(int maxConnections)
    {
        return new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{Host.Port}") { BypassProxyOnLocal = false },
            UseProxy = true,
            MaxConnectionsPerServer = maxConnections,
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectCallback = async (context, ct) =>
            {
                Interlocked.Increment(ref _clientConnections);
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync().ConfigureAwait(false);
    }
}
