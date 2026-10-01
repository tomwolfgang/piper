using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Piper.Core.Http;
using Piper.Core.Security;

namespace Piper.Core.Proxy;

/// <summary>
/// A connection to an origin server, kept alive across requests while the target and
/// scheme match the next request on the same client connection.
/// </summary>
internal sealed class UpstreamConnection : IDisposable
{
    private UpstreamConnection(Socket client, Stream stream, string host, int port, bool isTls, bool isHttp2,
        long remappingRevision, TimeSpan idleTimeout)
    {
        Client = client;
        RemoteEndpoint = client.RemoteEndPoint?.ToString();
        Stream = stream;
        Reader = new HttpStreamReader(stream) { IdleTimeout = idleTimeout };
        Host = host;
        Port = port;
        IsTls = isTls;
        IsHttp2 = isHttp2;
        RemappingRevision = remappingRevision;
    }

    public Socket Client { get; }
    public Stream Stream { get; }
    public HttpStreamReader Reader { get; }
    public string Host { get; }
    public int Port { get; }
    public bool IsTls { get; }

    /// <summary>True when the origin negotiated h2 via ALPN. Always false for plain (non-TLS)
    /// connections -- HTTP/2 without TLS (h2c) is not something Piper ever offers upstream.</summary>
    public bool IsHttp2 { get; }
    public long RemappingRevision { get; }

    /// <summary>Read once at connect: a disposed socket no longer reports its peer.</summary>
    public string? RemoteEndpoint { get; }

    public bool Matches(string host, int port, bool isTls, long remappingRevision) =>
        IsTls == isTls && Port == port && RemappingRevision == remappingRevision
        && string.Equals(Host, host, StringComparison.OrdinalIgnoreCase);

    public bool IsUsable
    {
        get
        {
            try
            {
                if (!Client.Connected) return false;
                // Poll reports readable-with-zero-available only when the peer has closed.
                return !(Client.Poll(0, SelectMode.SelectRead) && Client.Available == 0);
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
        var remapping = options.HostRemapping.ResolveTarget(host);
        var tlsHost = remapping.RewritesAuthority ? remapping.Host : host;

        Socket client;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(options.ConnectTimeout);
            try
            {
                client = await HappyEyeballs.ConnectAsync(remapping.Host, port, options, timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // An IOException so that every caller reports it as the upstream failing, with a
                // reason, instead of as an anonymous cancellation.
                throw new IOException($"Timed out after {options.ConnectTimeout.TotalSeconds:0.#}s connecting to {host}:{port}.");
            }
        }

        Stream stream = new NetworkStream(client, ownsSocket: true);
        var isHttp2 = false;

        if (isTls)
        {
            string? rejectionDetail = null;
            var ssl = new SslStream(stream, leaveInnerStreamOpen: false, (_, _, chain, errors) =>
            {
                if (!options.ValidateUpstreamCertificates || errors == SslPolicyErrors.None) return true;
                rejectionDetail = CertificateRejectionDetail.Describe(errors, chain);
                return false;
            });

            // Its own budget, so an origin that accepts the TCP connection and then says nothing
            // cannot hold the request (and a client connection slot) open for ever.
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeTimeout.CancelAfter(options.ConnectTimeout);

            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = tlsHost,
                    EnabledSslProtocols = SslProtocols.None, // negotiate the best the OS offers
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                    // Omitted entirely (rather than set to just http/1.1) when the toggle is off,
                    // so behaviour is byte-for-byte unchanged from before this feature existed.
                    ApplicationProtocols = options.EnableHttp2Upstream && allowHttp2
                        ? [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
                        : null,
                }, handshakeTimeout.Token).ConfigureAwait(false);
            }
            catch (AuthenticationException ex) when (rejectionDetail is not null)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                client.Dispose();
                throw new AuthenticationException($"{ex.Message} ({rejectionDetail})", ex);
            }
            catch (Exception) when (handshakeTimeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                client.Dispose();
                throw new IOException(
                    $"Timed out after {options.ConnectTimeout.TotalSeconds:0.#}s waiting for the TLS handshake with {host}:{port}.");
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

        return new UpstreamConnection(
            client, stream, host, port, isTls, isHttp2, remapping.Revision, options.UpstreamIdleTimeout);
    }

    public void Dispose()
    {
        Reader.Dispose();
        try { Stream.Dispose(); } catch { /* already torn down */ }
        try { Client.Dispose(); } catch { /* already torn down */ }
    }
}
