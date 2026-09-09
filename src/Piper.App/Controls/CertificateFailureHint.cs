namespace Piper.App.Controls;

/// <summary>A short, actionable nudge for a session that failed because Piper rejected the
/// origin's certificate -- otherwise the raw SslPolicyErrors text reads as a dead end with no
/// next step for whoever's looking at it.</summary>
internal static class CertificateFailureHint
{
    /// <summary>Empty unless <paramref name="error"/> is a certificate rejection (see
    /// <c>CertificateRejectionDetail</c> in Piper.Core, which is what puts "RemoteCertificate..."
    /// into the message in the first place). This only ever fires while the toggle it points at
    /// is on -- when it's off, the callback never rejects a certificate, so this text can't appear.</summary>
    public static string For(string? error) =>
        error is not null && error.Contains("RemoteCertificate", StringComparison.Ordinal)
            ? "  ->  If you trust this origin, turn off \"Verify origin server certificates\" in Configurations > HTTPS."
            : string.Empty;
}
