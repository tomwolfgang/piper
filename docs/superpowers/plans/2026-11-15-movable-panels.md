# Movable Panels Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let users move Piper's six panels between Left/Right/Bottom areas, floating windows and auto-hide flyouts, hide/show them, reset to default, and keep it across restarts.

**Architecture:** A pure `PanelLayout` model (no WinForms types) holds placement and enforces every invariant; `WindowLayoutStore` persists it tolerantly. A WinForms `PanelHost` renders a `PanelLayout` onto three `DarkTabControl` areas, floating forms and flyouts, and is the only thing `MainForm` talks to (`ShowPanel`, `FocusPanel`, `Snapshot`, `Reset`).

**Tech Stack:** .NET 10 WinForms, System.Text.Json, existing `Palette`/`FontScale`/`DarkTabControl`; custom smoke and UI test runners.

**Spec:** `docs/superpowers/specs/2026-11-15-movable-panels-design.md`

## Global Constraints

- No new packages. No new persistence location: only `%LOCALAPPDATA%\Piper\window-layout.json`.
- Every user-visible string in `src/Piper.App/Locales/en.json`, reached via `Strings.cs`; accelerator menu items via `Menus.Item`.
- Layout file: read cap 64 KiB; fractions clamped to [0.1, 0.9] after rejecting NaN/∞; atomic temp-file + `File.Move(..., overwrite: true)` save.
- `sessions` can never be hidden, and its area can never be auto-hidden.
- Passive reveals never un-hide a panel.
- Build stays warning-free; gate: `powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify.ps1`.
- `MainForm.cs` must end smaller than it starts (2,172 lines).

## Review Focus

1. **Zoom at 200% and back with an auto-hide strip and a floating window open.** Sideways labels fit; the strip width tracks font height; the floating window rescales. Test in Task 7.
2. **Main window shrunk to `MinimumSize` with Bottom populated and a flyout open.** No `SplitterDistance` exception, and the flyout clamps inside the window. Test in Task 5 and Task 7.
3. **Theme toggled while a panel is hidden.** It is re-themed when shown. Test in Task 5.
4. **Monitor removed between runs.** A floating window saved on it comes back on the nearest screen with its caption reachable. Test in Task 2.
5. **Float, then Dock, then Float the Inspector quickly while a WebP preview is loading.** No unhardened core and no crash. Test in Task 6.

---

## File structure

| File | Responsibility |
|---|---|
| `src/Piper.Core/Text/BoundedRead.cs` (new) | `TryRead(Stream, long max, out byte[], out int)` extracted from `AutoResponderSettingsStore` |
| `src/Piper.App/Layout/PanelLayout.cs` (new, no WinForms) | enums + placement model + `Normalise` + `Default` |
| `src/Piper.App/WindowLayoutStore.cs` (modify) | v2 file: tolerant parse, atomic save, carries `PanelLayout` |
| `src/Piper.App/Layout/PanelRegistry.cs` (new) | owns the six panels' content controls and `TabPage`s; dispose-once |
| `src/Piper.App/Layout/PanelHost.cs` (new) | renders layout: splits, three areas, tab menu, show/hide/reset, reveal |
| `src/Piper.App/Layout/FloatingPanelForm.cs` (new) | owned form for one panel; key forwarding; close-to-dock |
| `src/Piper.App/Layout/AutoHideStrip.cs` (new) | edge strip with (sideways) names, accessible page-tab items |
| `src/Piper.App/Layout/Flyout.cs` (new) | overlay container, resize grip, close rules |
| `src/Piper.App/Layout/TabDrag.cs` (new) | mouse-capture tab dragging across strips |
| `src/Piper.App/MainForm.cs` (modify) | drop `_rightTabs`/`_mainSplit`; use `PanelHost`; shared shortcut dispatcher |
| `src/Piper.App/Controls/WebpView.cs`, `VideoView.cs` (modify) | track hardened `CoreWebView2` instance |
| `tests/Piper.SmokeTests/PanelLayoutTests.cs`, `WindowLayoutStoreTests.cs` (new) | model + store |
| `tests/Piper.UiTests/PanelHostTests.cs` (new) | host behaviour with real panels |

---

### Task 1: Spike — Inspector survives reparenting (throwaway)

