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
    }
}
