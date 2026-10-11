using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;

namespace Piper.Bench;

/// <summary>
/// A TCP relay on loopback that delays every byte by <c>oneWay</c> in each direction, so a connection
/// through it behaves as if the far end were 2 x <c>oneWay</c> away. The proxy dials the relay instead
/// of the origin, which is what a download from a distant server looks like to it. Each direction
/// holds at most 256 chunks of 64 KB, so a slow reader backs up into TCP flow control rather than into
/// memory. While it exists the driver asks Windows for a 1 ms timer (the default is 15.6 ms, too coarse
/// for a 25 ms delay); that is a property of the driver process only and is undone on disposal.
/// </summary>
internal sealed class LatencyRelay : IAsyncDisposable
{
    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint milliseconds);

    private const int ChunkBytes = 64 * 1024;
    private const int MaxChunks = 256;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly int _targetPort;
    private readonly long _delayTicks;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;

    public LatencyRelay(int targetPort, TimeSpan oneWay)
    {
        _targetPort = targetPort;
        _delayTicks = (long)(oneWay.TotalSeconds * Stopwatch.Frequency);
        timeBeginPeriod(1);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accepting = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            _ = Task.Run(() => RelayAsync(client), CancellationToken.None);
        }
    }

    private async Task RelayAsync(TcpClient client)
    {
        using var near = client;
        using var far = new TcpClient { NoDelay = true };
        near.NoDelay = true;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        try
        {
            await far.ConnectAsync(IPAddress.Loopback, _targetPort, cts.Token).ConfigureAwait(false);
            var up = PumpAsync(near.Client, far.Client, cts.Token);
            var down = PumpAsync(far.Client, near.Client, cts.Token);
            await Task.WhenAny(up, down).ConfigureAwait(false); // one side closing ends the connection
            await cts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(up, down).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException or ChannelClosedException)
        {
            // Either end went away: the connection is over.
        }
    }

    private async Task PumpAsync(Socket from, Socket to, CancellationToken ct)
    {
        var queue = Channel.CreateBounded<(long At, byte[] Data, int Length)>(new BoundedChannelOptions(MaxChunks) { SingleReader = true, SingleWriter = true });
        var writer = Task.Run(async () =>
        {
            try
            {
                await foreach (var (at, data, length) in queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try
                    {
                        var wait = at + _delayTicks - Stopwatch.GetTimestamp();
                        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds((double)wait / Stopwatch.Frequency), ct).ConfigureAwait(false);
                        await to.SendAsync(data.AsMemory(0, length), SocketFlags.None, ct).ConfigureAwait(false);
                    }
                    finally { ArrayPool<byte>.Shared.Return(data); }
                }
                to.Shutdown(SocketShutdown.Send); // pass the end of the stream on, after the data
            }
            catch (Exception ex)
            {
                queue.Writer.TryComplete(ex); // the reader must not wait on a queue nobody drains
                throw;
            }
        }, CancellationToken.None);

        try
        {
            while (true)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(ChunkBytes);
                int read;
                try { read = await from.ReceiveAsync(buffer.AsMemory(0, ChunkBytes), SocketFlags.None, ct).ConfigureAwait(false); }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    throw;
                }
                if (read == 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    break;
                }
                await queue.Writer.WriteAsync((Stopwatch.GetTimestamp(), buffer, read), ct).ConfigureAwait(false);
            }
        }
        finally { queue.Writer.TryComplete(); }
        await writer.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        await _accepting.ConfigureAwait(false);
        _stop.Dispose();
        timeEndPeriod(1);
    }
}
