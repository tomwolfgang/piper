using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
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
        private string? _cachedText;
        private int _lastMouseDownCaret = -1;
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
            textBox.TextChanged += (_, _) =>
            {
                _cachedText = null;
                _selectionIsOurs = false;
            };
            if (textBox.IsHandleCreated) AssignHandle(textBox.Handle);
        }

        private void OnHandleCreated(object? sender, EventArgs e) => AssignHandle(_textBox.Handle);

        private void OnHandleDestroyed(object? sender, EventArgs e)
        {
            _cachedText = null;
            _lastMouseDownCaret = -1;
            _selectionIsOurs = false;
            ReleaseHandle();
        }

        private string CurrentText => _cachedText ??= _textBox.Text;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmLButtonDown)
            {
                _selectionIsOurs = false;
                base.WndProc(ref m);
                // The native first click positions a full-width caret. EM_CHARFROMPOS, which
                // GetCharIndexFromPosition uses, exposes only its low 16 index bits.
                _lastMouseDownCaret = _textBox.SelectionStart;
                return;
            }
            if (m.Msg == EmSetSel && !_settingSelection) _selectionIsOurs = false;

            if (m.Msg == WmLButtonDoubleClick)
            {
                var position = m.LParam.ToInt64();
                var location = new Point(unchecked((short)position), unchecked((short)(position >> 16)));
                SelectWordAt(location);
                _lastMouseDownCaret = -1;
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
            var text = CurrentText;
            if (!_textBox.ClientRectangle.Contains(location)) return;
            var index = _textBox.GetCharIndexFromPosition(location);
            if (_lastMouseDownCaret >= 0)
            {
                // Reconstruct the index nearest the first click's 32-bit caret. The two clicks
                // are within the system's double-click rectangle, even across a 64K boundary.
                long fullIndex = ((long)_lastMouseDownCaret & ~0xffffL) | ((long)index & 0xffff);
                if (fullIndex - _lastMouseDownCaret > 32_768) fullIndex -= 65_536;
                if (_lastMouseDownCaret - fullIndex > 32_768) fullIndex += 65_536;
                index = (int)Math.Clamp(fullIndex, 0, text.Length);
            }
            if ((uint)index >= (uint)text.Length) return;

            var start = ScalarStart(text, index);
            var end = NextScalarEnd(text, start);
            if (IsWordScalar(text, start))
            {
                while (start > 0)
                {
                    var previous = PreviousScalarStart(text, start);
                    if (!IsWordScalar(text, previous)) break;
                    start = previous;
                }
                while (end < text.Length && IsWordScalar(text, end)) end = NextScalarEnd(text, end);
            }

            Select(start, end);
        }

        private void MoveWord(Keys direction, bool extendSelection)
        {
            var text = CurrentText;
            var (anchor, caret) = SelectionEndpoints(text.Length);
            var next = direction == Keys.Left
                ? FindPreviousWordBoundary(text, caret)
                : FindNextWordBoundary(text, caret);
            Select(extendSelection ? anchor : next, next);
        }

        private void MoveCharacter(Keys direction)
        {
            var text = CurrentText;
            var next = direction == Keys.Left ? PreviousTextElementStart(text, _caret)
                : _caret + StringInfo.GetNextTextElementLength(text.AsSpan(_caret));
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
        while (index > 0)
        {
            var previous = PreviousScalarStart(text, index);
            if (IsWordScalar(text, previous)) break;
            index = previous;
        }
        while (index > 0)
        {
            var previous = PreviousScalarStart(text, index);
            if (!IsWordScalar(text, previous)) break;
            index = previous;
        }
        return index;
    }

    private static int FindNextWordBoundary(string text, int index)
    {
        index = index < text.Length ? ScalarStart(text, index) : index;
        while (index < text.Length && !IsWordScalar(text, index)) index = NextScalarEnd(text, index);
        while (index < text.Length && IsWordScalar(text, index)) index = NextScalarEnd(text, index);
        return index;
    }

    private static int ScalarStart(string text, int index) => index > 0 && index < text.Length
        && char.IsLowSurrogate(text[index]) && char.IsHighSurrogate(text[index - 1]) ? index - 1 : index;

    private static int PreviousScalarStart(string text, int index) => ScalarStart(text, Math.Max(0, index - 1));

    private static int NextScalarEnd(string text, int index) => index < text.Length - 1
        && char.IsHighSurrogate(text[index]) && char.IsLowSurrogate(text[index + 1]) ? index + 2 : Math.Min(index + 1, text.Length);

    private static int PreviousTextElementStart(string text, int index)
    {
        var start = PreviousScalarStart(text, index);
        while (start > 0 && IsCombiningMark(text, start)) start = PreviousScalarStart(text, start);
        return start;
    }

    private static bool IsWordScalar(string text, int index) => text[index] == '_' ||
        Rune.TryGetRuneAt(text, index, out var rune) &&
        (Rune.IsLetterOrDigit(rune) || IsCombiningMark(Rune.GetUnicodeCategory(rune)));

    private static bool IsCombiningMark(string text, int index) =>
        Rune.TryGetRuneAt(text, index, out var rune) && IsCombiningMark(Rune.GetUnicodeCategory(rune));

    private static bool IsCombiningMark(UnicodeCategory category) => category is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
}
