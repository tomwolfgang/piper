using System.Diagnostics;
using Piper.Core.Http;
using Piper.Core.Proxy;

// The per-stream deadline behind every client write and idle-bounded read: it has to keep the bound
// the per-call deadline gave, and survive the races that reusing it invites (a timer that fires
// late, a caller cancelling as the deadline expires, a disposal mid-call).
internal static class IoDeadlineTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    public static async Task RunIoDeadlineAsync(TestRunner runner)
    {
        await runner.RunAsync("a timer callback that arrives late does not cancel the next call", () =>
        {
            using var deadline = new IoDeadline();
            var first = deadline.Begin(Hour, CancellationToken.None);
            first.Arm();
            first.End();

            deadline.OnTimer();                                  // the callback of the call that ended
            var second = deadline.Begin(Hour, CancellationToken.None);
            runner.IsTrue(!second.Token.IsCancellationRequested, "a callback with no call in flight is ignored");
            runner.IsTrue(second.Token == first.Token, "and the source is reused, not built again");

            second.Arm();
            deadline.OnTimer();                                  // a callback before this call's own deadline
            runner.IsTrue(!second.Token.IsCancellationRequested, "one before the call's own deadline is ignored too");
            second.End();
            return Task.CompletedTask;
        });

        await runner.RunAsync("an armed call is cancelled at its timeout, and the next call starts clean", async () =>
        {
            using var deadline = new IoDeadline();
            var lease = deadline.Begin(TimeSpan.FromMilliseconds(150), CancellationToken.None);
            lease.Arm();
            var clock = Stopwatch.StartNew();
            var cut = await CaptureAsync(() => Task.Delay(Timeout.Infinite, lease.Token));
            lease.End();

            runner.IsTrue(cut is OperationCanceledException, "the call is cancelled");
            runner.IsTrue(clock.ElapsedMilliseconds is >= 120 and < 3000, $"at its timeout ({clock.ElapsedMilliseconds}ms)");
            var next = deadline.Begin(Hour, CancellationToken.None);
            runner.IsTrue(!next.Token.IsCancellationRequested, "the next call gets a source that is not cancelled");
            next.End();
        });

        await runner.RunAsync("the caller's token cancels the call, and does not leak into the next one", () =>
        {
            using var deadline = new IoDeadline();
            using var caller = new CancellationTokenSource();
            var lease = deadline.Begin(Hour, caller.Token);
            lease.Arm();
            caller.Cancel();
            runner.IsTrue(lease.Token.IsCancellationRequested, "cancelling the caller's token cancels the call");
            lease.End();

            using var other = new CancellationTokenSource();
            var next = deadline.Begin(Hour, other.Token);
            runner.IsTrue(!next.Token.IsCancellationRequested, "the next call starts clean");
            caller.Cancel();
            runner.IsTrue(!next.Token.IsCancellationRequested, "a token that is no longer the caller's cannot cancel it");
            other.Cancel();
            runner.IsTrue(next.Token.IsCancellationRequested, "the current one does");
            next.End();
            return Task.CompletedTask;
        });

        await runner.RunAsync("a token cancelled between two calls cannot cancel the next call", () =>
        {
            using var deadline = new IoDeadline();
            using var first = new CancellationTokenSource();
            var lease = deadline.Begin(Hour, first.Token);
            lease.End();
            first.Cancel();                                      // after End, before the next Begin

            using var second = new CancellationTokenSource();
            var next = deadline.Begin(Hour, second.Token);
            runner.IsTrue(!next.Token.IsCancellationRequested, "the next call, under a live token, is not cancelled");
            second.Cancel();
            runner.IsTrue(next.Token.IsCancellationRequested, "its own token still cancels it");
            next.End();

            using var live = new CancellationTokenSource();
            var again = deadline.Begin(Hour, live.Token);
            first.Cancel();
            runner.IsTrue(!again.Token.IsCancellationRequested, "and the cancelled ones stay out of the calls after");
            again.End();
            return Task.CompletedTask;
        });

        await runner.RunAsync("a call that overlaps another has a deadline of its own", async () =>
        {
            using var deadline = new IoDeadline();
            var shared = deadline.Begin(Hour, CancellationToken.None);
            shared.Arm();
            var overlapping = deadline.Begin(TimeSpan.FromMilliseconds(100), CancellationToken.None);
            overlapping.Arm();

            var cut = await CaptureAsync(() => Task.Delay(Timeout.Infinite, overlapping.Token));
            runner.IsTrue(cut is OperationCanceledException, "the overlapping call is bounded by its own timeout");
            runner.IsTrue(!shared.Token.IsCancellationRequested, "without touching the call that holds the shared source");
            overlapping.End();
            shared.End();
        });

        await runner.RunAsync("disposing the deadline mid-call does not lift the bound from that call", async () =>
        {
            var deadline = new IoDeadline();
            var lease = deadline.Begin(TimeSpan.FromMilliseconds(40), CancellationToken.None);
            lease.Arm();
            deadline.Dispose();
            deadline.Dispose();

            var cut = await CaptureAsync(() => Task.Delay(Timeout.Infinite, lease.Token));
            lease.End();                                         // after the dispose: must not throw
            runner.IsTrue(cut is OperationCanceledException, "the waiting call is still cut at its timeout");

            var after = deadline.Begin(TimeSpan.FromMilliseconds(60), CancellationToken.None);
            after.Arm();
            runner.IsTrue(await CaptureAsync(() => Task.Delay(Timeout.Infinite, after.Token)) is OperationCanceledException,
                "and a call after disposal is bounded too");
            after.End();
        });

        await runner.RunAsync("a stalled write is cut at the write timeout when the stream was busy before it", async () =>
        {
            var inner = new ScriptedStream
            {
                OnWrite = (call, ct) => call switch
                {
                    <= 20 => ValueTask.CompletedTask,                           // on the spot
                    <= 25 => new ValueTask(Task.Delay(15, ct)),                 // armed, then disarmed
                    _ => new ValueTask(Task.Delay(Timeout.Infinite, ct)),       // the client stops reading
                },
            };
            await using var stream = new GuardedClientStream(inner, TimeSpan.FromMilliseconds(400));
            for (var i = 0; i < 25; i++) await stream.WriteAsync(new byte[8]);

            var clock = Stopwatch.StartNew();
            var failure = await CaptureAsync(() => stream.WriteAsync(new byte[8]).AsTask());

            runner.IsTrue(failure is IOException, $"cut as an IOException (got {failure?.GetType().Name})");
            runner.IsTrue(clock.ElapsedMilliseconds is >= 350 and < 5000, $"at the timeout ({clock.ElapsedMilliseconds}ms)");
        });

        await runner.RunAsync("writes that each wait most of the timeout are not cut by the timers of the ones before", async () =>
        {
            var inner = new ScriptedStream { OnWrite = (_, ct) => new ValueTask(Task.Delay(90, ct)) };
            await using var stream = new GuardedClientStream(inner, TimeSpan.FromMilliseconds(250));
            for (var i = 0; i < 12; i++) await stream.WriteAsync(new byte[8]);
            runner.AreEqual(12, inner.Writes, "every write went through");
        });

        await runner.RunAsync("a caller cancelling as the write deadline expires gets one outcome or the other, and a usable stream", async () =>
        {
            var inner = new ScriptedStream();
            inner.OnWrite = (_, ct) => inner.Block ? new ValueTask(Task.Delay(Timeout.Infinite, ct)) : ValueTask.CompletedTask;
            await using var stream = new GuardedClientStream(inner, TimeSpan.FromMilliseconds(20));

            for (var i = 0; i < 120; i++)
            {
                using var caller = new CancellationTokenSource();
                caller.CancelAfter(TimeSpan.FromMilliseconds(14 + i % 12));
                inner.Block = true;
                var failure = await CaptureAsync(() => stream.WriteAsync(new byte[8], caller.Token).AsTask());
                inner.Block = false;

                if (failure is not (OperationCanceledException or IOException))
                {
                    runner.IsTrue(false, $"round {i}: the cancellation or the stall expected, got {failure?.GetType().Name}: {failure?.Message}");
                    return;
                }

                await stream.WriteAsync(new byte[8]);            // and still usable
            }
        });

        await runner.RunAsync("an idle read is cut at the idle timeout after earlier reads came in time", async () =>
        {
            var inner = new ScriptedStream
            {
                OnRead = (call, ct) => call switch
                {
                    <= 10 => new ValueTask<int>(1),
                    <= 15 => ByteAfterAsync(20, ct),
                    _ => SilenceAsync(ct),
                },
            };
            using var reader = new HttpStreamReader(inner) { IdleTimeout = TimeSpan.FromMilliseconds(400) };
            for (var i = 0; i < 15; i++) await reader.ReadAsync(new byte[1], CancellationToken.None);

            var clock = Stopwatch.StartNew();
            var failure = await CaptureAsync(() => reader.ReadAsync(new byte[1], CancellationToken.None).AsTask());

            runner.IsTrue(failure is HttpStalledException, $"silence is a stall (got {failure?.GetType().Name})");
            runner.IsTrue(clock.ElapsedMilliseconds is >= 350 and < 5000, $"at the timeout ({clock.ElapsedMilliseconds}ms)");
        });

        await runner.RunAsync("a change of the idle timeout between reads applies to the next read", async () =>
        {
            var inner = new ScriptedStream { OnRead = (call, ct) => call == 1 ? ByteAfterAsync(20, ct) : SilenceAsync(ct) };
            using var reader = new HttpStreamReader(inner) { IdleTimeout = TimeSpan.FromMinutes(5) };
            await reader.ReadAsync(new byte[1], CancellationToken.None);

            reader.IdleTimeout = TimeSpan.FromMilliseconds(200);
            var clock = Stopwatch.StartNew();
            var failure = await CaptureAsync(() => reader.ReadAsync(new byte[1], CancellationToken.None).AsTask());

            runner.IsTrue(failure is HttpStalledException && clock.ElapsedMilliseconds < 4000,
                $"cut at the new timeout, not the old one ({failure?.GetType().Name}, {clock.ElapsedMilliseconds}ms)");
        });

        await runner.RunAsync("a caller cancelling as the idle timeout expires gets one outcome or the other, and a usable reader", async () =>
        {
            var inner = new ScriptedStream();
            inner.OnRead = (_, ct) => inner.Block ? SilenceAsync(ct) : new ValueTask<int>(1);
            using var reader = new HttpStreamReader(inner) { IdleTimeout = TimeSpan.FromMilliseconds(20) };

            for (var i = 0; i < 120; i++)
            {
                using var caller = new CancellationTokenSource();
                caller.CancelAfter(TimeSpan.FromMilliseconds(14 + i % 12));
                inner.Block = true;
                var failure = await CaptureAsync(() => reader.ReadAsync(new byte[1], caller.Token).AsTask());
                inner.Block = false;

                if (failure is not (OperationCanceledException or HttpStalledException))
                {
                    runner.IsTrue(false, $"round {i}: the cancellation or the stall expected, got {failure?.GetType().Name}: {failure?.Message}");
                    return;
                }

                runner.AreEqual(1, await reader.ReadAsync(new byte[1], CancellationToken.None), $"round {i}: the reader still reads");
            }
        });
    }

    private static async ValueTask<int> ByteAfterAsync(int milliseconds, CancellationToken ct)
    {
        await Task.Delay(milliseconds, ct);
        return 1;
    }

    private static async ValueTask<int> SilenceAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return 0;
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> body)
    {
        try { await body(); return null; }
        catch (Exception ex) { return ex; }
    }
}
