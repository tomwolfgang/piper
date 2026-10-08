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
            var made = Process.Start(new ProcessStartInfo("cmd", $"/c mklink /J \"{junction}\" \"{target}\"") { CreateNoWindow = true, RedirectStandardOutput = true })!;
            made.WaitForExit();
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
}
