namespace Piper.Core.Security;

/// <summary>What Piper is really doing with HTTPS right now.</summary>
public enum HttpsState
{
    /// <summary>Decryption is switched on and Windows trusts the root, so HTTPS bodies are readable.</summary>
    Decrypting,

    /// <summary>
    /// Decryption is switched on and the user chose to decrypt although Windows does not trust the root:
    /// only clients that trust it some other way (their own store or CA file) can connect.
    /// </summary>
    DecryptingUntrusted,

    /// <summary>Decryption is switched on but Windows does not trust the root, so HTTPS is tunnelled unread.</summary>
    NotTrusted,

    /// <summary>The user switched decryption off; HTTPS is tunnelled unread by choice.</summary>
    Off,
}

/// <summary>The three answers to the startup prompt. Closing the prompt counts as <see cref="NotNow"/>.</summary>
public enum TrustPromptChoice
{
    InstallCertificate,
    NotNow,
    DontAskAgain,
}

/// <summary>What the startup trust check decided; see <see cref="TrustPolicy.DecideStartup"/>.</summary>
/// <param name="Prompted">The prompt was shown.</param>
/// <param name="InstallRequested">The user clicked Install; the caller must run the confirmed install.</param>
/// <param name="SuppressFuturePrompts">The user chose Don't ask again; the caller must persist it.</param>
/// <param name="StartCapture">Capture starts. Trust never decides this: plain HTTP is captured either way.</param>
public readonly record struct StartupTrustDecision(
    bool Prompted, bool InstallRequested, bool SuppressFuturePrompts, bool StartCapture);

/// <summary>
/// Pure rules relating the trust of Piper's root, the Decrypt HTTPS option, the opt-in to decrypt
/// without Windows trust, and the startup prompt. Nothing here touches the certificate store:
/// installing or removing the root stays an explicit user action carried out by the caller.
/// </summary>
public static class TrustPolicy
{
    /// <summary>
    /// Whether the proxy must tunnel HTTPS unread because the root is untrusted: when Windows does not
    /// trust it and the user has not opted in to decrypting anyway.
    /// </summary>
    public static bool IsSuspended(bool trusted, bool decryptWhenUntrusted) => !trusted && !decryptWhenUntrusted;

    /// <summary>Decryption is active when it is switched on and the root is trusted or the user opted in without trust.</summary>
    public static bool ShouldDecrypt(bool decryptHttps, bool trusted, bool decryptWhenUntrusted) =>
        decryptHttps && !IsSuspended(trusted, decryptWhenUntrusted);

    public static HttpsState StateOf(bool decryptHttps, bool trusted, bool decryptWhenUntrusted) =>
        !decryptHttps ? HttpsState.Off
        : trusted ? HttpsState.Decrypting
        : decryptWhenUntrusted ? HttpsState.DecryptingUntrusted
        : HttpsState.NotTrusted;

    /// <summary>
    /// Whether the startup prompt is shown. Not when decryption is off (trust would change nothing),
    /// when the root is already trusted, when the user opted in to decrypting without trust, or when
    /// they said Don't ask again.
    /// </summary>
    public static bool ShouldPrompt(bool decryptHttps, bool trusted, bool askAtStartup, bool decryptWhenUntrusted) =>
        decryptHttps && !trusted && askAtStartup && !decryptWhenUntrusted;

    /// <summary>
    /// Runs the startup decision. <paramref name="ask"/> is called only when <see cref="ShouldPrompt"/>
    /// holds. Declining, or choosing Don't ask again, never installs anything and never stops
    /// capture: it starts with decryption off.
    /// </summary>
    public static StartupTrustDecision DecideStartup(bool decryptHttps, bool trusted, bool askAtStartup,
        bool decryptWhenUntrusted, bool captureOnStartup, Func<TrustPromptChoice> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);

        if (!ShouldPrompt(decryptHttps, trusted, askAtStartup, decryptWhenUntrusted))
            return new StartupTrustDecision(false, false, false, captureOnStartup);

        var choice = ask();
        return new StartupTrustDecision(
            Prompted: true,
            InstallRequested: choice == TrustPromptChoice.InstallCertificate,
            SuppressFuturePrompts: choice == TrustPromptChoice.DontAskAgain,
            StartCapture: captureOnStartup);
    }
}
