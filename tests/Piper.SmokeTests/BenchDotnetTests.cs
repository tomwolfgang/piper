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

    // The order the driver looks for dotnet.exe in, and the registry views behind the registered install, with the
    // environment, the file system and the registry replaced by lambdas (the real registry is never read).
    public static Task RunDotnetLookupOrderAsync(TestRunner runner) => runner.RunAsync("bench dotnet lookup: driver, DOTNET_HOST_PATH, registry (64-bit then 32-bit view), Program Files", () =>
    {
        const string self = @"C:\sdk\dotnet.exe", env = @"D:\env\dotnet.exe", registered = @"E:\reg\dotnet.exe", programFiles = @"C:\Program Files\dotnet\dotnet.exe";
        var registryReads = 0;
        string? RegisteredFolder() { registryReads++; return @"E:\reg"; }
        string? Find(string? processPath, string? hostPath, params string[] existing) =>
            HostClient.FindDotnet(processPath, hostPath, RegisteredFolder, @"C:\Program Files", path => existing.Contains(path));

        runner.AreEqual(self, Find(self, env, self, env, registered, programFiles), "the dotnet that runs the driver comes first");
        runner.AreEqual(0, registryReads, "the registry is not read once an earlier candidate is usable");
        runner.AreEqual(env, Find(@"C:\tools\piper-bench.exe", env, env, registered, programFiles), "an apphost driver falls through to DOTNET_HOST_PATH");
        runner.AreEqual(registered, Find(@"C:\tools\piper-bench.exe", env, registered, programFiles), "then the registered install when DOTNET_HOST_PATH is missing on disk");
        runner.AreEqual(registered, Find(null, null, registered, programFiles), "and when it is not set");
        runner.AreEqual(programFiles, Find(null, null, programFiles), "the default Program Files install is last");
        runner.IsTrue(Find(null, null) is null, "nothing found is null");
        runner.AreEqual(programFiles, HostClient.FindDotnet(null, null, () => null, @"C:\Program Files", path => path == programFiles), "no registered install is skipped, not an error");

        var views = new List<SystemInfo.RegistryBits>();
        string? Folder(Func<SystemInfo.RegistryBits, string?> read) { views.Clear(); return SystemInfo.RegisteredDotnetFolder(v => { views.Add(v); return read(v); }); }
        runner.AreEqual(@"C:\x64", Folder(_ => @"C:\x64"), "the 64-bit view answers first");
        runner.IsTrue(views.SequenceEqual([SystemInfo.RegistryBits.Bits64]), "and the 32-bit view is then not read");
        runner.AreEqual(@"C:\x86", Folder(v => v == SystemInfo.RegistryBits.Bits32 ? @"C:\x86" : null), "a value only in the 32-bit view is found (a 32-bit process, or an x86 install)");
        runner.IsTrue(views.SequenceEqual([SystemInfo.RegistryBits.Bits64, SystemInfo.RegistryBits.Bits32]), "64-bit view before 32-bit view");
        runner.AreEqual(@"C:\x86", Folder(v => v == SystemInfo.RegistryBits.Bits32 ? @"C:\x86" : "  "), "a blank 64-bit value does not count");
        runner.IsTrue(Folder(_ => null) is null && views.Count == 2, "no value in either view is null, after trying both");
        return Task.CompletedTask;
    });
}
