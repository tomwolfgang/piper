using Piper.Core.Http;

namespace Piper.Core.Http3;

/// <summary>HTTP/3 frame types (RFC 9114 §11.2.1). Unlike HTTP/2 these are variable-length
/// integers, not fixed bytes, and there is no stream id in the frame -- the QUIC stream is the
/// stream, so framing carries only type and length.</summary>
public enum Http3FrameType : long
{
    Data = 0x00,
    Headers = 0x01,
    CancelPush = 0x03,
    Settings = 0x04,
    PushPromise = 0x05,
    GoAway = 0x07,
    MaxPushId = 0x0d,
}

/// <summary>Frame types RFC 9114 §7.2.8 reserves because they existed in HTTP/2 and mean nothing in
/// HTTP/3: receiving one is a connection error, unlike an unknown type, which is ignored.</summary>
public static class Http3FrameTypes
{
    public static bool IsReservedFromHttp2(long type) => type is 0x02 or 0x06 or 0x08 or 0x09;
}

/// <summary>HTTP/3 unidirectional stream types (RFC 9114 §11.2.4, RFC 9204 §4.2).</summary>
public static class Http3StreamType
{
    public const long Control = 0x00;
    public const long Push = 0x01;
    public const long QpackEncoder = 0x02;
    public const long QpackDecoder = 0x03;
}

/// <summary>HTTP/3 SETTINGS identifiers (RFC 9114 §11.2.2 and RFC 9204 §5).</summary>
public static class Http3SettingId
{
    public const long QpackMaxTableCapacity = 0x01;
    public const long MaxFieldSectionSize = 0x06;
    public const long QpackBlockedStreams = 0x07;
}

/// <summary>RFC 9114 §8.1 error codes, as used when closing QUIC streams and connections.</summary>
public enum Http3ErrorCode : long
{
    NoError = 0x0100,
    GeneralProtocolError = 0x0101,
    InternalError = 0x0102,
    StreamCreationError = 0x0103,
    ClosedCriticalStream = 0x0104,
    FrameUnexpected = 0x0105,
    FrameError = 0x0106,
    ExcessiveLoad = 0x0107,
    IdError = 0x0108,
    SettingsError = 0x0109,
    MissingSettings = 0x010a,
    RequestRejected = 0x010b,
    RequestCancelled = 0x010c,
    MessageError = 0x010e,
    ConnectError = 0x010f,
}

/// <summary>A violation by the peer that RFC 9114 makes a connection error: the connection is closed
/// with <see cref="ErrorCode"/>. Not an <see cref="HttpParseException"/>, which marks a malformed
/// message on one stream.</summary>
public sealed class Http3ProtocolException(Http3ErrorCode errorCode, string message) : Exception(message)
{
    public Http3ErrorCode ErrorCode { get; } = errorCode;
}

/// <summary>
/// The origin sent GOAWAY naming a stream at or below the request's, so it will not process the
/// request (RFC 9114 §5.2). Nothing was done with it, so repeating it elsewhere is safe -- and the
/// origin has not misbehaved, which is why this is a different type from a protocol error.
/// </summary>
public sealed class Http3GoAwayException(long goAwayStreamId, string message) : IOException(message)
{
    public long GoAwayStreamId { get; } = goAwayStreamId;
}

/// <summary>The response does not fit the buffered HTTP/3 path. The origin is fine; streaming the
/// body (epic E7) is what would carry it, and until then the TCP path does.</summary>
public sealed class Http3ResponseTooLargeException(string message) : IOException(message);

/// <summary>What the origin said in its SETTINGS frame (RFC 9114 §7.2.4, RFC 9204 §5).</summary>
public sealed record Http3PeerSettings(long? MaxFieldSectionSize, long QpackMaxTableCapacity, long QpackBlockedStreams);

/// <summary>One decoded HTTP/3 frame: <c>[varint type][varint length][payload]</c>.</summary>
public readonly record struct Http3Frame(Http3FrameType Type, ReadOnlyMemory<byte> Payload);

public static class Http3FrameWriter
{
    public static byte[] Encode(Http3FrameType type, ReadOnlySpan<byte> payload)
    {
        var head = EncodeHeader(type, payload.Length);
        var result = new byte[head.Length + payload.Length];
        head.CopyTo(result, 0);
        payload.CopyTo(result.AsSpan(head.Length));
        return result;
    }

