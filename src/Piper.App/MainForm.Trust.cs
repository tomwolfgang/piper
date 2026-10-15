using System.Windows.Forms;
using Piper.Core.Security;

namespace Piper.App;

/// <summary>
/// What Piper does about the trust of its root: the cached trust read, applying it to the proxy,
/// and the startup prompt. Installing or removing the root is still done only by
/// <c>TrustRootCertificate</c> and <c>UntrustRootCertificate</c>; nothing here calls the trust
/// store's write methods.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>
    /// How long a trust-store read is reused. The status bar asks every second; the Windows store is
    /// read only this often, or at once after the user trusts or removes the root.
    /// </summary>
    private static readonly TimeSpan TrustCheckLifetime = TimeSpan.FromSeconds(30);

    private readonly TrustStateCache _trust;
    private HttpsState _httpsState;
    private bool? _lastTrusted;

    /// <summary>
    /// Re-reads trust now. Call after the user trusts or removes the root, and before capture starts.
    /// </summary>
    private void RefreshTrustState()
    {
        _trust.Invalidate();
        ApplyTrustState();
    }

    /// <summary>
    /// Brings the proxy in line with the (cached) trust state and returns it: HTTPS is decrypted only
    /// while the option is on and the root is trusted; otherwise it is tunnelled unread rather than
    /// failing every client's handshake.
    /// </summary>
    private HttpsState ApplyTrustState()
    {
        var trusted = _trust.IsTrusted;
        _options.DecryptionSuspended = !trusted;

        if (_lastTrusted is { } before && before != trusted)
            AppendLog(trusted ? Strings.Log.RootNowTrusted : Strings.Log.RootNoLongerTrusted);
        _lastTrusted = trusted;

        _httpsState = TrustPolicy.StateOf(_options.DecryptHttps, trusted);
        return _httpsState;
    }

    /// <summary>
    /// The three-way startup question. Closing the dialog is "Not now": it neither installs anything
    /// nor silences the prompt.
    /// </summary>
    private TrustPromptChoice ShowTrustPrompt()
    {
        var install = new TaskDialogButton(Strings.Certificates.StartupTrustInstall);
        var notNow = new TaskDialogButton(Strings.Certificates.StartupTrustNotNow);
        var dontAskAgain = new TaskDialogButton(Strings.Certificates.StartupTrustDontAsk);
        var page = new TaskDialogPage
        {
            Caption = Strings.Certificates.StartupTrustCaption,
            Text = Strings.Certificates.StartupTrustBody,
            Icon = TaskDialogIcon.Warning,
            AllowCancel = true,
            DefaultButton = notNow,
            Buttons = { install, notNow, dontAskAgain },
        };

        var pressed = TaskDialog.ShowDialog(this, page);
        return pressed == install ? TrustPromptChoice.InstallCertificate
            : pressed == dontAskAgain ? TrustPromptChoice.DontAskAgain
            : TrustPromptChoice.NotNow;
    }
}