**Files:** scratch only: `tests/Piper.UiTests/ReparentSpike.cs`. It is deleted at the end of the task and not committed.

- [ ] **Step 1:** Write a UI-test-style method:
  1. Build `SessionListView` and `InspectorPanel` wired as in `TlsFailureInspectorUiTests`.
  2. Select a session whose response is a WebP image (reuse the `WebpInspectorTests` fixture).
  3. `PumpUntil` the WebP view reports loaded.
  4. Move the `InspectorPanel` into a new `Form`: `form.Controls.Add(inspector); form.Show();`.
  5. Pump, then move it back.
  6. Select a video session (the `VideoInspectorTests` fixture) and repeat the move.
- [ ] **Step 2:** Run `dotnet run --project tests/Piper.UiTests`. Record for each move:
  - whether `CoreWebView2` is the same instance (`ReferenceEquals`);
  - whether the content still renders (a `PumpUntil` on its loaded flag);
  - whether `inspector.Handle` changed.
- [ ] **Step 3:** Write the outcome as a 3-line "Spike result" note at the bottom of this plan. If WebView2 does not survive, and recreating it in Task 6 also fails, set `PanelHost.CanFloat(PanelId.Inspectors)` to return false in Task 6. Delete the spike file.

### Task 2: Bounded read helper and `PanelLayout` model

**Files:**
- Create: `src/Piper.Core/Text/BoundedRead.cs`
- Modify: `src/Piper.Core/Proxy/AutoResponderSettingsStore.cs:234-255` (call the helper; delete the private copy)
- Create: `src/Piper.App/Layout/PanelLayout.cs`
- Modify: `tests/Piper.SmokeTests/Piper.SmokeTests.csproj`: `<Compile Include="..\..\src\Piper.App\Layout\PanelLayout.cs" Link="PanelLayout.cs" />`
- Test: `tests/Piper.SmokeTests/PanelLayoutTests.cs`

**Interfaces (Produces):**

```csharp
namespace Piper.Core.Text;
public static class BoundedRead { public static bool TryRead(Stream stream, long maxBytes, out byte[] content, out int length); }

namespace Piper.App.Layout;
public enum PanelId { Sessions, Inspectors, Composer, Filters, AutoResponder, Log }   // order = Ctrl+Alt+1..6
public enum DockArea { Left, Right, Bottom }
public enum PanelReveal { Explicit, Passive }
internal sealed class PanelLayout
{
    public static PanelLayout Default();
    public PanelLayout Clone();
    // placement
    public DockArea AreaOf(PanelId id);                 // last docked area, also while floating/hidden
    public bool IsFloating(PanelId id);
    public bool IsHidden(PanelId id);
    public IReadOnlyList<PanelId> DockedIn(DockArea area); // visible, not floating, in order
    public PanelId? SelectedIn(DockArea area);
    public void Move(PanelId id, DockArea area, int? index = null); // docks if floating; selects it
    public void Float(PanelId id, Rectangle bounds, int dpi);
    public void Dock(PanelId id);                       // back to AreaOf(id)
    public bool Hide(PanelId id);                       // false for Sessions
    public void Show(PanelId id);
    public void Select(PanelId id);
    // floating extras
    public Rectangle? FloatingBounds(PanelId id); public int FloatingDpi(PanelId id);
    public bool TopMost(PanelId id); public void SetTopMost(PanelId id, bool value);
    // areas
    public bool CanAutoHide(DockArea area);             // false when Sessions is docked there
    public bool IsAutoHidden(DockArea area);
    public bool SetAutoHidden(DockArea area, bool value); // false if !CanAutoHide
    public double LeftFraction { get; set; }            // default 0.55, clamped
    public double BottomFraction { get; set; }          // default 0.30, clamped
    public double FlyoutFraction(DockArea area); public void SetFlyoutFraction(DockArea area, double value); // default 0.35
    public static double ClampFraction(double value, double fallback); // NaN/∞ → fallback, then [0.1,0.9]
    // repair
    public void Normalise(IReadOnlyList<Rectangle> workAreas);
}
```

