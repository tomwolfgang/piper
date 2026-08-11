namespace Piper.Core.Http2;

/// <summary>RFC 9113 §6 frame types Piper handles. PUSH_PROMISE is deliberately absent: Piper
/// never pushes as a server, and disables push from origins via SETTINGS_ENABLE_PUSH=0, so there
/// is nothing to decode. ALTSVC/ORIGIN (RFC 7838/8336) are unrelated extension frames.</summary>
public enum Http2FrameType : byte
{
    Data = 0x0,
    Headers = 0x1,
    Priority = 0x2,
    RstStream = 0x3,
    Settings = 0x4,
    PushPromise = 0x5,
    Ping = 0x6,
    GoAway = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9,
}

/// <summary>Flag bits. Several share a bit value across frame types (e.g. <see cref="EndStream"/>
/// and <see cref="Ack"/> are both 0x1) because the RFC defines flags per-frame-type, not globally.</summary>
[Flags]
public enum Http2FrameFlags : byte
{
    None = 0x0,
    EndStream = 0x1,   // DATA, HEADERS
    Ack = 0x1,         // SETTINGS, PING
    EndHeaders = 0x4,  // HEADERS, PUSH_PROMISE, CONTINUATION
    Padded = 0x8,      // DATA, HEADERS, PUSH_PROMISE
    Priority = 0x20,   // HEADERS
}
