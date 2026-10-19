using System.Runtime.InteropServices;
using System.Reflection;
using Piper.App;
using Piper.App.Controls;
using Piper.App.Theme;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;

// Regression: after Ctrl+X cleared a grid scrolled down to its newest rows, the next rows were drawn
// partway down the grid or not at all, and stayed that way. The native ListView's scroll origin
// was left past the end when the row count shrank, so later rows sat at a negative top index.
internal static class Program
{
    private const int LvmGetTopIndex = 0x1000 + 39;
    private const int WmMouseMove = 0x0200;
    private const int EmGetWordBreakProc = 0x00D1;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmLButtonDoubleClick = 0x0203;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int GwlExStyle = -20;
    private static int _failures;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hwnd, int msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKeyboardState(byte[] state);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        InstallWinFormsContext();

        if (args is ["--composer-history"])
        {
            RunComposerHistoryTooltipTest();
            return _failures == 0 ? 0 : 1;
        }

        RunTextBoxWordSelectionTest();
        RunTextWizardJwtMenuTest();
        RunComposerHistoryTooltipTest();
        ImageDecodingTests.Run(Check, PumpUntil);
        InspectorSaveTests.Run(Check, PumpUntil);
        WebpViewTests.Run(Check, PumpUntil);
        WebpInspectorTests.Run(Check, PumpUntil);
        VideoViewTests.Run(Check, PumpUntil);
        VideoViewLifecycleTests.Run(Check, PumpUntil);
        VideoInspectorTests.Run(Check, PumpUntil);
        JsonInspectorTests.Run(Check, PumpUntil);
        StatusImagesTests.Run(Check);
        TrustUiTests.Run(Check);
        TlsFailureUiTests.Run(Check, PumpUntil);

        Run("clearing a scrolled grid shows the next rows from the top", (store, grid, list) =>
        {
            store.Clear(); // MainForm's Ctrl+X handler
            PumpUntil(() => list.VirtualListSize == 0); // the grid redraws empty before traffic resumes
            AddSessions(store, 6);
            PumpUntil(() => list.VirtualListSize == 6);
            ExpectRowsFromTop(list, 6);

            AddSessions(store, 6);
            PumpUntil(() => list.VirtualListSize == 12);
            ExpectRowsFromTop(list, 12);
        });

        Run("a filter that matches far fewer rows shows them from the top", (store, grid, list) =>
        {
            grid.FilterText = $"host:{RareHost}"; // every 30th setup row: 10 rows, fewer than the old top row
            PumpUntil(() => list.VirtualListSize < 300);
            ExpectRowsFromTop(list, 10);
        });

        Run("hiding a host from a scrolled grid keeps the rows drawn in place", (store, grid, list) =>
        {
            // "Hide this host": a few rows go, but fewer than would bring the count under the top row.
            var topBefore = (int)SendMessage(list.Handle, LvmGetTopIndex, 0, 0);
            var anchor = list.Items[topBefore - 7].Tag; // scrolled up a little, so the place is not just "the bottom"
            list.EnsureVisible(topBefore - 7);
            Check(SendMessage(list.Handle, LvmGetTopIndex, 0, 0) == topBefore - 7 && ((Session)anchor!).Host != RareHost, "setup: scrolled up to a row that stays");
            grid.SessionHiddenHostsFilter = session => session.Host != RareHost;
            PumpUntil(() => list.VirtualListSize == 290);
            var topAfter = (int)SendMessage(list.Handle, LvmGetTopIndex, 0, 0);
            Check(ReferenceEquals(list.Items[topAfter].Tag, anchor), $"the same session stays at the top (top index {topAfter})");
            var first = list.GetItemRect(topAfter);
            Check(first.Top >= 0 && first.Top <= first.Height * 2, $"the top row is drawn at the top (y={first.Top})");
        });

        Run("hiding the rows at the bottom of a scrolled grid keeps the rest drawn in place", (store, grid, list) =>
        {
            // The top row survives but what is left no longer fills a screen from it.
            grid.SessionHiddenHostsFilter = session => int.Parse(session.Path.AsSpan("/item/".Length)) < 290;
            PumpUntil(() => list.VirtualListSize == 290);
            ExpectBottomInView(list, 290);
        });

        Run("deleting the top row of a scrolled grid keeps the place", (store, grid, list) =>
        {
            var topBefore = (int)SendMessage(list.Handle, LvmGetTopIndex, 0, 0);
            list.EnsureVisible(topBefore - 50);
            var top = (int)SendMessage(list.Handle, LvmGetTopIndex, 0, 0);
            var doomed = (Session)list.Items[top].Tag!;
            store.RemoveAll(session => ReferenceEquals(session, doomed)); // Del
            PumpUntil(() => list.VirtualListSize == 299);
            var topAfter = (int)SendMessage(list.Handle, LvmGetTopIndex, 0, 0);
            Check(topAfter == top, $"the view stays at row {top} (got {topAfter})");
        });

        Run("hiding a host from a grid scrolled a few rows shows the rest from the top", (store, grid, list) =>
        {
            // As recorded: a list a little over a screen, a selected row's host hidden, and what is
            // left now fits on one screen though it still reaches past the old top row.
            var topBefore = (int)SendMessage(list.Handle, LvmGetTopIndex, 0, 0);
            list.SelectedIndices.Add(list.VirtualListSize - 3); // item 27, whose host is hidden
            grid.SessionHiddenHostsFilter = session => int.Parse(session.Path.AsSpan("/item/".Length)) % 3 != 0;
            PumpUntil(() => list.VirtualListSize == 20);
            Check(20 > topBefore, $"setup: the rows left still reach the old top row {topBefore}");
            ExpectRowsFromTop(list, 20);
        }, setupRows: 30);

        Run("Ctrl+A selects every visible session", (store, grid, list) =>
        {
            PressKey(list, Keys.A, controlPressed: true, shift: false);
            Check(list.SelectedIndices.Count == list.VirtualListSize,
                $"Ctrl+A selects all {list.VirtualListSize} visible rows (got {list.SelectedIndices.Count})");
        });