`Normalise` does all of the following:
- forces `Sessions` to be visible, and sets `IsAutoHidden(area)` to false for its area;
- renumbers each area's order to 0..n−1;
- makes each area's selection one of its visible docked panels (the first one when the current selection is invalid);
- makes sure every visible panel is in some area or floating;
- clamps each floating rectangle to at least 300×200 and at most the largest work area;
- moves a floating window onto the nearest work area when its caption strip (the top 32 px) does not intersect any work area;
- docks a floating panel when `workAreas` is empty.

- [ ] **Step 1: Write the failing tests** in `PanelLayoutTests.RunAsync(TestRunner runner)`, one `runner.RunAsync` per scenario:
  - **"default layout matches today's window":**
    - `DockedIn(Left)` is `[Sessions]`;
    - `DockedIn(Right)` is `[Inspectors, Composer, Filters, AutoResponder, Log]`;
    - `SelectedIn(Right)` is `Inspectors`;
    - `LeftFraction` is 0.55 and `BottomFraction` is 0.30.
  - **"move selects the panel at its target and keeps order dense":**
    - `Move(Log, Bottom)` gives `DockedIn(Bottom)` `[Log]` and `SelectedIn(Bottom)` `Log`;
    - `Move(Composer, Bottom, 0)` gives `[Composer, Log]`.
  - **"hide and show return to the last area":**
    - `Move(Log, Bottom); Hide(Log); Show(Log)` leaves `AreaOf(Log)` as `Bottom`;
    - `Hide(Sessions)` returns false and `IsHidden(Sessions)` stays false.
  - **"floating then docking returns to the area it came from":** `Move(Filters, Left); Float(Filters, r, 96); Dock(Filters)` leaves `AreaOf` as `Left` and `IsFloating` false.
  - **"the sessions area cannot auto-hide":** `SetAutoHidden(Left, true)` returns false. After `Move(Sessions, Right)`, `CanAutoHide(Right)` is false and `CanAutoHide(Left)` is true.
  - **"fractions reject NaN, infinity and out of range":**
    - `ClampFraction(double.NaN, 0.55)` is 0.55;
    - `ClampFraction(double.PositiveInfinity, 0.3)` is 0.3;
    - `ClampFraction(-4, 0.5)` is 0.1;
    - `ClampFraction(7, 0.5)` is 0.9.
  - **"normalise brings a floating window on a removed monitor back":**
    - with work areas `[(0,0,1920,1040)]`, floating bounds `(2500,100,800,600)` end up fully inside;
    - bounds `(100,-500,800,600)` (caption above the screen) end up with `Y >= 0`;
    - bounds of 100000×100000 are clamped to at most 1920×1040;
    - with an empty work-area list, the panel is docked.
  - **"normalise leaves every visible panel reachable":** build a layout that has been hand-broken through the store in Task 3, normalise it, and check the union of `DockedIn(*)` and the floating panels equals all non-hidden ids.
  - **"bounded read stops at the cap":** a 70 KiB `MemoryStream` with `maxBytes` 65536 returns false, and exactly 65536 bytes returns true with length 65536.
- [ ] **Step 2:** Run `dotnet run --project tests/Piper.SmokeTests`. Expected: build fails, `PanelLayout` not defined.
- [ ] **Step 3:** Implement `BoundedRead.TryRead` by moving `AutoResponderSettingsStore.TryReadBounded` unchanged except for the `maxBytes` parameter, and switch its caller. Implement `PanelLayout` as specified, backed by one private `Entry { DockArea Area; int Order; bool Hidden; bool Floating; Rectangle? Bounds; int Dpi; bool TopMost }` per id plus a `Dictionary<DockArea, AreaState>`.
- [ ] **Step 4:** Run the smoke tests. Expected: all PASS, including the existing AutoResponder store tests.
- [ ] **Step 5:** Commit: `Add PanelLayout model and shared bounded read`.

### Task 3: `WindowLayoutStore` v2

**Files:**
- Modify: `src/Piper.App/WindowLayoutStore.cs`
- Modify: the smoke-test csproj, to link `WindowLayoutStore.cs`
- Test: `tests/Piper.SmokeTests/WindowLayoutStoreTests.cs`

**Interfaces:**
- Consumes: `PanelLayout`, `BoundedRead`.
- Produces:
  - `WindowLayout { int Version (=2); X; Y; Width; Height; bool Maximized; PanelLayout Panels }`, never null after `Load`;
  - `static void Save(WindowLayout layout, string? path = null)`;
  - `static WindowLayout? Load(string? path = null)`, which returns null only when there are no usable window bounds;
  - `const int MaxFileBytes = 65536`.

