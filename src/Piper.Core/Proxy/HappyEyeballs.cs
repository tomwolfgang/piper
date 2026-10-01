using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;

namespace Piper.Core.Proxy;

/// <summary>
/// Connects to a host name the way RFC 8305 ("Happy Eyeballs") describes: the addresses are tried
/// alternating between IP families, a new attempt is started when the previous one fails or has had
/// <see cref="ProxyOptions.ConnectionAttemptDelay"/> without finishing, and the first connection to
/// succeed wins.
/// </summary>
/// <remarks>
/// <c>TcpClient.ConnectAsync(host, port)</c> tries the addresses one after another, each until the
/// operating system gives up on it. A name that resolves to an IPv6 address on a path that silently
/// drops packets, followed by a working IPv4 address, therefore spent the whole connect timeout on
/// the first and failed before the second was ever tried. The number of attempts is bounded by
/// <see cref="MaxAttempts"/> whatever the resolver returns, and the whole connect (name resolution
/// included) by <see cref="ProxyOptions.ConnectTimeout"/>.
/// </remarks>
internal static class HappyEyeballs
{
    /// <summary>The most addresses of one name that are tried. A resolver answer is not trusted to be short.</summary>
    internal const int MaxAttempts = 6;

    /// <summary>Resolves <paramref name="host"/> and returns the first connection that is established.</summary>
    /// <exception cref="IOException">
    /// The connect timed out or every attempt failed. The message names the host and port and, for a
    /// timeout, how long was waited; the inner exception is the most informative attempt's error.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static Task<Socket> ConnectAsync(string host, int port, ProxyOptions options, CancellationToken ct) =>
        ConnectAsync(host, port, options.ConnectTimeout, options.ConnectionAttemptDelay,
            ResolveAsync, ConnectOneAsync, ct);

    internal static async Task<Socket> ConnectAsync(
        string host, int port, TimeSpan timeout, TimeSpan attemptDelay,
        Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<IPAddress, int, CancellationToken, Task<Socket>> connectOne,
        CancellationToken ct)
    {
        // Scoped to this call: the timer is gone as soon as the connection is made or has failed.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeout);
        try
        {
            // A literal needs no lookup, and must not get one: the resolver would send it to a DNS server.
            var resolved = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await resolve(host, budget.Token).ConfigureAwait(false);

            return await RaceAsync(Order(resolved), (address, token) => connectOne(address, port, token),
                attemptDelay, MaxAttempts, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new IOException($"Timed out after {timeout.TotalSeconds:0.#}s connecting to {host}:{port}.");
        }
        catch (SocketException ex)
        {
            // A refusal, an unreachable network, an unknown name. Named, so that the log line says
            // which target and does not rely on the socket's own text.
            throw new IOException(
                $"Could not connect to {host}:{port} ({ex.SocketErrorCode}).", ex);
        }
    }

    /// <summary>
    /// Wraps a connected socket in the <see cref="TcpClient"/> the rest of the proxy holds a
    /// connection as. A new <c>TcpClient</c> creates a socket of its own, which assigning
    /// <see cref="TcpClient.Client"/> does not dispose, so it is released first.
    /// </summary>
    internal static TcpClient Adopt(Socket connected)
    {
        var client = new TcpClient();
        client.Client.Dispose();
        client.Client = connected;
        return client;
    }

    private static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct)
    {
        var resolved = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return Usable(resolved);
    }

    /// <summary>Drops the addresses of a family this machine cannot use.</summary>
    internal static IPAddress[] Usable(IPAddress[] resolved)
    {
        var usable = resolved.Where(a => a.AddressFamily == AddressFamily.InterNetwork
                                         || (a.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6))
            .ToArray();

        // The name exists but every address it has is one this machine cannot use (an IPv6-only name
        // on a machine with no IPv6): say so, rather than reporting an unknown host.
        if (usable.Length == 0 && resolved.Length > 0)
            throw new SocketException((int)SocketError.AddressFamilyNotSupported);
        return usable;
    }

    /// <summary>
    /// Alternates the families, starting with the family the resolver listed first (it has already
    /// applied the system's address-selection policy), and drops duplicates. Within a family the
    /// resolver's order is kept.
    /// </summary>
    internal static IReadOnlyList<IPAddress> Order(IEnumerable<IPAddress> resolved)
    {
        var distinct = resolved.Distinct().ToList();
        if (distinct.Count == 0) return distinct;

        var preferred = distinct[0].AddressFamily;
        var same = distinct.Where(a => a.AddressFamily == preferred).ToList();
        var other = distinct.Where(a => a.AddressFamily != preferred).ToList();

        var ordered = new List<IPAddress>(distinct.Count);
        for (var i = 0; i < Math.Max(same.Count, other.Count); i++)
        {
            if (i < same.Count) ordered.Add(same[i]);
            if (i < other.Count) ordered.Add(other[i]);
        }
        return ordered;
    }