        Run("an imported session stays in the grid under a capture scope and a filterset", (store, grid, list) =>
        {
            // Live traffic is hidden by both; a file the user opened is not live traffic.
            grid.VisibilityFilter = session => session.ProcessName == "browser";
            grid.FiltersetFilter = _ => false;
            PumpUntil(() => list.VirtualListSize == 0);
            Check(list.VirtualListSize == 0, "setup: the scope and the filterset hide captured traffic");

            var imported = new Session
            {
                IsImported = true,
                ProcessName = "Fiddler SAZ",
                Request = new HttpRequestData { Method = "GET", Url = new Uri("http://imported.test/one") },
                Response = new HttpResponseData { StatusCode = 200 },
                Completed = DateTimeOffset.Now,
            };
            store.AddRange([imported]);
            PumpUntil(() => list.VirtualListSize == 1);
            Check(list.VirtualListSize == 1, $"the imported session is listed (got {list.VirtualListSize})");

            AddSessions(store, 3);
            PumpFor(500);
            Check(list.VirtualListSize == 1, $"live traffic stays hidden (got {list.VirtualListSize})");
        });

        RunArrowKeysKeepFocus("arrowing through sessions keeps the keyboard focus in the grid");
        RunPanel("an AutoResponder rule whose pattern timed out is marked broken in the panel");
        RunWindowActivation("a second launch restores the existing Piper window");
        RunAboutDialog("the About dialog identifies the running Piper version");

