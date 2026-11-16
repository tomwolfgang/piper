using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Piper.App.Theme;

namespace Piper.App.Layout;

/// <summary>
/// A window holding one floating panel, in a one-page strip of its own with the tab row hidden, so the
/// strip still takes dropped sessions while the title bar alone names the panel. The title bar stands in
/// for the tab: right-click it for the tab menu, double-click it to dock, or drag it over an area's tabs
/// to dock the panel there. It is owned by the main window, so it minimises with Piper and stays above
/// it, and it is not a tool window, so it shows in Alt+Tab; it has no icon and no minimise or maximise
/// box, as a tool window would not. The page is only lent to it: whenever the window closes or is
/// disposed the page is taken out first and goes back to the <see cref="PanelRegistry"/>, which alone
/// disposes panels.
/// </summary>
internal sealed class FloatingPanelForm : Form
{
    private readonly TabPage _page;
    private readonly Func<Keys, bool> _processShortcut;

    /// <summary>Set once the window is closing because the layout changed, so closing it must not ask to dock.</summary>
    private bool _closingSilently;
    private bool _closed;

    /// <param name="processShortcut">The main window's shortcut dispatcher; true when it handled the key.</param>
    /// <param name="keyDown">The main window's <c>KeyDown</c> handler, so keys it previews work here too.</param>
    public FloatingPanelForm(Form owner, TabPage page, Func<Keys, bool> processShortcut, KeyEventHandler keyDown)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(processShortcut);
        ArgumentNullException.ThrowIfNull(keyDown);
        _page = page;
        _processShortcut = processShortcut;

        Owner = owner;
        ShowInTaskbar = false;
        // Sizable, not SizableToolWindow: a tool window's WS_EX_TOOLWINDOW takes it out of Alt+Tab.
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(PanelLayout.MinFloatingWidth, PanelLayout.MinFloatingHeight);
        // Still set: Alt+Tab shows it even though the title bar does not.
        Icon = owner.Icon;
        Text = page.Text;
        KeyPreview = true;
        KeyDown += keyDown;

        // Not a tab stop: with its tab row hidden it would take the focus with nothing to show for it,
        // so the focus goes to the panel's content. Shift+F10 there still opens the tab menu (WndProc).
        Strip = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont, ShowTabs = false, TabStop = false };
        Strip.TabPages.Add(page);
        Controls.Add(Strip);
    }

    /// <summary>
    /// Raised when the user asks for the panel to go back to its area: by closing the window, by
    /// double-clicking its title bar. Not raised when the owner closes or while Piper shuts down.
    /// </summary>
    public event EventHandler? DockRequested;

    /// <summary>
    /// Raised with a screen point for the panel's tab menu: where the title bar was right-clicked, or the
    /// panel's top-left corner for Shift+F10 or the Menu key.
    /// </summary>
    public event EventHandler<Point>? MenuRequested;

    /// <summary>Raised with the pointer's screen position as the user drags the window by its title bar.</summary>
    public event EventHandler<Point>? CaptionDragging;

    /// <summary>
    /// Raised when a drag by the title bar ends, with the pointer's screen position and whether the
    /// window moved: Esc puts it back where it started, and that is not a drop.
    /// </summary>
    public event EventHandler<(Point Screen, bool Moved)>? CaptionDragEnded;

    /// <summary>Where the pointer is; tests replace it, as they cannot run the system's move loop.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Func<Point> PointerPosition { get; set; } = () => Cursor.Position;

    private Point _moveStart;
    private bool _captionDragging;

    /// <summary>The window's one-page strip.</summary>
    public DarkTabControl Strip { get; }

    /// <summary>Set while Piper shuts down: closing then hands the page back without docking it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShuttingDown { get; set; }

    /// <summary>Opening a floating window must not take the focus from the grid.</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Shows the window, restoring it if it is minimised, and brings it to the front without activating
    /// it, so the grid keeps the focus. <see cref="Control.BringToFront"/> is not used: on a top-level
    /// window it activates it, as setting <see cref="Form.WindowState"/> can.
    /// </summary>
    public void Reveal()
    {
        Show();
        if (!IsHandleCreated) return;
        if (WindowState == FormWindowState.Minimized) ShowWindow(Handle, SwShowNoActivate);
        SetWindowPos(Handle, HwndTop, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    private const nint HwndTop = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const int SwShowNoActivate = 4;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>Hands the page back and closes the window without asking to dock, because the layout already moved the panel.</summary>
    public void CloseWithoutDocking()
    {
        _closingSilently = true;
        ReleasePage();
        if (!_closed && !IsDisposed) Close();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Apply also gives the window the dark title bar (Palette.ApplyWindowChrome).
        Palette.Apply(this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        _processShortcut(keyData) || base.ProcessCmdKey(ref msg, keyData);

    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmNcRButtonUp = 0x00A5;
    private const int WmContextMenu = 0x007B;
    private const int WmMoving = 0x0216;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int HtCaption = 2;

    /// <summary>The title bar stands in for the hidden tab: double-click docks, right-click opens the tab menu, dragging can dock.</summary>
    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmNcLButtonDblClk when m.WParam == HtCaption:
                // With no maximise box Windows would do nothing; docking closes this window, so not inside its own message.
                BeginInvoke(() =>
                {
                    if (!_closed && !IsDisposed && !ShuttingDown) DockRequested?.Invoke(this, EventArgs.Empty);
                });
                return;
            case WmNcRButtonUp when m.WParam == HtCaption:
                // Instead of the system menu, which Alt+Space still opens.
                MenuRequested?.Invoke(this, ScreenPoint(m.LParam));
                return;
            case WmContextMenu when m.LParam == -1:
                // Shift+F10 or the Menu key in a control with no menu of its own, bubbled up: the keyboard's
                // way to the tab menu, shown at the top-left of the panel. A right-click is left alone.
                MenuRequested?.Invoke(this, PointToScreen(Point.Empty));
                return;
            case WmEnterSizeMove:
                _moveStart = Location;
                _captionDragging = false;
                break;
            case WmMoving:
                _captionDragging = true;
                if (!ShuttingDown) CaptionDragging?.Invoke(this, PointerPosition());
                break;
            case WmExitSizeMove:
                if (_captionDragging)
                {
                    _captionDragging = false;
                    CaptionDragEnded?.Invoke(this, (PointerPosition(), Location != _moveStart));
                }
                break;
        }

        base.WndProc(ref m);
    }

    private static Point ScreenPoint(nint lParam) => new((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _closed = true;
        ReleasePage();
        base.OnFormClosed(e);
        // Only the user's own close docks: an owner closing, Windows shutting down, Task Manager or
        // Application.Exit must leave the layout as it was saved, floating.
        if (!_closingSilently && !ShuttingDown && e.CloseReason == CloseReason.UserClosing)
            DockRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        // The page goes back before the window's own disposal could reach the panel inside it.
        if (disposing) ReleasePage();
        base.Dispose(disposing);
    }

    private void ReleasePage()
    {
        if (_page.Parent == Strip) Strip.TabPages.Remove(_page);
    }
}
