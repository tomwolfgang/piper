using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Piper.Core.Http;
using Piper.Core.Http2;
using Piper.Core.Security;
using Piper.Core.Sessions;

namespace Piper.Core.Proxy;

/// <summary>
/// HTTP/1.1 forward proxy with optional TLS termination. One task per accepted client
/// connection; each connection loops over keep-alive requests until the peer closes.
/// </summary>
public sealed class ProxyServer : IAsyncDisposable
{
    /// <summary>Hop-by-hop headers that must not be forwarded (RFC 9110 7.6.1).</summary>
    internal static readonly string[] HopByHopHeaders =
    [
        "Connection", "Proxy-Connection", "Keep-Alive", "Transfer-Encoding",
        "TE", "Trailer", "Upgrade", "Proxy-Authenticate", "Proxy-Authorization",
    ];

    private readonly ProxyOptions _options;
    private readonly CertificateAuthority _ca;
    private readonly SessionStore _store;

    /// <summary>Which origins have advertised HTTP/3, shared across every connection this proxy
    /// handles so one origin's Alt-Svc informs later requests from any client connection.</summary>
    private readonly Http3.AltSvcCache _altSvc = new();

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;
    private int _activeConnections;

    public ProxyServer(ProxyOptions options, CertificateAuthority certificateAuthority, SessionStore store)
    {
        _options = options;
        _ca = certificateAuthority;
        _store = store;
    }

    public bool IsRunning { get; private set; }

    public int ActiveConnections => Volatile.Read(ref _activeConnections);

    public IPEndPoint? Endpoint { get; private set; }

    public event EventHandler<string>? Log;

    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _listener = new TcpListener(_options.ListenAddress, _options.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _listener.Start(512);

        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        IsRunning = true;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        Log?.Invoke(this, $"Listening on {Endpoint}. HTTPS decryption {(_options.DecryptHttps ? "enabled" : "disabled")}.");
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;
        IsRunning = false;

        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        try { _listener?.Stop(); } catch (SocketException) { /* already closed */ }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
        }