Format:

```json
{"Version":2,"X":0,"Y":0,"Width":1200,"Height":800,"Maximized":false,
 "Panels":[{"Id":"log","Area":"bottom","Order":0,"Hidden":false,"Selected":true,"Floating":null}],
 "Areas":{"LeftFraction":0.55,"BottomFraction":0.3,"left":{"AutoHidden":false,"Flyout":0.35}}}
```

- `Id` and `Area` are lowercase strings.
- `Floating` is either null or `{"X":0,"Y":0,"Width":0,"Height":0,"Dpi":96,"TopMost":false}`.
- Parsing goes through `JsonDocument` with `MaxDepth = 8`. Each panel entry and each area is read in its own `try`. A bad entry falls back to that id's default.

- [ ] **Step 1: Write the failing tests**, each using a temp path:
  - round trip of bounds, maximised and a moved layout (`Log` at the bottom, `Filters` floating);
  - a v1 file with only the bounds (`{"X":1,"Y":2,"Width":900,"Height":700,"Maximized":true}`) loads its bounds plus `Panels` equal to the default layout;
  - malformed JSON returns null;
  - a 65537-byte file returns null;
  - `"Panels":[]` gives the default layout;
  - `"Panels":"x"` keeps the bounds and gives the default panels;
  - an entry `{"Id":"log","Area":"top"}` leaves `Log` at its default, while a valid `filters` entry beside it is kept;
  - an unknown id `"zzz"` is ignored, and for a duplicate `log` the first entry wins;
  - `"Order":-5` and `"Order":2147483647` normalise to a dense order;
  - `"LeftFraction":"NaN"` and `1e308` give 0.55 and 0.9;
  - `{"Id":"sessions","Hidden":true}` loads visible;
  - `"Version":99` with extra unknown fields still loads;
  - `Save` leaves no `*.tmp` beside the file, and a save into a read-only directory does not throw.
- [ ] **Step 2:** Run the smoke tests. Expected: FAIL.
- [ ] **Step 3:** Implement the reader and writer:
  - **Read:** `BoundedRead.TryRead` on a `FileStream` opened with `FileShare.ReadWrite`.
  - **Write:** use `Utf8JsonWriter` to a temp file `$"{path}.{Guid:N}.tmp"`, then `File.Move(..., overwrite: true)`. Delete the temp file when a write fails.
  - **Normalise:** do not call `Normalise` here. The caller passes work areas, so the store stays screen-free.
- [ ] **Step 4:** Run the smoke tests. Expected: PASS.
- [ ] **Step 5:** Commit: `Store panel layout in window-layout.json`.

### Task 4: `PanelRegistry` and `PanelHost`, then MainForm with no behaviour change

**Files:**
- Create: `src/Piper.App/Layout/PanelRegistry.cs`
- Create: `src/Piper.App/Layout/PanelHost.cs`
- Modify: `src/Piper.App/MainForm.cs`. Remove `_rightTabs` (field line 34), `_mainSplit` (293), the split built at lines 132–150, and line 379. Replace every `_rightTabs`/`SelectedIndex` use at lines 159–162, 181, 189, 213, 235, 253, 595, 606, 688–699, 829, 1106, 1119, 1455 and 1736.
- Test: `tests/Piper.UiTests/PanelHostTests.cs`, registered in `Program.Main` after `TlsFailureSettingUiTests.Run`.

**Interfaces:**

```csharp
internal sealed class PanelRegistry : IDisposable
{
    public PanelRegistry(IReadOnlyDictionary<PanelId, (Control Content, string Title)> panels);
    public TabPage PageOf(PanelId id);
    public PanelId? IdOf(TabPage page);
    public IEnumerable<Control> Contents { get; }   // all six, wherever they are
    public void Dispose();                          // disposes each content exactly once
}
internal sealed class PanelHost : UserControl
{
    public PanelHost(PanelRegistry registry, PanelLayout layout);
    public void ShowPanel(PanelId id, PanelReveal reveal);   // spec "Programmatic reveal" steps 1-4
    public void FocusPanel(PanelId id);                      // ShowPanel(Explicit) + focus content
    public PanelId? SelectedPanel(DockArea area);
    public void SetTabChecked(PanelId id, bool value);
    public PanelLayout Snapshot();                           // clone incl. current splitter fractions
    public event EventHandler<DarkTabControl>? StripCreated; // MainForm attaches session drag/drop
    public event EventHandler? LayoutChanged;
}
```

