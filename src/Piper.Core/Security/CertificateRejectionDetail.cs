using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Piper.Core.Security;

/// <summary>
/// Describes why an origin certificate failed validation. SslStream and QuicConnection both
/// collapse a validation-callback rejection into a bare "Authentication failed, see inner
/// exception" -- which has no inner exception with the detail -- so the callback itself must
/// capture the <see cref="SslPolicyErrors"/> flags and chain status before returning false, since
/// nothing downstream can recover them afterward.
/// </summary>
public static class CertificateRejectionDetail
{
    /// <summary>Builds a one-line description. Must be called from inside the validation callback:
    /// the chain is torn down once the callback returns, so its status cannot be read later.</summary>
    public static string Describe(SslPolicyErrors errors, X509Chain? chain)
    {
        var reasons = new List<string> { errors.ToString() };
        if (chain is not null)
        {
            reasons.AddRange(chain.ChainStatus
                .Where(status => status.Status != X509ChainStatusFlags.NoError)
                .Select(status => $"{status.Status}: {status.StatusInformation.Trim()}"));
        }
        return string.Join("; ", reasons);
    }
}
