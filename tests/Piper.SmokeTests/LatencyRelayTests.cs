using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Piper.Bench;

// The delaying relay behind the benchmark's rtt50_* scenarios (the benchmark runs are not tests).
internal static class LatencyRelayTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("bench latency relay: delay, order, half-close and disposal", async () =>
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = cancel.Token;
        // An echo server, and one that answers with the byte count once its client has finished sending.
        var echo = Serve(async s => { var b = new byte[4096]; int n; while ((n = await s.ReceiveAsync(b, ct)) > 0) await s.SendAsync(b.AsMemory(0, n), ct); }, ct);
        var counter = Serve(async s =>
        {
            long total = 0;
            var b = new byte[65536];
            int n;
            while ((n = await s.ReceiveAsync(b, ct)) > 0) total += n;
            await s.SendAsync(BitConverter.GetBytes(total), ct);
        }, ct);

        await using (var relay = new LatencyRelay(echo, TimeSpan.FromMilliseconds(20)))
        {
            using var client = await ConnectAsync(relay.Port, ct);
            var clock = Stopwatch.StartNew();
            await client.SendAsync(new byte[1], ct);
            await client.ReceiveAsync(new byte[1], ct);
            runner.IsTrue(clock.ElapsedMilliseconds >= 36, $"a byte comes back no sooner than twice the one-way delay ({clock.ElapsedMilliseconds} ms for 2 x 20)");
            var sent = Enumerable.Range(0, 5000).SelectMany(i => BitConverter.GetBytes(i)).ToArray();
            await client.SendAsync(sent, ct);
            var echoed = new byte[sent.Length];
            for (var got = 0; got < echoed.Length;) got += await client.ReceiveAsync(echoed.AsMemory(got), ct);
            runner.IsTrue(sent.AsSpan().SequenceEqual(echoed), "bytes come back in the order they were sent");
        }

        await using (var relay = new LatencyRelay(counter, TimeSpan.FromMilliseconds(5)))
        {
            // 20 MB, then the client's FIN: the reply to it must still arrive (it used to be lost: 0 bytes).
            using var client = await ConnectAsync(relay.Port, ct);
            for (var i = 0; i < 320; i++) await client.SendAsync(new byte[64 * 1024], ct);
            client.Shutdown(SocketShutdown.Send);
            var reply = new byte[8];
            for (int got = 0, n = 1; got < 8 && n > 0; got += n) n = await client.ReceiveAsync(reply.AsMemory(got), ct);
            runner.AreEqual(320L * 64 * 1024, BitConverter.ToInt64(reply), "20 MB through the relay, and the answer after a half-close arrives");
        }

        Socket idle;
        await using (var relay = new LatencyRelay(echo, TimeSpan.FromMilliseconds(5)))
        {
            idle = await ConnectAsync(relay.Port, ct);
            await idle.SendAsync(new byte[1], ct); // once it comes back the relay has accepted the connection
            await idle.ReceiveAsync(new byte[1], ct);
        }
        using (idle) // disposing the relay with a connection open ends it and does not hang
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(5));
            var ended = false;
            try { ended = await idle.ReceiveAsync(new byte[1], limit.Token) == 0; }
            catch (SocketException) { ended = true; }
            catch (OperationCanceledException) { /* still open after 5 s */ }
            runner.IsTrue(ended, "disposing the relay ends an open connection");
        }
    });

    private static async Task<Socket> ConnectAsync(int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(IPAddress.Loopback, port, ct);
        return socket;
    }

    // A loopback server running handle for each connection until the token ends; returns its port.
    private static int Serve(Func<Socket, Task> handle, CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        ct.Register(listener.Stop);
        _ = Task.Run(async () =>
        {
            while (true)
            {
                Socket socket;
                try { socket = await listener.AcceptSocketAsync(ct); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
                _ = Task.Run(async () =>
                {
                    using (socket)
                        try { await handle(socket); }
                        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException) { /* the test ended */ }
                });
            }
        }, CancellationToken.None);
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
