using Piper.Bench;

// The maxRequests budget of tools/Piper.Bench: requests before the measured window and requests inside it
// each have the limit, so neither a ramp can exhaust the window's share nor either phase send without bound.
internal static class BenchBudgetTests
{
    public static Task RunRequestBudgetAsync(TestRunner runner) => runner.RunAsync("bench budget: the window has its own share and neither phase is unbounded", () =>
    {
        var budget = new RequestBudget(3);
        var before = Enumerable.Range(0, 3).Select(_ => budget.Take(false)).ToList();
        runner.IsTrue(before.All(d => d == BudgetDecision.Go), "the first three requests before the window are allowed");
        var waits = Enumerable.Range(0, 1000).Select(_ => budget.Take(false)).ToList();
        runner.IsTrue(waits.All(d => d == BudgetDecision.Wait), "after that a request before the window waits (never goes, never stops the worker)");

        runner.IsTrue(budget.Take(true) == BudgetDecision.Go && budget.Take(true) == BudgetDecision.Go && budget.Take(true) == BudgetDecision.Go,
            "the window still has its full three: the pre-window requests used none of them");
        runner.IsTrue(budget.Take(true) == BudgetDecision.Stop, "the fourth request in the window stops the worker");
        runner.IsTrue(budget.Take(true) == BudgetDecision.Stop, "and so does every later one");
        runner.IsTrue(budget.Take(false) == BudgetDecision.Wait, "a request after the window closed is still capped");

        runner.IsTrue(new RequestBudget(0).Take(true) == BudgetDecision.Stop && new RequestBudget(0).Take(false) == BudgetDecision.Wait, "a budget of zero sends nothing in either phase");
        var unlimited = new RequestBudget(long.MaxValue);
        runner.IsTrue(unlimited.Take(true) == BudgetDecision.Go && unlimited.Take(false) == BudgetDecision.Go, "the default budget is not reached");

        // 16 threads race for 5,000 slots out of 64,000 attempts in each phase: exactly 5,000 go in each.
        var racing = new RequestBudget(5000);
        int wentBefore = 0, wentIn = 0;
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            for (var i = 0; i < 4000; i++)
            {
                if (racing.Take(false) == BudgetDecision.Go) Interlocked.Increment(ref wentBefore);
                if (racing.Take(true) == BudgetDecision.Go) Interlocked.Increment(ref wentIn);
            }
        });
        runner.AreEqual(5000, wentBefore, "concurrent callers never exceed or underuse the pre-window cap");
        runner.AreEqual(5000, wentIn, "or the window's");
        return Task.CompletedTask;
    });
}
