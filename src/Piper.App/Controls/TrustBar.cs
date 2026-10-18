using System.Windows.Forms;
using Piper.App.Theme;

namespace Piper.App.Controls;

/// <summary>
/// A bar under the toolbar that stays visible while HTTPS is switched on but not decrypted because
/// Windows does not trust Piper's certificate. It has no dismiss control: it is gone only when the
/// state it reports is.
/// </summary>
public sealed class TrustBar : Panel
{
    private readonly Label _message;

    public TrustBar(Action trust)
    {
        ArgumentNullException.ThrowIfNull(trust);

        Dock = DockStyle.Top;
        Height = 34;
        Visible = false;
        Padding = new Padding(10, 3, 6, 3);
        AccessibleName = Strings.StatusBar.TrustBarMessage;

        _message = new Label
        {
            Dock = DockStyle.Fill,
            Text = Strings.StatusBar.TrustBarMessage,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var button = new Button
        {
            Dock = DockStyle.Right,
            Text = Strings.StatusBar.TrustBarButton,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        button.Click += (_, _) => trust();

        Controls.Add(_message);
        Controls.Add(button);
    }

    /// <summary>Puts the warning colours back; every <see cref="Palette.Apply"/> walk resets them.</summary>
    public void ApplyColours()
    {
        BackColor = Palette.SurfaceWarning;
        _message.BackColor = Color.Transparent;
        _message.ForeColor = Palette.Text;
    }
}