- In this task `PanelHost` only renders docked areas: an outer horizontal `SplitContainer` with the Left/Right split inside it on top and Bottom below.
- An empty area sets `Panel{1,2}Collapsed`.
- Splitter fractions are applied in `OnLayout` once the control has a real size, converted to pixels within `Panel1MinSize`/`Panel2MinSize`.
- The session-drop target logic (`SessionDropTarget`, lines 686–700) is rewritten to take the `DarkTabControl` from `sender` and resolve the panel through `registry.IdOf`.
- Every reveal at the old call sites maps as follows:
  - Send to Composer, Ctrl+K, the Composer toolbar button, SAZ import and Send to AutoResponder: `Explicit`;
  - double-click to reveal the Inspector: `Explicit`;
  - the Rules menu's AutoResponder rules item: `Explicit`;
  - the Log on a listen failure: `Passive`.

- [ ] **Step 1: Write the failing UI tests** in `PanelHostTests.Run(check, pumpUntil)`. Each test builds a registry from real `SessionListView`, `InspectorPanel`, `ComposerPanel`, `FilterPanel`, `AutoResponderPanel` and `TextBox`, hosted in a 1200×800 `Form`. Tests:
  - "default host shows sessions left, inspectors selected right";
  - "ShowPanel Explicit selects without moving focus off the grid": focus the `ListView`, call `ShowPanel(Composer, Explicit)`, and the `ListView` still has focus;
  - "arrow keys keep focus in the grid while the inspector re-renders": port the existing arrow-key focus test to run against the host;
  - "SetTabChecked draws on the panel's tab": the page is in the strip's checked set (expose `DarkTabControl.IsTabChecked(TabPage)` as `internal`).
- [ ] **Step 2:** Run `dotnet run --project tests/Piper.UiTests`. Expected: FAIL to build.
- [ ] **Step 3:** Implement `PanelRegistry` and `PanelHost` (docked areas only), and switch `MainForm` over to them. The old `NewPage` helper moves into `PanelRegistry`.
- [ ] **Step 4:** Run the smoke tests, the UI tests and `eng/verify.ps1`. Expected: PASS, with no warnings.
- [ ] **Step 5:** Launch Piper by hand. The window looks and behaves exactly as before.
- [ ] **Step 6:** Commit: `Route panel access through PanelHost`.

### Task 5: Moving, View > Panels, Reset, tab menu, focus shortcuts and persistence

**Files:** modify `PanelHost.cs`, `MainForm.cs` (`BuildViewMenu`, `RestoreWindowBounds`/`SaveWindowBounds`, `OnFormKeyDown`, `ToggleTheme`, `ChangeFontScale`), `Strings.cs`, `Locales/en.json`, `DarkTabControl.cs` (context menu by tab index, Shift+F10/Apps key). Extend `PanelHostTests`.

**Interfaces:**
- Add to `PanelHost`:
  - `void MoveTo(PanelId id, DockArea area)`
  - `void HidePanel(PanelId id)`
  - `void ResetLayout()`
  - `bool IsHidden(PanelId id)`
  - `void ApplyTheme()`, which runs `Palette.Apply` and `InvalidateTheme` over `registry.Contents` and every host-owned window
- Add to `DarkTabControl`:
  - `event EventHandler<int>? TabMenuRequested`, raised by a right-click on a tab, by Shift+F10 and by `Keys.Apps`
  - `int TabIndexAt(Point client)`

**Strings** (`en.json`), shown here as key: text:
- `panels.title`: "&Panels"
- `panels.resetLayout`: "Reset &layout"
- `panels.moveLeft`: "Move to &Left"
- `panels.moveRight`: "Move to &Right"
- `panels.moveBottom`: "Move to &Bottom"
- `panels.float`: "&Float"
- `panels.dock`: "&Dock"
- `panels.autoHide`: "&Auto-hide area"
- `panels.topMost`: "Always on &top"
- `panels.hide`: "&Hide"
- `panels.sessions`: "Sessions"
- `shortcuts.focusPanel`: "Ctrl+Alt+{{n}}"

