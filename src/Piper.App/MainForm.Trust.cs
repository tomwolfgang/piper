using System.Windows.Forms;
using Piper.App.Controls;
using Piper.App.Theme;
using Piper.Core.Proxy;
using Piper.Core.Security;

namespace Piper.App;

/// <summary>
/// What Piper does about the trust of its root: the cached trust read, applying it to the proxy and
/// to the status bar and warning bar, and the startup prompt. Installing or removing the root is still done only by
/// <c>TrustRootCertificate</c> and <c>UntrustRootCertificate</c>; nothing here calls the trust
/// store's write methods.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>
    /// How long a trust-store read is reused. The status bar asks every second, so for now the store is
    /// read only at startup, before capture starts, and after the user trusts or removes the root
    /// (each calls Invalidate); a later change in this stack also notices changes made outside Piper.
    /// </summary>
    private static readonly TimeSpan TrustCheckLifetime = TimeSpan.MaxValue;

    private readonly TrustStateCache _trust;
    private ToolStripStatusLabel _httpsStatusLabel = null!;
    private TrustBar _trustBar = null!;
    private HttpsState _httpsState;
    private bool? _lastTrusted;

    /// <summary>Adds the HTTPS state to the status bar and builds the warning bar shown while it is untrusted.</summary>
    private TrustBar BuildTrustUi(StatusStrip statusBar)
    {
        _httpsStatusLabel = new ToolStripStatusLabel
        {
            BorderSides = ToolStripStatusLabelBorderSides.Left,
        };
        _httpsStatusLabel.Click += (_, _) =>
        {
            if (_httpsState == HttpsState.NotTrusted) TrustRootCertificate();
            else ShowConfigurations(httpsTab: true);
        };

        // After the capture state and process scope: the controls that describe what is captured.
        statusBar.Items.Insert(2, _httpsStatusLabel);
        _trustBar = new TrustBar(TrustRootCertificate);
        return _trustBar;
    }

    /// <summary>
    /// Re-reads trust now. Call after the user trusts or removes the root, and before capture starts.
    /// </summary>
    private void RefreshTrustState()
    {
        _trust.Invalidate();
        ApplyTrustState();
    }

    /// <summary>
    /// Brings the proxy and the indicator in line with the cached trust state and returns it: HTTPS is decrypted only
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
        _httpsStatusLabel.Text = Strings.StatusBar.HttpsLabel(_httpsState);
        _httpsStatusLabel.ToolTipText = Strings.StatusBar.HttpsTooltip(_httpsState);
        _httpsStatusLabel.ForeColor = _httpsState switch
        {
            HttpsState.Decrypting => Palette.StatusOk,
            HttpsState.NotTrusted => Palette.StatusClientError,
            _ => Palette.TextDim,
        };
        _trustBar.Visible = _httpsState == HttpsState.NotTrusted;
        _trustBar.ApplyColours();
        return _httpsState;
    }

    /// <summary>
    /// Saves Don't ask again. The settings store is best-effort and swallows I/O failures, so this
    /// reads the file back: when it did not stick the user is told the prompt will return.
    /// </summary>
    private void PersistDontAskAgain()
    {
        _options.AskToTrustCertificate = false;
        try
        {
            ProxyConfigurationSettingsStore.Save(ProxyConfigurationSettings.From(_options));
            if (ProxyConfigurationSettingsStore.Load() is { AskToTrustCertificate: false }) return;
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            // A profile path the store cannot use; reported below like any other failure to save.
        }

        AppendLog(Strings.Log.DontAskAgainNotSaved);
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
