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

    /// <summary>Maximum UTF-16 code units examined to keep a drag-time text-element scan bounded.</summary>
    private const int MaxTextElementScan = 4096;

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
        private const int EmLineScroll = 0x00B6;
        private const int WmMouseMove = 0x0200;
        private const int WmLButtonDown = 0x0201;
        private const int WmLButtonUp = 0x0202;
        private const int WmLButtonDoubleClick = 0x0203;
        private const int WmCaptureChanged = 0x0215;
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;

        private readonly TextBox _textBox;
        private readonly System.Windows.Forms.Timer _dragScrollTimer;
        private string? _cachedText;
        private int _lastMouseDownCaret = -1;
        private int _anchor;
        private int _caret;
        private int _selectionStart;
        private int _selectionLength;
        private bool _selectionIsOurs;
        private bool _settingSelection;
        private bool _characterDragActive;
        private int _dragWordStart;
        private int _dragWordEnd;
        private int _dragReference;
        private Point _dragLocation;

        public SelectionState(TextBox textBox)
        {
            _textBox = textBox;
            _dragScrollTimer = new System.Windows.Forms.Timer { Interval = 50 };
            _dragScrollTimer.Tick += OnDragScrollTimerTick;
            textBox.HandleCreated += OnHandleCreated;
            textBox.HandleDestroyed += OnHandleDestroyed;
            textBox.TextChanged += (_, _) =>
            {
                _cachedText = null;
                _selectionIsOurs = false;
                EndCharacterDrag(releaseCapture: true);
            };
            textBox.Disposed += (_, _) => _dragScrollTimer.Dispose();
            if (textBox.IsHandleCreated) Attach();
        }

        private void OnHandleCreated(object? sender, EventArgs e) => Attach();

        private void Attach()
        {
            AssignHandle(_textBox.Handle);
        }

        private void OnHandleDestroyed(object? sender, EventArgs e)
        {
            _cachedText = null;
            _lastMouseDownCaret = -1;
            _selectionIsOurs = false;
            EndCharacterDrag(releaseCapture: false);
            ReleaseHandle();
        }

        private string CurrentText => _cachedText ??= _textBox.Text;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmLButtonDown)
            {
                _selectionIsOurs = false;
                EndCharacterDrag(releaseCapture: true);
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
                // The native edit control would enter its word-drag mode here. Keep the exact URI
                // component selection, but use a captured character drag so extending it does not
                // snap each additional character to a whole word.
                m.Result = 0;
                if (SelectWordAt(location))
                {
                    _dragWordStart = _selectionStart;
                    _dragWordEnd = _selectionStart + _selectionLength;
                    _dragReference = _lastMouseDownCaret >= 0 ? _lastMouseDownCaret : _dragWordStart;
                    _dragLocation = location;
                    _characterDragActive = true;
                    SetCapture(_textBox.Handle);
                }
                _lastMouseDownCaret = -1;
                return;
            }

            if (m.Msg == WmMouseMove && _characterDragActive)
            {
                if ((m.WParam.ToInt64() & 1) != 0)
                {
                    var position = m.LParam.ToInt64();
                    _dragLocation = new Point(unchecked((short)position), unchecked((short)(position >> 16)));
                    ExtendCharacterSelection(_dragLocation);
                    UpdateDragScrollTimer();
                    return;
                }
                EndCharacterDrag(releaseCapture: true);
                base.WndProc(ref m);
                return;
            }

            if (m.Msg == WmLButtonUp && _characterDragActive)
            {
                var position = m.LParam.ToInt64();
                _dragLocation = new Point(unchecked((short)position), unchecked((short)(position >> 16)));
                ExtendCharacterSelection(_dragLocation);
                EndCharacterDrag(releaseCapture: true);
                base.WndProc(ref m);
                return;
            }

            if (m.Msg == WmCaptureChanged) EndCharacterDrag(releaseCapture: false);

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

        private bool SelectWordAt(Point location)
        {
            var text = CurrentText;
            if (!_textBox.ClientRectangle.Contains(location)) return false;
            var index = CharacterIndexAt(location, _lastMouseDownCaret, text.Length);
            if ((uint)index >= (uint)text.Length) return false;

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
            return true;
        }

        private void ExtendCharacterSelection(Point location)
        {
            var text = CurrentText;
            ScrollToward(location);
            var index = CharacterIndexAt(ClampToClient(location), _dragReference, text.Length);
            _dragReference = index;
            if (index <= _dragWordStart)
            {
                Select(_dragWordEnd, TextElementStart(text, index));
            }
            else if (index >= _dragWordEnd)
            {
                Select(_dragWordStart, TextElementEnd(text, index));
            }
            else
            {
                Select(_dragWordStart, _dragWordEnd);
            }
        }

        private void OnDragScrollTimerTick(object? sender, EventArgs e)
        {
            if (!_characterDragActive || !IsOutsideClient(_dragLocation))
            {
                _dragScrollTimer.Stop();
                return;
            }
            ExtendCharacterSelection(_dragLocation);
        }

        private void UpdateDragScrollTimer()
        {
            if (IsOutsideClient(_dragLocation)) _dragScrollTimer.Start();
            else _dragScrollTimer.Stop();
        }

        private void EndCharacterDrag(bool releaseCapture)
        {
            var wasActive = _characterDragActive;
            _characterDragActive = false;
            _dragScrollTimer.Stop();
            if (releaseCapture && wasActive) ReleaseCapture();
        }

        private bool IsOutsideClient(Point location) => !_textBox.ClientRectangle.Contains(location);

        private Point ClampToClient(Point location)
        {
            var client = _textBox.ClientRectangle;
            return new Point(
                Math.Clamp(location.X, client.Left, Math.Max(client.Left, client.Right - 1)),
                Math.Clamp(location.Y, client.Top, Math.Max(client.Top, client.Bottom - 1)));
        }

        private void ScrollToward(Point location)
        {
            var client = _textBox.ClientRectangle;
            var horizontal = _textBox.Multiline && _textBox.WordWrap ? 0
                : location.X < client.Left ? -1 : location.X >= client.Right ? 1 : 0;
            var vertical = location.Y < client.Top ? -1 : location.Y >= client.Bottom ? 1 : 0;
            if (horizontal != 0 || vertical != 0)
                SendMessage(_textBox.Handle, EmLineScroll, horizontal, vertical);
        }

        private int CharacterIndexAt(Point location, int reference, int textLength)
        {
            var index = _textBox.GetCharIndexFromPosition(location);
            if (reference < 0 || index < 0) return Math.Clamp(index, 0, textLength);

            // EM_CHARFROMPOS, which GetCharIndexFromPosition uses, exposes only the low 16 index
            // bits. Reconstruct the nearby 32-bit index for long single-line text boxes.
            long fullIndex = ((long)reference & ~0xffffL) | ((long)index & 0xffff);
            if (fullIndex - reference > 32_768) fullIndex -= 65_536;
            if (reference - fullIndex > 32_768) fullIndex += 65_536;
            return (int)Math.Clamp(fullIndex, 0, textLength);
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

    private static int TextElementStart(string text, int index)
    {
        if (index == 0 || index == text.Length) return index;
        var start = ScalarStart(text, index);
        var lowerBound = Math.Max(0, start - MaxTextElementScan);
        while (start > lowerBound && IsCombiningMark(text, start))
        {
            var previous = PreviousScalarStart(text, start);
            if (previous < lowerBound) break;
            start = previous;
        }
        return start;
    }

    private static int TextElementEnd(string text, int index)
    {
        if (index >= text.Length) return text.Length;
        var start = TextElementStart(text, index);
        var length = Math.Min(MaxTextElementScan, text.Length - start);
        if (length < text.Length - start && char.IsHighSurrogate(text[start + length - 1])
            && char.IsLowSurrogate(text[start + length]))
        {
            length--;
        }
        return start + StringInfo.GetNextTextElementLength(text.AsSpan(start, length));
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

    [DllImport("user32.dll")]
    private static extern nint SetCapture(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();
}
