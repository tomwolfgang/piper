using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;

// Micro-benchmarks of Piper.Core's hot paths. On demand only: not part of eng/verify.ps1, not run in CI.
//
//   dotnet run -c Release --project tests/Piper.Benchmarks -- --filter "*HttpParser*"
//   dotnet run -c Release --project tests/Piper.Benchmarks -- --list flat
//
// They run in this process (InProcessNoEmit) instead of in a generated project that BenchmarkDotNet
// would restore and build with the network, so a run starts no build and downloads nothing.
var config = ManualConfig.Create(DefaultConfig.Instance)
    .AddJob(Job.Default.WithToolchain(InProcessNoEmitToolchain.Instance));
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
