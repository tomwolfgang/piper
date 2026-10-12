using Piper.Bench;

// Which dotnet.exe the benchmark driver starts for a host .dll: a full path it chose, never the bare name
// (CreateProcess searches the current directory first, so a dotnet.exe left there would run instead).
internal static class BenchDotnetTests
{
    public static Task RunChooseDotnetAsync(TestRunner runner) => runner.RunAsync("bench dotnet: a full path to an existing dotnet.exe, never a name", () =>
    {
        const string real = @"C:\Program Files\dotnet\dotnet.exe";
        static bool Exists(string path) => path is real or @"D:\sdk\dotnet.exe" or @"C:\tools\piper-bench.exe";

        runner.AreEqual(real, BenchHygiene.ChooseDotnet([real, @"D:\sdk\dotnet.exe"], Exists), "the first usable candidate wins");
        runner.AreEqual(@"D:\sdk\dotnet.exe", BenchHygiene.ChooseDotnet([@"C:\tools\piper-bench.exe", null, "", "  ", @"D:\sdk\dotnet.exe", real], Exists),
            "a driver that is not dotnet (apphost), a null and a blank are skipped");
        runner.AreEqual(real, BenchHygiene.ChooseDotnet([@"C:\Missing\dotnet.exe", real], Exists), "a candidate that does not exist is skipped");
        runner.IsTrue(BenchHygiene.ChooseDotnet(["dotnet", "dotnet.exe", @".\dotnet.exe", @"dotnet\dotnet.exe", @"\dotnet.exe", "C:dotnet.exe"], _ => true) is null,
            "a bare or relative name is never chosen, whatever exists: it would resolve in the current directory");
        runner.IsTrue(BenchHygiene.ChooseDotnet([@"C:\tools\piper-bench.exe", @"C:\Program Files\dotnet\dotnet.dll"], _ => true) is null,
            "only a file named dotnet or dotnet.exe is chosen");
        runner.AreEqual(@"C:\x\DOTNET.EXE", BenchHygiene.ChooseDotnet([@"C:\x\DOTNET.EXE"], _ => true), "the file name is compared without case");
        runner.IsTrue(BenchHygiene.ChooseDotnet([], _ => true) is null, "no candidates gives null");
        runner.IsTrue(BenchHygiene.ChooseDotnet([real], _ => false) is null, "none existing gives null");
        return Task.CompletedTask;
    });
}
