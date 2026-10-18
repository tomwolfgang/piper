using System.ComponentModel;
using System.Net.Sockets;
using System.Security.Authentication;
using Piper.Core.Sessions;

namespace Piper.Core.Proxy;

/// <summary>
/// Turns the exception a client handshake died with into <see cref="TlsFailureInfo"/>. Pure, and run on the
/// failure path alone. The client's TLS alert is not observable through a public API; the indirect signals
/// are the inner SChannel code and, on the TLS 1.3 path, an alert name in the exception text. Anything not
/// recognised is <see cref="TlsFailureKind.Other"/> or a plain close, never a stronger claim.
/// </summary>
internal static class TlsFailureClassifier
{
    internal const uint SecUntrustedRoot = 0x80090325;
    internal const uint SecCertUnknown = 0x80090327;
    internal const uint SecNoCommonAlgorithm = 0x80090331;

    /// <summary>Longest SNI kept: the longest DNS name.</summary>
    internal const int MaxSniLength = 253;

    // Exception chains are short; the bound only matters for a hostile one.
    private const int MaxChainDepth = 8;

    // .NET's own alert text: "...the remote party sent a TLS alert: 'UnknownCA'". Only this marker is
    // read, and only a name from AlertNames is ever kept.
    private const string AlertMarker = "TLS alert: '";
    private const int LongestAlertName = 32;

    // RFC 8446 alert names as .NET spells them. A match is stored by this spelling, never as the text found.
    private static readonly string[] AlertNames =
    [
        "CloseNotify", "UnexpectedMessage", "BadRecordMac", "DecryptionFailed", "RecordOverflow",
        "DecompressionFail", "HandshakeFailure", "NoCertificate", "BadCertificate", "UnsupportedCert",
        "CertificateRevoked", "CertificateExpired", "CertificateUnknown", "IllegalParameter", "UnknownCA",
        "AccessDenied", "DecodeError", "DecryptError", "ExportRestriction", "ProtocolVersion",
        "InsufficientSecurity", "InternalError", "InappropriateFallback", "UserCanceled", "NoRenegotiation",
        "MissingExtension", "UnsupportedExtension", "CertificateUnobtainable", "UnrecognizedName",
        "BadCertificateStatusResponse", "BadCertificateHashValue", "UnknownPskIdentity", "CertificateRequired",
        "NoApplicationProtocol",
    ];

    // The alerts that mean "I do not accept the certificate you sent".
    private static readonly string[] RejectingAlerts =
    [
        "UnknownCA", "BadCertificate", "CertificateUnknown", "CertificateExpired", "UnsupportedCert", "CertificateRevoked",
    ];

    /// <param name="exception">What <c>AuthenticateAsServerAsync</c> threw.</param>
    /// <param name="helloSeen">True when a complete ClientHello was read (the certificate-selection callback ran).</param>
    /// <param name="budgetExpired">True when the handshake budget, not a stop, cancelled the handshake.</param>
    public static TlsFailureInfo Classify(Exception exception, bool helloSeen, bool budgetExpired)
    {
        var phase = helloSeen ? TlsFailurePhase.InHandshake : TlsFailurePhase.BeforeHello;
        if (budgetExpired) return new TlsFailureInfo(TlsFailureKind.Timeout, phase);

        uint? secStatus = null;
        string? alert = null;
        var notTls = false;
        var depth = 0;
        for (var current = exception; current is not null && depth < MaxChainDepth; current = current.InnerException, depth++)
        {
            if (current is Win32Exception win32) secStatus ??= unchecked((uint)win32.NativeErrorCode);
            alert ??= FindAlert(current.Message);
            if (current is AuthenticationException && current.Message.Contains("frame size", StringComparison.Ordinal))
                notTls = true;
        }

        TlsFailureKind kind;
        if ((alert is not null && Array.IndexOf(RejectingAlerts, alert) >= 0)
            || secStatus is SecUntrustedRoot or SecCertUnknown)
            kind = TlsFailureKind.Rejected;
        else if (secStatus == SecNoCommonAlgorithm)
            kind = TlsFailureKind.NoCommonProtocol;
        else if (notTls)
            kind = TlsFailureKind.NotTls;
        else if (exception is IOException or SocketException)
            kind = helloSeen ? TlsFailureKind.ClosedInHandshake : TlsFailureKind.ClosedEarly;
        else
            kind = TlsFailureKind.Other;

        return new TlsFailureInfo(kind, phase, alert, secStatus);
    }

    /// <summary>The SNI if it is a plain host name (letters, digits, dot, hyphen, underscore, at most
    /// 253 characters), else null: control characters, non-ASCII (including an IDN in its Unicode form),
    /// spaces and over-long values are dropped whole rather than cut or escaped. The value is
    /// client-chosen, so nothing else is ever kept.</summary>
    public static string? SanitizeSni(string? sni)
    {
        if (sni is null || sni.Length == 0 || sni.Length > MaxSniLength) return null;
        foreach (var c in sni)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '-' or '_'))
                return null;
        }
        return sni;
    }

    private static string? FindAlert(string message)
    {
        var start = message.IndexOf(AlertMarker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += AlertMarker.Length;
        var end = message.IndexOf('\'', start);
        if (end < 0 || end - start is 0 or > LongestAlertName) return null;

        var found = message.AsSpan(start, end - start);
        foreach (var name in AlertNames)
        {
            if (found.SequenceEqual(name)) return name;
        }
        return null;
    }
}
