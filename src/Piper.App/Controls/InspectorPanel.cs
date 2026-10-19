using System.Text;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;

namespace Piper.App.Controls;

/// <summary>Request above, response below, for the currently selected session.</summary>
public sealed class InspectorPanel : UserControl
{
    private readonly MessageInspector _request = new(Strings.Inspector.Request, showWebForms: true) { Dock = DockStyle.Fill };
    private readonly MessageInspector _response = new(Strings.Inspector.Response, showImageViewer: true) { Dock = DockStyle.Fill };

    /// <summary>Raised when the selected session's timing/transfer summary changes.</summary>
    public event EventHandler? TimingChanged;

    /// <summary>The selected session's timing/transfer summary, for the main status bar.</summary>
    public string TimingText { get; private set; } = string.Empty;

    public InspectorPanel()
    {
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 4,
        };
        _split.Panel1.Controls.Add(_request);
        _split.Panel2.Controls.Add(_response);

        Controls.Add(_split);
    }

    private readonly SplitContainer _split;
    private bool _splitPositioned;

    /// <summary>
    /// Splits request and response evenly once the panel has a real height. Setting this
    /// in the constructor is silently clamped against the 150px design-time default.
    /// </summary>
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_splitPositioned || _split.Height <= 200) return;
        _split.SplitterDistance = _split.Height / 2;
        _splitPositioned = true;
    }

    public void Show(Session? session)
    {
        if (session is null)
        {
            _request.SetMessage(null, Strings.Inspector.Request);
            _response.SetMessage(null, Strings.Inspector.Response);
            SetTimingText(string.Empty);
            return;
        }

        _request.SetMessage(session.Request,
            session.Request is null ? Strings.Inspector.Request : Strings.Inspector.RequestSummary(session.Request.StartLine));

        ShowResponse(session, reload: false);
        SetTimingText(BuildTimingLine(session));
    }

    /// <summary>
    /// Brings the session already on display up to date while it is in flight: its figures while
    /// they move, and, when <paramref name="reload"/> says its state has changed, the response it
    /// has now. The request never changes once sent, so it is left alone.
    /// </summary>
    public void Refresh(Session session, bool reload)
    {
        ShowResponse(session, reload);
        SetTimingText(BuildTimingLine(session));
    }

    private void ShowResponse(Session session, bool reload)
    {
        // State first: the proxy publishes the response before the state that describes it.
        var state = session.State;
        if (session.Response is not { } response)
        {
            if (session.TlsFailure is { } tlsFailure)
            {
                _response.SetNotes(TlsFailureText.Summary(tlsFailure), TlsFailureText.Panel(tlsFailure, session.Host, session.ProcessName));
                return;
            }

            _response.SetMessage(null, state switch
            {
                SessionState.Failed => Strings.Inspector.ResponseFailed(session.Error, CertificateFailureHint.For(session.Error)),
                SessionState.Tunnel => Strings.Inspector.ResponseTunnel,
                _ => Strings.Inspector.ResponseWaiting,
            });
            return;
        }

        var summary = state == SessionState.ReceivingBody
            ? Strings.Inspector.ResponseReceiving(response.StartLine,
                Format.ProgressDetail(session.BytesReceived, session.ExpectedResponseBytes))
            : Strings.Inspector.ResponseSummary(response.StartLine);

        if (!ReferenceEquals(_response.Message, response)) _response.SetMessage(response, summary);
        else if (reload) _response.Reload(response, summary);
        else _response.SetSummary(summary);
    }

    private void SetTimingText(string value)
    {
        if (TimingText == value) return;
        TimingText = value;
        TimingChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string BuildTimingLine(Session session)
    {
        var sb = new StringBuilder();
        sb.Append(Strings.Inspector.TimingStarted(session.Started));
        sb.Append(Strings.Inspector.TimingTotal(session.Duration.TotalMilliseconds));

        if (session.ConnectTime is { } connect)
            sb.Append(Strings.Inspector.TimingConnect(connect.TotalMilliseconds));
        if (session.TimeToFirstByte is { } ttfb)
            sb.Append(Strings.Inspector.TimingTimeToFirstByte(ttfb.TotalMilliseconds));

        sb.Append(Strings.Inspector.TimingUp(FormatBytes(session.RequestSize)));
        sb.Append(Strings.Inspector.TimingDown(session.State == SessionState.ReceivingBody
            ? Format.ProgressDetail(session.BytesReceived, session.ExpectedResponseBytes)
            : FormatBytes(session.ResponseSize)));

        if (session.ServerEndpoint is { } endpoint) sb.Append(Strings.Inspector.TimingServer(endpoint));
        if (session.IsComposed) sb.Append(Strings.Inspector.TimingComposed);

        return sb.ToString();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => Strings.Units.Bytes(bytes),
        < 1024 * 1024 => Strings.Units.Kilobytes(bytes / 1024.0),
        _ => Strings.Units.Megabytes(bytes / (1024.0 * 1024)),
    };
}
