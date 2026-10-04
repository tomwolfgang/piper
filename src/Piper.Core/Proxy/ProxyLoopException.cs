namespace Piper.Core.Proxy;

/// <summary>
/// A request whose target is the proxy itself. An <see cref="IOException"/> so that every path that
/// already reports an upstream failure reports this one too; it is answered with 508 Loop Detected
/// rather than 502, because the origin is not at fault and retrying cannot help. The message names
/// the target as <c>host:port</c> and nothing else of the request.
/// </summary>
internal sealed class ProxyLoopException(string host, int port)
    : IOException($"{host}:{port} is this proxy itself; forwarding the request there would loop.")
{
    /// <summary>The answer for a client that asked for the proxy itself. The body repeats the
    /// <c>host:port</c> the client already named, and no part of its request.</summary>
    internal Http.HttpResponseData ToResponse() =>
        Http.HttpResponseData.Simple(508, "Loop Detected", $"Piper will not forward a request to itself.\r\n\r\n{Message}");
}
