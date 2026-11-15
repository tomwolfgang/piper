using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Piper.App.Theme;

namespace Piper.App.Layout;

/// <summary>
/// A window holding one floating panel, in a one-page strip of its own so its tab still has the tab
/// menu and still takes dropped sessions. It is owned by the main window, so it minimises with Piper
/// and stays above it, and it is not a tool window, so it shows in Alt+Tab. The page is only lent to
/// it: whenever the window closes or is disposed the page is taken out first and goes back to the
/// <see cref="PanelRegistry"/>, which alone disposes panels.
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
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = new Size(PanelLayout.MinFloatingWidth, PanelLayout.MinFloatingHeight);
        Icon = owner.Icon;
        Text = page.Text;
        KeyPreview = true;
        KeyDown += keyDown;

        Strip = new DarkTabControl { Dock = DockStyle.Fill, Font = Palette.UiFont };
        Strip.TabPages.Add(page);
        Strip.MouseDoubleClick += OnStripDoubleClick;
        Controls.Add(Strip);
    }

    /// <summary>
    /// Raised when the user asks for the panel to go back to its area: by closing the window, by
    /// double-clicking its tab. Not raised when the owner closes or while Piper shuts down.
    /// </summary>
    public event EventHandler? DockRequested;

    /// <summary>The window's one-page strip.</summary>
    public DarkTabControl Strip { get; }

    /// <summary>Set while Piper shuts down: closing then hands the page back without docking it.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShuttingDown { get; set; }

    /// <summary>Opening a floating window must not take the focus from the grid.</summary>
    protected override bool ShowWithoutActivation => true;

    /// <summary>
    /// Shows the window and brings it to the front without activating it, so the grid keeps the focus.
    /// <see cref="Control.BringToFront"/> is not used: on a top-level window it activates it.
    /// </summary>
    public void Reveal()
    {
        Show();
        if (IsHandleCreated) SetWindowPos(Handle, HwndTop, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
    }

    private const nint HwndTop = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

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

    private void OnStripDoubleClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || Strip.TabIndexAt(e.Location) < 0) return;
        // Docking closes this window, which must not happen inside its own strip's mouse handling.
        BeginInvoke(() =>
        {
            if (!_closed && !IsDisposed && !ShuttingDown) DockRequested?.Invoke(this, EventArgs.Empty);
        });
    }
}
