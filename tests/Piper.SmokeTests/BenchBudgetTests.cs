using Piper.Bench;

// The maxRequests budget of tools/Piper.Bench: only requests that start inside the measured window use it.
internal static class BenchBudgetTests
{
    public static Task RunRequestBudgetAsync(TestRunner runner) => runner.RunAsync("bench budget: only requests in the window use it", () =>
    {
        var budget = new RequestBudget(3);
        var beforeWindow = true;
        for (var i = 0; i < 1000; i++) beforeWindow &= budget.TryTake(false);
        runner.IsTrue(beforeWindow, "requests before the window are always allowed, and use none of the budget");
        runner.IsTrue(budget.TryTake(true) && budget.TryTake(true) && budget.TryTake(true), "the first three requests in the window are allowed");
        runner.IsTrue(!budget.TryTake(true), "the fourth is refused");
        runner.IsTrue(!budget.TryTake(true), "and so is every later one");
        runner.IsTrue(budget.TryTake(false), "a request outside the window is still allowed");

        runner.IsTrue(!new RequestBudget(0).TryTake(true), "a budget of zero refuses the first request in the window");
        var unlimited = new RequestBudget(long.MaxValue);
        runner.IsTrue(unlimited.TryTake(true) && unlimited.TryTake(true), "the default budget is not reached");

        // 16 threads race for a budget of 5,000 out of 64,000 attempts: exactly 5,000 win.
        var racing = new RequestBudget(5000);
        var allowed = 0;
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            for (var i = 0; i < 4000; i++)
                if (racing.TryTake(true)) Interlocked.Increment(ref allowed);
        });
        runner.AreEqual(5000, allowed, "concurrent callers never exceed or underuse the budget");
        return Task.CompletedTask;
    });
}
