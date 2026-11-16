using System.Windows.Forms;

namespace Piper.App;

/// <summary>Whether the keyboard is typing into a text box, where Ctrl+X, Ctrl+R and friends belong to the box.</summary>
internal static class TextInputFocus
{
    /// <summary>
    /// Resolves the focused control from <paramref name="activeForm"/>, the window that has the keyboard:
    /// the main window or a floating panel's. <see cref="ContainerControl.ActiveControl"/> stops at the
    /// nearest container (every panel here is a UserControl), so walk down to the control that has focus.
    /// </summary>
    internal static bool IsTextInputFocused(Form? activeForm)
    {
        Control? focused = activeForm?.ActiveControl;
        while (focused is ContainerControl { ActiveControl: { } inner } and not UpDownBase) focused = inner;

        // Read-only boxes count too: someone reading a body in the inspector who reaches for Ctrl+X
        // expects nothing, or at most a copy, not an empty capture.
        return focused switch
        {
            TextBoxBase => true,
            ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList } => true,
            UpDownBase => true,
            _ => false,
        };
    }
}
