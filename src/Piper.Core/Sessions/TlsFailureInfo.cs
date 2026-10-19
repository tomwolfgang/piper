namespace Piper.Core.Sessions;

/// <summary>What Piper could observe about a failed client TLS handshake. Any wording for a kind states
/// the observed fact only: never "pinning", which the server side cannot tell from an untrusted root.</summary>
public enum TlsFailureKind
{
    /// <summary>The client refused Piper's certificate (rejecting alert or SChannel status); the cause is unknown.</summary>
    Rejected,

    /// <summary>A full ClientHello arrived, then the client closed with no alert evidence.</summary>
    ClosedInHandshake,

    /// <summary>Handshake completed, client closed before any request, within a short window. Low confidence: a speculative connection looks the same.</summary>
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

/// <summary>Immutable evidence for a failed client handshake, on <see cref="Session.TlsFailure"/>; replaced,
/// never edited. Only closed values, numbers and a sanitised SNI: nothing here is captured traffic.</summary>
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

    /// <summary>How many failures this evidence stands for: 1 for a fresh row, more once repeats from the same
    /// process to the same host and port, with the same kind, are collapsed into it.</summary>
    public int Attempts { get; init; } = 1;

    /// <summary>When the latest of those failures happened. The session's own start and completion stay
    /// those of the first one.</summary>
    public DateTimeOffset LastSeen { get; init; }

    /// <summary>True for the one row per process that counts failures the new-row limit kept from getting
    /// rows of their own. It stands for many hosts and kinds, so its kind says nothing and its request has no host.</summary>
    public bool IsOverflow { get; init; }
}