Use `tabs.*` for the other panel names.

**Behaviour:**
- **The View menu** gets a separator, then **Panels ▸**, which holds six check items. Each is built with `Menus.Item(name, Strings.Shortcuts.FocusPanel(n), …)`. `Sessions` is checked and disabled. Then comes **Reset layout**.
- **Shortcuts:** `OnFormKeyDown` handles `e.Control && e.Alt && e.KeyCode is Keys.D1..Keys.D6`, calling `FocusPanel((PanelId)(n-1))`. Ctrl+K and the toolbar button call `FocusPanel(Composer)`, then `_composer.FocusSearch()`.
- **The tab menu** this task builds holds Move to Left, Move to Right, Move to Bottom and Hide. Float, Auto-hide area and Always on top are added in Tasks 6 and 7.
- **Persistence:**
  - `RestoreWindowBounds` loads the store, calls `Normalise(Screen.AllScreens.Select(s => s.WorkingArea).ToList())`, then constructs `PanelHost` with the result.
  - `SaveWindowBounds` saves `new WindowLayout(bounds, maximized, _panelHost.Snapshot())`.
- **Theme and zoom:** `ToggleTheme` and `ChangeFontScale` call `_panelHost.ApplyTheme()`. `ShowPanel` re-applies the theme to a panel that is being un-hidden.

- [ ] **Step 1: Write the failing UI tests:**
  - "move log to bottom shows a bottom area with log selected";
  - "an emptied area collapses": move all five right-hand panels to Left, and `Panel2Collapsed` becomes true;
  - "hide then Passive reveal keeps it hidden; Explicit reveal shows it in its last area";
  - "reset restores default placement and fractions": after several moves, `Snapshot()` equals `PanelLayout.Default()` on every `AreaOf`, `DockedIn` and fraction;
  - "send-to-composer, dropping a session on a tab and the check glyph work with Composer, AutoResponder and Filters moved to Bottom": drive them through `ShowPanel`, the strip's `DragDrop` handler and `SetTabChecked`;
  - "a window shrunk to MinimumSize with Bottom populated throws nothing": set `form.Size = form.MinimumSize` and pump;
  - "a panel hidden during a theme toggle is themed when shown": the content's `BackColor` equals `Palette.Background`.
- [ ] **Step 2:** Run the UI tests. Expected: FAIL.
- [ ] **Step 3:** Implement the behaviour above.
- [ ] **Step 4:** Run all tests and `eng/verify.ps1`. Expected: PASS.
- [ ] **Step 5:** Commit: `Move, hide and reset panels`.

### Task 6: Floating windows

**Files:**
- Create: `FloatingPanelForm.cs`
- Modify: `PanelHost.cs`, `MainForm.cs` (shortcut dispatcher, `IsTextInputFocused`, `OnFormClosing`), `WebpView.cs:42,88-91`, `VideoView.cs:43,95-98`
- Extend: `PanelHostTests`, and the existing `WebpViewTests`/`VideoViewLifecycleTests`

**Interfaces:**

```csharp
internal sealed class FloatingPanelForm : Form
{
    public FloatingPanelForm(Form owner, TabPage page, Func<Keys, bool> processShortcut, KeyEventHandler keyDown);
    public event EventHandler? DockRequested;   // close (unless FormOwnerClosing), tab double-click, Dock menu
}
// PanelHost additions
public bool CanFloat(PanelId id);               // false for Inspectors only if Task 1 says so
public void FloatPanel(PanelId id, Point? screenLocation = null);
public void DockPanel(PanelId id);
public bool ShuttingDown { get; set; }
```

- **MainForm keyboard dispatch:** extract `internal bool ProcessShortcut(Keys keyData)` from `ProcessCmdKey` and the `OnFormKeyDown` bodies.
- **Floating form keyboard:** the floating form overrides `ProcessCmdKey` so it calls `processShortcut` first, and sets `KeyPreview = true` with `KeyDown += keyDown`.
- **Text-input check:** `IsTextInputFocused` starts from `(Form.ActiveForm ?? this).ActiveControl`.
- **Floating form setup:**
  - `ShowInTaskbar = false`;
  - `Owner = MainForm`;
  - `FormBorderStyle.Sizable`;
  - `Palette.Apply(this)` and `Palette.ApplyWindowChrome(this)` in `OnHandleCreated`;
  - its own one-page `DarkTabControl`, which raises `StripCreated` so session drops work.