    /// <summary>A frame's type and length, for a caller that writes the payload separately in pieces.</summary>
    public static byte[] EncodeHeader(Http3FrameType type, long payloadLength)
    {
        var head = new List<byte>(16);
        VarInt.Write(head, (long)type);
        VarInt.Write(head, payloadLength);
        return head.ToArray();
    }

    /// <summary>Builds a SETTINGS payload from id/value pairs.</summary>
    public static byte[] EncodeSettings(params (long Id, long Value)[] settings)
    {
        var payload = new List<byte>(settings.Length * 4);
        foreach (var (id, value) in settings)
        {
            VarInt.Write(payload, id);
            VarInt.Write(payload, value);
        }
        return payload.ToArray();
    }

    /// <summary>
    /// Decodes a SETTINGS payload. A repeated identifier and an identifier that HTTP/2 used for
    /// something else (0x00, 0x02-0x05) are connection errors (RFC 9114 §7.2.4), not something to
    /// resolve by letting the last one win.
    /// </summary>
    public static Dictionary<long, long> DecodeSettings(ReadOnlySpan<byte> payload)
    {
        var result = new Dictionary<long, long>();
        var position = 0;
        while (position < payload.Length)
        {
            var id = VarInt.Read(payload, ref position);
            var value = VarInt.Read(payload, ref position);
            if (id is 0x00 or 0x02 or 0x03 or 0x04 or 0x05)
                throw new Http3ProtocolException(Http3ErrorCode.SettingsError, $"SETTINGS carries the HTTP/2-only identifier 0x{id:x}.");
            if (!result.TryAdd(id, value))
                throw new Http3ProtocolException(Http3ErrorCode.SettingsError, $"SETTINGS repeats identifier 0x{id:x}.");
        }
        return result;
    }

    /// <summary>The known settings out of a SETTINGS payload; unknown identifiers are ignored (§7.2.4.1).</summary>
    public static Http3PeerSettings ParseSettings(ReadOnlySpan<byte> payload)
    {
        Dictionary<long, long> all;
        try { all = DecodeSettings(payload); }
        catch (HttpParseException ex) { throw new Http3ProtocolException(Http3ErrorCode.FrameError, $"Malformed SETTINGS frame: {ex.Message}"); }

        return new Http3PeerSettings(
            all.TryGetValue(Http3SettingId.MaxFieldSectionSize, out var section) ? section : null,
            all.GetValueOrDefault(Http3SettingId.QpackMaxTableCapacity),
            all.GetValueOrDefault(Http3SettingId.QpackBlockedStreams));
    }
}

/// <summary>
/// Buffered reader over a QUIC stream. HTTP/3 needs to read variable-length integers, whose size
/// is only known after the first byte, so a reader that can peek one byte and then pull an exact
/// count without losing buffered data is the natural primitive -- the same reason
/// <see cref="HttpStreamReader"/> exists for HTTP/1.1.
/// </summary>
public sealed class Http3StreamReader(Stream stream)
{
    private byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    public bool EndOfStream { get; private set; }

