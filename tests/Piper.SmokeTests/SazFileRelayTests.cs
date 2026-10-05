using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Piper.App;

internal static class SazFileRelayTests
{
    // Each test listens on its own pipe, so a leftover listener can never answer another test.
    private static string NewPipeName() => $"Piper.SmokeTests.{Guid.NewGuid():N}";

    private static async Task<bool> SendAsync(string pipeName, byte[] payload, int connectTimeoutMs = 5000)
    {
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(connectTimeoutMs);
            await client.WriteAsync(payload);
            await client.FlushAsync();
            return true;
        }
        catch (IOException) { return false; }
        catch (TimeoutException) { return false; }
    }

    private static string NewSaz()
    {
        var saz = Path.Combine(Path.GetTempPath(), $"piper-relay-{Guid.NewGuid():N}.saz");
        File.WriteAllBytes(saz, [0]);
        return saz;
    }

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("a relayed open request reaches the running window, and non-saz paths are dropped", async () =>
        {
            var saz = NewSaz();
            try
            {
                var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pipe = NewPipeName();
                using var relay = new SazFileRelay(files => received.TrySetResult(files), pipe);

                var sent = await SendAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new[] { saz, "C:\\nope\\notes.txt", saz + ".missing" }));
                runner.IsTrue(sent, "the client could hand the request over");
                var winner = await Task.WhenAny(received.Task, Task.Delay(5000));
                runner.IsTrue(winner == received.Task, "the listener passed the request on");
                if (winner == received.Task)
                    runner.AreEqual(saz, string.Join("|", received.Task.Result), "only the existing .saz survives");
            }
            finally { File.Delete(saz); }
        });

        await runner.RunAsync("an oversize pipe message is rejected, and the listener keeps serving", async () =>
        {
            var saz = NewSaz();
            try
            {
                var batches = new List<string>();
                var pipe = NewPipeName();
                using var relay = new SazFileRelay(files => { lock (batches) batches.Add(string.Join("|", files)); }, pipe);

                // A valid message that carries a real file, padded past the limit: if the read were
                // unbounded the file would be handed on.
                var oversize = JsonSerializer.SerializeToUtf8Bytes(new[] { saz, new string('x', SazFileRelay.MaxPayloadBytes) });
                runner.IsTrue(oversize.Length > SazFileRelay.MaxPayloadBytes, "the message is over the limit");
                await SendAsync(pipe, oversize);

                // A message exactly at the limit is still honoured.
                var atLimit = JsonSerializer.SerializeToUtf8Bytes(new[] { saz, new string('x', SazFileRelay.MaxPayloadBytes - JsonSerializer.SerializeToUtf8Bytes(new[] { saz, "" }).Length) });
                runner.AreEqual(SazFileRelay.MaxPayloadBytes, atLimit.Length, "the second message is exactly at the limit");
                runner.IsTrue(await SendAsync(pipe, atLimit), "the listener accepted another client after the oversize one");
                runner.IsTrue(await Poll.UntilAsync(() => { lock (batches) return batches.Count > 0; }, 5000), "the at-limit message arrived");
                await Task.Delay(100);
                lock (batches)
                {
                    runner.AreEqual(1, batches.Count, "only one message was delivered");
                    runner.AreEqual(saz, batches.FirstOrDefault(), "and it was the at-limit one, not the oversize one");
                }
            }
            finally { File.Delete(saz); }
        });

        await runner.RunAsync("a client that connects and goes quiet does not hold the listener", async () =>
        {
            var saz = NewSaz();
            try
            {
                var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pipe = NewPipeName();
                using var relay = new SazFileRelay(files => received.TrySetResult(files), pipe, TimeSpan.FromMilliseconds(300));

                await using var idle = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.Asynchronous);
                await idle.ConnectAsync(2000);
                // Never writes and never closes. The read deadline has to release the instance.
                runner.IsTrue(await SendAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(new[] { saz })),
                    "a second client got through once the idle one timed out");
                runner.IsTrue(await Task.WhenAny(received.Task, Task.Delay(5000)) == received.Task, "and its request was delivered");
            }
            finally { File.Delete(saz); }
        });

        await runner.RunAsync("disposing the relay waits for its listener and leaves nothing unobserved", async () =>
        {
            var unobserved = new List<Exception>();
            void Collect(object? sender, UnobservedTaskExceptionEventArgs e) { lock (unobserved) unobserved.AddRange(e.Exception.InnerExceptions); }
            TaskScheduler.UnobservedTaskException += Collect;
            try
            {
                var listenerStillRunning = 0;
                for (var i = 0; i < 100; i++)
                {
                    var pipe = NewPipeName();
                    var relay = new SazFileRelay(_ => { }, pipe);
                    // Half the rounds have a client arriving while the relay goes away.
                    var client = i % 2 == 0 ? SendAsync(pipe, Encoding.UTF8.GetBytes("[]"), 25) : Task.FromResult(true);
                    relay.Dispose();
                    if (!relay.Listener.IsCompleted) listenerStillRunning++;
                    await client;
                }
                runner.AreEqual(0, listenerStillRunning, "Dispose returned only once the listener had stopped");

                await Task.Delay(100);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                lock (unobserved)
                    runner.AreEqual(0, unobserved.Count(e => e is ObjectDisposedException or IOException),
                        "no listener failure went unobserved");
            }
            finally { TaskScheduler.UnobservedTaskException -= Collect; }
        });

        await runner.RunAsync("the pipe is gone once the relay is disposed", async () =>
        {
            var pipe = NewPipeName();
            new SazFileRelay(_ => { }, pipe).Dispose();
            runner.IsTrue(!await SendAsync(pipe, Encoding.UTF8.GetBytes("[]"), 200), "nothing is listening any more");
        });
    }
}