- **Alt+Tab:** owned forms appear in Alt+Tab when they are not tool windows, so do not set `WS_EX_TOOLWINDOW`.
- **Reveal:** `ShowPanel` on a floating panel calls `form.Show(); form.BringToFront();` and never `Activate`.
- **Shutdown:** `OnFormClosing` runs `SaveWindowBounds()` first (as today), then sets `_panelHost.ShuttingDown = true` before anything else. When `CloseReason == FormOwnerClosing` or `ShuttingDown` is set, the floating form returns its page to the registry and does not raise `DockRequested`.
- **WebView2 hardening:** replace `bool _hardened` with `CoreWebView2? _hardenedCore`, and harden whenever `!ReferenceEquals(_hardenedCore, webView.CoreWebView2)`.
- **Grid state across moves:** save and restore `SessionListView` selection, `FocusedItem` and `TopItem` around every move. Add `internal (IReadOnlyList<Session> Selected, Session? Focused, Session? Top) CaptureViewState()` and `void RestoreViewState(...)` to `SessionListView`.

- [ ] **Step 1: Write the failing tests:**
  - "float then dock keeps the panel and its area": `AreaOf` is unchanged and the page is back in its strip;
  - "closing a floating window docks it; owner closing does not": close the owner with Composer floating, and there is no exception, `DockRequested` is not raised and each content is disposed once (count `Disposed` events);
  - "Ctrl+X in a floating Composer's text box doesn't clear the store, Ctrl+X on a floating grid does";
  - "zoom and theme reach a floating window": after `FontScale.ZoomIn()` and `ApplyTheme()`, the floating content's font equals the docked font size;
  - "the inspector renders and is hardened after float, dock, float": skip when `!CanFloat`;
  - "sessions floated keep their selection and top row";
  - "a recreated CoreWebView2 is hardened again": in `WebpViewTests`, force `RecreateHandle()` and assert that the navigation block is active.
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the floating windows, and add Float, Dock and Always on top to the tab menu.
- [ ] **Step 4:** Run all tests and `eng/verify.ps1`. Expected: PASS.
- [ ] **Step 5:** Commit: `Float panels in their own windows`.

### Task 7: Auto-hide strip and flyout

**Files:**
- Create: `AutoHideStrip.cs`, `Flyout.cs`
- Modify: `PanelHost.cs`
- Extend: `PanelHostTests`

**Interfaces:**

```csharp
internal sealed class AutoHideStrip : Control
{
    public AutoHideStrip(DockArea edge);             // Left/Right: vertical, text rotated 90°; Bottom: horizontal
    public void SetPanels(IReadOnlyList<(PanelId Id, string Title)> panels);
    public event EventHandler<PanelId>? PanelClicked;
    protected override AccessibleObject CreateAccessibilityInstance(); // children: AccessibleRole.PageTab
}
internal sealed class Flyout : Control
{
    public Flyout(DockArea edge, Func<double> fraction, Action<double> setFraction);
    public void Open(TabPage page, bool takeFocus);   // BringToFront; clamps to parent; no animation
    public void Close();
    public bool IsOpen { get; }
}
```

- **Strip size:** the strip is `Font.Height + 12` pixels thick and recomputed on `FontChanged`.
- **Closing the flyout:**
  - an `IMessageFilter` added while the flyout is open closes it on `WM_LBUTTONDOWN`, `WM_RBUTTONDOWN` or `WM_MBUTTONDOWN` whose screen point is outside the flyout and outside the strip;
  - it also closes on `MainForm.Deactivate`;
  - and on `GotFocus` of any `Microsoft.Web.WebView2.WinForms.WebView2` found under `registry.Contents` outside the flyout, subscribed on `Open` and unsubscribed on `Close`;
  - and on `Esc` in `ProcessCmdKey` when the flyout contains focus;
  - when it was opened with `takeFocus`, it closes on `Leave`.
