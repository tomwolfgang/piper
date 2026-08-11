namespace Piper.Core.Http2;

/// <summary>Writes RFC 9113 frames to any <see cref="Stream"/>, splitting header blocks into
/// HEADERS+CONTINUATION and bodies into multiple DATA frames at the peer's advertised
/// SETTINGS_MAX_FRAME_SIZE boundary (§6.2, §6.10).</summary>
public static class Http2FrameWriter
{
    private const int HeaderSize = 9;

    /// <summary>Writes one frame verbatim; callers needing HEADERS/CONTINUATION or multi-frame
    /// DATA splitting should use <see cref="WriteHeadersAsync"/>/<see cref="WriteDataAsync"/> instead.</summary>
    public static Task WriteAsync(Stream stream, Http2FrameType type, Http2FrameFlags flags, int streamId,
        ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var buffer = new byte[HeaderSize + payload.Length];
        WriteHeader(buffer, payload.Length, type, flags, streamId);
        payload.Span.CopyTo(buffer.AsSpan(HeaderSize));
        return stream.WriteAsync(buffer, ct).AsTask();
    }

    private static void WriteHeader(byte[] buffer, int length, Http2FrameType type, Http2FrameFlags flags, int streamId)
    {
        buffer[0] = (byte)(length >> 16);
        buffer[1] = (byte)(length >> 8);
        buffer[2] = (byte)length;
        buffer[3] = (byte)type;
        buffer[4] = (byte)flags;
        buffer[5] = (byte)((streamId >> 24) & 0x7f);
        buffer[6] = (byte)(streamId >> 16);
        buffer[7] = (byte)(streamId >> 8);
        buffer[8] = (byte)streamId;
    }

    /// <summary>Writes a HEADERS frame, continuing with CONTINUATION frames if the HPACK-encoded
    /// header block is larger than <paramref name="peerMaxFrameSize"/>.</summary>
    public static async Task WriteHeadersAsync(Stream stream, int streamId, ReadOnlyMemory<byte> headerBlock,
        bool endStream, int peerMaxFrameSize, CancellationToken ct)
    {
        var firstChunkSize = Math.Min(headerBlock.Length, peerMaxFrameSize);
        var isLast = firstChunkSize == headerBlock.Length;

        var flags = endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
        if (isLast) flags |= Http2FrameFlags.EndHeaders;

        await WriteAsync(stream, Http2FrameType.Headers, flags, streamId, headerBlock[..firstChunkSize], ct).ConfigureAwait(false);

        var offset = firstChunkSize;
        while (offset < headerBlock.Length)
        {
            var chunkSize = Math.Min(headerBlock.Length - offset, peerMaxFrameSize);
            var last = offset + chunkSize >= headerBlock.Length;
            await WriteAsync(stream, Http2FrameType.Continuation, last ? Http2FrameFlags.EndHeaders : Http2FrameFlags.None,
                streamId, headerBlock.Slice(offset, chunkSize), ct).ConfigureAwait(false);
            offset += chunkSize;
        }
    }

    /// <summary>Writes a body as one or more DATA frames. Does not perform flow-control accounting
    /// against the peer's window -- callers that must respect it should chunk before calling this.</summary>
    public static async Task WriteDataAsync(Stream stream, int streamId, ReadOnlyMemory<byte> body,
        bool endStream, int peerMaxFrameSize, CancellationToken ct)
    {
        if (body.Length == 0)
        {
            await WriteAsync(stream, Http2FrameType.Data, endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None,
                streamId, ReadOnlyMemory<byte>.Empty, ct).ConfigureAwait(false);
            return;
        }

        var offset = 0;
        while (offset < body.Length)
        {
            var chunkSize = Math.Min(body.Length - offset, peerMaxFrameSize);
            var last = offset + chunkSize >= body.Length;
            var flags = last && endStream ? Http2FrameFlags.EndStream : Http2FrameFlags.None;
            await WriteAsync(stream, Http2FrameType.Data, flags, streamId, body.Slice(offset, chunkSize), ct).ConfigureAwait(false);
            offset += chunkSize;
        }
    }
}
