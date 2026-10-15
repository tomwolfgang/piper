using System.Diagnostics;
using System.Text.Json;
using Piper.App.Controls;

// The inspector's JSON tree reads a hostile body on demand. These tests drive the WinForms-free
// model with the shapes that used to hang the UI thread or exhaust memory.
internal static class JsonTreeTests
{
    private static string Texts(JsonTreeLevel level) => string.Join('|', level.Children.Select(c => c.Text));

    private static string Paths(IReadOnlyList<int[]> paths) => string.Join('|', paths.Select(p => string.Join(',', p)));

    private static string Repeated(string item, int count) => "[" + string.Join(',', Enumerable.Repeat(item, count)) + "]";

    public static async Task RunAsync(TestRunner runner)
    {
        await runner.RunAsync("json tree makes nothing for a node until it is expanded, and shows a small document whole", () =>
        {
            var source = JsonTreeSource.Parse("""{"a":[1,{"b":"c"}],"d":null,"e":"","f":true,"g":{}}""");
            var root = source.Root()!;
            runner.AreEqual("root: { }", root.Text, "the top node");
            var level = source.Children(root.Value)!;
            runner.AreEqual("a: [ ]|d: null|e: \"\"|f: true|g: { }", Texts(level), "its children, in document order");
            runner.AreEqual("True|False|False|False|False", string.Join('|', level.Children.Select(c => c.HasChildren)), "only a non-empty container can expand");
            runner.AreEqual(0, level.Omitted, "nothing is left out of a small document");

            var items = source.Children(level.Children[0].Value)!;
            runner.AreEqual("[0]: 1|[1]: { }", Texts(items), "an array's items are named by index");
            runner.AreEqual("""{"b":"c"}""", source.RawText(items.Children[1].Value), "raw text is read when asked for");
            var b = source.Children(items.Children[1].Value)!.Children[0];
            runner.AreEqual("c", source.StringValue(b.Value), "a string value is read when asked for");
            runner.AreEqual("b: \"c\"", source.FullText(b.Name, b.Value), "and so is a node's whole text");
            source.Retire();
            return Task.CompletedTask;
        });

        await runner.RunAsync("json tree caps the children of one node and says how many are not shown", () =>
        {
            var watch = Stopwatch.StartNew();
            var source = JsonTreeSource.Parse("[" + string.Join(',', Enumerable.Range(0, 1_000_000)) + "]");
            var level = source.Children(source.Root()!.Value)!;
            watch.Stop();
            runner.AreEqual(JsonTreeLimits.Default.MaxChildren, level.Children.Count, "a million-item array yields the per-node cap");
            runner.AreEqual(1_000_000 - JsonTreeLimits.Default.MaxChildren, level.Omitted, "and reports the rest as not shown");
            runner.AreEqual("[4999]: 4999", level.Children[^1].Text, "what is shown is the start of the array");
            runner.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(10), $"parsing and expanding it is quick ({watch.ElapsedMilliseconds} ms)");
            source.Retire();

            var three = JsonTreeSource.Parse("""{"a":1,"b":2,"c":3,"d":4,"e":5}""", new JsonTreeLimits { MaxChildren = 3 });
            var shown = three.Children(three.Root()!.Value)!;
            runner.AreEqual("a: 1|b: 2|c: 3", Texts(shown), "an object is cut the same way");
            runner.AreEqual(2, shown.Omitted, "two properties are not shown");
            three.Retire();
            return Task.CompletedTask;
        });

        await runner.RunAsync("json tree caps the nodes of a whole document however much is expanded", () =>
        {
            var source = JsonTreeSource.Parse("[[1,2,3,4],[5,6,7,8],[9,10,11,12]]", new JsonTreeLimits { MaxChildren = 4, MaxNodes = 10 });
            var top = source.Children(source.Root()!.Value)!;
            var made = 1 + top.Children.Count; // the root and the top level
            var omitted = 0;
            foreach (var child in top.Children)
            {
                var level = source.Children(child.Value)!;
                made += level.Children.Count;
                omitted += level.Omitted;
            }

            runner.AreEqual(10, made, "the nodes made stop at the node cap");
            runner.AreEqual(6, omitted, "and what could not be made is reported as not shown");
            source.Retire();
            return Task.CompletedTask;
        });

        await runner.RunAsync("json tree clips a multi-megabyte value instead of making it a tree node", () =>
        {
            var big = new string('x', 5 * 1024 * 1024);
            var key = new string('k', 3000);
            var source = JsonTreeSource.Parse($"{{\"{key}\":\"{big}\",\"n\":{new string('9', 3000)}}}");
            var level = source.Children(source.Root()!.Value)!;
            runner.IsTrue(level.Children.All(c => c.Text.Length <= JsonTreeLimits.Default.MaxDisplayChars + 64), "no node text is much longer than the display limit");
            runner.IsTrue(level.Children.All(c => c.Text.EndsWith("characters)", StringComparison.Ordinal)), "a clipped line says how long it was");
            runner.AreEqual(big.Length, source.StringValue(level.Children[0].Value)!.Length, "the whole string is still there to copy");
            runner.AreEqual(key.Length + 2 + 2 + big.Length, source.FullText(level.Children[0].Name, level.Children[0].Value)!.Length, "and so is the whole line");
            source.Retire();

            // 1,001 characters of `a: "` and emoji ends on the first half of a surrogate pair.
            using var emoji = JsonDocument.Parse("\"" + string.Concat(Enumerable.Repeat("\\ud83d\\ude00", 600)) + "\"");
            var clipped = JsonTreeText.Format("a", emoji.RootElement, 1001);
            runner.IsTrue(clipped.Contains("... (", StringComparison.Ordinal) && !char.IsHighSurrogate(clipped[clipped.IndexOf("... (", StringComparison.Ordinal) - 1]),
                "a cut never leaves half of a surrogate pair");
            return Task.CompletedTask;
        });