- **Resizing:** a 5 px grip on the inner edge drags the size, stored through `setFraction`, clamped to [0.1, 0.9] of the parent.
- **Tab menu:** Auto-hide area is checked when `IsAutoHidden` and disabled when `!CanAutoHide`.
- **Reveal:** `ShowPanel` and `FocusPanel` on a panel in an auto-hidden area call `Open(page, takeFocus: reveal is Explicit && from FocusPanel)`.

- [ ] **Step 1: Write the failing tests:**
  - "auto-hiding Right gives the space to Left and shows a strip with five names";
  - "the Auto-hide item is disabled for the sessions area";
  - "clicking a strip name opens a flyout over Left without changing its width": `SplitterDistance` is unchanged;
  - "a mouse-down outside closes the flyout, Esc closes it, and a Passive reveal opens it without taking focus from the grid";
  - "the flyout stays inside the window at MinimumSize";
  - "zoom 200% then 100%: the strip thickness tracks Font.Height";
  - "the accessible children are page tabs named after the panels".
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the strip and the flyout.
- [ ] **Step 4:** Run all tests and `eng/verify.ps1`. Expected: PASS.
- [ ] **Step 5:** Commit: `Auto-hide panel areas behind a flyout`.

### Task 8: Tab dragging

**Files:**
- Create: `TabDrag.cs`
- Modify: `DarkTabControl.cs` (raise drag start), `PanelHost.cs`
- Extend: `PanelHostTests`

**Interfaces:**
- `internal sealed class TabDrag`:
  - `public TabDrag(PanelHost host, IEnumerable<DarkTabControl> strips)`
  - `public void Begin(DarkTabControl source, int tabIndex, Point screenStart)`
- Add to `DarkTabControl`: `event EventHandler<(int Index, Point Screen)>? TabDragStarted`, raised once the mouse moves past `SystemInformation.DragSize` with the left button down on a tab.

**Behaviour:**
- **Capture:** `TabDrag` sets `Capture = true` on the source strip and tracks `MouseMove`.
- **Hit-testing:** it hit-tests every strip by `RectangleToScreen(stripRect)`, including floating strips, and paints a 2 px `Palette.Accent` marker at the insert index.
- **On mouse-up:**
  - over a strip in another area: `MoveTo(id, area)` at the index;
  - over the same strip: reorder;
  - outside `MainForm.Bounds` and every floating form: `FloatPanel(id, screenPoint)` if `CanFloat`.
- **Cancelling:** `Esc`, or `WM_CAPTURECHANGED` with no mouse-up, cancels the drag and leaves the layout unchanged.

- [ ] **Step 1: Write the failing tests.** Synthesise mouse messages with the existing `PostMessage` helpers in UI `Program`:
  - "dragging Log to the Bottom strip moves it";
  - "reordering within a strip";
  - "Esc mid-drag changes nothing";
  - "a short click under the drag threshold just selects the tab";
  - "a session OLE drop onto a strip still loads the Composer after a tab drag".
- [ ] **Step 2:** Run the tests. Expected: FAIL.
- [ ] **Step 3:** Implement the drag.
- [ ] **Step 4:** Run all tests and `eng/verify.ps1`. Expected: PASS.
- [ ] **Step 5:** Commit: `Drag panel tabs between areas`.

### Task 9: Docs and a final check in the real app

**Files:** `README.md`: add a "Window layout" section after "Shortcuts" and add Ctrl+Alt+1–6 to the shortcuts table.

- [ ] **Step 1:** Write the README section. Cover:
  - moving panels through the tab menu or by dragging a tab;
  - floating windows, and closing or double-clicking one to dock it;
  - Auto-hide area (not available for the area that holds Sessions);
  - View > Panels and Reset layout;
  - that downgrading Piper keeps the window size but drops the panel layout.
- [ ] **Step 2:** Run `powershell -NoProfile -ExecutionPolicy Bypass -File eng/verify.ps1`. Expected: it exits 0 with no warnings.
- [ ] **Step 3:** Commit: `Document movable panels`.
- [ ] **Step 4:** Launch Piper from the user's Terminal panel (not Claude's shell; see the memory note on the sandboxed profile) and show the user. Do not open a PR until the user approves.

---

## Spike result

_(filled in by Task 1)_
