using Piper.App.Controls;

/// <summary>How the session list shares its width between columns.</summary>
internal static class ColumnLayoutTests
{
    public static Task RunAsync(TestRunner runner) => runner.RunAsync("session list columns fit the view", () =>
    {
        int[] minimums = [50, 60, 100, 200];
        int[] weights = [0, 0, 1, 2];

        runner.AreEqual("50,60,100,200", Join(ColumnLayout.Fit(410, minimums, weights)), "exactly the minimum total gives the minimums");
        runner.AreEqual("50,60,100,201", Join(ColumnLayout.Fit(411, minimums, weights)),
            "one spare pixel goes to the last weighted column, which takes the rounding remainder");
        runner.AreEqual("50,60,110,220", Join(ColumnLayout.Fit(440, minimums, weights)), "surplus is shared by weight");
        runner.AreEqual("50,60,100,200", Join(ColumnLayout.Fit(300, minimums, weights)),
            "below the minimum total the grid keeps its minimums and scrolls, rather than crushing a column");
        runner.AreEqual("50,60,100,200", Join(ColumnLayout.Fit(0, minimums, weights)), "a zero-width view keeps the minimums");
        runner.AreEqual("50,60,100,200", Join(ColumnLayout.Fit(-5, minimums, weights)), "so does a negative one");
        runner.AreEqual("50,60,100,200", Join(ColumnLayout.Fit(900, minimums, [0, 0, 0, 0])),
            "with no weights nothing grows");

        var filled = true;
        for (var available = 410; available < 2_000; available += 7)
            filled &= ColumnLayout.Fit(available, minimums, weights).Sum() == available;
        runner.IsTrue(filled, "above the minimum total the columns fill the view exactly");

        runner.AreEqual(int.MaxValue, ColumnLayout.Fit(int.MaxValue, [0, 0], [3, 5]).Sum(),
            "sharing a huge surplus by weight does not overflow");

        runner.IsTrue(Throws(() => ColumnLayout.Fit(500, minimums, [1, 1])), "a weight for every column is required");
        runner.IsTrue(Throws(() => ColumnLayout.Fit(500, [10, -1], [1, 1])), "a negative minimum is rejected");
        runner.IsTrue(Throws(() => ColumnLayout.Fit(500, [10, 10], [1, -1])), "a negative weight is rejected");

        // The regression this layout exists for. With the minimums the grid used to have, a list
        // 720 px wide -- a 1366 px laptop's share of the window -- left Size and Time scrolled off to
        // the right, so a download's progress could not be seen. The compact columns here are the
        // widths SessionListView measures at 100% for its samples; the long ones are its floors.
        int[] oldMinimums = [52, 55, 62, 170, 300, 130, 110, 112, 88];
        int[] newMinimums = [59, 66, 66, 72, 100, 56, 56, 129, 108];
        int[] growth = [0, 0, 0, 3, 6, 2, 2, 0, 0];
        runner.IsTrue(ColumnLayout.Fit(720, oldMinimums, growth).Sum() > 720, "the old minimums scroll Size and Time off a 720 px list");
        runner.AreEqual(720, ColumnLayout.Fit(720, newMinimums, growth).Sum(), "the new ones keep every column on a 720 px list");

        return Task.CompletedTask;
    });

    private static string Join(int[] widths) => string.Join(',', widths);

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
