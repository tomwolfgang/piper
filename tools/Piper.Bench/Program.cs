using Piper.Bench;

// A benchmark, not a test: eng/verify.ps1 builds it (so it stays warning-free) but never runs it, and neither does CI.

if (args.Length > 0 && args[0] == "host") return await HostMode.RunAsync(args[1..]);

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(BenchOptions.Usage);
    return 0;
}

var self = typeof(HostMode).Assembly.Location;
if (!BenchOptions.TryParse(args, self, out var options, out var error))
{
    Console.Error.WriteLine(error);
    return 2;
}

if (options.List)
{
    foreach (var scenario in Scenarios.All) Console.WriteLine($"{scenario.Name,-18}{scenario.Description}");
    return 0;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the runner end its proxy hosts and close the results file
    cancel.Cancel();
};
return await BenchRunner.RunAsync(options, cancel.Token);
