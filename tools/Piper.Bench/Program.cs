using Piper.Bench;

// A benchmark, not a test: eng/verify.ps1 builds it (so it stays warning-free) but never runs it, and neither does CI.

if (args.Length > 0 && args[0] == "host") return await HostMode.RunAsync(args[1..]);

Console.OutputEncoding = System.Text.Encoding.UTF8; // a label or path with non-ASCII characters prints as written

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

try
{
    if (options.Summary is { } file)
    {
        Console.Write(BenchReport.Summary(BenchReport.Load(file, out var skipped)));
        if (skipped > 0) Console.Error.WriteLine($"{skipped} unreadable line(s) skipped.");
        return 0;
    }

    if (options.Compare is { } pair)
    {
        Console.Write(BenchReport.Compare(BenchReport.Load(pair.A, out var skippedA), BenchReport.Load(pair.B, out var skippedB), pair.A, pair.B,
            BenchReport.LoadEnv(pair.A), BenchReport.LoadEnv(pair.B)));
        if (skippedA + skippedB > 0) Console.Error.WriteLine($"{skippedA + skippedB} unreadable line(s) skipped.");
        return 0;
    }
}
catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // let the runner end its proxy hosts and close the results file
    cancel.Cancel();
};
return await BenchRunner.RunAsync(options, cancel.Token);
