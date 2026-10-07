using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Piper.Bench;

/// <summary>
/// What the proxy talks to, on loopback and in the driver's process: a Kestrel HTTP/1.1 origin. Every
/// size a client can ask for is fixed or clamped. <c>/small</c> answers 256 bytes.
/// </summary>
internal sealed class Origin : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly HashSet<string> _connectionIds = [];

    private Origin(WebApplication app) => _app = app;

    public int HttpPort { get; private set; }

    /// <summary>Distinct connections the origin has accepted since <see cref="ResetConnectionCount"/>.</summary>
    public int ConnectionCount { get { lock (_connectionIds) return _connectionIds.Count; } }

    public void ResetConnectionCount() { lock (_connectionIds) _connectionIds.Clear(); }

    public static async Task<Origin> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http1));
        var origin = new Origin(builder.Build());
        origin._app.Run(origin.HandleAsync);
        await origin._app.StartAsync().ConfigureAwait(false);
        var feature = origin._app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        origin.HttpPort = new Uri(feature!.Addresses.First()).Port;
        return origin;
    }

    private static readonly byte[] Small = Enumerable.Repeat((byte)'x', 256).ToArray();

    private async Task HandleAsync(HttpContext context)
    {
        lock (_connectionIds) _connectionIds.Add(context.Connection.Id);
        var response = context.Response;
        switch (context.Request.Path.Value)
        {
            case "/small":
                response.ContentType = "text/plain";
                response.ContentLength = Small.Length;
                await response.Body.WriteAsync(Small, context.RequestAborted).ConfigureAwait(false);
                break;
            default:
                response.StatusCode = 404;
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
