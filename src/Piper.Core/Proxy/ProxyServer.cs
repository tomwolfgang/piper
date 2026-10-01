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

/// <summary>Why <see cref="ProxyServer.Start"/> could not listen, as far as the bind error says.</summary>
public enum ProxyStartFailure
{
    /// <summary>Anything that is not one of the bind errors below.</summary>
    Other,

    /// <summary>Another listener already holds the port - typically a second Piper.</summary>
    PortInUse,

    /// <summary>Windows refused the port: a reserved or excluded range, or an exclusive bind.</summary>
    PortDenied,
}

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

    internal Http3.AltSvcCache AltSvc => _altSvc; // for tests

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

        _cts?.Dispose(); // left over from a run that ended by failing rather than by StopAsync
        _cts = new CancellationTokenSource();
        _listener = new TcpListener(_options.ListenAddress, _options.Port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        _listener.Start(512);

        Endpoint = (IPEndPoint)_listener.LocalEndpoint;
        _options.ListeningEndpoint = Endpoint;
        IsRunning = true;

        // One gate per run: connections still ending after a Stop give their slot back to the gate
        // they took it from, not to the next run's.
        var gate = new SemaphoreSlim(_options.MaxConcurrentConnections, _options.MaxConcurrentConnections);
        var token = _cts.Token;
        var listener = _listener;
        _acceptLoop = Task.Run(() => RunAcceptLoopAsync(gate, ct => listener.AcceptTcpClientAsync(ct), listener.Pending, token));

        Log?.Invoke(this, $"Listening on {Endpoint}. HTTPS decryption {(_options.DecryptHttps ? "enabled" : "disabled")}.");
    }

    /// <summary>
    /// Why a <see cref="Start"/> failure happened, from the first bind error among the first 16
    /// exceptions reachable through inner exceptions - every entry of an
    /// <see cref="AggregateException"/>, not just its first. Kept here so the UI never has to reason
    /// about socket semantics.
    /// </summary>
    public static ProxyStartFailure ClassifyStartFailure(Exception exception)
    {
        // Bounded by exceptions visited rather than depth: a tree is caller-built and nothing stops
        // it being arbitrarily deep or wide. Past the bound the answer degrades to Other.
        var pending = new Queue<Exception>();
        pending.Enqueue(exception);
        for (var visited = 0; visited < 16 && pending.TryDequeue(out var current); visited++)
        {
            switch (current)
            {
                case SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse }:
                    return ProxyStartFailure.PortInUse;

                // What Windows reports for a port inside a reserved or excluded range (Hyper-V and
                // WSL reserve blocks that often cover proxy ports), or one another process holds
                // with an exclusive bind. A different fix from a busy port, so counted apart.
                case SocketException { SocketErrorCode: SocketError.AccessDenied }:
                    return ProxyStartFailure.PortDenied;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions) pending.Enqueue(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Enqueue(inner);
            }
        }

        return ProxyStartFailure.Other;
    }

    public async Task StopAsync()
    {
        // Not "if not running": a run that ended by failing has still to be cleaned up.
        if (_cts is null) return;
        IsRunning = false;
        _options.ListeningEndpoint = null;

        await _cts.CancelAsync().ConfigureAwait(false);
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

    /// <summary>
    /// Runs the accept loop, and if it fails in a way nothing in it expects, says so and stops
    /// claiming to be running: a proxy that has silently stopped accepting is far worse than one that
    /// reports it.
    /// </summary>
    internal async Task RunAcceptLoopAsync(
        SemaphoreSlim gate, Func<CancellationToken, ValueTask<TcpClient>> accept, Func<bool> hasPending, CancellationToken ct)
    {
        try
        {
            await AcceptLoopAsync(gate, accept, hasPending, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            IsRunning = false;
            Log?.Invoke(this, $"Accept loop failed; the proxy has stopped accepting connections: {ex.GetType().Name}: {ex.Message}");
            try { _listener?.Stop(); } catch (SocketException) { /* already closed */ }
        }
    }

    /// <summary>Connections closed to make room because the connection limit was reached.</summary>
    public long EvictedIdleConnections => Interlocked.Read(ref _evictedIdleConnections);

    /// <summary>How many times the connection limit has been reached (each stretch of time at the limit counts once).</summary>
    public long SaturationEpisodes => Interlocked.Read(ref _saturationEpisodes);

    private long _evictedIdleConnections;
    private long _saturationEpisodes;
    private bool _saturated; // touched only by the accept loop

    /// <summary>How often a full gate looks for a waiting client and an idle connection to close for it.</summary>
    private static readonly TimeSpan EvictionPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly Lock _connectionsLock = new();
    private readonly HashSet<ConnectionState> _connections = [];

    /// <summary>What the accept loop needs to know about a connection it may have to close: whether it
    /// is idle, since when, and a way to tell it to go.</summary>
    internal sealed class ConnectionState
    {
        private readonly Lock _gate = new();

        // One source per idle period, so that an eviction aimed at one wait can never close the
        // connection's next. Whoever takes it out of this field under the lock (the end of the wait,
        // or an eviction) is its only user from then on, and disposes it.
        private CancellationTokenSource? _evict;
        private long _idleSince;

        /// <summary>Timestamp from which the connection has been waiting for a request, or 0 while busy.</summary>
        public long IdleSince
        {
            get { lock (_gate) return _idleSince; }
        }

        /// <summary>Starts an idle period; the token is cancelled if the connection is evicted during it.</summary>
        public CancellationToken BeginIdle()
        {
            lock (_gate)
            {
                _evict = new CancellationTokenSource();
                _idleSince = Stopwatch.GetTimestamp();
                return _evict.Token;
            }
        }

        public void EndIdle()
        {
            CancellationTokenSource? ended;
            lock (_gate)
            {
                ended = _evict;
                _evict = null;
                _idleSince = 0;
            }

            ended?.Dispose();
        }

        /// <summary>Cancels the current idle period, if there is one. False when the connection is busy.</summary>
        public bool TryEvict()
        {
            CancellationTokenSource? evict;
            lock (_gate)
            {
                evict = _evict;
                _evict = null;
                _idleSince = 0; // so a second pass does not pick it again before it has gone
            }

            if (evict is null) return false;
            evict.Cancel();
            evict.Dispose();
            return true;
        }
    }

    /// <summary>Closes the connection that has been idle longest, if any is idle. Returns whether one was closed.</summary>
    private bool EvictOldestIdle()
    {
        // A candidate can stop being idle between choosing it and closing it; then the next oldest is tried.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            ConnectionState? oldest = null;
            var oldestSince = 0L;
            lock (_connectionsLock)
            {
                foreach (var candidate in _connections)
                {
                    var since = candidate.IdleSince;
                    if (since != 0 && (oldest is null || since < oldestSince))
                    {
                        oldest = candidate;
                        oldestSince = since;
                    }
                }
            }

            if (oldest is null) return false;
            if (!oldest.TryEvict()) continue;

            Interlocked.Increment(ref _evictedIdleConnections);
            return true;
        }

        return false;
    }

    /// <param name="accept">Where the next connection comes from: the listener, or a stand-in in a test.</param>
    /// <param name="hasPending">Whether a client is waiting to be accepted; asked only while the gate is full.</param>
    internal async Task AcceptLoopAsync(
        SemaphoreSlim gate, Func<CancellationToken, ValueTask<TcpClient>> accept, Func<bool> hasPending, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // The slot is taken before the next connection is accepted, not after: a connection
            // over the limit then waits in the operating system's accept queue, which is bounded,
            // instead of becoming a socket and a task of ours.
            if (gate.Wait(0))
            {
                _saturated = false;
            }
            else
            {
                if (!_saturated)
                {
                    _saturated = true;
                    Interlocked.Increment(ref _saturationEpisodes);
                    Log?.Invoke(this, $"Connection limit ({_options.MaxConcurrentConnections}) reached: new clients wait for a "
                                      + "free slot, and the connection idle longest is closed to make room for each.");
                }

                // Waiting is not enough on its own. Silent sockets would hold every slot until their
                // timeout, so each waiting client closes the idle connection that has waited longest.
                try
                {
                    while (!await gate.WaitAsync(EvictionPollInterval, ct).ConfigureAwait(false))
                    {
                        if (hasPending()) EvictOldestIdle();
                    }
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) when (ct.IsCancellationRequested && ex is InvalidOperationException or ObjectDisposedException)
                {
                    break; // the listener was stopped under the poll
                }
            }

            // The slot is this iteration's until a connection takes it over. The one finally gives
            // it back on every other way out, including an exception nothing here expects.
            var handedOver = false;
            var failed = false;
            try
            {
                var client = await accept(ct).ConfigureAwait(false);
                handedOver = true;
                _ = ServeConnectionAsync(client, gate, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            // A Stop that lands before the listener was accepting, which is how a stop racing the
            // start reaches this loop.
            catch (InvalidOperationException) when (ct.IsCancellationRequested) { break; }
            catch (SocketException ex)
            {
                Log?.Invoke(this, $"Accept failed: {ex.Message}");
                failed = true;
            }
            finally
            {
                if (!handedOver) gate.Release();
            }

            if (!failed) continue;

            // A failure that persists (out of handles, say) must not spin this loop and the log.
            try { await Task.Delay(AcceptRetryDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Owns an accepted connection from here to its end: the counter, the socket and the slot taken
    /// for it are all given back whichever way it ends, including a Stop that races its start.
    /// </summary>
    private async Task ServeConnectionAsync(TcpClient client, SemaphoreSlim gate, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeConnections);
        var state = new ConnectionState();
        lock (_connectionsLock) _connections.Add(state);
        try
        {
            // Everything past this point runs on the thread pool rather than on the accept loop:
            // working out which process owns the socket is synchronous and must not hold up accepting.
            await Task.Yield();
            await HandleClientAsync(client, state, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { Log?.Invoke(this, $"Connection error: {ex.Message}"); }
        finally
        {
            try
            {
                lock (_connectionsLock) _connections.Remove(state);
                Interlocked.Decrement(ref _activeConnections);
                client.Dispose();
            }
            finally
            {
                gate.Release();
            }
        }
    }

    // ------------------------------------------------------------ connection loop

    private async Task HandleClientAsync(TcpClient client, ConnectionState state, CancellationToken ct)
    {
        client.NoDelay = true;
        var clientEndpoint = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var processName = ClientProcessLookup.Resolve(client.Client.RemoteEndPoint as IPEndPoint);

        var clientStream = new GuardedClientStream(client.GetStream(), _options.IdleTimeout);
        using var reader = new HttpStreamReader(clientStream);
        using var slot = new ConnectionSlot();

        try
        {
            for (var first = true; !ct.IsCancellationRequested; first = false)
            {
                var request = await ReadRequestAsync(
                    reader, clientStream, state, first, isHttps: false, locate: null, clientEndpoint, processName, ct)
                    .ConfigureAwait(false);
                if (request is null) break;

                if (string.Equals(request.Method, "CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    // CONNECT takes over the connection entirely; it never returns to this loop.
                    await HandleConnectAsync(request, clientStream, client.Client, state, clientEndpoint, processName, ct)
                        .ConfigureAwait(false);
                    return;
                }

                var keepAlive = await HandleRequestAsync(
                    request, clientStream, reader, client.Client, slot, clientEndpoint, processName, isHttps: false, ct)
                    .ConfigureAwait(false);

                if (!keepAlive) break;
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (IOException) { /* peer went away mid-message */ }
        catch (HttpParseException ex) { Log?.Invoke(this, $"Protocol error from {clientEndpoint}: {ex.Message}"); }
    }

    /// <summary>How long a 408 is given to reach a client that has just been cut off. It may well
    /// not be reading, and this must never hold a connection slot.</summary>
    private static readonly TimeSpan TimeoutReplyLimit = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads the next request from a client connection, with a different clock for each stage:
    /// <list type="number">
    /// <item>the wait for its first byte is bounded by <see cref="ProxyOptions.RequestHeadTimeout"/>
    /// for the first request of a connection and by <see cref="ProxyOptions.IdleTimeout"/> between
    /// kept-alive requests, and ends quietly. While it waits the connection is idle, so a full
    /// connection gate may close it (oldest first) to make room;</item>
    /// <item>the request line and headers must then arrive within
    /// <see cref="ProxyOptions.RequestHeadTimeout"/> in total, so a client dripping bytes cannot
    /// hold the connection for ever by never being silent for long;</item>
    /// <item>the body is bounded by silence (<see cref="ProxyOptions.IdleTimeout"/> between reads)
    /// and by a progress floor (<see cref="ProxyOptions.MinRequestBodyBytesPerWindow"/> per window),
    /// so a large upload that keeps flowing is never cut for taking long, and one trickling a byte at
    /// a time is.</item>
    /// </list>
    /// A client cut off at stage 2 or 3 is answered with 408 (best effort: a client that is not
    /// reading, or a TLS stream broken by the read that was cancelled, may never see it); one cut off
    /// at stage 3 also leaves a failed session, because by then there is a request to show.
    /// </summary>
    /// <returns>The request, or null when the connection should be closed.</returns>
    private async Task<HttpRequestData?> ReadRequestAsync(
        HttpStreamReader reader, GuardedClientStream clientStream, ConnectionState state, bool first, bool isHttps,
        Func<HttpRequestData, Uri?>? locate, string clientEndpoint, string processName, CancellationToken ct)
    {
        reader.IdleTimeout = Timeout.InfiniteTimeSpan; // stages 1 and 2 have clocks of their own
        var evicted = state.BeginIdle();
        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct, evicted))
        {
            idle.CancelAfter(first ? _options.RequestHeadTimeout : _options.IdleTimeout);
            try
            {
                if (!await reader.HasMoreDataAsync(idle.Token).ConfigureAwait(false)) return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return null; // idle (or evicted) - close it quietly
            }
            finally
            {
                state.EndIdle();
            }
        }

        HttpRequestData? request;
        using (var head = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            head.CancelAfter(_options.RequestHeadTimeout);
            try
            {
                request = await HttpParser.ReadRequestHeadAsync(reader, head.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Log?.Invoke(this, $"{clientEndpoint} did not finish its request head within "
                                  + $"{_options.RequestHeadTimeout.TotalSeconds:0.#}s; connection closed.");
                await TryReplyAsync(clientStream, RequestTimeout("The request head was not received in time.")).ConfigureAwait(false);
                return null;
            }
        }

        if (request is null) return null;

        reader.IdleTimeout = _options.IdleTimeout;
        clientStream.ArmProgressFloor(_options.IdleTimeout, _options.MinRequestBodyBytesPerWindow);
        try
        {
            request.Body = await HttpParser.ReadBodyAsync(reader, HttpParser.DescribeRequestBody(request.Headers), ct)
                .ConfigureAwait(false);
            clientStream.DisarmProgressFloor();
        }
        catch (HttpStalledException ex)
        {
            clientStream.DisarmProgressFloor();
            var reply = RequestTimeout("The request body stopped arriving.");
            request.Url = locate?.Invoke(request) ?? request.Url;
            _store.Add(new Session
            {
                Request = request,
                Response = reply,
                IsHttps = isHttps,
                ClientEndpoint = clientEndpoint,
                ProcessName = processName,
                State = SessionState.Failed,
                Error = $"The client stopped sending the request body: {ex.Message}",
                Completed = DateTimeOffset.Now,
            });
            await TryReplyAsync(clientStream, reply).ConfigureAwait(false);
            return null;
        }

        return request;
    }

    private static HttpResponseData RequestTimeout(string reason) =>
        HttpResponseData.Simple(408, "Request Timeout", $"Piper closed this connection: {reason}");

    /// <summary>Best-effort answer to a client being cut off. It is going away either way, and it may
    /// not be reading, so a failure to deliver this is not worth reporting.</summary>
    private static async Task TryReplyAsync(Stream stream, HttpResponseData reply)
    {
        using var limit = new CancellationTokenSource(TimeoutReplyLimit);
        try
        {
            await stream.WriteAsync(reply.ToBytes(), limit.Token).ConfigureAwait(false);
            await stream.FlushAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException
                                       or InvalidOperationException)
        {
            // Includes a TLS stream left unusable by the read that was just cancelled.
        }
    }

    // ------------------------------------------------------------------- CONNECT

    private async Task HandleConnectAsync(
        HttpRequestData connect, Stream clientStream, Socket clientSocket, ConnectionState state,
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
        if (!await AuthenticateClientAsync(ssl, connect, host, port, clientEndpoint, processName, ct).ConfigureAwait(false))
            return;

        if (ssl.NegotiatedApplicationProtocol == SslApplicationProtocol.Http2)
        {
            await RunHttp2Async(ssl, clientEndpoint, processName, ct).ConfigureAwait(false);
            return;
        }

        var tunnel = new GuardedClientStream(ssl, _options.IdleTimeout);
        using var tlsReader = new HttpStreamReader(tunnel);
        using var slot = new ConnectionSlot();

        try
        {
            for (var first = true; !ct.IsCancellationRequested; first = false)
            {
                var request = await ReadRequestAsync(
                    tlsReader, tunnel, state, first, isHttps: true, r => BuildTunnelUrl(r, host, port), clientEndpoint, processName, ct)
                    .ConfigureAwait(false);
                if (request is null) break;

                // Inside a tunnel the target is origin-form; rebuild the absolute URL as https.
                request.Url = BuildTunnelUrl(request, host, port);

                var keepAlive = await HandleRequestAsync(
                    request, tunnel, tlsReader, clientSocket, slot, clientEndpoint, processName, isHttps: true, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Completes the TLS handshake of a decrypted tunnel with the client, within
    /// <see cref="ProxyOptions.RequestHeadTimeout"/>: a client that opened the tunnel and then never
    /// sends its ClientHello would otherwise hold the connection, and its slot, for ever. A failure
    /// (typically an untrusted root or a pinned client) is recorded as a failed session so the cause
    /// is visible, and the stream is disposed.
    /// </summary>
    private async Task<bool> AuthenticateClientAsync(
        SslStream ssl, HttpRequestData connect, string host, int port, string clientEndpoint, string processName,
        CancellationToken ct)
    {
        using var handshakeBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshakeBudget.CancelAfter(_options.RequestHeadTimeout);
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
            }, handshakeBudget.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            var timedOut = handshakeBudget.IsCancellationRequested && !ct.IsCancellationRequested;
            var failed = new Session
            {
                Request = connect,
                IsTunnel = true,
                IsHttps = true,
                State = SessionState.Failed,
                ClientEndpoint = clientEndpoint,
                ProcessName = processName,
                ServerEndpoint = $"{host}:{port}",
                Error = timedOut
                    ? $"TLS handshake with client failed: no handshake within {_options.RequestHeadTimeout.TotalSeconds:0.#}s"
                    : $"TLS handshake with client failed: {Describe(ex)}",
                Completed = DateTimeOffset.Now,
            };
            _store.Add(failed);
            await ssl.DisposeAsync().ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Runs one browser-facing HTTP/2 connection. Each stream is forwarded independently
    /// (see <see cref="Http2RequestForwarder"/>) and recorded as its own <see cref="Session"/>,
    /// exactly like an HTTP/1.1 request -- the multiplexing is invisible below this point.</summary>
    private async Task RunHttp2Async(SslStream ssl, string clientEndpoint, string processName, CancellationToken ct)
    {
        var connection = new Http2Connection(ssl,
            (request, streamCt) => Http2RequestForwarder.ForwardAsync(request, _options, _store, _altSvc, clientEndpoint, processName, streamCt))
        {
            Log = message => Log?.Invoke(this, $"Request from {clientEndpoint} refused: {message}"),
        };

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

        Socket? server = null;
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(_options.ConnectTimeout);
                server = await HappyEyeballs.ConnectAsync(
                    _options.HostRemapping.Resolve(host), port, _options, timeout.Token).ConfigureAwait(false);
            }

            await WriteAsciiAsync(clientStream, "HTTP/1.1 200 Connection Established\r\n\r\n", ct).ConfigureAwait(false);

            using var serverStream = new NetworkStream(server, ownsSocket: false);
            await RelayBothWaysAsync(clientStream, clientSocket, serverStream, server, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            session.State = SessionState.Failed;
            session.Error = $"Tunnel to {host}:{port} failed: {Describe(ex)}";
            var refusal = ex is ProxyLoopException ? "508 Loop Detected" : "502 Bad Gateway";
            try { await WriteAsciiAsync(clientStream, $"HTTP/1.1 {refusal}\r\n\r\n", CancellationToken.None).ConfigureAwait(false); }
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
    internal static async Task RelayBothWaysAsync(
        Stream first, Socket? firstSocket, Stream second, Socket? secondSocket, CancellationToken ct)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var forward = PumpAsync(first, second, secondSocket, both.Token);
        var backward = PumpAsync(second, first, firstSocket, both.Token);

        // A direction ending toward a leg that cannot be half-closed (a TLS one) has no way to tell
        // that peer, which would then hold the other direction open for ever. End both instead.
        var ended = await Task.WhenAny(forward, backward).ConfigureAwait(false);
        if ((ended == forward ? secondSocket : firstSocket) is null) await both.CancelAsync().ConfigureAwait(false);

        await Task.WhenAll(forward, backward).ConfigureAwait(false);
    }

    /// <summary>Copies bytes one way, then passes the end of the stream on to the destination.</summary>
    private static async Task PumpAsync(Stream from, Stream to, Socket? toSocket, CancellationToken ct)
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
                    // An upgrade must stay on HTTP/1.1: h2 has no Connection or Upgrade (the adapter
                    // strips them), so an origin that offers h2 would answer a plain response, and
                    // the 101 handing the connection to WebSocket could never come.
                    upstream = await UpstreamConnection.ConnectAsync(
                        host, port, targetIsTls, _options, ct, allowHttp2: !isUpgrade).ConfigureAwait(false);
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
            if (targetIsTls) _altSvc.RecordAltSvc(host, port, response.Headers["Alt-Svc"]); // plain HTTP can be forged by the path

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
                    upgraded.Stream, upgraded.IsTls ? null : upgraded.Client, ct).ConfigureAwait(false);
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
                EnterReceivingBody(session, upstreamResponse.Body);
                _store.NotifyUpdated(session);

                var relayFinished = false;
                try
                {
                    var relayed = await HttpBodyRelay.RelayAsync(
                        upstreamResponse.BodyReader!, upstreamResponse.Body, clientStream,
                        rechunk, _options.MaxCapturedBodyBytes, session.ReportBytesReceived, ct).ConfigureAwait(false);

                    response.Body = relayed.Captured;
                    response.BodyTotalLength = relayed.TotalBytes;
                    session.InvalidateSearchIndex();
                    relayFinished = true;
                }
                finally
                {
                    if (!relayFinished) LeaveReceivingBodyFailed(session, _store);
                }
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
                var failure = ex is ProxyLoopException
                    ? HttpResponseData.Simple(508, "Loop Detected", $"Piper will not forward a request to itself.\r\n\r\n{ex.Message}")
                    : HttpResponseData.Simple(502, "Bad Gateway", $"Piper could not reach {host}:{port}.\r\n\r\n{ex.Message}");
                await clientStream.WriteAsync(failure.ToBytes(), CancellationToken.None).ConfigureAwait(false);
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

    /// <summary>
    /// Marks a session whose head has gone to the client as still receiving its body, when there is
    /// one to relay. A HEAD, 204, 304 or zero-length response has nothing left to arrive and goes
    /// straight to complete, so it never flickers through the state.
    /// </summary>
    internal static void EnterReceivingBody(Session session, HttpBodyDescriptor body)
    {
        if (body.Framing == HttpBodyFraming.None) return;
        if (body is { Framing: HttpBodyFraming.Length, Length: 0 }) return;

        // Before the state: the UI reads the state first and the expected length after it.
        session.ExpectedResponseBytes = body.Framing == HttpBodyFraming.Length ? body.Length : -1;
        session.State = SessionState.ReceivingBody;
    }

    /// <summary>
    /// Ends a relay that stopped without finishing. The callers' catch blocks record the failures
    /// they expect, with the reason; this covers anything else, so a session can never be left
    /// looking as though its body were still arriving.
    /// </summary>
    internal static void LeaveReceivingBodyFailed(Session session, SessionStore store)
    {
        if (session.State != SessionState.ReceivingBody) return;
        session.State = SessionState.Failed;
        session.Completed ??= DateTimeOffset.Now;
        store.NotifyUpdated(session);
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
