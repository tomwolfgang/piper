using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

// Micro-benchmarks of Piper.Core's hot paths. On demand only: not part of eng/verify.ps1, not run in CI.
//
//   dotnet run -c Release --project tests/Piper.Benchmarks -- --filter "*HttpParser*"
//   dotnet run -c Release --project tests/Piper.Benchmarks -- --list flat
//   dotnet run -c Release --project tests/Piper.Benchmarks -- --filter "*HttpParser*" --warmupCount 1 --iterationCount 3 --launchCount 1 --iterationTime 20   # quick
//
// They run in this process (InProcessNoEmit) instead of in a generated project that BenchmarkDotNet
// would restore and build with the network, so a run starts no build and downloads nothing.
//
// A command-line job (--job, --runtimes, --inProcess, ...) would ADD a second job, built out of
// process in a generated project with a NuGet restore (checked with --job Dry), so those are refused.
// --warmupCount, --iterationCount, --launchCount and --iterationTime adjust the in-process job itself.
string[] outOfProcess = ["--job", "-j", "--runtimes", "-r", "--inprocess", "-i", "--corerun", "--cli", "--clrversion", "--monopath"];
var refused = args.Where(a => a.StartsWith('-') && outOfProcess.Contains(a.Split('=')[0], StringComparer.OrdinalIgnoreCase)).ToList();
if (refused.Count > 0)
{
    Console.Error.WriteLine($"Not supported here: {string.Join(", ", refused)}. Piper.Benchmarks runs in-process so a run builds and downloads nothing; "
        + "use --warmupCount 1 --iterationCount 3 --launchCount 1 --iterationTime 20 for a quick run.");
    return 2;
}

var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default.WithToolchain(InProcessNoEmitToolchain.Instance));
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
return 0;
