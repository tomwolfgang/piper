using System.Windows.Forms;
using Piper.App.Theme;

namespace Piper.App;

/// <summary>
/// Shows a SAZ export that runs on a worker thread, with Cancel. It closes itself once the work has
/// ended, however it ended, so the caller never resumes while the worker still holds its temporary file.
/// </summary>
internal sealed class SazExportProgressDialog : Form
{
    private readonly Task _work;
    private readonly CancellationTokenSource _cancellation;
    private readonly Label _message;
    private readonly ProgressBar _bar;
    private readonly Button _cancel;
    private bool _finished;

    public SazExportProgressDialog(string fileName, int total, Task work, CancellationTokenSource cancellation)
    {
        _work = work;
        _cancellation = cancellation;

        Text = Strings.SessionList.SaveSessionsProgressCaption;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(420, 130);
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Font = Palette.UiFont;

        _message = new Label
        {
            Text = Strings.SessionList.SaveSessionsProgress(total, fileName),
            Dock = DockStyle.Top,
            AutoEllipsis = true,
            Height = 28,
        };
        _bar = new ProgressBar { Dock = DockStyle.Top, Maximum = Math.Max(1, total), Height = 18 };
        var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 14, 16, 0) };
        content.Controls.Add(_bar);
        content.Controls.Add(_message);

        _cancel = new Button
        {
            Text = Strings.Common.Cancel,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14, 5, 14, 5),
            Dock = DockStyle.Right,
        };
        _cancel.Click += (_, _) => RequestCancel();
        var footer = new Panel { Dock = DockStyle.Bottom, Height = _cancel.PreferredSize.Height + 20, Padding = new Padding(12, 10, 12, 10) };
        footer.Controls.Add(_cancel);

        Controls.Add(content);
        Controls.Add(footer);
        CancelButton = _cancel;

        Palette.ScaleDialogSize(this);
        Palette.Apply(this);
    }

    /// <summary>Moves the bar to <paramref name="written"/> sessions; safe to call after the dialog closed.</summary>
    public void Report(int written)
    {
        if (!IsDisposed) _bar.Value = Math.Clamp(written, 0, _bar.Maximum);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // WhenAny never throws: the caller awaits the work itself and reports how it ended.
        await Task.WhenAny(_work);
        _finished = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_finished)
        {
            RequestCancel();
            // Escape, Alt+F4 and the title-bar X all mean Cancel. The dialog stays until the worker
            // has stopped; only Windows shutting down or the app exiting may close it sooner.
            if (e.CloseReason is CloseReason.UserClosing or CloseReason.None) e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    private void RequestCancel()
    {
        if (_cancellation.IsCancellationRequested) return;
        _cancellation.Cancel();
        _cancel.Enabled = false;
        _message.Text = Strings.SessionList.SaveSessionsCancelling;
    }
}
