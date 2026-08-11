namespace Piper.Core.Http2;

/// <summary>One decoded HTTP/2 frame: the 9-byte header (RFC 9113 §4.1) plus its payload.</summary>
public readonly record struct Http2Frame(Http2FrameType Type, Http2FrameFlags Flags, int StreamId, ReadOnlyMemory<byte> Payload)
{
    public bool HasFlag(Http2FrameFlags flag) => (Flags & flag) == flag;

    /// <summary>
    /// The DATA frame's actual body bytes, with the padding fields removed. RFC 9113 §6.1: a
    /// padded DATA payload is <c>[1-byte Pad Length][Data][Padding]</c>, so treating the raw
    /// payload as body prepends the pad-length byte and appends the padding -- silently
    /// corrupting the content. Some large origins (Google notably) pad routinely as a
    /// traffic-analysis mitigation, so this is not an edge case.
    /// </summary>
    /// <remarks>
    /// Note for flow control: <see cref="Payload"/>.Length, not this, is what counts against the
    /// window -- §6.9.1 counts the whole payload including Pad Length and Padding.
    /// </remarks>
    public ReadOnlyMemory<byte> DataPayload
    {
        get
        {
            if (!HasFlag(Http2FrameFlags.Padded)) return Payload;
            return StripPadding(Payload, offset: 1, padLength: ReadPadLength(Payload));
        }
    }

    /// <summary>
    /// The HPACK header block from a HEADERS or CONTINUATION frame, with the padding and
    /// priority fields removed. RFC 9113 §6.2: a HEADERS payload may carry a pad length, then a
    /// 5-byte priority block (stream dependency + weight), before the header block itself.
    /// Feeding those bytes to HPACK produces garbage, and an HPACK failure is a *connection*
    /// error, so getting this wrong takes down every stream on the connection, not just one.
    /// CONTINUATION frames carry neither field.
    /// </summary>
    public ReadOnlyMemory<byte> HeaderBlockPayload
    {
        get
        {
            if (Type == Http2FrameType.Continuation) return Payload;

            var padded = HasFlag(Http2FrameFlags.Padded);
            var padLength = padded ? ReadPadLength(Payload) : 0;
            var offset = padded ? 1 : 0;
            if (HasFlag(Http2FrameFlags.Priority)) offset += 5;

            return StripPadding(Payload, offset, padLength);
        }
    }

    private static int ReadPadLength(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length < 1)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "Padded frame has an empty payload.");
        return payload.Span[0];
    }

    private static ReadOnlyMemory<byte> StripPadding(ReadOnlyMemory<byte> payload, int offset, int padLength)
    {
        var length = payload.Length - offset - padLength;
        if (offset > payload.Length || length < 0)
            throw new Http2ProtocolException(Http2ErrorCode.ProtocolError,
                "Frame padding and priority fields exceed the payload length.");
        return payload.Slice(offset, length);
    }
}
