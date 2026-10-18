namespace Piper.Core.Security;

/// <summary>
/// Removing the Piper root from the trusted store is a security-relevant change the user must
/// confirm. The confirmation lives here, apart from any window, so a test can check that declining
/// removes nothing.
/// </summary>
public static class RootRemoval
{
    /// <summary>
    /// Calls <paramref name="uninstall"/> only when <paramref name="confirm"/> returns true.
    /// Returns how many certificates it removed, or null when the user declined.
    /// </summary>
    public static int? RemoveIfConfirmed(Func<bool> confirm, Func<int> uninstall)
    {
        ArgumentNullException.ThrowIfNull(confirm);
        ArgumentNullException.ThrowIfNull(uninstall);
        return confirm() ? uninstall() : null;
    }
}
