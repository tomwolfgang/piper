using System.Text;
using System.Windows.Forms;
using Piper.App.Theme;
using Piper.Core.Sessions;

namespace Piper.App.Controls;

/// <summary>Request above, response below, for the currently selected session.</summary>
public sealed class InspectorPanel : UserControl
{
    private readonly MessageInspector _request = new("Request") { Dock = DockStyle.Fill };
    private readonly MessageInspector _response = new("Response", showImageViewer: true) { Dock = DockStyle.Fill };
    private readonly Label _timings;

    public InspectorPanel()
    {
        _timings = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 22,
            ForeColor = Palette.TextDim,
            Font = Palette.Mono,
            Padding = new Padding(6, 4, 0, 0),
        };

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
            SplitterWidth = 4,
        };
        _split.Panel1.Controls.Add(_request);
        _split.Panel2.Controls.Add(_response);

        Controls.Add(_split);
        Controls.Add(_timings);
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
            _request.SetMessage(null, "Request");
            _response.SetMessage(null, "Response");
            _timings.Text = string.Empty;
            return;
        }

        _request.SetMessage(session.Request,
            session.Request is null ? "Request" : $"Request   {session.Request.StartLine}",
            session.Host);

        if (session.Response is not null)
        {
            _response.SetMessage(session.Response, $"Response   {session.Response.StartLine}", session.Host);
        }
        else
        {
            _response.SetMessage(null, session.State switch
            {
                SessionState.Failed => $"Response   FAILED - {session.Error}",
                SessionState.Tunnel => "Response   (encrypted tunnel - not decrypted)",
                _ => "Response   (waiting)",
            }, session.Host);
        }

        _timings.Text = BuildTimingLine(session);
    }

    private static string BuildTimingLine(Session session)
    {
        var sb = new StringBuilder();
        sb.Append(session.Started.ToString("HH:mm:ss.fff"));
        sb.Append("   total ").Append($"{session.Duration.TotalMilliseconds:N0} ms");

        if (session.ConnectTime is { } connect)
            sb.Append("   connect ").Append($"{connect.TotalMilliseconds:N0} ms");
        if (session.TimeToFirstByte is { } ttfb)
            sb.Append("   ttfb ").Append($"{ttfb.TotalMilliseconds:N0} ms");

        sb.Append("   up ").Append(FormatBytes(session.RequestSize));
        sb.Append("   down ").Append(FormatBytes(session.ResponseSize));

        if (session.ServerEndpoint is { } endpoint) sb.Append("   server ").Append(endpoint);
        if (session.IsComposed) sb.Append("   [composed]");

        return sb.ToString();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N1} KB",
        _ => $"{bytes / (1024.0 * 1024):N2} MB",
    };
}
