namespace Piper.Core.Sessions;

/// <summary>
/// What Piper could observe about a failed TLS handshake with a client. The wording any screen gives
/// a kind states the observed fact only: a client that dropped the connection is "closed", never
/// "pinning", because the server side of the handshake cannot tell a pin from an untrusted root.
/// </summary>
public enum TlsFailureKind
{
    /// <summary>The client refused Piper's certificate: a rejecting TLS alert or a rejecting SChannel
    /// status. The cause (untrusted root, private trust store, pin) is not known.</summary>
    Rejected,

    /// <summary>A full ClientHello arrived, then the client closed with no alert evidence.</summary>
    ClosedInHandshake,

    /// <summary>The handshake completed and the client closed before any request. Reserved for the
    /// post-handshake check; the handshake classifier never produces it.</summary>
    ClosedAfterHandshake,

    /// <summary>The client closed before a ClientHello was complete. It never saw Piper's certificate.</summary>
    ClosedEarly,

    /// <summary>No handshake completed within the handshake budget.</summary>
    Timeout,

    /// <summary>The client and Piper share no TLS version or cipher.</summary>
    NoCommonProtocol,

    /// <summary>The client sent bytes that are not TLS after the CONNECT was accepted.</summary>
    NotTls,

    /// <summary>Anything else. Nothing more is claimed.</summary>
    Other,
}

/// <summary>How far the connection had got when it failed.</summary>
public enum TlsFailurePhase
{
    BeforeHello,
    InHandshake,
    AfterHandshake,
}

/// <summary>
/// Immutable evidence for a client handshake that failed, stored on <see cref="Session.TlsFailure"/>.
/// Replaced, never edited, when more is learned. Holds only closed values, a number and a sanitised
/// SNI: nothing here is captured traffic.
/// </summary>
/// <param name="Kind">The classification.</param>
/// <param name="Phase">How far the connection got.</param>
/// <param name="Alert">A TLS alert name from the closed list the classifier knows, or null.</param>
/// <param name="SecStatus">The SChannel HRESULT from the inner Win32 error, or null.</param>
public sealed record TlsFailureInfo(
    TlsFailureKind Kind,
    TlsFailurePhase Phase,
    string? Alert = null,
    uint? SecStatus = null)
{
    /// <summary>The client's SNI when it is a plain host name (see <c>TlsFailureClassifier.SanitizeSni</c>), else null.</summary>
    public string? Sni { get; init; }

    /// <summary>How many failures this evidence stands for. Always 1 until repeats are collapsed.</summary>
    public int Attempts { get; init; } = 1;

    public DateTimeOffset LastSeen { get; init; }
}