        Console.WriteLine(_failures == 0 ? "UI tests passed." : $"{_failures} UI check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    private static void RunTextBoxWordSelectionTest()
    {
        const string text = "POST /v1/auth/passwordless/init HTTP/1.1";
        const string word = "passwordless";
        var wordStart = text.IndexOf(word, StringComparison.Ordinal);

        Console.WriteLine("== URI components are individual text-box words");
        using var form = new Form { Width = 600, Height = 100, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        var textBox = new TextBox { Dock = DockStyle.Top, Text = text };
        form.Controls.Add(textBox);
        Palette.Apply(form);
        form.Show();
        Palette.Apply(form); // theme/zoom reapplication must not attach a second message handler

        var point = textBox.GetPositionFromCharIndex(wordStart + 2);
        DoubleClick(textBox, new Point(point.X + 1, point.Y + 1));
        Check(textBox.SelectedText == word, "double-click selects only the URI component");

        // Regression: the double-click was swallowed, so the native control never entered drag mode.
        var dragTarget = text.IndexOf("HTTP", StringComparison.Ordinal) + 2;
        var dragPoint = textBox.GetPositionFromCharIndex(dragTarget);
        DoubleClickAndHold(textBox, new Point(point.X + 1, point.Y + 1));
        Check(textBox.SelectedText == word, "double-click and hold selects only the URI component");
        MouseMove(textBox, new Point(dragPoint.X + 1, dragPoint.Y + 1), leftButton: true);
        Check(textBox.SelectedText == "passwordless/init HTTP",
            "dragging right after a double-click extends by URI component from the word");
        var dragged = (textBox.SelectionStart, textBox.SelectionLength);
        LeftButtonUp(textBox, new Point(dragPoint.X + 1, dragPoint.Y + 1));
        MouseMove(textBox, new Point(point.X + 1, point.Y + 1), leftButton: false);
        Check((textBox.SelectionStart, textBox.SelectionLength) == dragged,
            "moving after the button is released leaves the selection alone");

        var initPoint = textBox.GetPositionFromCharIndex(text.IndexOf("init", StringComparison.Ordinal) + 1);
        var authPoint = textBox.GetPositionFromCharIndex(text.IndexOf("auth", StringComparison.Ordinal) + 1);
        DoubleClickAndHold(textBox, new Point(initPoint.X + 1, initPoint.Y + 1));
        MouseMove(textBox, new Point(authPoint.X + 1, authPoint.Y + 1), leftButton: true);
        LeftButtonUp(textBox, new Point(authPoint.X + 1, authPoint.Y + 1));
        Check(textBox.SelectedText == "auth/passwordless/init",
            "dragging left after a double-click keeps the word and extends by URI component");

        textBox.Select(wordStart, 0);
        PressWordKey(textBox, Keys.Right, shift: true);
        Check(textBox.SelectedText == word, "Ctrl+Shift+Right selects only the URI component");

        textBox.Select(wordStart + word.Length, 0);
        PressWordKey(textBox, Keys.Left, shift: true);
        Check(textBox.SelectedText == word, "Ctrl+Shift+Left selects only the URI component");

        PressWordKey(textBox, Keys.Left, shift: true);
        Check(textBox.SelectedText == "auth/passwordless", "a second Ctrl+Shift+Left extends to the previous component");
        PressWordKey(textBox, Keys.Right, shift: true);
        Check(textBox.SelectedText == "/passwordless", "reversing direction shrinks the selection");

        textBox.Select(wordStart + word.Length, 0);
        PressWordKey(textBox, Keys.Left, shift: true);
        PressKey(textBox, Keys.Right, control: false, shift: true);
        Check(textBox.SelectedText == "asswordless", "Shift+Right continues from the active left edge");

        textBox.Select(wordStart, 0);
        PressWordKey(textBox, Keys.Right, shift: false);
        Check(textBox.SelectionStart == wordStart + word.Length && textBox.SelectionLength == 0,
            "Ctrl+Right moves to the end of the URI component");

        PressWordKey(textBox, Keys.Left, shift: false);
        Check(textBox.SelectionStart == wordStart && textBox.SelectionLength == 0,
            "Ctrl+Left moves to the start of the URI component");

        textBox.Select(wordStart, 0);
        PostWordKey(textBox, Keys.Right, shift: true);
        Check(textBox.SelectedText == word, "posted Ctrl+Shift+Right follows the normal message path");
        var configuredStyle = GetWindowLong(textBox.Handle, GwlExStyle);
        PressDirectionShortcut(textBox, Keys.ShiftKey);
        PressDirectionShortcut(textBox, Keys.ControlKey);
        Check(GetWindowLong(textBox.Handle, GwlExStyle) == configuredStyle,
            "Ctrl+Shift does not switch the native text box to right-to-left reading order");

        textBox.BorderStyle = BorderStyle.None;
        textBox.BorderStyle = BorderStyle.FixedSingle; // recreate the native edit handle
        textBox.Select(0, 0);
        DoubleClick(textBox, new Point(point.X + 1, point.Y + 1));
        Check(textBox.SelectedText == word, "double-click still works after the handle is recreated");

        using var multilineForm = new Form { Width = 600, Height = 120, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        var multiline = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, Text = "GET /health HTTP/1.1\r\n" + text };
        multilineForm.Controls.Add(multiline);
        Palette.Apply(multilineForm);
        multilineForm.Show();
        var multilineWordStart = multiline.Text.IndexOf(word, StringComparison.Ordinal);
        var multilinePoint = multiline.GetPositionFromCharIndex(multilineWordStart + 2);
        DoubleClick(multiline, new Point(multilinePoint.X + 1, multilinePoint.Y + 1));
        Check(multiline.SelectedText == word, "read-only multiline text boxes use the same boundaries");
        multilineForm.Close();

        Console.WriteLine("== large and Unicode text-box selection");
        using var largeForm = new Form { Width = 600, Height = 120, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        var largeText = new string('x', 65_536) + "\r\n" + text;
        var large = new TextBox { Dock = DockStyle.Fill, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both, MaxLength = 200_000, Text = largeText };
        largeForm.Controls.Add(large);
        Palette.Apply(largeForm);
        largeForm.Show();
        var largeWordStart = largeText.IndexOf(word, 65_536, StringComparison.Ordinal);
        large.Select(largeWordStart + 2, 0);
        large.ScrollToCaret();
        var largePoint = large.GetPositionFromCharIndex(largeWordStart + 2);
        Check(large.Text.Length > 65_536 && large.ClientRectangle.Contains(largePoint),
            "setup: the target word past offset 65,536 is visible in a multiline text box");
        DoubleClick(large, new Point(largePoint.X + 1, largePoint.Y + 1));
        Check(large.SelectionStart == largeWordStart && large.SelectedText == word,
            "double-click selects the correct URI component past offset 65,536");
        var largeDragPoint = large.GetPositionFromCharIndex(largeWordStart + word.Length + 2);
        DoubleClickAndHold(large, new Point(largePoint.X + 1, largePoint.Y + 1));
        MouseMove(large, new Point(largeDragPoint.X + 1, largeDragPoint.Y + 1), leftButton: true);
        LeftButtonUp(large, new Point(largeDragPoint.X + 1, largeDragPoint.Y + 1));
        Check(large.SelectionStart == largeWordStart && large.SelectedText == word + "/init",
            "dragging after a double-click past offset 65,536 extends by URI component");
        large.Select(largeWordStart, 0);
        PressWordKey(large, Keys.Right, shift: true);
        Check(large.SelectionStart == largeWordStart && large.SelectedText == word,
            "Ctrl+Shift+Right selects the same component past offset 65,536");
        Console.WriteLine("== the drag word-break proc is bounded and leaves wrapping alone");
        Check(SendMessage(large.Handle, EmGetWordBreakProc, 0, 0) != 0, "a non-wrapping box drags by URI component");
        large.WordWrap = true;
        Check(SendMessage(large.Handle, EmGetWordBreakProc, 0, 0) == 0,
            "a wrapping box keeps the native word break, which its line layout also uses");
        large.Select(largeWordStart + 2, 0);
        large.ScrollToCaret();
        var wrappedPoint = large.GetPositionFromCharIndex(largeWordStart + 2);
        DoubleClickAndHold(large, new Point(wrappedPoint.X + 1, wrappedPoint.Y + 1));
        MouseMove(large, new Point(wrappedPoint.X + 1, wrappedPoint.Y + 1), leftButton: true);
        LeftButtonUp(large, new Point(wrappedPoint.X + 1, wrappedPoint.Y + 1));
        Check(large.SelectionStart == largeWordStart && large.SelectedText == word,
            "in a wrapping box a held double-click still selects only the URI component");
        large.WordWrap = false;
        Check(SendMessage(large.Handle, EmGetWordBreakProc, 0, 0) != 0, "turning wrapping off restores it");

        // One megabyte without a boundary: each callback must look at a bounded window, not the line.
        large.Text = new string('a', 1024 * 1024);
        large.Select(0, 0);
        large.ScrollToCaret();
        var longPoint = large.GetPositionFromCharIndex(10);
        var longDragPoint = large.GetPositionFromCharIndex(40);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        DoubleClickAndHold(large, new Point(longPoint.X + 1, longPoint.Y + 1));
        for (var move = 0; move < 50; move++)
            MouseMove(large, new Point(longDragPoint.X + 1 + move % 2, longDragPoint.Y + 1), leftButton: true);
        LeftButtonUp(large, new Point(longDragPoint.X + 1, longDragPoint.Y + 1));
        Check(watch.Elapsed < TimeSpan.FromSeconds(2), $"dragging in a 1 MiB word stays responsive ({watch.ElapsedMilliseconds} ms)");
        Check(large.SelectionStart == 0 && large.SelectionLength > 0, "the selection still starts at the word");
        largeForm.Close();

        const string astralLetter = "\U00010437";
        var astralText = "GET /v1/" + astralLetter + "flag/path HTTP/1.1";
        var astralWord = astralLetter + "flag";
        var astralStart = astralText.IndexOf(astralWord, StringComparison.Ordinal);
        textBox.Text = astralText;
        var astralPoint = textBox.GetPositionFromCharIndex(astralStart);
        DoubleClick(textBox, new Point(astralPoint.X + 1, astralPoint.Y + 1));
        Check(textBox.SelectedText == astralWord, "double-click keeps a supplementary letter with its word");
        textBox.Select(astralStart, 0);
        PressWordKey(textBox, Keys.Right, shift: true);
        Check(textBox.SelectedText == astralWord, "Ctrl+Shift+Right includes a supplementary letter");
        textBox.Select(astralStart + astralWord.Length, 0);
        PressWordKey(textBox, Keys.Left, shift: true);
        Check(textBox.SelectedText == astralWord, "Ctrl+Shift+Left includes a supplementary letter");

        textBox.Text = "x/\U0001F600/y";
        var emojiPoint = textBox.GetPositionFromCharIndex(2);
        DoubleClick(textBox, new Point(emojiPoint.X + 1, emojiPoint.Y + 1));
        Check(textBox.SelectedText == "\U0001F600", "double-click never selects half a surrogate pair");
        textBox.SelectedText = "Z";
        Check(textBox.Text == "x/Z/y", "editing a selected surrogate pair leaves no orphaned code unit");

        const string combinedWord = "cafe\u0301";
        var combinedText = "GET /v1/" + combinedWord + "/path HTTP/1.1";
        var combinedStart = combinedText.IndexOf(combinedWord, StringComparison.Ordinal);
        textBox.Text = combinedText;
        var combinedPoint = textBox.GetPositionFromCharIndex(combinedStart + 3);
        DoubleClick(textBox, new Point(combinedPoint.X + 1, combinedPoint.Y + 1));
        Check(textBox.SelectedText == combinedWord, "double-click keeps a combining mark with its word");
        textBox.Select(combinedStart, 0);
        PressWordKey(textBox, Keys.Right, shift: true);
        Check(textBox.SelectedText == combinedWord, "Ctrl+Shift+Right includes the combining mark");
        textBox.Select(combinedStart + combinedWord.Length, 0);
        PressWordKey(textBox, Keys.Left, shift: true);
        Check(textBox.SelectedText == combinedWord, "Ctrl+Shift+Left includes the combining mark");
        textBox.SelectedText = "tea";
        Check(textBox.Text == "GET /v1/tea/path HTTP/1.1", "editing a combined word leaves no detached mark");
        form.Close();
    }

    private static void RunComposerHistoryTooltipTest()
    {
        Console.WriteLine("== a truncated Composer history row shows its full text on hover");

        using var form = new Form
        {
            Width = 360,
            Height = 420,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
        };
        var history = new ComposerHistoryTree { Dock = DockStyle.Fill };
        form.Controls.Add(history);
        Palette.Apply(form);
        form.Show();

        var target = "/" + new string('a', 100);
        var request = new HttpRequestData
        {
            Method = "GET",
            Url = new Uri("https://history-tooltip.test" + target),
            RequestTarget = target,
        };
        var sessions = new[]
        {
            new Session
            {
                IsComposed = true,
                Request = request,
                Response = new HttpResponseData { StatusCode = 200 },
                State = SessionState.Complete,
                Completed = DateTimeOffset.Now,
            },
            new Session
            {
                IsComposed = true,
                Request = new HttpRequestData
                {
                    Method = request.Method,
                    Url = request.Url,
                    RequestTarget = request.RequestTarget,
                },
                Response = new HttpResponseData { StatusCode = 200 },
                State = SessionState.Complete,
                Completed = DateTimeOffset.Now,
            },
        };
        history.SetHistory(sessions);

        // The tree normally opens host groups folded. Open this one so the repeated request's
        // count badge consumes some of the target's horizontal space.
        PrivateField<HashSet<string>>(history, "_collapsedHosts").Clear();
        history.Rebuild();

        var list = PrivateField<ListView>(history, "_list");
        PumpUntil(() => list.VirtualListSize == 2);
        var requestBounds = list.GetItemRect(1, ItemBoundsPortion.Entire);

        // The previous fixed 64px reserve missed this narrow range: the repeat count squeezes
        // the target into an ellipsis while the method-and-target label still fits that estimate.
        int WidthOf(string text) => TextRenderer.MeasureText(text, Palette.Mono, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        var right = requestBounds.Right - 4;
        right -= WidthOf("200") + 8;
        right -= WidthOf("×2") + 8;
        var targetAvailable = right - (requestBounds.X + 2 + 14 + 14) - WidthOf("OPTIONS ");
        var previousEstimate = list.Columns[0].Width - 2 - 14 - 14 - 64;
        target = Enumerable.Range(1, 100)
            .Select(length => "/" + new string('a', length))
            .FirstOrDefault(candidate => WidthOf(candidate) > targetAvailable
                && WidthOf("GET " + candidate) <= previousEstimate);
        if (target is null)
        {
            Check(false, "setup: a target fits the old estimate but is ellipsized in the request row");
            return;
        }

        foreach (var session in sessions)
        {
            session.Request!.Url = new Uri("https://history-tooltip.test" + target);
            session.Request.RequestTarget = target;
        }
        history.SetHistory(sessions);
        history.Rebuild();
        PumpUntil(() => list.VirtualListSize == 2);
        requestBounds = list.GetItemRect(1, ItemBoundsPortion.Entire);
        var row = PrivateField<IReadOnlyList<ComposerRow>>(history, "_rows")[1];
        Check(InvokePrivate<bool>(history, "IsLabelTruncated", 1, row),
            "setup: the request target is ellipsized");
        var hover = new Point(requestBounds.Left + Math.Min(100, requestBounds.Width / 2),
            Math.Min(requestBounds.Top + requestBounds.Height / 2, list.ClientSize.Height - 1));
        Check(InvokePrivate<int>(history, "IndexAt", hover) == 1,
            "setup: the hover point belongs to the request row");
        // Storing tooltip text is not enough: WinForms can accept SetToolTip without ever
        // opening a popup. Dispatch the list's mouse events and inspect the native window.
        var rowToolTip = PrivateField<ToolTip>(history, "_rowToolTip");
        var tooltipWindow = PrivateField<NativeWindow>(rowToolTip, "_window");
        // Shorten the delay for this test while still running the real WinForms timer and popup.
        rowToolTip.InitialDelay = 1;
        PrivateField<System.Windows.Forms.Timer>(history, "_rowHoverTimer").Interval = 10;
        form.Activate();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            InvokePrivate(list, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, hover.X, hover.Y, 0));
            PumpUntil(() => IsWindowVisible(tooltipWindow.Handle));
            Check(IsWindowVisible(tooltipWindow.Handle),
                $"the tooltip popup is visible on hover #{attempt + 1}");
            Check(rowToolTip.GetToolTip(history)?.Contains(target, StringComparison.Ordinal) == true,
                "the visible tooltip contains the full request target");
            InvokePrivate(list, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, 100, list.ClientSize.Height - 4, 0));
            Check(!IsWindowVisible(tooltipWindow.Handle), "the popup hides over blank list space");
        }

        // Exercise adjacent truncated rows and scrolling with the pointer stationary. The popup
        // must follow the new row even though the mouse never left the ListView.
        var scrolledSessions = Enumerable.Range(0, 20)
            .Select(index => new Session
            {
                IsComposed = true,
                Request = new HttpRequestData
                {
                    Method = "GET",
                    Url = new Uri($"https://history-tooltip.test/request-{index}/" + new string('b', 80) + "?x=1&y=2"),
                    RequestTarget = $"/request-{index}/" + new string('b', 80) + "?x=1&y=2",
                },
                Response = new HttpResponseData { StatusCode = 200 },
                State = SessionState.Complete,
                Completed = DateTimeOffset.Now,
            })
            .ToArray();
        history.SetHistory(scrolledSessions);
        history.Rebuild();
        const int scrolledRequestIndex = 15;
        list.EnsureVisible(scrolledRequestIndex);
        PumpUntil(() =>
        {
            var bounds = list.GetItemRect(scrolledRequestIndex, ItemBoundsPortion.Entire);
            return bounds.Top < list.ClientSize.Height && bounds.Bottom > 0;
        });
        var scrolledBounds = list.GetItemRect(scrolledRequestIndex, ItemBoundsPortion.Entire);
        var scrolledHover = new Point(scrolledBounds.Left + Math.Min(100, scrolledBounds.Width / 2),
            Math.Clamp(scrolledBounds.Top + scrolledBounds.Height / 2, 0, list.ClientSize.Height - 1));
        Check(InvokePrivate<int>(history, "IndexAt", scrolledHover) == scrolledRequestIndex,
            "the hover point resolves the visible request after scrolling a virtual list");
        InvokePrivate(list, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, scrolledHover.X, scrolledHover.Y, 0));
        PumpUntil(() => IsWindowVisible(tooltipWindow.Handle));
        Check(IsWindowVisible(tooltipWindow.Handle) && rowToolTip.GetToolTip(history) == list.Items[scrolledRequestIndex].Text,
            "the visible tooltip shows the full text of a scrolled request, including query ampersands");

        var adjacentBounds = list.GetItemRect(scrolledRequestIndex - 1, ItemBoundsPortion.Entire);
        var adjacentHover = new Point(scrolledHover.X, adjacentBounds.Top + adjacentBounds.Height / 2);
        InvokePrivate(list, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, adjacentHover.X, adjacentHover.Y, 0));
        Check(!IsWindowVisible(tooltipWindow.Handle), "changing rows hides the old tooltip immediately");
        PumpUntil(() => IsWindowVisible(tooltipWindow.Handle));
        Check(IsWindowVisible(tooltipWindow.Handle) && rowToolTip.GetToolTip(history) == list.Items[scrolledRequestIndex - 1].Text,
            "moving directly to the next truncated row shows its own tooltip");

