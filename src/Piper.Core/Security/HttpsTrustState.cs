namespace Piper.Core.Security;

/// <summary>What Piper is really doing with HTTPS right now.</summary>
public enum HttpsState
{
    /// <summary>Decryption is switched on and the root is trusted, so HTTPS bodies are readable.</summary>
    Decrypting,

    /// <summary>Decryption is switched on but the root is not trusted, so HTTPS is tunnelled unread.</summary>
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
/// <param name="Decrypt">HTTPS decryption is active for this start. False until the root is actually trusted.</param>
public readonly record struct StartupTrustDecision(
    bool Prompted, bool InstallRequested, bool SuppressFuturePrompts, bool StartCapture, bool Decrypt);

/// <summary>
/// Pure rules relating the trust of Piper's root, the Decrypt HTTPS option and the startup prompt.
/// Nothing here touches the certificate store: installing or removing the root stays an explicit
/// user action carried out by the caller.
/// </summary>
public static class TrustPolicy
{
    /// <summary>Decryption is active only when it is switched on and the root is trusted.</summary>
    public static bool ShouldDecrypt(bool decryptHttps, bool trusted) => decryptHttps && trusted;

    public static HttpsState StateOf(bool decryptHttps, bool trusted) =>
        !decryptHttps ? HttpsState.Off : trusted ? HttpsState.Decrypting : HttpsState.NotTrusted;

    /// <summary>
    /// Whether the startup prompt is shown. Not when decryption is off (trust would change nothing),
    /// when the root is already trusted, or when the user said Don't ask again.
    /// </summary>
    public static bool ShouldPrompt(bool decryptHttps, bool trusted, bool askAtStartup) =>
        decryptHttps && !trusted && askAtStartup;

    /// <summary>
    /// Runs the startup decision. <paramref name="ask"/> is called only when <see cref="ShouldPrompt"/>
    /// holds. Declining, or choosing Don't ask again, never installs anything and never stops
    /// capture: it starts with decryption off.
    /// </summary>
    public static StartupTrustDecision DecideStartup(
        bool decryptHttps, bool trusted, bool askAtStartup, bool captureOnStartup, Func<TrustPromptChoice> ask)
    {
        ArgumentNullException.ThrowIfNull(ask);

        var decrypt = ShouldDecrypt(decryptHttps, trusted);
        if (!ShouldPrompt(decryptHttps, trusted, askAtStartup))
            return new StartupTrustDecision(false, false, false, captureOnStartup, decrypt);

        var choice = ask();
        return new StartupTrustDecision(
            Prompted: true,
            InstallRequested: choice == TrustPromptChoice.InstallCertificate,
            SuppressFuturePrompts: choice == TrustPromptChoice.DontAskAgain,
            StartCapture: captureOnStartup,
            Decrypt: false);
    }
}

/// <summary>
/// Remembers the answer to "is the Piper root trusted?" so the status bar can ask every second without
/// reading the Windows certificate store every second. The answer is re-read when <see cref="Invalidate"/>
/// is called (after the user trusts or removes the root) and otherwise when it is older than the
/// lifetime, which also notices a root trusted or removed outside Piper. Use from one thread.
/// </summary>
public sealed class TrustStateCache(Func<bool> query, TimeSpan lifetime, TimeProvider? clock = null)
{
    private readonly Func<bool> _query = query ?? throw new ArgumentNullException(nameof(query));
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private bool _valid;
    private bool _value;
    private long _readAt;

    /// <summary>How many times the underlying check has run.</summary>
    public int QueryCount { get; private set; }

    public bool IsTrusted
    {
        get
        {
            if (!_valid || _clock.GetElapsedTime(_readAt) >= lifetime)
            {
                _value = _query();
                _readAt = _clock.GetTimestamp();
                _valid = true;
                QueryCount++;
            }

            return _value;
        }
    }

    /// <summary>Forces the next read to ask the store again.</summary>
    public void Invalidate() => _valid = false;
}
