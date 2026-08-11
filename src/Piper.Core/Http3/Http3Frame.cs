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
    RequestCancelled = 0x010c,
    MessageError = 0x010e,
    ConnectError = 0x010f,
}

/// <summary>One decoded HTTP/3 frame: <c>[varint type][varint length][payload]</c>.</summary>
public readonly record struct Http3Frame(Http3FrameType Type, ReadOnlyMemory<byte> Payload);

public static class Http3FrameWriter
{
    public static byte[] Encode(Http3FrameType type, ReadOnlySpan<byte> payload)
    {
        var head = new List<byte>(16);
        VarInt.Write(head, (long)type);
        VarInt.Write(head, payload.Length);

        var result = new byte[head.Count + payload.Length];
        head.CopyTo(result);
        payload.CopyTo(result.AsSpan(head.Count));
        return result;
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

    public static Dictionary<long, long> DecodeSettings(ReadOnlySpan<byte> payload)
    {
        var result = new Dictionary<long, long>();
        var position = 0;
        while (position < payload.Length)
        {
            var id = VarInt.Read(payload, ref position);
            var value = VarInt.Read(payload, ref position);
            result[id] = value;
        }
        return result;
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
