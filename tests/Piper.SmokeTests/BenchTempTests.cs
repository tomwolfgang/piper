using System.Diagnostics;
using Piper.Bench;

// The temporary folders a benchmark run leaves behind when it is killed (the benchmark runs are not tests).
internal static class BenchTempTests
{
    public static Task RunTempFoldersAsync(TestRunner runner) => runner.RunAsync("bench temp folders: stale run folders are removed, nothing else", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), $"piper-bench-temp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string Make(string name, bool old)
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "Piper-Root.pfx"), "x");
                if (old) Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-3));
                return path;
            }

            var stale = Make("piper-bench-" + new string('a', 32), old: true);
            var fresh = Make("piper-bench-" + new string('b', 32), old: false);
            var otherName = Make("piper-bench-options-" + new string('c', 32), old: true);
            var notHex = Make("piper-bench-" + new string('z', 32), old: true);
            var target = Make("junction-target", old: true);
            var junction = Path.Combine(root, "piper-bench-" + new string('d', 32));
            MakeJunction(junction, target);
            Directory.SetCreationTimeUtc(junction, DateTime.UtcNow.AddDays(-3));
            var asFile = Path.Combine(root, "piper-bench-" + new string('e', 32) + ".jsonl");
            File.WriteAllText(asFile, "x");
            File.SetCreationTimeUtc(asFile, DateTime.UtcNow.AddDays(-3));

            runner.AreEqual(1, BenchTemp.DeleteStaleFolders(root, TimeSpan.FromDays(1)), "one old run folder is removed");
            runner.IsTrue(!Directory.Exists(stale), "the folder older than a day is gone");
            runner.IsTrue(Directory.Exists(fresh), "a folder from today is kept");
            runner.IsTrue(Directory.Exists(otherName) && Directory.Exists(notHex) && File.Exists(asFile), "other names, non-hex names and files are never touched");
            runner.IsTrue(Directory.Exists(junction) && File.Exists(Path.Combine(target, "Piper-Root.pfx")), "a junction named like a run folder is neither followed nor deleted");
            runner.AreEqual(0, BenchTemp.DeleteStaleFolders(Path.Combine(root, "missing"), TimeSpan.FromDays(1)), "a missing temp folder is not an error");
            Directory.Delete(junction); // the link only
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    });

    // A folder another process still holds is left in place without an exception, and the rest are still cleaned.
    public static Task RunTempFoldersInUseAsync(TestRunner runner) => runner.RunAsync("bench temp folders: a locked folder is skipped without an exception, the others still go", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), $"piper-bench-temp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string Make(char c)
            {
                var path = Path.Combine(root, "piper-bench-" + new string(c, 32));
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "Piper-Root.pfx"), "x");
                Directory.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-3));
                return path;
            }

            var locked = Make('a');
            var plain = Make('c');

            int deleted;
            using (new FileStream(Path.Combine(locked, "Piper-Root.pfx"), FileMode.Open, FileAccess.Read, FileShare.None))
                deleted = BenchTemp.DeleteStaleFolders(root, TimeSpan.FromDays(1));

            runner.AreEqual(1, deleted, "the folder that could be removed is counted, the locked one is not");
            runner.IsTrue(Directory.Exists(locked) && File.Exists(Path.Combine(locked, "Piper-Root.pfx")), "the locked folder stays, with its locked file");
            runner.IsTrue(!Directory.Exists(plain), "an unlocked stale folder beside it is still removed");
            runner.AreEqual(1, BenchTemp.DeleteStaleFolders(root, TimeSpan.FromDays(1)), "the folder goes on a later run once it is free");
            runner.IsTrue(!Directory.Exists(locked), "and is then gone");
        }
        finally { Directory.Delete(root, recursive: true); }
        return Task.CompletedTask;
    });

    public static Task RunTempFoldersNestedLinkAsync(TestRunner runner) => runner.RunAsync("bench temp folders: a junction inside a stale run folder never costs the folder it points at", () =>
    {
        var root = Path.Combine(Path.GetTempPath(), $"piper-bench-temp-test-{Guid.NewGuid():N}");
        var outside = Path.Combine(Path.GetTempPath(), $"piper-bench-temp-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var link = Path.Combine(root, "piper-bench-" + new string('b', 32), "link");
        try
        {
            File.WriteAllText(Path.Combine(outside, "precious.txt"), "keep");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            Directory.SetCreationTimeUtc(Path.GetDirectoryName(link)!, DateTime.UtcNow.AddDays(-3));
            MakeJunction(link, outside);
            runner.IsTrue(Directory.Exists(link), "the test could make a junction inside a run folder");

            // Whether the OS lets the folder go with the link in it is not the point (this .NET refuses, so it stays); the target must survive either way.
            BenchTemp.DeleteStaleFolders(root, TimeSpan.FromDays(1));
            runner.IsTrue(File.ReadAllText(Path.Combine(outside, "precious.txt")) == "keep", "what the junction pointed at is intact");
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link); // the link only
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
        return Task.CompletedTask;
    });

    private static void MakeJunction(string link, string target)
    {
        using var made = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true })!;
        if (!made.WaitForExit(TimeSpan.FromSeconds(30))) made.Kill(true);
    }
}
