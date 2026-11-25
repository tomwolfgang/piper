using Piper.App.Controls;

// The request/response split can stand side by side, and re-centres whenever it is switched.
internal static class InspectorLayoutTests
{
    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== Inspector switches between stacked and side by side");
        using var form = new Form { Width = 1000, Height = 600 };
        var inspector = new InspectorPanel { Dock = DockStyle.Fill };
        form.Controls.Add(inspector);
        form.Show();
        var split = inspector.Controls.OfType<SplitContainer>().Single();
        pumpUntil(() => split.Height > 200);

        check(!inspector.SideBySide && split.Orientation == Orientation.Horizontal, "the default is stacked");
        check(Math.Abs(split.SplitterDistance - split.Height / 2) <= 1, "stacked starts centred vertically");

        split.SplitterDistance = 60;
        inspector.SideBySide = true;
        check(split.Orientation == Orientation.Vertical, "side by side puts request and response in columns");
        check(Math.Abs(split.SplitterDistance - split.Width / 2) <= 1,
            $"switching re-centres across the width (distance {split.SplitterDistance}, width {split.Width})");

        split.SplitterDistance = 60;
        inspector.SideBySide = false;
        check(split.Orientation == Orientation.Horizontal, "switching back stacks again");
        check(Math.Abs(split.SplitterDistance - split.Height / 2) <= 1, "switching back re-centres vertically");

        split.SplitterDistance = split.Height / 4;
        check(Math.Abs(inspector.SplitFraction - 0.25) < 0.02, $"a moved splitter reports its fraction ({inspector.SplitFraction})");
        form.Height = 150;
        pumpUntil(() => split.Height <= 200);
        check(Math.Abs(inspector.SplitFraction - 0.25) < 0.02, $"squeezing the panel keeps the fraction ({inspector.SplitFraction}, height {split.Height})");
        form.Height = 600;
        pumpUntil(() => split.Height > 200);

        // Regression: the split was always centred on start, losing where the user had left it.
        foreach (var sideBySide in new[] { false, true })
        {
            using var restored = new Form { Width = 1000, Height = 600 };
            var again = new InspectorPanel { Dock = DockStyle.Fill, SideBySide = sideBySide, SplitFraction = 0.3 };
            restored.Controls.Add(again);
            restored.Show();
            var restoredSplit = again.Controls.OfType<SplitContainer>().Single();
            int Length() => sideBySide ? restoredSplit.Width : restoredSplit.Height;
            pumpUntil(() => Length() > 200);
            var length = Length();
            check(Math.Abs(restoredSplit.SplitterDistance - (int)(length * 0.3)) <= 1,
                $"a saved split is restored {(sideBySide ? "side by side" : "stacked")} (distance {restoredSplit.SplitterDistance}, length {length})");
        }

        inspector.SplitFraction = double.NaN;
        check(Math.Abs(split.SplitterDistance - split.Height / 2) <= 1, "an unusable fraction centres the split");
        inspector.SplitFraction = 5;
        check(Math.Abs(inspector.SplitFraction - 0.9) < 0.02, $"an out-of-range fraction is clamped ({inspector.SplitFraction})");
    }
}