        _cts?.Dispose();
        _cts = null;
        _listener = null;
        Log?.Invoke(this, "Proxy stopped.");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException ex)
            {
                Log?.Invoke(this, $"Accept failed: {ex.Message}");
                continue;
            }

            _ = Task.Run(async () =>
            {
                Interlocked.Increment(ref _activeConnections);
                try { await HandleClientAsync(client, ct).ConfigureAwait(false); }
                catch (Exception ex) { Log?.Invoke(this, $"Connection error: {ex.Message}"); }
                finally
                {
                    Interlocked.Decrement(ref _activeConnections);
                    try { client.Dispose(); } catch { /* already gone */ }
                }
            }, ct);
        }
    }

    // ------------------------------------------------------------ connection loop

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var processName = ClientProcessLookup.Resolve(client.Client.RemoteEndPoint as IPEndPoint);

        Stream clientStream = client.GetStream();
        using var reader = new HttpStreamReader(clientStream);
        using var slot = new ConnectionSlot();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await ReadRequestWithIdleTimeoutAsync(reader, ct).ConfigureAwait(false);
                if (request is null) break;

                if (string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    // CONNECT takes over the connection entirely; it never returns to this loop.
                    await HandleConnectAsync(request, clientStream, client.Client, clientEndpoint, processName, ct)
                        .ConfigureAwait(false);
                    return;
                }

                var keepAlive = await HandleRequestAsync(
                    request, clientStream, client.Client, slot, clientEndpoint, processName, isHttps: false, ct)
                    .ConfigureAwait(false);

                if (!keepAlive) break;
            }
        }
        catch (OperationCanceledException) { /* shutting down or idle timeout */ }
        catch (IOException) { /* peer went away mid-message */ }
        catch (HttpParseException ex) { Log?.Invoke(this, $"Protocol error from {clientEndpoint}: {ex.Message}"); }
    }

    private async Task<HttpRequestData?> ReadRequestWithIdleTimeoutAsync(HttpStreamReader reader, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_options.IdleTimeout);
        try
        {
            return await HttpParser.ReadRequestAsync(reader, idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // idle keep-alive socket timed out - close it quietly
        }
    }

    // ------------------------------------------------------------------- CONNECT

    private async Task HandleConnectAsync(
        HttpRequestData connect, Stream clientStream, Socket clientSocket,
        string clientEndpoint, string processName, CancellationToken ct)
    {
        var (host, port) = SplitAuthority(connect.RequestTarget, defaultPort: 443);

        if (!_options.ShouldDecrypt(host))
        {
            await BlindTunnelAsync(connect, host, port, clientStream, clientEndpoint, processName, ct).ConfigureAwait(false);
            return;
        }

        await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct).ConfigureAwait(false);

        var ssl = new SslStream(clientStream, leaveInnerStreamOpen: false);
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                // Honour SNI when the client sends it; fall back to the CONNECT authority.
                ServerCertificateSelectionCallback = (_, sni) =>
                    _ca.GetCertificateFor(string.IsNullOrEmpty(sni) ? host : sni),
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck,
                // Omitted entirely (rather than set to just http/1.1) when the toggle is off, so
                // behaviour is byte-for-byte unchanged from before this feature existed.
                ApplicationProtocols = _options.EnableHttp2Downstream
                    ? [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
                    : null,
            }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Typically an untrusted root or a pinned client. Record it so the cause is visible.
            var failed = new Session
            {
                Request = connect,
                IsTunnel = true,
                IsHttps = true,
                State = SessionState.Failed,
                ClientEndpoint = clientEndpoint,
                ProcessName = processName,
                ServerEndpoint = $"{host}:{port}",
                Error = $"TLS handshake with client failed: {Describe(ex)}",
                Completed = DateTimeOffset.Now,
            };
            _store.Add(failed);
            await ssl.DisposeAsync().ConfigureAwait(false);
            return;
        }

        if (ssl.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
        {
            await RunHttp2Async(ssl, clientEndpoint, processName, ct).ConfigureAwait(false);
            return;
        }

        using var tlsReader = new HttpStreamReader(ssl);
        using var slot = new ConnectionSlot();

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var request = await ReadRequestWithIdleTimeoutAsync(tlsReader, ct).ConfigureAwait(false);
                if (request is null) break;

                // Inside a tunnel the target is origin-form; rebuild the absolute URL as https.
                request.Url = BuildTunnelUrl(request, host, port);

                var keepAlive = await HandleRequestAsync(
                    request, ssl, clientSocket, slot, clientEndpoint, processName, isHttps: true, ct).ConfigureAwait(false);

                if (!keepAlive) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (HttpParseException ex) { Log?.Invoke(this, $"Protocol error in tunnel to {host}: {ex.Message}"); }
        finally
        {
            // An AutoResponder *reset rule may already have aborted the socket underneath this
            // stream, in which case writing close_notify throws.
            try { await ssl.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { /* the connection is already gone */ }
        }
    }

    /// <summary>Runs one browser-facing HTTP/2 connection. Each stream is forwarded independently
    /// (see <see cref="Http2RequestForwarder"/>) and recorded as its own <see cref="Session"/>,
    /// exactly like an HTTP/1.1 request -- the multiplexing is invisible below this point.</summary>
    private async Task RunHttp2Async(SslStream ssl, string clientEndpoint, string processName, CancellationToken ct)
    {
        var connection = new Http2Connection(ssl,
            (request, streamCt) => Http2RequestForwarder.ForwardAsync(request, _options, _store, _altSvc, clientEndpoint, processName, streamCt));

        try
        {
            await connection.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (IOException ex) { Log?.Invoke(this, $"HTTP/2 IO error from {clientEndpoint}: {ex.GetType().Name}: {ex.Message}"); }
        catch (Http2ProtocolException ex) { Log?.Invoke(this, $"HTTP/2 protocol error from {clientEndpoint}: {ex.Message}"); }
        catch (Exception ex) { Log?.Invoke(this, $"HTTP/2 unexpected error from {clientEndpoint}: {ex.GetType().Name}: {ex.Message}"); }
        finally
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task BlindTunnelAsync(
        HttpRequestData connect, string host, int port, Stream clientStream, string clientEndpoint, string processName, CancellationToken ct)
    {
        var session = new Session
        {
            Request = connect,
            IsTunnel = true,
            IsHttps = true,
            State = SessionState.Tunnel,
            ClientEndpoint = clientEndpoint,
            ProcessName = processName,
            ServerEndpoint = $"{host}:{port}",
        };
        _store.Add(session);

        TcpClient? server = null;
        try
        {
            server = new TcpClient { NoDelay = true };
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_options.ConnectTimeout);
                await server.ConnectAsync(_options.HostRemapping.Resolve(host), port, timeout.Token).ConfigureAwait(false);
            }

            await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct).ConfigureAwait(false);

            var serverStream = server.GetStream();
            var up = PumpAsync(clientStream, serverStream, ct);
            var down = PumpAsync(serverStream, clientStream, ct);
            await Task.WhenAny(up, down).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            session.State = SessionState.Failed;
            session.Error = $"Tunnel to {host}:{port} failed: {Describe(ex)}";
            try { await WriteAsciiAsync(clientStream, "HTTP/1.1 502 Bad Gateway\r\n\r\n", CancellationToken.None).ConfigureAwait(false); }
            catch (IOException) { /* client already gone */ }
        }
        finally
        {
            session.Completed = DateTimeOffset.Now;
            _store.NotifyUpdated(session);
            server?.Dispose();
        }
    }

    /// <summary>Copies bytes one way until either side closes.</summary>
    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(32 * 1024);
        try
        {
            while (true)
            {
                int read;
                try { read = await from.ReadAsync(buffer, ct).ConfigureAwait(false); }
                catch (IOException) { break; }
                catch (OperationCanceledException) { break; }
                if (read <= 0) break;

                try { await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false); }
                catch (IOException) { break; }
                catch (OperationCanceledException) { break; }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // -------------------------------------------------------------- request path

    /// <summary>Holds the reusable upstream connection for one client connection. A plain
    /// <c>ref</c> parameter cannot cross an <c>async</c> boundary, so the slot is boxed.</summary>
    private sealed class ConnectionSlot : IDisposable
    {
        public UpstreamConnection? Connection;

        public void Reset()
        {
            Connection?.Dispose();
            Connection = null;
        }

        public void Dispose() => Reset();
    }

    /// <summary>Forwards one request and writes the response back. Returns false when the connection must close.</summary>
    private async Task<bool> HandleRequestAsync(
        HttpRequestData request, Stream clientStream, Socket clientSocket,
        ConnectionSlot slot, string clientEndpoint, string processName, bool isHttps, CancellationToken ct)
    {
        var session = new Session
        {
            Request = request,
            IsHttps = isHttps,
            ClientEndpoint = clientEndpoint,
            ProcessName = processName,
            State = SessionState.SendingRequest,
        };

        request.Url ??= HttpParser.ResolveUrl(request, assumeHttps: isHttps);
        _store.Add(session);

        if (request.Url is null)
        {
            await RespondLocallyAsync(clientStream, session,
                HttpResponseData.Simple(400, "Bad Request", "Piper could not determine the target URL for this request."), ct)
                .ConfigureAwait(false);
            return false;
        }

        var host = request.Url.Host;
        var port = request.Url.Port;
        var targetIsTls = request.Url.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);

        var clientWantsClose = request.Headers.HasToken("Connection", "close")
                               || request.Headers.HasToken("Proxy-Connection", "close")
                               || request.HttpVersion == "HTTP/1.0";

        var isUpgrade = request.Headers.HasToken("Connection", "Upgrade") && request.Headers.Contains("Upgrade");
        Uri? refetchTarget = null;

        // AutoResponder rules run here: late enough that the URL is resolved and the session exists,
        // early enough that nothing has been sent upstream. Outside the try below, so an IOException
        // from writing a canned response is not reported as a 502 from an origin we never contacted.
        //
        // Answering here and keeping the connection alive is only safe because HttpParser has already
        // read the whole request body -- no unread bytes are left on the socket to desynchronise the
        // next request. If body reading ever becomes lazy, revisit this.
        var decision = _options.AutoResponder.Evaluate(session);
        if (decision.Outcome is not AutoResponderOutcome.Passthrough)
        {
            if (decision.Delay > TimeSpan.Zero) await Task.Delay(decision.Delay, ct).ConfigureAwait(false);

            switch (decision.Outcome)
            {
                case AutoResponderOutcome.Respond:
                {
                    var canned = await decision.Action!
                        .BuildResponseAsync(decision.Rule!, decision.Match, request, _options.MaxBodyBytes, ct)
                        .ConfigureAwait(false);
                    return await RespondFromRuleAsync(clientStream, session, request, canned, decision,
                        clientWantsClose || isUpgrade, ct).ConfigureAwait(false);
                }

                case AutoResponderOutcome.Drop:
                    FinishAborted(session, decision, "closed the connection without responding");
                    return false;

                case AutoResponderOutcome.Reset:
                    FinishAborted(session, decision, "reset the connection");
                    AbortConnection(clientSocket);
                    return false;

                // A bare-URL action is Fiddler's transparent refetch: fetch somewhere else, but let the
                // client keep believing it called the address it asked for. session.Request stays
                // untouched so the grid still shows what the client actually sent.
                case AutoResponderOutcome.Redirect when decision.Action!.ResolveTarget(decision.Match, request.Url) is { } target:
                    session.AutoResponderRule = decision.Description;
                    refetchTarget = target;
                    host = target.Host;
                    port = target.Port;
                    targetIsTls = target.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var outbound = BuildOutboundRequest(request, isUpgrade, _options);
            if (refetchTarget is not null)
            {
                outbound.Url = refetchTarget;
                outbound.Headers.Set("Host",
                    refetchTarget.IsDefaultPort ? refetchTarget.Host : $"{refetchTarget.Host}:{refetchTarget.Port}");
            }
            var beforeResponse = stopwatch.Elapsed;

            void MarkSent()
            {
                session.State = SessionState.AwaitingResponse;
                _store.NotifyUpdated(session);
                beforeResponse = stopwatch.Elapsed;
            }

            // HTTP/3 first when this origin has advertised it, falling through to TCP on any
            // failure. An upgrade handshake is excluded: 101 hands the connection to another
            // protocol, which has no meaning over QUIC.
            var response = isUpgrade
                ? null
                : await Http3Attempt.TryFetchAsync(outbound, request.Url, _options, _altSvc, MarkSent, ct).ConfigureAwait(false);

            if (response is null)
            {
                var upstream = slot.Connection;
                if (upstream is not null && (!upstream.Matches(host, port, targetIsTls, _options.HostRemapping.Revision) || !upstream.IsUsable))
                {
                    slot.Reset();
                    upstream = null;
                }

                if (upstream is null)
                {
                    var connectStart = stopwatch.Elapsed;
                    upstream = await UpstreamConnection.ConnectAsync(host, port, targetIsTls, _options, ct).ConfigureAwait(false);
                    slot.Connection = upstream;
                    session.ConnectTime = stopwatch.Elapsed - connectStart;
                }

                session.ServerEndpoint = upstream.RemoteEndpoint;
                response = await UpstreamRequestSender.SendAsync(upstream, outbound, MarkSent, ct).ConfigureAwait(false);

                // Http2ClientConnection is one-shot, so an h2 upstream can never be pooled.
                if (upstream.IsHttp2) slot.Reset();
            }

            session.TimeToFirstByte = stopwatch.Elapsed - beforeResponse;
            _altSvc.RecordAltSvc(host, response.Headers["Alt-Svc"]);

            session.Response = response;
            session.State = SessionState.Complete;
            session.Completed = DateTimeOffset.Now;
            session.InvalidateSearchIndex();

            // 101 hands the connection over to another protocol (WebSocket, h2c). Relay
            // the switch and then pump raw bytes; there is no more HTTP to parse. Only reachable
            // on the TCP path -- upgrades are never attempted over h3, so the slot holds the
            // connection the 101 arrived on.
            if (response.StatusCode == 101 && slot.Connection is { } upgraded)
            {
                await clientStream.WriteAsync(response.ToBytes(), ct).ConfigureAwait(false);
                await clientStream.FlushAsync(ct).ConfigureAwait(false);
                _store.NotifyUpdated(session);

                var up = PumpAsync(clientStream, upgraded.Stream, ct);
                var down = PumpAsync(upgraded.Stream, clientStream, ct);
                await Task.WhenAny(up, down).ConfigureAwait(false);
                return false;
            }

            var inbound = BuildInboundResponse(response, clientWantsClose);
            // This clone's HttpVersion is only ever used for the literal wire bytes about to go
            // out on *this* h1.1 connection -- it must say "HTTP/1.1" no matter what the upstream
            // leg actually spoke (h2, or a legacy 1.0 origin). session.Response above still holds
            // the original, untouched `response`, so the captured/displayed HttpVersion keeps
            // recording the real upstream protocol.
            inbound.HttpVersion = "HTTP/1.1";
            await clientStream.WriteAsync(inbound.ToBytes(), ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);
            _store.NotifyUpdated(session);

            var serverWantsClose = response.Headers.HasToken("Connection", "close")
                                   || response.HttpVersion == "HTTP/1.0";
            if (serverWantsClose) slot.Reset();

            return !clientWantsClose && !serverWantsClose;
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                       or HttpParseException or OperationCanceledException)
        {
            session.State = SessionState.Failed;
            session.Error = Describe(ex);
            session.Completed = DateTimeOffset.Now;
            _store.NotifyUpdated(session);

            slot.Reset();

            try
            {
                await clientStream.WriteAsync(
                    HttpResponseData.Simple(502, "Bad Gateway",
                        $"Piper could not reach {host}:{port}.\r\n\r\n{ex.Message}").ToBytes(),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException) { /* client gone too */ }

            return false;
        }
    }

    /// <summary>
    /// Writes a response produced by an AutoResponder rule. Unlike <see cref="RespondLocallyAsync"/>,
    /// which serves terminal errors on a connection that is closing anyway, this goes through
    /// <see cref="BuildInboundResponse"/> so a faked response can keep the connection alive - otherwise
    /// every rule hit would cost a fresh TCP handshake and look artificially slow.
    /// </summary>
    private async Task<bool> RespondFromRuleAsync(
        Stream clientStream, Session session, HttpRequestData request, HttpResponseData canned,
        AutoResponderDecision decision, bool clientWantsClose, CancellationToken ct)
    {
        session.Response = canned;
        session.AutoResponderRule = decision.Description;
        session.State = SessionState.Complete;
        session.Completed = DateTimeOffset.Now;
        session.InvalidateSearchIndex();
        _store.NotifyUpdated(session);

        var inbound = BuildInboundResponse(canned, clientWantsClose);
        inbound.HttpVersion = "HTTP/1.1";

        // After BuildInboundResponse, so Content-Length still describes the body a GET would receive.
        if (string.Equals(request.Method, "HEAD", StringComparison.OrdinalIgnoreCase)) inbound.Body = [];

        try
        {
            await clientStream.WriteAsync(inbound.ToBytes(), ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false; // client gone
        }

        return !clientWantsClose;
    }

    /// <summary>Records a session that a rule deliberately killed. Failed is honest: the client sees one.</summary>
    private void FinishAborted(Session session, AutoResponderDecision decision, string what)
    {
        session.AutoResponderRule = decision.Description;
        session.State = SessionState.Failed;
        session.Error = $"AutoResponder rule '{decision.Description}' {what}.";
        session.Completed = DateTimeOffset.Now;
        session.InvalidateSearchIndex();
        _store.NotifyUpdated(session);
    }

    /// <summary>
    /// Closes a connection so the client sees a TCP reset rather than an orderly shutdown, which is
    /// the failure most worth being able to reproduce deliberately.
    /// </summary>
    private static void AbortConnection(Socket socket)
    {
        try
        {
            // Linger zero turns Close() into an RST instead of a FIN. Order matters.
            socket.LingerState = new LingerOption(true, 0);
            socket.Close();
        }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task RespondLocallyAsync(Stream clientStream, Session session, HttpResponseData response, CancellationToken ct)
    {
        session.Response = response;
        session.State = SessionState.Complete;
        session.Completed = DateTimeOffset.Now;
        session.InvalidateSearchIndex();
        _store.NotifyUpdated(session);

        try { await clientStream.WriteAsync(response.ToBytes(), ct).ConfigureAwait(false); }
        catch (IOException) { /* client gone */ }
    }

    /// <summary>Strips hop-by-hop headers and re-frames the body with an explicit Content-Length.
    /// Internal and static (with <paramref name="options"/> passed in rather than read off an
    /// instance field) so the HTTP/2 request forwarder can share this exact header-hygiene logic
    /// without touching the proven HTTP/1.1 hot path at all.</summary>
    internal static HttpRequestData BuildOutboundRequest(HttpRequestData request, bool preserveUpgrade, ProxyOptions options)
    {
        var outbound = request.Clone();

        foreach (var header in HopByHopHeaders)
        {
            if (preserveUpgrade && header is "Connection" or "Upgrade") continue;
            outbound.Headers.Remove(header);
        }

        if (options.NormalizeAcceptEncoding && outbound.Headers.Contains("Accept-Encoding"))
            outbound.Headers.Set("Accept-Encoding", "gzip, deflate, br");

        // Mapping to an IP is a conventional hosts-file override: the connection moves, but the
        // requested authority remains intact. Mapping to another hostname is a full authority
        // rewrite, which is what lets a virtual host such as a CDN select the replacement site.
        if (outbound.Url is { } url)
        {
            var remapping = options.HostRemapping.ResolveTarget(url.Host);
            if (remapping.RewritesAuthority)
            {
                var target = new UriBuilder(url) { Host = remapping.Host }.Uri;
                outbound.Url = target;
                outbound.Headers.Set("Host", target.IsDefaultPort ? target.Host : $"{target.Host}:{target.Port}");
            }
        }

        if (!string.IsNullOrWhiteSpace(options.GlobalUserAgent))
            outbound.Headers.Set("User-Agent", options.GlobalUserAgent);

        // The parser already de-chunked, so length framing is now authoritative.
        if (outbound.Body.Length > 0 || outbound.Headers.Contains("Content-Length"))
            outbound.Headers.Set("Content-Length", outbound.Body.Length.ToString());

        outbound.Headers.Set("Connection", preserveUpgrade ? "Upgrade" : "keep-alive");
        return outbound;
    }

    internal static HttpResponseData BuildInboundResponse(HttpResponseData response, bool clientWantsClose)
    {
        var inbound = response.Clone();

        foreach (var header in HopByHopHeaders)
            inbound.Headers.Remove(header);

        // Body was de-chunked during parsing; re-advertise it with a length.
        var bodyAllowed = inbound.StatusCode is not (204 or 304) && inbound.StatusCode is < 100 or >= 200;
        if (bodyAllowed)
            inbound.Headers.Set("Content-Length", inbound.Body.Length.ToString());

        inbound.Headers.Set("Connection", clientWantsClose ? "close" : "keep-alive");
        return inbound;
    }

    // ---------------------------------------------------------------- utilities

    /// <summary>Flattens an exception chain into one line. TLS failures in particular surface as
    /// <c>AuthenticationException("Authentication failed, see inner exception.")</c>, where every
    /// bit of diagnostic value -- the SChannel error, the alert the peer sent -- lives in the
    /// inner exception the top-level message is pointing at.</summary>
    internal static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (sb.Length > 0) sb.Append(" -> ");
            sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
        }
        return sb.ToString();
    }

    private static Uri? BuildTunnelUrl(HttpRequestData request, string connectHost, int connectPort)
    {
        if (request.RequestTarget.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(request.RequestTarget, UriKind.Absolute, out var absolute))
            return absolute;

        // Host header wins over the CONNECT authority when both are present.
        var authority = request.Headers["Host"];
        if (string.IsNullOrEmpty(authority))
            authority = connectPort == 443 ? connectHost : $"{connectHost}:{connectPort}";

        var target = request.RequestTarget.StartsWith('/') ? request.RequestTarget : "/" + request.RequestTarget;
        return Uri.TryCreate($"https://{authority}{target}", UriKind.Absolute, out var url) ? url : null;
    }

    internal static (string Host, int Port) SplitAuthority(string authority, int defaultPort)
    {
        if (authority.StartsWith('['))
        {
            // IPv6 literal: [::1]:443
            var close = authority.IndexOf(']');
            if (close > 0)
            {
                var address = authority[1..close];
                var rest = authority[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p6)) return (address, p6);
                return (address, defaultPort);
            }
        }

        var colon = authority.LastIndexOf(':');
        if (colon > 0 && int.TryParse(authority[(colon + 1)..], out var port))
            return (authority[..colon], port);

        return (authority, defaultPort);
    }

    private static Task WriteAsciiAsync(Stream stream, string text, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).AsTask();

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
