namespace Piper.Core.Http2;

/// <summary>Reads one RFC 9113 §4.1 frame (9-byte header + payload) at a time off any <see cref="Stream"/>.
/// Binary length-prefixed framing needs none of <c>HttpStreamReader</c>'s line-oriented buffering.</summary>
public static class Http2FrameReader
{
    private const int HeaderSize = 9;

    /// <summary>Reads the next frame. Throws <see cref="Http2ProtocolException"/> (FRAME_SIZE_ERROR)
    /// if the peer sends a frame larger than <paramref name="maxFrameSize"/> (the value this side
    /// advertised via SETTINGS_MAX_FRAME_SIZE). Returns null at a clean end of stream.</summary>
    public static async Task<Http2Frame?> ReadAsync(Stream stream, int maxFrameSize, CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        if (!await TryReadExactlyAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        var length = (header[0] << 16) | (header[1] << 8) | header[2];
        var type = (Http2FrameType)header[3];
        var flags = (Http2FrameFlags)header[4];
        var streamId = ((header[5] & 0x7f) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];

        if (length > maxFrameSize)
            throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError,
                $"Frame of {length} bytes exceeds the {maxFrameSize}-byte limit this side advertised.");

        var payload = length == 0 ? [] : new byte[length];
        if (length > 0 && !await TryReadExactlyAsync(stream, payload, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Connection closed mid-frame.");

        return new Http2Frame(type, flags, streamId, payload);
    }

    /// <summary>Like <see cref="ReadAsync"/> but throws instead of returning null, for call sites
    /// that never expect a clean close (e.g. mid-handshake).</summary>
    public static async Task<Http2Frame> ReadRequiredAsync(Stream stream, int maxFrameSize, CancellationToken ct) =>
        await ReadAsync(stream, maxFrameSize, ct).ConfigureAwait(false)
        ?? throw new EndOfStreamException("Connection closed before a frame was received.");

    private static async ValueTask<bool> TryReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct).ConfigureAwait(false);
            if (n == 0) return offset == 0 ? false : throw new EndOfStreamException("Connection closed mid-frame.");
            offset += n;
        }
        return true;
    }
}