    private async ValueTask<bool> FillAsync(CancellationToken ct)
    {
        if (_start == _end) { _start = _end = 0; }
        else if (_end == _buffer.Length)
        {
            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            else Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = await stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
        if (read <= 0) { EndOfStream = true; return false; }
        _end += read;
        return true;
    }

    private async ValueTask<bool> EnsureAsync(int count, CancellationToken ct)
    {
        while (_end - _start < count)
            if (!await FillAsync(ct).ConfigureAwait(false)) return false;
        return true;
    }

    /// <summary>Reads one variable-length integer, or null at a clean end of stream.</summary>
    public async ValueTask<long?> ReadVarIntAsync(CancellationToken ct)
    {
        if (!await EnsureAsync(1, ct).ConfigureAwait(false)) return null;

        var length = 1 + VarInt.TrailingBytes(_buffer[_start]);
        if (!await EnsureAsync(length, ct).ConfigureAwait(false))
            throw new HttpParseException("Stream ended inside a variable-length integer.");

        var position = _start;
        var value = VarInt.Read(_buffer.AsSpan(0, _end), ref position);
        _start = position;
        return value;
    }

    public async ValueTask<byte[]> ReadExactlyAsync(int count, CancellationToken ct)
    {
        if (count == 0) return [];
        if (!await EnsureAsync(count, ct).ConfigureAwait(false))
            throw new HttpParseException($"Stream ended after {_end - _start} of {count} expected bytes.");

        var result = _buffer.AsSpan(_start, count).ToArray();
        _start += count;
        return result;
    }

    /// <summary>Reads a frame's type and length and nothing more, or null once the stream ends on a
    /// frame boundary. The payload is then taken with <see cref="ReadPayloadAsync"/>,
    /// <see cref="CopyPayloadAsync"/> or <see cref="SkipPayloadAsync"/>, whichever the caller's
    /// limits call for: a caller that decides by frame type never allocates for a length the
    /// peer merely claimed.</summary>
    public async ValueTask<(long Type, long Length)?> ReadFrameHeaderAsync(CancellationToken ct)
    {
        var type = await ReadVarIntAsync(ct).ConfigureAwait(false);
        if (type is null) return null;

        var length = await ReadVarIntAsync(ct).ConfigureAwait(false)
                     ?? throw new HttpParseException("Stream ended between a frame type and its length.");
        return (type.Value, length);
    }

    /// <summary>Reads a whole payload of at most <paramref name="maxLength"/> bytes.</summary>
    public async ValueTask<byte[]> ReadPayloadAsync(long length, long maxLength, CancellationToken ct)
    {
        if (length > maxLength || length > int.MaxValue)
            throw new HttpParseException($"HTTP/3 frame payload of {length} bytes exceeds the {maxLength}-byte cap.");
        return await ReadExactlyAsync((int)length, ct).ConfigureAwait(false);
    }

    /// <summary>Moves what is available of the next <paramref name="remaining"/> payload bytes -- at
    /// least one, at most what one read brought in -- into <paramref name="destination"/> and
    /// returns how many. One call per network read lets the caller count progress and enforce a
    /// running total instead of holding a whole frame.</summary>
    public async ValueTask<int> CopyPayloadAsync(Stream destination, long remaining, CancellationToken ct)
    {
        if (remaining <= 0) return 0;
        if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
            throw new HttpParseException("Stream ended inside a frame payload.");

        var count = (int)Math.Min(remaining, _end - _start);
        destination.Write(_buffer, _start, count);
        _start += count;
        return count;
    }

    /// <summary>Discards everything to the end of the stream, which is refused past
    /// <paramref name="maxBytes"/>: a stream nothing is expected on must not be allowed to
    /// occupy the connection for ever.</summary>
    public async ValueTask DrainAsync(long maxBytes, CancellationToken ct)
    {
        long total = 0;
        while (true)
        {
            total += _end - _start;
            _start = _end = 0;
            if (total > maxBytes) throw new HttpParseException($"Stream carried more than the {maxBytes} bytes allowed.");
            if (!await FillAsync(ct).ConfigureAwait(false)) return;
        }
    }

    /// <summary>Discards <paramref name="length"/> payload bytes without keeping them.</summary>
    public async ValueTask SkipPayloadAsync(long length, CancellationToken ct)
    {
        while (length > 0)
        {
            if (_start == _end && !await FillAsync(ct).ConfigureAwait(false))
                throw new HttpParseException("Stream ended inside a frame payload.");
            var count = (int)Math.Min(length, _end - _start);
            _start += count;
            length -= count;
        }
    }

    /// <summary>Reads the next frame, or null once the stream ends cleanly on a frame boundary.</summary>
    public async ValueTask<Http3Frame?> ReadFrameAsync(long maxPayload, CancellationToken ct)
    {
        var type = await ReadVarIntAsync(ct).ConfigureAwait(false);
        if (type is null) return null;

        var length = await ReadVarIntAsync(ct).ConfigureAwait(false)
                     ?? throw new HttpParseException("Stream ended between a frame type and its length.");

        if (length > maxPayload)
            throw new HttpParseException($"HTTP/3 frame payload of {length} bytes exceeds the {maxPayload}-byte cap.");

        var payload = await ReadExactlyAsync((int)length, ct).ConfigureAwait(false);
        return new Http3Frame((Http3FrameType)type, payload);
    }
}
