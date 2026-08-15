namespace Piper.Core.Http2;

/// <summary>RFC 9113 §7 error codes, used in RST_STREAM and GOAWAY frames.</summary>
public enum Http2ErrorCode : uint
{
    NoError = 0x0,
    ProtocolError = 0x1,
    InternalError = 0x2,
    FlowControlError = 0x3,
    SettingsTimeout = 0x4,
    StreamClosed = 0x5,
    FrameSizeError = 0x6,
    RefusedStream = 0x7,
    Cancel = 0x8,
    CompressionError = 0x9,
    ConnectError = 0xa,
    EnhanceYourCalm = 0xb,
    InadequateSecurity = 0xc,
    Http11Required = 0xd,
}

/// <summary>A violation of the HTTP/2 wire protocol. Connection-level (no <see cref="StreamId"/>)
/// unless set, in which case only that stream is reset rather than the whole connection.</summary>
/// <summary>
/// A deliberate abort of one stream rather than a protocol failure, raised by an AutoResponder
/// <c>*drop</c> or <c>*reset</c> rule. Only the stream dies; the connection and every other stream
/// on it carry on, which is as close as multiplexed HTTP/2 gets to killing one TCP connection.
/// </summary>
public sealed class Http2StreamAbortException(Http2ErrorCode errorCode) : Exception($"stream aborted ({errorCode})")
{
    public Http2ErrorCode ErrorCode { get; } = errorCode;
}

public sealed class Http2ProtocolException(Http2ErrorCode errorCode, string message) : Exception(message)
{
    public Http2ErrorCode ErrorCode { get; } = errorCode;

    public int? StreamId { get; init; }
}
