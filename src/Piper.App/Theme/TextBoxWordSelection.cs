using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Piper.App.Theme;

/// <summary>
/// Makes URI and header punctuation a boundary when selecting text in a standard text box.
/// </summary>
internal static class TextBoxWordSelection
{
    private static readonly ConditionalWeakTable<TextBox, SelectionState> States = [];

    /// <summary>
    /// Enables the selection behavior once for a text box.
    /// </summary>
    public static void Configure(TextBox textBox)
    {
        ArgumentNullException.ThrowIfNull(textBox);
        States.GetValue(textBox, static box => new SelectionState(box));
    }

    private sealed class SelectionState : NativeWindow
    {
        private const int EmSetSel = 0x00B1;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonDoubleClick = 0x0203;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;

        private readonly TextBox _textBox;
        private int _anchor;
        private int _caret;
        private int _selectionStart;
        private int _selectionLength;
        private bool _selectionIsOurs;
        private bool _settingSelection;

        public SelectionState(TextBox textBox)
        {
            _textBox = textBox;
            textBox.HandleCreated += OnHandleCreated;
            textBox.HandleDestroyed += OnHandleDestroyed;
            textBox.TextChanged += (_, _) => _selectionIsOurs = false;
            if (textBox.IsHandleCreated) AssignHandle(textBox.Handle);
        }

        private void OnHandleCreated(object? sender, EventArgs e) => AssignHandle(_textBox.Handle);

        private void OnHandleDestroyed(object? sender, EventArgs e)
        {
            _selectionIsOurs = false;
            ReleaseHandle();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmLButtonDown) _selectionIsOurs = false;
            if (m.Msg == EmSetSel && !_settingSelection) _selectionIsOurs = false;

            if (m.Msg == WmLButtonDoubleClick)
            {
                var position = m.LParam.ToInt64();
                var location = new Point(unchecked((short)position), unchecked((short)(position >> 16)));
                SelectWordAt(location);
                m.Result = 0;
                return;
            }

            // On bidirectional Windows installations, the native edit control treats Ctrl+Shift
            // itself as a reading-order shortcut. Keep the modifier state for other shortcuts but
            // do not deliver those modifier messages to the edit control.
            if (m.Msg is WmKeyDown or WmKeyUp && IsModifierKey((Keys)m.WParam)
                && Control.ModifierKeys.HasFlag(Keys.Control)
                && Control.ModifierKeys.HasFlag(Keys.Shift)
                && !Control.ModifierKeys.HasFlag(Keys.Alt))
            {
                m.Result = 0;
                return;
            }

            if (m.Msg == WmKeyDown && m.WParam is (nint)Keys.Left or (nint)Keys.Right)
            {
                var modifiers = Control.ModifierKeys;
                if (!modifiers.HasFlag(Keys.Alt))
                {
                    var direction = (Keys)m.WParam;
                    if (modifiers.HasFlag(Keys.Control))
                    {
                        MoveWord(direction, modifiers.HasFlag(Keys.Shift));
                        m.Result = 0;
                        return;
                    }

                    if (modifiers.HasFlag(Keys.Shift) && OwnsCurrentSelection())
                    {
                        MoveCharacter(direction);
                        m.Result = 0;
                        return;
                    }
                }
            }

            base.WndProc(ref m);
        }

        private static bool IsModifierKey(Keys key) => key is Keys.ControlKey or Keys.LControlKey
            or Keys.RControlKey or Keys.ShiftKey or Keys.LShiftKey or Keys.RShiftKey;

        private void SelectWordAt(Point location)
        {
            var text = _textBox.Text;
            var index = _textBox.GetCharIndexFromPosition(location);
            if ((uint)index >= (uint)text.Length) return;

            var start = index;
            var end = index + 1;
            if (IsWordCharacter(text[index]))
            {
                while (start > 0 && IsWordCharacter(text[start - 1])) start--;
                while (end < text.Length && IsWordCharacter(text[end])) end++;
            }

            Select(start, end);
        }

        private void MoveWord(Keys direction, bool extendSelection)
        {
            var text = _textBox.Text;
            var (anchor, caret) = SelectionEndpoints(text.Length);
            var next = direction == Keys.Left
                ? FindPreviousWordBoundary(text, caret)
                : FindNextWordBoundary(text, caret);
            Select(extendSelection ? anchor : next, next);
        }

        private void MoveCharacter(Keys direction)
        {
            var text = _textBox.Text;
            var next = direction == Keys.Left ? Math.Max(0, _caret - 1) : Math.Min(text.Length, _caret + 1);
            if (direction == Keys.Left && next > 0 && char.IsLowSurrogate(text[next]) && char.IsHighSurrogate(text[next - 1])) next--;
            if (direction == Keys.Right && next < text.Length && char.IsHighSurrogate(text[next - 1]) && char.IsLowSurrogate(text[next])) next++;
            Select(_anchor, next);
        }

        private bool OwnsCurrentSelection() => _selectionIsOurs
            && _selectionStart == _textBox.SelectionStart
            && _selectionLength == _textBox.SelectionLength;

        private (int Anchor, int Caret) SelectionEndpoints(int textLength)
        {
            if (OwnsCurrentSelection())
            {
                return (Math.Clamp(_anchor, 0, textLength), Math.Clamp(_caret, 0, textLength));
            }

            // TextBox exposes an ordered range, not the active caret side. Its normal Select API
            // places the caret at the end, which is the least surprising starting point when the
            // selection originated outside this helper.
            var anchor = _textBox.SelectionStart;
            return (anchor, anchor + _textBox.SelectionLength);
        }

        private void Select(int anchor, int caret)
        {
            // EM_SETSEL accepts either ordering. Track the active caret ourselves so subsequent
            // Shift+arrow movement stays at the same end of a leftward selection.
            _settingSelection = true;
            try { SendMessage(_textBox.Handle, EmSetSel, anchor, caret); }
            finally { _settingSelection = false; }
            _anchor = anchor;
            _caret = caret;
            _selectionStart = Math.Min(anchor, caret);
            _selectionLength = Math.Abs(caret - anchor);
            _selectionIsOurs = true;
        }
    }

    private static int FindPreviousWordBoundary(string text, int index)
    {
        while (index > 0 && !IsWordCharacter(text[index - 1])) index--;
        while (index > 0 && IsWordCharacter(text[index - 1])) index--;
        return index;
    }

    private static int FindNextWordBoundary(string text, int index)
    {
        while (index < text.Length && !IsWordCharacter(text[index])) index++;
        while (index < text.Length && IsWordCharacter(text[index])) index++;
        return index;
    }

    private static bool IsWordCharacter(char value) => char.IsLetterOrDigit(value) || value == '_';

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
}