    private static async Task<Socket> ConnectOneAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="attempt"/> for the addresses in order, no more than
    /// <paramref name="maxAttempts"/> of them, with a new one starting when the last started has
    /// failed or has been running for <paramref name="attemptDelay"/>. Returns the first to succeed;
    /// every other attempt is cancelled, and any that still succeeds is disposed. When all fail, the
    /// most informative failure is rethrown (see <see cref="Informativeness"/>).
    /// </summary>
    internal static async Task<T> RaceAsync<T>(
        IReadOnlyList<IPAddress> addresses, Func<IPAddress, CancellationToken, Task<T>> attempt,
        TimeSpan attemptDelay, int maxAttempts, CancellationToken ct) where T : class, IDisposable
    {
        var total = Math.Min(addresses.Count, Math.Max(1, maxAttempts));
        if (total == 0) throw new SocketException((int)SocketError.HostNotFound);

        using var attempts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var running = new List<Task<T>>(total);
        Exception? best = null;
        var started = 0;

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                if (started < total) running.Add(Start(addresses[started++], attempt, attempts.Token));

                // The next attempt goes when this one has had its delay, or sooner if it fails.
                using var pause = CancellationTokenSource.CreateLinkedTokenSource(attempts.Token);
                var timer = started < total ? Task.Delay(attemptDelay, pause.Token) : null;

                while (running.Count > 0)
                {
                    var waitingOn = new List<Task>(running.Count + 1);
                    waitingOn.AddRange(running);
                    if (timer is not null) waitingOn.Add(timer);
                    var finished = await Task.WhenAny(waitingOn).ConfigureAwait(false);

                    if (ReferenceEquals(finished, timer)) break;

                    var done = (Task<T>)finished;
                    running.Remove(done);
                    if (done.IsCompletedSuccessfully)
                    {
                        await pause.CancelAsync().ConfigureAwait(false); // the timer is not left running
                        return done.Result;
                    }

                    var failure = done.IsCanceled
                        ? new OperationCanceledException(attempts.Token)
                        : done.Exception!.GetBaseException();
                    if (best is null || Informativeness(failure) >= Informativeness(best)) best = failure;
                    if (started < total) break;
                }

                await pause.CancelAsync().ConfigureAwait(false);
                if (started >= total && running.Count == 0) break;
            }

            ct.ThrowIfCancellationRequested();
            ExceptionDispatchInfo.Capture(best ?? new SocketException((int)SocketError.HostNotFound)).Throw();
            throw new InvalidOperationException("unreachable");
        }
        finally
        {
            await attempts.CancelAsync().ConfigureAwait(false);
            foreach (var loser in running) _ = ReleaseAsync(loser);
        }
    }

    /// <summary>
    /// How much a failure says about why a name could not be reached, so that the one reported is not
    /// simply the last to arrive: a timeout (the path exists and swallows packets) says more than a
    /// refusal or reset, which says more than "unreachable" from an address family this path cannot
    /// use. On a tie the later failure wins.
    /// </summary>
    internal static int Informativeness(Exception failure) => failure switch
    {
        SocketException { SocketErrorCode: SocketError.TimedOut } => 3,
        SocketException
        {
            SocketErrorCode: SocketError.NetworkUnreachable or SocketError.HostUnreachable
                or SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable,
        } => 1,
        _ => 2,
    };

    private static Task<T> Start<T>(
        IPAddress address, Func<IPAddress, CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        try { return attempt(address, ct); }
        catch (Exception ex) { return Task.FromException<T>(ex); }
    }

    /// <summary>Disposes what a losing attempt produced, if it produced anything, once it finishes.</summary>
    private static async Task ReleaseAsync<T>(Task<T> loser) where T : class, IDisposable
    {
        try { (await loser.ConfigureAwait(false)).Dispose(); }
        catch (Exception)
        {
            // It lost, so whatever it ended in (cancelled by the winner, failed on its own, anything
            // a connect delegate throws) is discarded on purpose: there is no result to dispose and
            // nobody waiting for the error. Awaiting it here is what keeps the exception observed.
        }
    }
}
