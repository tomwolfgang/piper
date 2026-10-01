namespace Piper.Core.Proxy;

/// <summary>
/// A request whose target is the proxy itself. An <see cref="IOException"/> so that every path that
/// already reports an upstream failure reports this one too; it is answered with 508 Loop Detected
/// rather than 502, because the origin is not at fault and retrying cannot help.
/// </summary>
internal sealed class ProxyLoopException(string host, int port)
    : IOException($"{host}:{port} is this proxy itself; forwarding the request there would loop.");
