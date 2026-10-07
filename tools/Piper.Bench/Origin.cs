using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Piper.Bench;

/// <summary>
/// What the proxy talks to, on loopback and in the driver's process: a Kestrel HTTP/1.1 origin and a
/// raw TCP server for CONNECT tunnels. Every size a client can ask for is clamped.
///
/// HTTP: <c>/small</c> (256 B), <c>/hdr</c> (40 response headers), <c>/big?mb=N</c> and
/// <c>/bigchunked?mb=N</c> (N MB, with a Content-Length or chunked), <c>/up</c> (counts the request body).
/// Tunnel: the first byte of the first <see cref="PayloadBytes"/> bytes picks the mode, <c>P</c> echoes a
/// payload, <c>D</c> sends <see cref="TunnelMegabytes"/> MB, <c>U</c> receives until the client half-closes.
/// The payload is never one byte: ESET buffers loopback HTTP and stalls tiny payloads with or without Piper.
/// </summary>
internal sealed class Origin : IAsyncDisposable
{
    public const int PayloadBytes = 1024;
    public const int TunnelMegabytes = 256;
    public const int MaxMegabytes = 2048;

    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly TcpListener _tunnels = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _connectionIds = [];

    private Origin(WebApplication app, X509Certificate2 certificate)
    {
        _app = app;
        _certificate = certificate;
    }

    // Self-signed and never trusted by anything: the proxy under test is told not to validate it. The key
    // is not ephemeral because SChannel refuses an ephemeral key for a server certificate (the same reason
    // Piper's own leaf certificates are loaded this way); without PersistKeySet Windows deletes the
    // temporary key container when the certificate is disposed.
    private static X509Certificate2 NewCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=piper-bench-origin", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var selfSigned = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(selfSigned.Export(X509ContentType.Pfx, "bench"), "bench", X509KeyStorageFlags.Exportable);
    }

    public int HttpPort { get; private set; }

    /// <summary>HTTPS (HTTP/1.1 and HTTP/2) with a throwaway self-signed certificate.</summary>
    public int TlsPort { get; private set; }
    public int TunnelPort { get; private set; }

    /// <summary>Distinct connections the origin has accepted since <see cref="ResetConnectionCount"/>.</summary>
    public int ConnectionCount { get { lock (_connectionIds) return _connectionIds.Count; } }

    public void ResetConnectionCount() { lock (_connectionIds) _connectionIds.Clear(); }

    public static async Task<Origin> StartAsync()
    {
        var certificate = NewCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1);
            options.Listen(IPAddress.Loopback, 0, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(certificate);
            });
            options.Limits.MaxRequestBodySize = null; // the upload scenario sends 64 MB
        });
        var origin = new Origin(builder.Build(), certificate);
        origin._app.Run(origin.HandleAsync);
        await origin._app.StartAsync().ConfigureAwait(false);
        var feature = origin._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        origin.HttpPort = new Uri(feature!.Addresses.First(a => a.StartsWith("http://", StringComparison.Ordinal))).Port;
        origin.TlsPort = new Uri(feature.Addresses.First(a => a.StartsWith("https://", StringComparison.Ordinal))).Port;
        origin._tunnels.Start();
        origin.TunnelPort = ((IPEndPoint)origin._tunnels.LocalEndpoint).Port;
        _ = Task.Run(() => origin.AcceptTunnelsAsync(origin._stop.Token));
        return origin;
    }

    private static readonly byte[] Small = Enumerable.Repeat((byte)'x', 256).ToArray();
    private static readonly byte[] Chunk = MakeChunk();

    private static byte[] MakeChunk()
    {
        var chunk = new byte[64 * 1024];
        new Random(1).NextBytes(chunk); // incompressible and identical on every run
        return chunk;
    }

    private async Task HandleAsync(HttpContext context)
    {
        lock (_connectionIds) _connectionIds.Add(context.Connection.Id);
        var response = context.Response;
        var path = context.Request.Path.Value;
        switch (path)
        {
            case "/small":
                response.ContentType = "text/plain";
                response.ContentLength = Small.Length;
                await response.Body.WriteAsync(Small, context.RequestAborted).ConfigureAwait(false);
                break;
            case "/hdr":
                for (var i = 0; i < 40; i++) response.Headers[$"X-Resp-Header-{i}"] = "resp-value-" + new string('v', 40);
                response.ContentType = "text/plain";
                response.ContentLength = 512;
                await response.Body.WriteAsync(new byte[512], context.RequestAborted).ConfigureAwait(false);
                break;
            case "/big" or "/bigchunked":
            {
                if (!int.TryParse(context.Request.Query["mb"], out var mb) || mb < 1 || mb > MaxMegabytes) { response.StatusCode = 400; break; }
                var total = (long)mb * 1024 * 1024;
                response.ContentType = "application/octet-stream";
                if (path == "/big") response.ContentLength = total;
                for (long sent = 0; sent < total; sent += Chunk.Length)
                    await response.Body.WriteAsync(Chunk.AsMemory(0, (int)Math.Min(Chunk.Length, total - sent)), context.RequestAborted).ConfigureAwait(false);
                break;
            }
            case "/up":
            {
                var buffer = new byte[64 * 1024];
                long count = 0;
                int read;
                while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted).ConfigureAwait(false)) > 0) count += read;
                response.ContentType = "text/plain";
                response.ContentLength = 8;
                await response.Body.WriteAsync(Encoding.ASCII.GetBytes(count.ToString("D8", System.Globalization.CultureInfo.InvariantCulture)), context.RequestAborted).ConfigureAwait(false);
                break;
            }
            default:
                response.StatusCode = 404;
                break;
        }
    }

    private async Task AcceptTunnelsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _tunnels.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => ServeTunnelAsync(client, ct), CancellationToken.None);
        }
    }

    private static async Task ServeTunnelAsync(TcpClient client, CancellationToken stop)
    {
        using var _ = client;
        client.NoDelay = true;
        // A client that vanishes mid-transfer must not leave this task, or its socket, behind.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = cts.Token;
        try
        {
            var stream = client.GetStream();
            var first = new byte[PayloadBytes];
            await stream.ReadExactlyAsync(first, ct).ConfigureAwait(false);
            switch ((char)first[0])
            {
                case 'P':
                    await stream.WriteAsync(new byte[PayloadBytes], ct).ConfigureAwait(false);
                    await Task.Delay(50, ct).ConfigureAwait(false);
                    break;
                case 'D':
                    for (long sent = 0; sent < (long)TunnelMegabytes * 1024 * 1024; sent += Chunk.Length)
                        await stream.WriteAsync(Chunk, ct).ConfigureAwait(false);
                    client.Client.Shutdown(SocketShutdown.Send);
                    await stream.ReadAsync(new byte[1], ct).ConfigureAwait(false); // until the client closes
                    break;
                case 'U':
                    var buffer = new byte[64 * 1024];
                    while (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) > 0) { }
                    await stream.WriteAsync(new byte[PayloadBytes], ct).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or EndOfStreamException or ObjectDisposedException)
        {
            // The client went away, or the two minutes ran out: the scenario sees the short transfer.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _tunnels.Stop();
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        _certificate.Dispose();
        _stop.Dispose();
    }
}
