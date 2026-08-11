using Piper.Core.Http;

namespace Piper.Core.Http2;

/// <summary>
/// Per-stream accumulation state. A reduced state machine (headers accumulating -> body
/// accumulating -> complete, plus reset) is enough to know when a message is fully assembled
/// without modelling every RFC 9113 §5.1 transition -- malformed peer behaviour gets RST_STREAM,
/// not exhaustive validation.
/// </summary>
internal sealed class Http2Stream(int id)
{
    public int Id { get; } = id;

    public List<byte> HeaderBlockFragment { get; } = [];
    public bool HeadersComplete { get; set; }
    public bool EndStreamOnHeaders { get; set; }

    public MemoryStream Body { get; } = new();

    public HttpRequestData? Request { get; set; }

    /// <summary>Bytes this side may still send before it must wait for a WINDOW_UPDATE from the
    /// peer (send-side flow control, from this side acting as sender). Plain fields (not
    /// properties) so <see cref="System.Threading.Interlocked"/> can update them by ref.</summary>
    public long RemoteWindow;

    /// <summary>Bytes received on this stream that have not yet been credited back to the peer
    /// with a WINDOW_UPDATE (this side acting as receiver).</summary>
    public long BytesToAck;

    public readonly CancellationTokenSource Cancellation = new();
}
