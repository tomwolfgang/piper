using System.Security.Cryptography;
using System.Windows.Forms;
using Piper.App.Controls;
using Piper.App.Theme;
using Piper.Core.Security;

namespace Piper.App;

/// <summary>
/// The HTTPS trust indicator: what the status bar and the warning bar say, and the startup prompt.
/// Installing or removing the root is still done only by <c>TrustRootCertificate</c> and
/// <c>UntrustRootCertificate</c>, each behind a confirmation; nothing here calls the trust store's
/// write methods.
/// </summary>
public sealed partial class MainForm
{
    /// <summary>
    /// How long a trust-store read is reused. The status bar asks every second; the Windows store is
    /// read only this often, or at once after the user trusts or removes the root.
    /// </summary>
    private static readonly TimeSpan TrustCheckLifetime = TimeSpan.FromSeconds(30);

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
            else ShowConfigurations();
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
    /// Brings the proxy and the indicator in line with the (cached) trust state and returns it: HTTPS
    /// is decrypted only while the option is on and the root is trusted; otherwise it is tunnelled
    /// unread rather than failing every client's handshake. The status timer passes
    /// <paramref name="refreshInBackground"/> so an aged-out answer is re-read on a worker thread and
    /// the UI thread never waits on the certificate store (antivirus or policy hooks can make it slow).
    /// </summary>
    private HttpsState ApplyTrustState(bool refreshInBackground = false)
    {
        bool trusted;
        if (refreshInBackground && _trust.NeedsRead && _trust.Last is { } known)
        {
            QueueTrustRead();
            trusted = known;
        }
        else
        {
            trusted = _trust.IsTrusted;
        }

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

    private bool _trustReadQueued;

    /// <summary>Re-reads trust off the UI thread, then applies the fresh answer back on it.</summary>
    private void QueueTrustRead()
    {
        if (_trustReadQueued) return;
        _trustReadQueued = true;

        _ = Task.Run(() =>
        {
            try
            {
                _ = _trust.IsTrusted;
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
            {
                // Keep showing the last answer; the next tick asks again.
            }
        }).ContinueWith(_ =>
        {
            try
            {
                BeginInvoke(() =>
                {
                    _trustReadQueued = false;
                    ApplyTrustState();
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // The window is closing; nothing is left to update.
            }
        }, TaskScheduler.Default);
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
