using Piper.Core.Http;

namespace Piper.Core.Http2;

/// <summary>
/// Per-stream accumulation state. A stream exists only once its request headers have been
/// accepted; from there a reduced state machine (body accumulating -> dispatched, plus reset) is
/// enough to know when a message is fully assembled without modelling every RFC 9113 §5.1
/// transition.
/// </summary>
internal sealed class Http2Stream(int id, HttpRequestData request)
{
    public int Id { get; } = id;

    public MemoryStream Body { get; } = new();

    public HttpRequestData Request { get; } = request;

    /// <summary>The peer has ended its side (END_STREAM) and the request is with the handler.
    /// Anything more the peer sends on this stream, bar WINDOW_UPDATE, RST_STREAM and PRIORITY,
    /// is an error (RFC 9113 §5.1, half-closed (remote)).</summary>
    public bool Dispatched { get; set; }

    /// <summary>Bytes this side may still send before it must wait for a WINDOW_UPDATE from the
    /// peer (send-side flow control, from this side acting as sender). Plain fields (not
    /// properties) so <see cref="System.Threading.Interlocked"/> can update them by ref.</summary>
    public long RemoteWindow;

    /// <summary>Bytes received on this stream that have not yet been credited back to the peer
    /// with a WINDOW_UPDATE (this side acting as receiver).</summary>
    public long BytesToAck;

    public readonly CancellationTokenSource Cancellation = new();
}