        await runner.RunAsync("json tree refuses invalid, empty and over-deep documents with a JsonException only", () =>
        {
            string[] invalid =
            [
                "", " ", "{", "[1,]", "nul", "{\"a\":}", "[1] 2", "'a'",
                new string('[', 65) + new string(']', 65),
                new string('[', 10_000) + new string(']', 10_000),
                "[" + new string('1', 4_000_000),
            ];
            var refused = 0;
            foreach (var text in invalid)
            {
                try { JsonTreeSource.Parse(text).Retire(); }
                catch (JsonException) { refused++; }
            }

            runner.AreEqual(invalid.Length, refused, "each is refused with a JsonException, including 10,000 levels and an unterminated 4 MB number");
            JsonTreeSource.Parse(new string('[', 64) + new string(']', 64)).Retire();

            var lone = JsonTreeSource.Parse("""{"s":"\ud800","t":"ok"}""");
            var level = lone.Children(lone.Root()!.Value)!;
            runner.AreEqual("s: \"\\ud800\"", level.Children[0].Text, "a lone surrogate escape is shown as it was written");
            runner.AreEqual(null, lone.StringValue(level.Children[0].Value), "and has no string value to copy");
            runner.AreEqual("ok", lone.StringValue(level.Children[1].Value), "its neighbour is unaffected");
            lone.Retire();

            // As before: a byte order mark that reaches the parser as text is not JSON (a follow-up, not part of this change).
            string[] withBom = ["﻿{\"a\":1}", "﻿"];
            var bomRefused = 0;
            foreach (var text in withBom)
            {
                try { JsonTreeSource.Parse(text).Retire(); }
                catch (JsonException) { bomRefused++; }
            }

            runner.AreEqual(withBom.Length, bomRefused, "a byte order mark in front is refused with a JsonException, not another exception");
            return Task.CompletedTask;
        });

        await runner.RunAsync("json tree search finds nodes in collapsed branches, in tree order, within its bounds", () =>
        {
            var source = JsonTreeSource.Parse("""{"users":[{"name":"Ann"},{"name":"Bob","tags":["needle"]}],"note":"a needle too"}""");
            runner.AreEqual("0,1,1,0|1", Paths(source.Search("NEEDLE", CancellationToken.None)!), "matches are paths of child indexes, in tree order");
            runner.AreEqual("", Paths(source.Search("root", CancellationToken.None)!), "the top node matches by its own label, with an empty path");
            runner.AreEqual(0, source.Search("absent", CancellationToken.None)!.Count, "no match is an empty list");
            source.Retire();

            var many = JsonTreeSource.Parse(Repeated("1", 50_000), new JsonTreeLimits { MaxMatches = 100 });
            runner.AreEqual(100, many.Search("1", CancellationToken.None)!.Count, "the matches kept are capped");
            many.Retire();

            var visited = JsonTreeSource.Parse(Repeated("1", 50_000), new JsonTreeLimits { MaxSearchVisits = 500 });
            runner.AreEqual(500, visited.Search("[", CancellationToken.None)!.Count, "the nodes looked at are capped (the root and 499 items all match)");
            visited.Retire();

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var wide = JsonTreeSource.Parse(Repeated("1", 4_000));
            var threw = false;
            try { wide.Search("zzz", cancelled.Token); }
            catch (OperationCanceledException) { threw = true; }
            runner.IsTrue(threw, "a cancelled search stops");
            wide.Retire();
            return Task.CompletedTask;
        });

        await runner.RunAsync("a retired json tree serves nothing, and a search still reading keeps its document until it ends", async () =>
        {
            var source = JsonTreeSource.Parse(Repeated("1", 1_000_000), new JsonTreeLimits { MaxChildren = 1_000_000, MaxSearchVisits = 2_000_000 });
            var root = source.Root()!;
            using var started = new ManualResetEventSlim();
            var search = Task.Run(() =>
            {
                started.Set();
                return source.Search("zzz", CancellationToken.None); // a few hundred ms over a million nodes
            });
            started.Wait();
            Thread.Sleep(20);
            source.Retire();
            source.Retire(); // a second retire is harmless
            runner.IsTrue(source.Root() is null && source.Children(root.Value) is null && source.RawText(root.Value) is null && source.Search("x", CancellationToken.None) is null,
                "once retired, no read is served");
            var result = await search.WaitAsync(TimeSpan.FromSeconds(60));
            runner.IsTrue(result is { Count: 0 }, "the search that was already reading finishes normally instead of reading a released document");
        });
    }
}
