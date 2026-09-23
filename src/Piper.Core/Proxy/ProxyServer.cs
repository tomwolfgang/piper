using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
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
                var request = await ReadRequestWithIdleTimeoutAsync(reader, clientStream, ct).ConfigureAwait(false);
                if (request is null) break;

                if (string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    // CONNECT takes over the connection entirely; it never returns to this loop.
                    await HandleConnectAsync(request, clientStream, client.Client, clientEndpoint, processName, ct)
                        .ConfigureAwait(false);
                    return;
                }

                var keepAlive = await HandleRequestAsync(
                    request, clientStream, reader, client.Client, slot, clientEndpoint, processName, isHttps: false, ct)
                    .ConfigureAwait(false);

                if (!keepAlive) break;
            }
        }
        catch (OperationCanceledException) { /* shutting down or idle timeout */ }
        catch (IOException) { /* peer went away mid-message */ }
        catch (HttpParseException ex) { Log?.Invoke(this, $"Protocol error from {clientEndpoint}: {ex.Message}"); }
    }

    /// <remarks>
    /// A request that cannot be parsed is answered with a 400 on <paramref name="clientStream"/>
    /// before the exception is rethrown, and the caller then closes the connection (RFC 9112 6.3
    /// rule 6). Nothing further can be read from it: where the malformed request ends is unknown.
    /// </remarks>
    private async Task<HttpRequestData?> ReadRequestWithIdleTimeoutAsync(
        HttpStreamReader reader, Stream clientStream, CancellationToken ct)
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
        catch (HttpParseException)
        {
            var reply = HttpResponseData.Simple(400, "Bad Request", "Piper could not parse this request.");
            // Shares whatever is left of the read's idle deadline, so a client that never drains its
            // receive window cannot hold the handler open until shutdown. A head that trickled in
            // for nearly the whole window may leave no time and lose the 400; the close still happens.
            try { await clientStream.WriteAsync(reply.ToBytes(), idle.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                /* client gone or stalled; the parse error is still what gets logged */
            }
            throw;
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
            await BlindTunnelAsync(connect, host, port, clientStream, clientSocket, clientEndpoint, processName, ct)
                .ConfigureAwait(false);
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
                var request = await ReadRequestWithIdleTimeoutAsync(tlsReader, ssl, ct).ConfigureAwait(false);
                if (request is null) break;

                // Inside a tunnel the target is origin-form; rebuild the absolute URL as https.
                request.Url = BuildTunnelUrl(request, host, port);

                var keepAlive = await HandleRequestAsync(
                    request, ssl, tlsReader, clientSocket, slot, clientEndpoint, processName, isHttps: true, ct).ConfigureAwait(false);

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
        HttpRequestData connect, string host, int port, Stream clientStream, Socket clientSocket,
        string clientEndpoint, string processName, CancellationToken ct)
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
            await RelayBothWaysAsync(clientStream, clientSocket, serverStream, server.Client, _options.UpstreamIdleTimeout, ct).ConfigureAwait(false);
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

    /// <summary>Hands whatever a reader has buffered but not consumed to the stream now relaying it.</summary>
    private static async Task FlushPendingAsync(HttpStreamReader reader, Stream destination, CancellationToken ct)
    {
        var pending = reader.TakeBuffered();
        if (pending.Length == 0) return;
        await destination.WriteAsync(pending, ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Relays bytes both ways until both directions have finished.</summary>
    /// <remarks>
    /// One direction ending must not end the other. TCP connections close one half at a time, and
    /// a client that has finished sending its request and shut down its send side is still waiting
    /// for the response. Tearing the pair down on the first direction to finish aborted exactly
    /// the transfer the connection existed for, mid-body. Each direction instead passes its close
    /// on, so the peer learns no more data is coming and can finish its own half in its own time.
    /// </remarks>
    /// <param name="idleAfterHalfClose">
    /// Once one direction has ended and its close has been passed on, how long the other may go
    /// without a byte before the pair is ended anyway. Idle rather than absolute, so a download
    /// still flowing after the client half-closed is left alone; only a peer that has gone silent
    /// without closing its own half is cut off.
    /// </param>
    internal static async Task RelayBothWaysAsync(
        Stream first, Socket? firstSocket, Stream second, Socket? secondSocket,
        TimeSpan idleAfterHalfClose, CancellationToken ct)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progress = new StrongBox<long>();
        var forward = PumpAsync(first, second, secondSocket, progress, both.Token);
        var backward = PumpAsync(second, first, firstSocket, progress, both.Token);

        // A direction ending toward a leg that cannot be half-closed (a TLS one) has no way to tell
        // that peer, which would then hold the other direction open for ever. End both instead.
        var ended = await Task.WhenAny(forward, backward).ConfigureAwait(false);
        var remaining = ended == forward ? backward : forward;
        if ((ended == forward ? secondSocket : firstSocket) is null)
        {
            await both.CancelAsync().ConfigureAwait(false);
        }
        else
        {
            var seen = Volatile.Read(ref progress.Value);
            while (await Task.WhenAny(remaining, Task.Delay(idleAfterHalfClose, ct)).ConfigureAwait(false) != remaining)
            {
                var now = Volatile.Read(ref progress.Value);
                if (now == seen || ct.IsCancellationRequested)
                {
                    await both.CancelAsync().ConfigureAwait(false);
                    break;
                }
                seen = now;
            }
        }

        await Task.WhenAll(forward, backward).ConfigureAwait(false);
    }

    /// <summary>Copies bytes one way, then passes the end of the stream on to the destination.</summary>
    private static async Task PumpAsync(Stream from, Stream to, Socket? toSocket, StrongBox<long> progress, CancellationToken ct)
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
                Interlocked.Add(ref progress.Value, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            HalfClose(toSocket);
        }
    }

    /// <summary>
    /// Tells the destination that nothing further is coming from this direction, without disturbing
    /// what it may still be sending back. Best effort: a TLS leg has no half-close to offer, and a
    /// socket already torn down needs none.
    /// </summary>
    private static void HalfClose(Socket? socket)
    {
        if (socket is null) return;
        try { socket.Shutdown(SocketShutdown.Send); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
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
        HttpRequestData request, Stream clientStream, HttpStreamReader clientReader, Socket clientSocket,
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

        // Set just before the first byte of a response is written to the client. Past that point a
        // failure can no longer be answered with a 502: it would land inside the response already
        // in flight -- as body bytes, as a corrupt chunk, or, on a close-delimited body, as content
        // nothing could tell apart from the origin's.
        var responseStarted = false;
        var oneShotUpstream = false;
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
            var overHttp3 = isUpgrade
                ? null
                : await Http3Attempt.TryFetchAsync(outbound, request.Url, _options, _altSvc, MarkSent, ct).ConfigureAwait(false);

            // HTTP/3 and HTTP/2 upstream legs still hand back a message read in full; only the
            // HTTP/1.1 leg leaves its body on the connection to be relayed.
            var upstreamResponse = overHttp3 is not null
                ? new UpstreamResponse(overHttp3, HttpBodyDescriptor.None, IsBuffered: true)
                : default;

            if (overHttp3 is null)
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
                upstreamResponse = await UpstreamRequestSender.SendAsync(upstream, outbound, MarkSent, ct).ConfigureAwait(false);

                // Http2ClientConnection is one-shot, so an h2 upstream can never be pooled. It is let
                // go once the body has been relayed off it, not before.
                oneShotUpstream = upstream.IsHttp2;
            }

            var response = upstreamResponse.Head;

            // Genuinely the time to the first byte now. While the whole message was read before
            // returning, this measured the time to the last one, so the column reported how long
            // each download was rather than how responsive the origin was.
            session.TimeToFirstByte = stopwatch.Elapsed - beforeResponse;
            _altSvc.RecordAltSvc(host, response.Headers["Alt-Svc"]);

            session.Response = response;
            session.InvalidateSearchIndex();

            // 101 hands the connection over to another protocol (WebSocket, h2c). Relay
            // the switch and then pump raw bytes; there is no more HTTP to parse. Only reachable
            // on the TCP path -- upgrades are never attempted over h3, so the slot holds the
            // connection the 101 arrived on.
            if (response.StatusCode == 101 && slot.Connection is { } upgraded)
            {
                session.State = SessionState.Complete;
                session.Completed = DateTimeOffset.Now;
                responseStarted = true;
                await clientStream.WriteAsync(response.ToBytes(), ct).ConfigureAwait(false);
                await clientStream.FlushAsync(ct).ConfigureAwait(false);
                _store.NotifyUpdated(session);

                // Both sides are read through a buffering reader, so bytes of the new protocol may
                // already have been pulled off a socket while the 101 exchange was being parsed.
                // PumpAsync reads the raw streams and would never see them, so hand them over
                // first -- otherwise a WebSocket loses whichever frames arrived early.
                await FlushPendingAsync(upgraded.Reader, clientStream, ct).ConfigureAwait(false);
                await FlushPendingAsync(clientReader, upgraded.Stream, ct).ConfigureAwait(false);

                // Sockets are passed only for plaintext legs. Half-closing the TCP socket under a
                // live TLS session would send a FIN with no close_notify, which a peer is entitled
                // to read as a truncation attack; those legs end naturally instead.
                await RelayBothWaysAsync(
                    clientStream, isHttps ? null : clientSocket,
                    upgraded.Stream, upgraded.IsTls ? null : upgraded.Client.Client, _options.UpstreamIdleTimeout, ct).ConfigureAwait(false);
                return false;
            }

            var canHaveBody = HttpParser.ResponseCanHaveBody(request.Method, response.StatusCode);
            var serverWantsClose = response.Headers.HasToken("Connection", "close")
                                   || response.HttpVersion == "HTTP/1.0";

            // A body still to be read is delimited by the connection closing only when nothing
            // else frames it, and then neither leg can carry anything after it.
            var closeAfterBody = !upstreamResponse.IsBuffered
                                 && upstreamResponse.Body.Framing == HttpBodyFraming.UntilClose;

            // HTTP/1.0 has no chunked coding (RFC 9112 7.1), so a chunked body goes to such a client
            // de-chunked and delimited by the close instead.
            var dechunkForClient = !upstreamResponse.IsBuffered
                                   && upstreamResponse.Body.Framing is HttpBodyFraming.Chunked or HttpBodyFraming.StreamEnd
                                   && request.HttpVersion == "HTTP/1.0";
            var clientCloseAfterBody = closeAfterBody || dechunkForClient;

            var inbound = BuildInboundResponse(
                response, upstreamResponse.IsBuffered && canHaveBody,
                clientWantsClose || clientCloseAfterBody);

            // This clone's HttpVersion is only ever used for the literal wire bytes about to go
            // out on *this* h1.1 connection -- it must say "HTTP/1.1" no matter what the upstream
            // leg actually spoke (h2, or a legacy 1.0 origin). session.Response above still holds
            // the original, untouched `response`, so the captured/displayed HttpVersion keeps
            // recording the real upstream protocol.
            inbound.HttpVersion = "HTTP/1.1";

            var rechunk = false;
            if (!upstreamResponse.IsBuffered)
            {
                // Framing is carried over from the origin rather than recomputed, because there is
                // no buffered body left to recompute it from -- and because a client that reports
                // progress from Content-Length shows a frozen bar for the whole transfer if the
                // length is dropped, which looks exactly like the hang being fixed here.
                // A Content-Length is only the framing when nothing overrides it. Beside chunked it
                // does not describe the bytes relayed, and the next hop may frame on either one, so
                // RFC 9112 6.1 has a proxy drop it.
                if (upstreamResponse.Body.Framing is HttpBodyFraming.Chunked or HttpBodyFraming.UntilClose or HttpBodyFraming.StreamEnd)
                    inbound.Headers.Remove("Content-Length");

                // A body with no length that does not end the connection -- chunked, or an HTTP/2
                // stream -- has to be chunked for an HTTP/1.1 client to find its end.
                rechunk = upstreamResponse.Body.Framing is HttpBodyFraming.Chunked or HttpBodyFraming.StreamEnd
                          && !dechunkForClient;
                if (rechunk) inbound.Headers.Set("Transfer-Encoding", "chunked");
            }

            responseStarted = true;
            if (upstreamResponse.IsBuffered)
            {
                await clientStream.WriteAsync(inbound.ToBytes(), ct).ConfigureAwait(false);
                await clientStream.FlushAsync(ct).ConfigureAwait(false);

                // Forwarded whole, but kept only as far as a relayed body would be.
                response.KeepPrefix(_options.MaxCapturedBodyBytes);
                session.InvalidateSearchIndex();
            }
            else
            {
                // Head first, then the body as it arrives. This is the whole point: the client can
                // start writing the file to disk while the origin is still sending it.
                await clientStream.WriteAsync(Encoding.Latin1.GetBytes(inbound.HeadAsText()), ct).ConfigureAwait(false);
                await clientStream.FlushAsync(ct).ConfigureAwait(false);
                _store.NotifyUpdated(session);

                var relayed = await HttpBodyRelay.RelayAsync(
                    upstreamResponse.BodyReader!, upstreamResponse.Body, clientStream,
                    rechunk, _options.MaxCapturedBodyBytes, ct).ConfigureAwait(false);

                response.Body = relayed.Captured;
                response.BodyTotalLength = relayed.TotalBytes;
                session.InvalidateSearchIndex();
            }

            session.State = SessionState.Complete;
            session.Completed = DateTimeOffset.Now;
            _store.NotifyUpdated(session);

            if (serverWantsClose || closeAfterBody || oneShotUpstream) slot.Reset();

            return !clientWantsClose && !serverWantsClose && !clientCloseAfterBody;
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                       or HttpParseException or Http2ProtocolException or OperationCanceledException)
        {
            session.State = SessionState.Failed;
            session.Error = Describe(ex);
            session.Completed = DateTimeOffset.Now;
            _store.NotifyUpdated(session);

            slot.Reset();

            // A reset rather than a FIN, so the client sees the transfer fail. An orderly close
            // would pass a truncated close-delimited body off as a complete one.
            if (responseStarted)
            {
                AbortConnection(clientSocket);
                return false;
            }

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

        var inbound = BuildInboundResponse(canned, bodyIsAuthoritative: true, clientWantsClose);
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

    /// <param name="bodyIsAuthoritative">
    /// True when <c>response.Body</c> really is this response's body, so its length can be
    /// advertised downstream. False when the response is bodiless and its framing headers instead
    /// describe the body some other request would have received -- a HEAD reply, a 204 or a 304.
    /// Those headers are the origin's answer and must reach the client untouched: rewriting a HEAD
    /// reply's Content-Length to 0 tells a client sizing a resource before fetching it that the
    /// resource is empty.
    /// </param>
    internal static HttpResponseData BuildInboundResponse(
        HttpResponseData response, bool bodyIsAuthoritative, bool clientWantsClose)
    {
        var inbound = response.Clone();

        foreach (var header in HopByHopHeaders)
            inbound.Headers.Remove(header);

        // Body was de-chunked during parsing; re-advertise it with a length.
        if (bodyIsAuthoritative)
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
