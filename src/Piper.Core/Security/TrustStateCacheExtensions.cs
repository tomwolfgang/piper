using System.Security.Cryptography;

namespace Piper.Core.Security;

/// <summary>How a UI that must never wait on the certificate store reads a <see cref="TrustStateCache"/>.</summary>
public static class TrustStateCacheExtensions
{
    /// <summary>
    /// The answer to paint with. When the cached answer has aged out but an earlier one exists, returns
    /// that one and calls <paramref name="queueRead"/> so the refresh happens elsewhere: the calling
    /// thread does not read the store. With no earlier answer there is nothing to show, so it reads.
    /// </summary>
    public static bool ForDisplay(this TrustStateCache cache, Action queueRead)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(queueRead);

        if (cache.NeedsRead && cache.Last is { } known)
        {
            queueRead();
            return known;
        }

        return cache.IsTrusted;
    }

    /// <summary>
    /// Reads the store if the answer is due, for a worker thread. Returns false when the read failed;
    /// the last answer is kept and nothing is thrown, so a caller can keep painting it and try again later.
    /// </summary>
    public static bool TryRefresh(this TrustStateCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);
        try
        {
            _ = cache.IsTrusted;
            return true;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
