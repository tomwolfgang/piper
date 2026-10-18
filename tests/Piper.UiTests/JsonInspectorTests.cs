using System.Diagnostics;
using System.Text;
using Piper.App.Controls;
using Piper.Core.Http;

// The JSON tab with the real TreeView. A response body is the sender's, so the tree is built off the UI
// thread, one level at a time, within fixed bounds; these tests use the sizes that used to freeze the app.
internal static class JsonInspectorTests
{
    public static void Run(Action<bool, string> check, Action<Func<bool>> pumpUntil)
    {
        Console.WriteLine("== a small JSON response shows the same tree, built off the UI thread");
        using (var form = new Form { Width = 700, Height = 500 })
        {
            var inspector = Show(form);
            var tree = Tree(inspector);
            inspector.SetMessage(Json("""{"a":[1,{"b":"c"}],"d":null}"""), "small");
            check(tree.Nodes.Count == 1 && tree.Nodes[0].Text.Contains("Parsing"), "SetMessage returns before the tree is built");
            pumpUntil(() => tree.Nodes.Count == 1 && tree.Nodes[0].Text == "root: { }");
            check(tree.Nodes[0].IsExpanded && Texts(tree.Nodes[0]) == "a: [ ]|d: null", $"the root is expanded to its top level (got {Texts(tree.Nodes[0])})");
            tree.Nodes[0].Nodes[0].Expand();
            check(Texts(tree.Nodes[0].Nodes[0]) == "[0]: 1|[1]: { }", "expanding a node shows its items");
            tree.Nodes[0].Nodes[0].Nodes[1].Expand();
            check(Texts(tree.Nodes[0].Nodes[0].Nodes[1]) == "b: \"c\"", "and theirs");

            tree.SelectedNode = tree.Nodes[0].Nodes[0].Nodes[1].Nodes[0];
            var copy = typeof(MessageInspector).GetMethod("CopySelectedJson", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            check(copy.Invoke(inspector, [false]) is null, "copying the selected pair does not throw");

            var search = Find<TextBox>(inspector, box => box.PlaceholderText == "Search JSON");
            search.Text = "\"c\"";
            pumpUntil(() => tree.SelectedNode?.Text == "b: \"c\"");
            check(tree.SelectedNode?.Text == "b: \"c\"", "searching selects the match");
            inspector.SetMessage(Json("""{"x":{"y":[0,{"needle":1}]}}"""), "nested");
            pumpUntil(() => tree.Nodes.Count == 1 && tree.Nodes[0].Text == "root: { }");
            search.Text = "needle";
            pumpUntil(() => tree.SelectedNode?.Text == "needle: 1");
            check(tree.SelectedNode is { Text: "needle: 1", Parent.IsExpanded: true } && tree.SelectedNode.Parent.Parent?.IsExpanded == true,
                "a search finds a node in a collapsed branch and expands the path to it");
        }

        Console.WriteLine("== a multi-megabyte array does not freeze the UI thread and shows a truncated tree");
        using (var form = new Form { Width = 700, Height = 500 })
        {
            var inspector = Show(form);
            var tree = Tree(inspector);
            var body = "[" + string.Join(',', Enumerable.Range(0, 1_000_000)) + "]";
            var response = Json(body);
            Console.WriteLine($"   body {body.Length / (1024.0 * 1024):N1} MiB");

            var gap = 0L;
            var last = Stopwatch.StartNew();
            using var ticker = new System.Windows.Forms.Timer { Interval = 10 };
            ticker.Tick += (_, _) => { gap = Math.Max(gap, last.ElapsedMilliseconds); last.Restart(); };
            ticker.Start();
            var watch = Stopwatch.StartNew();
            inspector.SetMessage(response, "big");
            var returned = watch.ElapsedMilliseconds;
            pumpUntil(() => tree.Nodes.Count == 1 && tree.Nodes[0].Nodes.Count > 1);
            ticker.Stop();
            Console.WriteLine($"   SetMessage held the UI thread {returned:N0} ms; longest gap between UI ticks {gap:N0} ms; tree ready after {watch.ElapsedMilliseconds:N0} ms");
            check(returned < 1500 && gap < 2000, $"the UI thread is never held for long (SetMessage {returned} ms, longest gap {gap} ms)");
            var root = tree.Nodes[0];
            check(root.Nodes.Count == 5_001 && root.Nodes[^1].Text.Contains("more entries are not shown") && root.Nodes[^1].Tag is null,
                $"the first 5,000 items are shown, then a node that says the rest are not (got {root.Nodes.Count}: '{root.Nodes[^1].Text}')");
        }

        Console.WriteLine("== a selection change drops a tree that is still being built");
        using (var form = new Form { Width = 700, Height = 500 })
        {
            var inspector = Show(form);
            var tree = Tree(inspector);
            inspector.SetMessage(Json("[" + string.Join(',', Enumerable.Range(0, 1_000_000)) + "]"), "big");
            inspector.SetMessage(Json("""{"from":"second"}"""), "second");
            pumpUntil(() => tree.Nodes.Count == 1 && tree.Nodes[0].Text == "root: { }");
            var ticks = 0;
            pumpUntil(() => ++ticks > 100); // the first build ends, and must not replace the second tree
            check(tree.Nodes.Count == 1 && Texts(tree.Nodes[0]) == "from: \"second\"", $"the tree is the second response's (got {Texts(tree.Nodes[0])})");
            inspector.SetMessage(null, string.Empty);
            check(tree.Nodes.Count == 0, "clearing the inspector empties the tree");
        }

        Console.WriteLine("== hostile and broken JSON bodies end in a message, never a hang or a throw");
        var deep = new string('[', 10_000) + new string(']', 10_000);
        var bigString = "{\"s\":\"" + new string('x', 5 * 1024 * 1024) + "\"}";
        (string Name, byte[] Body, Func<TreeView, bool> Expected)[] cases =
        [
            ("10,000 levels of nesting", Encoding.UTF8.GetBytes(deep), t => t.Nodes[0].Text.StartsWith("[not valid JSON:")),
            ("invalid JSON", "{\"a\":"u8.ToArray(), t => t.Nodes[0].Text.StartsWith("[not valid JSON:")),
            ("a UTF-8 byte order mark (refused, as before)", [0xEF, 0xBB, 0xBF, .. "{\"a\":1}"u8], t => t.Nodes[0].Text.StartsWith("[not valid JSON:")),
            ("a single 5 MB string", Encoding.UTF8.GetBytes(bigString), t => t.Nodes[0].Text == "root: { }" && t.Nodes[0].Nodes[0].Text.Length < 1100 && t.Nodes[0].Nodes[0].Text.Contains("characters)")),
            ("a body over the size limit", Encoding.UTF8.GetBytes("[" + string.Join(',', Enumerable.Repeat("0", 5_000_000)) + "]"), t => t.Nodes[0].Text.Contains("not shown as a tree")),
        ];
        using (var form = new Form { Width = 700, Height = 500 })
        {
            var inspector = Show(form);
            var tree = Tree(inspector);
            foreach (var (name, body, expected) in cases)
            {
                var watch = Stopwatch.StartNew();
                inspector.SetMessage(Json(body), name);
                var returned = watch.ElapsedMilliseconds;
                pumpUntil(() => tree.Nodes.Count == 1 && !tree.Nodes[0].Text.Contains("Parsing"));
                check(tree.Nodes.Count == 1 && expected(tree), $"{name}: '{Short(tree.Nodes.Count > 0 ? tree.Nodes[0].Text : "(none)")}' after {watch.ElapsedMilliseconds:N0} ms, SetMessage held the UI {returned:N0} ms");
                check(returned < 1500, $"{name}: SetMessage returns promptly");
            }

            // An empty body opens on the Headers tab, so the JSON tab is picked by hand.
            inspector.SetMessage(Json([]), "empty");
            Find<TabControl>(inspector, _ => true).SelectedIndex = 4;
            pumpUntil(() => tree.Nodes.Count == 1 && tree.Nodes[0].Text == "(no body)");
            check(tree.Nodes.Count == 1 && tree.Nodes[0].Text == "(no body)", "an empty body says so");
        }
    }

    private static MessageInspector Show(Form form)
    {
        var inspector = new MessageInspector("test", showImageViewer: true) { Dock = DockStyle.Fill };
        form.Controls.Add(inspector);
        form.Show();
        return inspector;
    }

    private static HttpResponseData Json(string body) => Json(Encoding.UTF8.GetBytes(body));

    private static HttpResponseData Json(byte[] body)
    {
        var response = new HttpResponseData { Body = body };
        response.Headers.Add("Content-Type", "application/json");
        return response;
    }

    private static string Texts(TreeNode node) => string.Join('|', node.Nodes.Cast<TreeNode>().Select(n => n.Text));

    private static string Short(string text) => text.Length <= 60 ? text : text[..60] + "...";

    private static TreeView Tree(Control root) => Find<TreeView>(root, _ => true);

    private static T Find<T>(Control root, Func<T, bool> match) where T : Control =>
        FindOrNull(root, match) ?? throw new InvalidOperationException($"No {typeof(T).Name} in the inspector.");

    private static T? FindOrNull<T>(Control root, Func<T, bool> match) where T : Control =>
        root is T hit && match(hit) ? hit : root.Controls.Cast<Control>().Select(c => FindOrNull(c, match)).FirstOrDefault(c => c is not null);
}
