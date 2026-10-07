using System.Diagnostics;

namespace Piper.Bench;

internal sealed record Scenario(string Name, string Description, Func<ScenarioContext, Task<Dictionary<string, double>>> Run);

/// <summary>The scenarios. Each starts a fresh proxy host, so no run inherits another's heap, caches or
/// JIT state, warms it up unmeasured, and returns metrics named with their unit. Names are stable:
/// results files are compared by them.</summary>
internal static class Scenarios
{
    private static readonly TimeSpan Ramp = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<Scenario> All { get; } =
    [
        new("startup", "Process start to the first response through the proxy", StartupAsync),
        Get(1), Get(16), Get(64),
    ];

    private static Scenario Get(int concurrency) => new($"get_c{concurrency}",
        $"Keep-alive GET of 256 bytes, {concurrency} concurrent", async c =>
        {
            await c.StartHostAsync().ConfigureAwait(false);
            await WarmUpAsync(c).ConfigureAwait(false);
            var url = c.OriginUrl + "/small";
            return await LoadGenerator.RunAsync(c, concurrency, Ramp, c.Options.Duration, (client, ct) => client.GetAsync(url, ct)).ConfigureAwait(false);
        });

    // JIT and the first pool growth are not what is being measured.
    private static async Task WarmUpAsync(ScenarioContext c)
    {
        var url = c.OriginUrl + "/small";
        await LoadGenerator.RunAsync(c, 16, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), (client, ct) => client.GetAsync(url, ct)).ConfigureAwait(false);
    }

    private static async Task<Dictionary<string, double>> StartupAsync(ScenarioContext c)
    {
        var timer = Stopwatch.StartNew();
        var host = await c.StartHostAsync().ConfigureAwait(false);
        using var client = c.NewClient(1);
        using var response = await client.GetAsync(c.OriginUrl + "/small", c.Token).ConfigureAwait(false);
        await response.Content.ReadAsByteArrayAsync(c.Token).ConfigureAwait(false);
        return new() { ["first_response_ms"] = timer.Elapsed.TotalMilliseconds, ["host_start_ms"] = host.StartMs };
    }
}
