namespace Piper.Core.Http2;

/// <summary>
/// RFC 9113 §6.5.2 SETTINGS values. One instance tracks what the *peer* has told us (starting
/// from the RFC's own defaults, updated as SETTINGS frames arrive); a separate instance built via
/// <see cref="Advertised"/> is what Piper sends about itself.
/// </summary>
public sealed class Http2Settings
{
    public int HeaderTableSize { get; set; } = 4096;
    public bool EnablePush { get; set; } = true;
    public int? MaxConcurrentStreams { get; set; }
    public int InitialWindowSize { get; set; } = 65_535;
    public int MaxFrameSize { get; set; } = 16_384;
    public int? MaxHeaderListSize { get; set; }

    /// <summary>What Piper advertises on both legs: push disabled (Piper never pushes and doesn't
    /// want to model pushed streams as sessions), a generous initial window (Piper always fully
    /// buffers a message before forwarding it, so there is no value in real backpressure), and a
    /// concurrent-stream cap matching common browser defaults. Frame size and header table size
    /// stay at the RFC defaults -- Piper's own HPACK encoder never uses the dynamic table, and
    /// deviating from the default only risks surprising a well-behaved peer.</summary>
    public static Http2Settings Advertised() => new()
    {
        EnablePush = false,
        MaxConcurrentStreams = 100,
        InitialWindowSize = 1_048_576,
        MaxHeaderListSize = 65_536,
    };

    public byte[] ToPayload()
    {
        var entries = new List<(ushort Id, uint Value)>
        {
            (1, (uint)HeaderTableSize),
            (2, EnablePush ? 1u : 0u),
            (4, (uint)InitialWindowSize),
            (5, (uint)MaxFrameSize),
        };
        if (MaxConcurrentStreams is int mcs) entries.Add((3, (uint)mcs));
        if (MaxHeaderListSize is int mhls) entries.Add((6, (uint)mhls));

        var buffer = new byte[entries.Count * 6];
        var offset = 0;
        foreach (var (id, value) in entries)
        {
            buffer[offset++] = (byte)(id >> 8);
            buffer[offset++] = (byte)id;
            buffer[offset++] = (byte)(value >> 24);
            buffer[offset++] = (byte)(value >> 16);
            buffer[offset++] = (byte)(value >> 8);
            buffer[offset++] = (byte)value;
        }
        return buffer;
    }

    /// <summary>Applies one SETTINGS frame's payload on top of the current values (§6.5: settings
    /// persist for the connection's lifetime and are updated incrementally, not replaced wholesale).</summary>
    public void ApplyPeerPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.Length % 6 != 0)
            throw new Http2ProtocolException(Http2ErrorCode.FrameSizeError, "SETTINGS payload length must be a multiple of 6.");

        for (var offset = 0; offset < payload.Length; offset += 6)
        {
            var id = (ushort)((payload[offset] << 8) | payload[offset + 1]);
            var value = (uint)((payload[offset + 2] << 24) | (payload[offset + 3] << 16)
                              | (payload[offset + 4] << 8) | payload[offset + 5]);
            switch (id)
            {
                case 1:
                    HeaderTableSize = unchecked((int)value);
                    break;
                case 2:
                    if (value > 1) throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "SETTINGS_ENABLE_PUSH must be 0 or 1.");
                    EnablePush = value != 0;
                    break;
                case 3:
                    MaxConcurrentStreams = unchecked((int)value);
                    break;
                case 4:
                    if (value > int.MaxValue)
                        throw new Http2ProtocolException(Http2ErrorCode.FlowControlError, "SETTINGS_INITIAL_WINDOW_SIZE exceeds the maximum flow-control window.");
                    InitialWindowSize = (int)value;
                    break;
                case 5:
                    if (value < 16_384 || value > 16_777_215)
                        throw new Http2ProtocolException(Http2ErrorCode.ProtocolError, "SETTINGS_MAX_FRAME_SIZE out of the allowed range.");
                    MaxFrameSize = (int)value;
                    break;
                case 6:
                    MaxHeaderListSize = unchecked((int)value);
                    break;
                default:
                    break; // unknown settings identifiers are ignored, per §6.5.2
            }
        }
    }
}