        list.EnsureVisible(list.VirtualListSize - 1);
        var afterScroll = InvokePrivate<int>(history, "IndexAt", adjacentHover);
        Check(afterScroll >= 0 && afterScroll != scrolledRequestIndex - 1,
            "setup: scrolling changes the row under the stationary pointer");
        PumpUntil(() => afterScroll >= 0 && IsWindowVisible(tooltipWindow.Handle)
            && rowToolTip.GetToolTip(history) == list.Items[afterScroll].Text);
        Check(afterScroll >= 0 && IsWindowVisible(tooltipWindow.Handle)
            && rowToolTip.GetToolTip(history) == list.Items[afterScroll].Text,
            "scrolling updates the visible popup without another mouse move");

        InvokePrivate(list, "OnMouseLeave", EventArgs.Empty);
        Check(!IsWindowVisible(tooltipWindow.Handle), "leaving the list hides the popup");

        // A click dismisses the tip, but releasing the mouse while remaining on that row must
        // allow another hover. No additional MouseMove is required after selecting an item.
        InvokePrivate(list, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, adjacentHover.X, adjacentHover.Y, 0));
        InvokePrivate(list, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, adjacentHover.X, adjacentHover.Y, 0));
        PumpUntil(() => IsWindowVisible(tooltipWindow.Handle));
        Check(afterScroll >= 0 && IsWindowVisible(tooltipWindow.Handle)
            && rowToolTip.GetToolTip(history) == list.Items[afterScroll].Text,
            "a tooltip can appear again after clicking the same row");

        history.SetHistory([new Session
        {
            Request = new HttpRequestData { Method = "GET", Url = new Uri("https://history-tooltip.test/ok"), RequestTarget = "/ok" },
        }]);
        list.EnsureVisible(1);
        var shortBounds = list.GetItemRect(1, ItemBoundsPortion.Entire);
        InvokePrivate(list, "OnMouseMove", new MouseEventArgs(MouseButtons.None, 0, 100, shortBounds.Top + shortBounds.Height / 2, 0));
        PumpFor(100);
        Check(!IsWindowVisible(tooltipWindow.Handle), "a request that fits does not show a tooltip");
        form.Close();
    }

    private static T PrivateField<T>(object target, string name) where T : class
    {
        var field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(target) as T
            ?? throw new InvalidOperationException($"Missing {name} on {target.GetType().Name}.");
    }

    private static void InvokePrivate(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing {name} on {target.GetType().Name}.");
        method.Invoke(target, arguments);
    }

    private static T InvokePrivate<T>(object target, string name, params object?[] arguments)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing {name} on {target.GetType().Name}.");
        return method.Invoke(target, arguments) is T value
            ? value
            : throw new InvalidOperationException($"{name} on {target.GetType().Name} did not return {typeof(T).Name}.");
    }

    private static void PressDirectionShortcut(TextBox textBox, Keys secondModifier)
    {
        var original = new byte[256];
        if (!GetKeyboardState(original)) throw new InvalidOperationException("Cannot read keyboard state.");
        var pressed = (byte[])original.Clone();
        pressed[(int)Keys.ControlKey] |= 0x80;
        pressed[(int)Keys.ShiftKey] |= 0x80;
        pressed[(int)Keys.RShiftKey] |= 0x80;
        try
        {
            if (!SetKeyboardState(pressed)) throw new InvalidOperationException("Cannot set keyboard state.");
            SendMessage(textBox.Handle, WmKeyDown, (nint)secondModifier, 0);
            pressed[(int)secondModifier] &= 0x7F;
            if (secondModifier == Keys.ShiftKey) pressed[(int)Keys.RShiftKey] &= 0x7F;
            if (!SetKeyboardState(pressed)) throw new InvalidOperationException("Cannot release modifier state.");
            SendMessage(textBox.Handle, WmKeyUp, (nint)secondModifier, 0);
        }
        finally
        {
            SetKeyboardState(original);
        }
    }

    private static void PostWordKey(TextBox textBox, Keys direction, bool shift)
    {
        textBox.Focus();
        var original = new byte[256];
        if (!GetKeyboardState(original)) throw new InvalidOperationException("Cannot read keyboard state.");
        var pressed = (byte[])original.Clone();
        pressed[(int)Keys.ControlKey] |= 0x80;
        if (shift) pressed[(int)Keys.ShiftKey] |= 0x80;
        try
        {
            if (!SetKeyboardState(pressed)) throw new InvalidOperationException("Cannot set keyboard state.");
            if (!PostMessage(textBox.Handle, WmKeyDown, (nint)direction, 0))
                throw new InvalidOperationException("Cannot post key message.");
            Application.DoEvents();
        }
        finally
        {
            SetKeyboardState(original);
        }
    }

    private static void PressWordKey(TextBox textBox, Keys direction, bool shift)
        => PressKey(textBox, direction, control: true, shift);

    private static void PressKey(TextBox textBox, Keys direction, bool control, bool shift)
    {
        var original = new byte[256];
        if (!GetKeyboardState(original)) throw new InvalidOperationException("Cannot read keyboard state.");
        var pressed = (byte[])original.Clone();
        if (control) pressed[(int)Keys.ControlKey] |= 0x80;
        if (shift) pressed[(int)Keys.ShiftKey] |= 0x80;
        try
        {
            if (!SetKeyboardState(pressed)) throw new InvalidOperationException("Cannot set keyboard state.");
            SendMessage(textBox.Handle, WmKeyDown, (nint)direction, 0);
        }
        finally
        {
            SetKeyboardState(original);
        }
    }

    private static void PressKey(Control control, Keys key, bool controlPressed, bool shift)
    {
        control.Focus();
        var original = new byte[256];
        if (!GetKeyboardState(original)) throw new InvalidOperationException("Cannot read keyboard state.");
        var pressed = (byte[])original.Clone();
        if (controlPressed) pressed[(int)Keys.ControlKey] |= 0x80;
        if (shift) pressed[(int)Keys.ShiftKey] |= 0x80;
        try
        {
            if (!SetKeyboardState(pressed)) throw new InvalidOperationException("Cannot set keyboard state.");
            SendMessage(control.Handle, WmKeyDown, (nint)key, 0);
        }
        finally
        {
            SetKeyboardState(original);
        }
    }

    private static void RunTextWizardJwtMenuTest()
    {
        Console.WriteLine("== the TextWizard JWT tree copies claims from its context menu");
        // Opening and closing the wizard saves its window size and transform; keep the developer's file.
        var settingsPath = TextWizardSettingsStore.DefaultPath;
        var savedSettings = File.Exists(settingsPath) ? File.ReadAllBytes(settingsPath) : null;
        try
        {
            static string Part(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            const string subject = "fe4aecaf-9172-4b20-874c-1daf6c227f7e";
            var token = Part("{\"alg\":\"HS256\",\"typ\":\"JWT\"}") + "."
                + Part("{\"id\":\"tomwolfgang\",\"scope\":[\"logged_in\",\"dev\"],\"sub\":\"" + subject + "\",\"exp\":1792060137}")
                + ".c2ln";

            TextWizardDialog.Open(null, "Bearer " + token);
            var wizard = Application.OpenForms.OfType<TextWizardDialog>().Single();
            var transform = Descendants(wizard).OfType<ComboBox>().Single();
            transform.SelectedIndex = transform.Items.Cast<string>().ToList().FindIndex(item => item.Contains("JWT", StringComparison.Ordinal));
            var tree = Descendants(wizard).OfType<TreeView>().Single();
            var input = Descendants(wizard).OfType<TextBox>().First(box => !box.ReadOnly);
            var menu = tree.ContextMenuStrip;
            Check(menu is not null, "the JWT tree has a context menu");
            if (menu is null) return;

            static IEnumerable<TreeNode> All(TreeNodeCollection nodes) =>
                nodes.Cast<TreeNode>().SelectMany(node => All(node.Nodes).Prepend(node));
            // The copy commands share their value with Send value to input; the system clipboard is
            // not reliably available to a test process, so the value is checked through the input.
            var sub = All(tree.Nodes).Single(node => node.Text.StartsWith("sub: ", StringComparison.Ordinal));
            Check(sub.Text == "sub: \"" + subject + "\"", "the claim line, which Copy key-value copies, is shown as JSON");
            var scope = All(tree.Nodes).Single(node => node.Text.StartsWith("scope: ", StringComparison.Ordinal));
            tree.SelectedNode = scope;
            menu.Items[3].PerformClick();
            Check(input.Text == "[\"logged_in\",\"dev\"]", "an array claim's value is its JSON");
            // Sending a value to the input re-detects the transform, so choose Inspect JWT again.
            transform.SelectedIndex = transform.Items.Cast<string>().ToList().FindIndex(item => item.Contains("JWT", StringComparison.Ordinal));
            input.Text = "Bearer " + token;
            sub = All(tree.Nodes).Single(node => node.Text.StartsWith("sub: ", StringComparison.Ordinal));
            tree.SelectedNode = sub;
            menu.Items[3].PerformClick();
            Check(input.Text == subject, "a string claim's value has no JSON quotes");

            input.Text = "Bearer " + token;
            sub = All(tree.Nodes).Single(node => node.Text.StartsWith("sub: ", StringComparison.Ordinal));
            tree.SelectedNode = sub;
            menu.Show(tree, Point.Empty);
            Check(menu.Items[0].Enabled && menu.Items[1].Enabled && menu.Items[3].Enabled, "items are enabled on a claim");
            menu.Close();
            menu.Items[3].PerformClick(); // the input is now a bare UUID, so the tree shows only a status line
            Check(tree.Nodes.Count == 1 && tree.Nodes[0].Tag is null, "setup: the claim that is not a JWT shows one status line");
            tree.SelectedNode = tree.Nodes[0];
            menu.Show(tree, Point.Empty);
            Check(!menu.Items[0].Enabled && !menu.Items[1].Enabled && !menu.Items[3].Enabled,
                "items are disabled on a status line");
            menu.Close();
        }
        finally
        {
            Application.OpenForms.OfType<TextWizardDialog>().SingleOrDefault()?.Close();
            if (savedSettings is null) File.Delete(settingsPath);
            else File.WriteAllBytes(settingsPath, savedSettings);
        }
    }

    private static void DoubleClick(Control control, Point location)
    {
        var position = (nint)(location.X | location.Y << 16);
        SendMessage(control.Handle, WmLButtonDown, 1, position);
        SendMessage(control.Handle, WmLButtonUp, 0, position);
        SendMessage(control.Handle, WmLButtonDoubleClick, 1, position);
        SendMessage(control.Handle, WmLButtonUp, 0, position);
    }

    private static void DoubleClickAndHold(Control control, Point location)
    {
        var position = (nint)(location.X | location.Y << 16);
        SendMessage(control.Handle, WmLButtonDown, 1, position);
        SendMessage(control.Handle, WmLButtonUp, 0, position);
        SendMessage(control.Handle, WmLButtonDoubleClick, 1, position);
    }

    private static void MouseMove(Control control, Point location, bool leftButton) =>
        SendMessage(control.Handle, WmMouseMove, leftButton ? 1 : 0, (nint)(location.X | location.Y << 16));

    private static void LeftButtonUp(Control control, Point location) =>
        SendMessage(control.Handle, WmLButtonUp, 0, (nint)(location.X | location.Y << 16));

    private static void RunWindowActivation(string name)
    {
        Console.WriteLine($"== {name}");
        using var form = new Form
        {
            Width = 600,
            Height = 400,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(0, 0),
        };

        form.Show();
        form.WindowState = FormWindowState.Minimized;
        Application.DoEvents();

        WindowActivation.BringToFront(form);
        Application.DoEvents();

        Check(form.WindowState != FormWindowState.Minimized, "a minimized existing window is restored");
        Check(form.ContainsFocus, "the restored window is activated");
        form.Close();
    }

    private static void RunAboutDialog(string name)
    {
        Console.WriteLine($"== {name}");
        using var dialog = new AboutDialog();
        var version = typeof(AboutDialog).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var labels = Descendants(dialog).OfType<Label>().ToList();

        Check(dialog.FormBorderStyle == FormBorderStyle.FixedDialog && !dialog.MinimizeBox && !dialog.MaximizeBox,
            "the About window is a compact dialog, not a resizable message box");
        Check(labels.Any(label => label.Text.Contains(version, StringComparison.Ordinal)),
            "the running application version is visible");
        Check(Descendants(dialog).OfType<Button>().Any(button => button.DialogResult == DialogResult.OK),
            "the dialog has a close action");
    }

    /// <summary>Selecting a session renders it in the inspector, which must not take the focus from the grid.</summary>
    private static void RunArrowKeysKeepFocus(string name)
    {
        Console.WriteLine($"== {name}");
        var store = new SessionStore();
        using var form = new Form { Width = 1200, Height = 700, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        var split = new SplitContainer { Dock = DockStyle.Fill };
        var grid = new SessionListView(store) { Dock = DockStyle.Fill };
        var inspector = new InspectorPanel { Dock = DockStyle.Fill };
        split.Panel1.Controls.Add(grid);
        split.Panel2.Controls.Add(inspector);
        form.Controls.Add(split);
        grid.SelectionChanged += (_, session) => inspector.Show(session); // as MainForm wires it
        form.Show();
        var list = FindListView(grid);

        (string? type, string body)[] responses =
        [
            ("text/html", "<html>one</html>"),
            ("application/json", "{\"a\":1}"),
            (null, ""),
            ("text/plain", "plain"),
            ("image/png", "not really a png"),
            ("application/json", "{\"b\":2}"),
            ("text/html", "<html>two</html>"),
        ];
        foreach (var (type, body) in responses)
        {
            var response = new HttpResponseData { StatusCode = 200, Body = System.Text.Encoding.UTF8.GetBytes(body) };
            if (type is not null) response.Headers.Add("Content-Type", type);
            var session = new Session
            {
                Request = new HttpRequestData { Method = "GET", Url = new Uri($"http://example.test/{_next++}") },
                Response = response,
            };
            session.Completed = DateTimeOffset.Now;
            store.Add(session);
        }
        PumpUntil(() => list.VirtualListSize == responses.Length);

        list.Focus();
        list.SelectedIndices.Add(0);
        list.FocusedItem = list.Items[0];
        PumpFor(100);
        for (var i = 1; i < responses.Length; i++)
        {
            SendMessage(list.Handle, WmKeyDown, (nint)Keys.Down, 0);
            SendMessage(list.Handle, WmKeyUp, (nint)Keys.Down, 0);
            PumpFor(100);
            var focused = Descendants(form).FirstOrDefault(control => control.Focused);
            Check(list.Focused && list.SelectedIndices.Count == 1 && list.SelectedIndices[0] == i,
                $"after Down #{i} row {i} is selected and the grid has focus (focus: {focused?.GetType().Name ?? "none"} '{focused?.Name}', parent {focused?.Parent?.GetType().Name})");
        }
        form.Close();
    }

    /// <summary>A rule that was skipped for timing out says so in its Last match cell, until it is edited.</summary>
    private static void RunPanel(string name)
    {
        Console.WriteLine($"== {name}");
        var broken = new AutoResponderRule { Match = @"NOT:REGEX:^http://api\.example\.test/(a+)+$", Action = "*418" };
        var healthy = new AutoResponderRule { Match = "orders", Action = "*503" };
        var settings = new AutoResponderSettings { Enabled = true, Rules = [broken, healthy] };

        var responder = new AutoResponder();
        responder.Apply(settings);
        using var form = new Form { Width = 1000, Height = 500, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        var panel = new AutoResponderPanel(responder) { Dock = DockStyle.Fill };
        form.Controls.Add(panel);
        form.Show();
        panel.ApplySettings(settings);
        var list = FindListView(panel);
        Check(list.Items.Count == 2, "setup: both rules are listed");
        Check(list.Items[0].SubItems[4].Text.Length == 0, "before any request the Last match cell is empty");

        var uri = new Uri("http://api.example.test/" + new string('a', 40) + "!");
        var trap = new Session { Request = new HttpRequestData { Method = "GET", Url = uri, RequestTarget = uri.PathAndQuery } };
        Check(responder.Evaluate(trap).Outcome == AutoResponderOutcome.Passthrough, "the catastrophic request is not answered");
        panel.RefreshStatistics();
        Check(list.Items[0].SubItems[4].Text.Length > 0, "the timed-out rule's Last match cell says so");
        Check(list.Items[0].ToolTipText.Length > 0, "and explains it in a tooltip");
        Check(list.Items[1].SubItems[4].Text.Length == 0 && list.Items[1].ToolTipText.Length == 0, "its neighbour is not marked");

        responder.Apply(settings); // an edit recompiles the rules
        panel.RefreshStatistics();
        Check(list.Items[0].SubItems[4].Text.Length == 0 && list.Items[0].ToolTipText.Length == 0, "editing the rules clears the mark");
        form.Close();
    }

    /// <summary>Hosts a real grid, fills it past a screen so it follows the tail, then runs the case.</summary>
    private static void Run(string name, Action<SessionStore, SessionListView, ListView> body, int setupRows = 300)
    {
        Console.WriteLine($"== {name}");
        var store = new SessionStore();
        using var form = new Form { Width = 1000, Height = 500, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        var grid = new SessionListView(store) { Dock = DockStyle.Fill };
        form.Controls.Add(grid);
        form.Show();
        var list = FindListView(grid);

        _next = 0;
        AddSessions(store, setupRows);
        PumpUntil(() => list.VirtualListSize == setupRows);
        Check(SendMessage(list.Handle, LvmGetTopIndex, 0, 0) > (setupRows > 100 ? 100 : 0), "setup: the grid followed the tail");

        body(store, grid, list);
        form.Close();
    }

    private static void ExpectRowsFromTop(ListView list, int rows)
    {
        Check(list.VirtualListSize == rows, $"{rows} rows are listed (got {list.VirtualListSize})");
        if (list.VirtualListSize != rows || rows == 0) return;
        var topIndex = (int)SendMessage(list.Handle, LvmGetTopIndex, 0, 0);
        var first = list.GetItemRect(0);
        Check(topIndex == 0, $"the view starts at row 0 (top index {topIndex})");
        // Just under the header: one row height at most, not partway down the grid.
        Check(first.Top >= 0 && first.Top <= first.Height * 2, $"row 0 is drawn at the top (y={first.Top})");
        // Only where every row fits, so a high-DPI runner with taller rows does not fail it spuriously.
        if (first.Top + rows * first.Height <= list.ClientSize.Height)
            Check(list.GetItemRect(rows - 1).Bottom <= list.ClientSize.Height, "the newest row is on screen");
    }

    private static void ExpectBottomInView(ListView list, int rows)
    {
        Check(list.VirtualListSize == rows, $"{rows} rows are listed (got {list.VirtualListSize})");
        if (list.VirtualListSize != rows || rows == 0) return;
        var topIndex = (int)SendMessage(list.Handle, LvmGetTopIndex, 0, 0);
        var top = list.GetItemRect(topIndex);
        var last = list.GetItemRect(rows - 1);
        Check(top.Top >= 0 && top.Top <= top.Height * 2, $"the top row {topIndex} is drawn at the top (y={top.Top})");
        Check(last.Bottom <= list.ClientSize.Height, $"the last row is on screen (bottom {last.Bottom} of {list.ClientSize.Height})");
        // Past the end the view would show blank space below the last row with rows above it hidden.
        Check(list.ClientSize.Height - last.Bottom < last.Height * 2, $"no blank space under the last row (bottom {last.Bottom} of {list.ClientSize.Height})");
    }

    private const string RareHost = "rare.test";
    private static int _next;

    private static void AddSessions(SessionStore store, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var host = _next % 30 == 0 ? RareHost : "example.test";
            var session = new Session
            {
                Request = new HttpRequestData { Method = "GET", Url = new Uri($"http://{host}/item/{_next++}") },
                Response = new HttpResponseData { StatusCode = 200 },
            };
            session.Completed = DateTimeOffset.Now;
            store.Add(session);
        }
    }

    // Application.Run keeps the WinForms synchronization context installed; DoEvents swaps in a plain one
    // (posting to the thread pool), so awaits in the code under test would resume off the UI thread.
    private static void InstallWinFormsContext() =>
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

    /// <summary>
    /// Runs the message loop until the grid's 150 ms refresh timer has rebuilt it to the expected
    /// state, or 10 seconds pass, so a slow machine cannot fail a check that was merely early.
    /// </summary>
    private static void PumpUntil(Func<bool> rebuilt)
    {
        var deadline = Environment.TickCount64 + 10_000;
        while (!rebuilt() && Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        Application.DoEvents();
        InstallWinFormsContext();
    }

    /// <summary>Runs the message loop for a fixed time, for asserting that something does not happen.</summary>
    private static void PumpFor(int milliseconds)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
        InstallWinFormsContext();
    }

    private static ListView FindListView(Control root) =>
        Find(root) ?? throw new InvalidOperationException("The session grid has no ListView.");

    private static ListView? Find(Control root)
    {
        foreach (Control child in root.Controls)
            if ((child as ListView ?? Find(child)) is { } list) return list;
        return null;
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void Check(bool condition, string message)
    {
        Console.WriteLine($"   {(condition ? "ok  " : "FAIL")}  {message}");
        if (!condition) _failures++;
    }
}
