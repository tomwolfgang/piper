using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Piper.Core.Http;

namespace Piper.Core.Proxy;

/// <summary>
/// A connection to an origin server, kept alive across requests while the target and
/// scheme match the next request on the same client connection.
/// </summary>
internal sealed class UpstreamConnection : IDisposable
{
    private UpstreamConnection(TcpClient client, Stream stream, string host, int port, bool isTls, bool isHttp2)
    {
        Client = client;
        Stream = stream;
        Reader = new HttpStreamReader(stream);
        Host = host;
        Port = port;
        IsTls = isTls;
        IsHttp2 = isHttp2;
    }

    public TcpClient Client { get; }
    public Stream Stream { get; }
    public HttpStreamReader Reader { get; }
    public string Host { get; }
    public int Port { get; }
    public bool IsTls { get; }

    /// <summary>True when the origin negotiated h2 via ALPN. Always false for plain (non-TLS)
    /// connections -- HTTP/2 without TLS (h2c) is not something Piper ever offers upstream.</summary>
    public bool IsHttp2 { get; }

    public string? RemoteEndpoint => Client.Client?.RemoteEndPoint?.ToString();

    public bool Matches(string host, int port, bool isTls) =>
        IsTls == isTls && Port == port && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase);

    public bool IsUsable
    {
        get
        {
            try
            {
                var socket = Client.Client;
                if (socket is null || !Client.Connected) return false;
                // Poll reports readable-with-zero-available only when the peer has closed.
                return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch (SocketException) { return false; }
            catch (ObjectDisposedException) { return false; }
        }
    }

    /// <param name="allowHttp2">Set false to force h1.1 regardless of <see cref="ProxyOptions.EnableHttp2Upstream"/>.
    /// The Composer needs this: it sends verbatim wire bytes via <c>HttpRequestData.ToOriginFormBytes()</c>,
    /// which has no HTTP/2 equivalent, so it must never end up with an ALPN-negotiated h2 connection.</param>
    public static async Task<UpstreamConnection> ConnectAsync(
        string host, int port, bool isTls, ProxyOptions options, CancellationToken ct, bool allowHttp2 = true)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.ConnectTimeout);
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        Stream stream = client.GetStream();
        var isHttp2 = false;

        if (isTls)
        {
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, _, errors) =>
                options.ValidateUpstreamCertificates ? errors == SslPolicyErrors.None : true);

            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = SslProtocols.None, // negotiate the best the OS offers
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                    // Omitted entirely (rather than set to just http/1.1) when the toggle is off,
                    // so behaviour is byte-for-byte unchanged from before this feature existed.
                    ApplicationProtocols = options.EnableHttp2Upstream && allowHttp2
                        ? [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
                        : null,
                }, ct).ConfigureAwait(false);
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                client.Dispose();
                throw;
            }

            isHttp2 = ssl.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2;
            stream = ssl;
        }

        return new UpstreamConnection(client, stream, host, port, isTls, isHttp2);
    }

    public void Dispose()
    {
        Reader.Dispose();
        try { Stream.Dispose(); } catch { /* already torn down */ }
        try { Client.Dispose(); } catch { /* already torn down */ }
    }
}
