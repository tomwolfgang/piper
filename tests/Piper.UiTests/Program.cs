using System.Runtime.InteropServices;
using Piper.App;
using Piper.App.Controls;
using Piper.App.Theme;
using Piper.Core.Http;
using Piper.Core.Proxy;
using Piper.Core.Sessions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

// Regression: after Ctrl+X cleared a grid scrolled down to its newest rows, the next rows were drawn
// partway down the grid or not at all, and stayed that way. The native ListView's scroll origin
// was left past the end when the row count shrank, so later rows sat at a negative top index.
internal static class Program
{
    private const int LvmGetTopIndex = 0x1000 + 39;
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

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        RunTextBoxWordSelectionTest();
        RunImageDecodingTest();

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

        RunPanel("an AutoResponder rule whose pattern timed out is marked broken in the panel");
        RunWindowActivation("a second launch restores the existing Piper window");
        RunAboutDialog("the About dialog identifies the running Piper version");

        Console.WriteLine(_failures == 0 ? "UI tests passed." : $"{_failures} UI check(s) failed.");
        return _failures == 0 ? 0 : 1;
    }

    // Regression guard for the inspector's decoder options: a bare Configuration registers no image
    // formats, so every image failed to decode while the guard (which never calls the library) still
    // allowed it. Decodes what ImageSharp itself encodes, through the options the inspector uses.
    private static void RunImageDecodingTest()
    {
        Console.WriteLine("== the inspector's decoder options decode every allowed format");
        using var source = new Image<Rgba32>(5, 3);
        (string Name, Action<Image, Stream> Save)[] formats =
        [
            ("PNG", (image, stream) => image.SaveAsPng(stream)),
            ("JPEG", (image, stream) => image.SaveAsJpeg(stream)),
            ("GIF", (image, stream) => image.SaveAsGif(stream)),
            ("BMP", (image, stream) => image.SaveAsBmp(stream)),
            ("lossy WebP", (image, stream) => image.SaveAsWebp(stream, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy })),
            ("lossless WebP", (image, stream) => image.SaveAsWebp(stream, new WebpEncoder { FileFormat = WebpFileFormatType.Lossless })),
        ];
        foreach (var (name, save) in formats)
        {
            using var encoded = new MemoryStream();
            save(source, encoded);
            try
            {
                using var decoded = Image.Load(ImageDecoding.Options, encoded.ToArray());
                Check(decoded.Width == 5 && decoded.Height == 3, $"{name} decodes to its size (got {decoded.Width} x {decoded.Height})");
            }
            catch (Exception ex)
            {
                Check(false, $"{name} decodes ({ex.GetType().Name})");
            }
        }
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
        large.Select(largeWordStart, 0);
        PressWordKey(large, Keys.Right, shift: true);
        Check(large.SelectionStart == largeWordStart && large.SelectedText == word,
            "Ctrl+Shift+Right selects the same component past offset 65,536");
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

    private static void DoubleClick(Control control, Point location)
    {
        var position = (nint)(location.X | location.Y << 16);
        SendMessage(control.Handle, WmLButtonDown, 1, position);
        SendMessage(control.Handle, WmLButtonUp, 0, position);
        SendMessage(control.Handle, WmLButtonDoubleClick, 1, position);
        SendMessage(control.Handle, WmLButtonUp, 0, position);
    }

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
    private static void Run(string name, Action<SessionStore, SessionListView, ListView> body)
    {
        Console.WriteLine($"== {name}");
        var store = new SessionStore();
        using var form = new Form { Width = 1000, Height = 500, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
        var grid = new SessionListView(store) { Dock = DockStyle.Fill };
        form.Controls.Add(grid);
        form.Show();
        var list = FindListView(grid);

        _next = 0;
        AddSessions(store, 300);
        PumpUntil(() => list.VirtualListSize == 300);
        Check(SendMessage(list.Handle, LvmGetTopIndex, 0, 0) > 100, "setup: the grid followed the tail");

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
