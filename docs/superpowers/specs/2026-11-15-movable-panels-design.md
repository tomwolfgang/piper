# Movable panels: design

## Why

Piper's main window is fixed. The session list sits on the left and a single tab strip holds
Inspectors, Composer, Filters, AutoResponder and Log on the right, and the split resets to 55% on
every start. Users want to arrange the window around how they work: Log under the sessions,
Composer beside Inspectors, the Inspector on a second monitor, and panels they never use hidden.
When they get it wrong, one click should put everything back.

## Goals

- Every panel can move between three docked areas (**Left**, **Right**, **Bottom**) or into its own
  **floating** window.
- Panels can be shown or hidden from **View > Panels**, and **View > Reset layout** puts back the
  default.
- Right-clicking a tab gives a context menu: Move to…, Float or Dock, Auto-hide area, Always on top,
  and Hide.
- Every panel can be reached from the keyboard.
- The layout survives a restart.

## Non-goals

- Visual Studio-style drop targets and arbitrary nested splits. There are exactly three areas.
- A Top area. The toolbar and trust bar already sit there.
- Named, saved layout presets. They can come later on the same store.
- New packages. DockPanel Suite was rejected: its maintainer is retiring, it targets older .NET,
  and it would bring a second theme system alongside `Palette`.

## Anti-goals (a failure even if it works)

- A corrupt, hand-edited or newer-version layout file stops Piper from opening, loses the saved
  main-window bounds, or leaves a panel unreachable.
- A panel or window ends up somewhere it cannot be recovered from, such as off-screen or with its
  title bar out of reach.
- A move breaks an existing flow:
  - *Send to Composer*, *Send to AutoResponder* and double-click to reveal the Inspector;
  - dragging a session onto a tab;
  - the check glyphs on the Filters and AutoResponder tabs;
  - `SelectWithoutFocus`, which keeps arrow-key focus in the grid;
  - keyboard shortcuts;
  - theme and zoom.
- Floating the Inspector weakens the WebView2 lock-down (CSP, navigation and download blocks).

## Approaches considered

| | Approach | Trade-off |
|---|---|---|
| A | DockPanel Suite | Most freedom, but a new dependency with a retiring maintainer and theme clash. |
| **B** | **In-house: three areas plus floating windows plus a VS-style auto-hide flyout** | **Chosen by the user.** Reuses `DarkTabControl` and `Palette`, adds no package, and is less freeform than VS. |
| C | Preset layouts only | Least code, but users still cannot move panels themselves. |

## Behaviour

### Panels and areas

There are six panels, and the set is closed: `sessions`, `inspectors`, `composer`, `filters`,
`autoresponder` and `log`. Each area is a `DarkTabControl`. Each panel is a `TabPage` in exactly one
area or one floating window.

**Default layout** (today's): Left holds `sessions`. Right holds `inspectors`, `composer`, `filters`,
`autoresponder` and `log`, with Inspectors selected. Bottom is empty. The Left/Right split is 55%,
and the Bottom area takes 35% of the height once something is in it, but never less than its
panels need: 260 px of page for the Inspectors or the Composer, 120 px otherwise, scaled.

```
+---------------------------+------------------------------------+
| [Sessions]                | [Inspectors][Composer][Filters]... |
|                           |                                    |
+---------------------------+------------------------------------+
| [Log]   (Bottom appears only when a panel is in it)            |
+----------------------------------------------------------------+
```

Structure: an outer horizontal `SplitContainer` holds the Left/Right split above and Bottom below.
An empty area collapses its side of the split.

### Moving

- **Tab context menu** (right-click, Shift+F10 or the Menu key):
  - Move to Left, Move to Right, Move to Bottom (the current area is greyed out);
  - Float, or Dock when the panel is already floating;
  - Auto-hide area (a check item; docked tabs only);
  - Always on top (a check item; floating only);
  - Hide.

  Reset layout is **not** on this menu, so it cannot be clicked by mistake next to Hide. A panel
  that moves becomes the selected tab where it lands.
- **Dragging a tab header** uses mouse capture on `DarkTabControl`, not OLE `DoDragDrop`. Sessions
  already use OLE drag onto these strips, and WebView2 is its own drop target, so mouse capture
  keeps the two from clashing. The drag:
  - starts past the system drag threshold, so an ordinary click still selects the tab;
  - shows an accent drop marker;
  - hit-tests every area and floating strip by screen point;
  - dropped on another strip, moves the panel there at the drop position;
  - dropped on its own strip, reorders it;
  - dropped outside the main window, floats it at the drop point;
  - is cancelled by **Esc** or `WM_CAPTURECHANGED`, which leaves the layout as it was.

### Floating windows

- One panel per window. Its strip hides its tab row, so the title bar alone names the panel; it
  refuses tab drops but still accepts session drops.
- Each window is owned by the main window. It minimises with Piper, stays above it and shows in
  Alt+Tab (it is not a tool window). `Palette.ApplyWindowChrome` is called on it for the dark title
  bar.
- The title is the panel's name. The window has no icon and no minimise or maximise box. It keeps
  the `Sizable` border rather than `SizableToolWindow`, whose `WS_EX_TOOLWINDOW` would take it out
  of Alt+Tab.
- The title bar stands in for the tab: right-click it for the tab menu, and drag it over an area's
  tabs or an auto-hidden area's strip to dock the panel there (Esc, which puts the window back,
  docks nothing).
- To dock it again, close it, double-click its title bar, or choose **Dock**. All three put the
  panel back in the area it came from.
- **Always on top** sets `TopMost`.
- **Keyboard:** a floating window passes `ProcessCmdKey` and `KeyDown` to the same dispatcher
  MainForm uses, so menu accelerators, Ctrl+R, Ctrl+X and the zoom keys work there too. The
  text-box check that decides "is the user typing" resolves the focused control from
  `Form.ActiveForm`, not from `MainForm.ActiveControl`.
- **Restoring a floating window:**
  - its size is clamped to at least the minimum size and at most the work area;
  - its caption strip must sit at least partly on some work area;
  - if it does not, the window moves onto the nearest screen;
  - a floating window whose bounds are unusable docks back into its area.

### Auto-hide area (the VS flyout)

- Every area starts pinned. **Auto-hide area** takes it out of the split layout, and the rest of
  the window takes over its space.
- The area's panels become a thin strip along that window edge, labelled with their names. Left and
  Right strips are vertical with sideways text, and the Bottom strip is horizontal.
- Each name on the strip has a page-tab accessible role, so screen readers can read it.
- Clicking a name slides that panel out as a flyout **overlaying** the neighbouring area. The flyout
  is a child control at the top of the z-order, with no animation or translucency. Splitters do not
  move and the grid does not reflow.
- The flyout closes on any of:
  - a mouse-down outside it (watched by a message filter that is removed when the flyout closes);
  - the main window's `Deactivate`;
  - focus moving into a WebView2 outside the flyout (needed because clicks inside WebView2 never
    reach the message filter);
  - Esc while it has focus.
- The flyout has a title bar, as a Visual Studio tool window does: the panel's name, a button
  listing the area's panels, a pin that docks the area again and a close button that hides the
  panel. It is accent-coloured while the focus is in the flyout, and right-clicking it opens the
  tab menu. The flyout's one-tab row is hidden.
- The flyout's inner edge can be dragged to resize it. The size is stored as a fraction of the
  window (45% by default) and clamped, and the flyout is never narrower than its panel needs
  (Composer 560 px, AutoResponder 460, Inspectors 420, Filters 380, Log 300, scaled) unless the
  window itself is.
- An area whose last panel leaves it stops auto-hiding, so a panel moved into it later is docked
  and shown.
- **The area holding `sessions` cannot be auto-hidden.** Its menu item is disabled, so the grid is
  never buried under a flyout.

### Show and hide

- **View > Panels** has a check item for each panel, showing its focus shortcut. Hiding a panel and
  showing it again puts it back in the area it was last in.
- `sessions` cannot be hidden. Its item is shown checked and disabled.
- **View > Reset layout** brings back the default:
  - every floating window docks;
  - every panel is shown;
  - every area is pinned;
  - the splitters reset;
  - the result is saved.
- **Hiding a panel is respected.** Only explicit commands un-hide one: *Send to…*, double-click, a
  menu item or a focus shortcut. Passive events never do. For example, an error is still written to
  the Log, but the Log stays hidden if the user hid it.

### Keyboard

- **Ctrl+Alt+1 to Ctrl+Alt+6** focus Sessions, Inspectors, Composer, Filters, AutoResponder and Log.
  Each one shows the panel, opens its flyout or brings its floating window forward, and moves focus
  into it.
- When focus leaves a flyout that was opened this way, the flyout closes.
- Existing shortcuts that target a panel, such as Ctrl+K and Ctrl+E for Composer, reveal it the same
  way.

### Programmatic reveal

The roughly 30 `_rightTabs` call sites and the hard-coded indexes (`SelectedIndex = 1`, `= 4`) are
replaced by one entry point, `ShowPanel(PanelId, PanelReveal)`. The reveal is either **Explicit** or
**Passive**.

1. If the panel is hidden, it is un-hidden only for an Explicit reveal. A Passive reveal of a
   hidden panel stops here.
2. The panel is selected in its strip with `SelectWithoutFocus`.
3. If its area is auto-hidden, the flyout opens without activating.
4. If it is floating, the window is shown and brought to the front with `Show` and `BringToFront`.
   `Activate` is not called, so the grid keeps focus.

`SetTabChecked` and the session-drop handlers attach to every strip, including floating ones.

### Theme, zoom and handles

- A **panel registry** owns all six panels whether they are shown, hidden or floating.
  - Theme toggle and font zoom walk the registry, not just `MainForm`'s control tree, so floating and
    hidden panels pick up changes.
  - A panel is re-themed when it is shown or docked.
  - The registry disposes every panel exactly once.
- Moving a panel can recreate its window handles, for example a floating window on a monitor with a
  different DPI. The session grid's selection, focused item and top item are saved before every move
  and restored after it.
- **WebView2 hardening:** `WebpView` and `VideoView` record *which* `CoreWebView2` instance they
  hardened instead of keeping a single `_hardened` flag. A recreated core is then always hardened
  again.

## Persistence

The layout extends the existing `%LOCALAPPDATA%\Piper\window-layout.json`. No new location is added.

- **What the file holds:**
  - a `Version` integer;
  - the existing main-window bounds and maximised flag;
  - a `Panels` array: id, area (`left|right|bottom|floating`), order, hidden, selected, floating
    bounds with the DPI they were saved at, and topmost;
  - an `Areas` object: auto-hidden and flyout fraction for each area, plus the splitter fractions.

  It holds nothing captured, only ids, numbers and booleans.
- **Tolerant reads:**
  - The main-window fields are parsed as today. `Panels` and `Areas` are read as `JsonElement`, and
    each entry is parsed on its own, so a bad entry falls back to its default without losing the
    rest of the file or the window bounds.
  - Strings, not a strict enum converter, are used for `area`; an unknown value such as `"top"` is
    treated as missing.
  - A file from a newer version is read best-effort, never rejected.
- **Bounded reads:** reads use the bounded reader that `AutoResponderSettingsStore` uses, capped at
  64 KiB.
- **Normalising what is read:**
  - unknown ids are dropped, and a duplicate id keeps its first entry;
  - a panel missing from the file takes its default place;
  - `order` is renumbered 0..n−1;
  - each area keeps at most one selected tab, and never a hidden one;
  - `sessions` is forced visible, and its area pinned;
  - NaN and infinite values are rejected *before* clamping;
  - fractions are clamped to [0.1, 0.9] and then turned into pixels within each `SplitContainer`'s
    panel minimum sizes, so `SplitterDistance` never throws;
  - every visible panel is checked to end up in some area.
- **Atomic saves:** the file is written to a temporary file and then moved over the original with
  `File.Move(..., overwrite: true)`, as `AutoResponderSettingsStore` does. Each save writes one
  snapshot of the whole layout.
- **Downgrade:** an older Piper keeps the window bounds but drops the panel layout the next time it
  saves. This is documented and accepted.

The placement model, the normaliser and the reset are a plain class with **no
`System.Windows.Forms` types**. The smoke tests link it the way they link `ColumnLayout.cs`. The
screen check takes the list of work areas as a parameter so it can be tested.

## Shutdown

`OnFormClosing` saves the layout snapshot first and then sets a shutting-down flag.

- A floating window closed with `CloseReason.FormOwnerClosing` neither docks nor saves.
- Each panel is handed back to the registry before its floating form is disposed. Panels are
  disposed only by the registry.
- The existing `_closeAfterShutdown` second pass must not save again.

## Delivery

The work ships as one branch, reviewed by the user in the running app before any PR. It is built
and committed in this order so that each step is reviewable and the risky part comes first:

1. **Spike (throwaway).** Move the Inspector, with WebView2 and HexBox, into a `Form` and back.
   Check rendering, the hardening and the handles. If floating the Inspector cannot be made safe,
   only the Inspector's Float item is dropped and the rest goes ahead.
2. **Refactor with no behaviour change.** Add the panel registry and `ShowPanel`, and remove
   `_rightTabs` and the hard-coded indexes. UI tests cover the existing flows.
3. **Model, store and docked areas.** Left, Right and Bottom areas; View > Panels; Reset; the tab
   context menu; focus shortcuts.
4. **Floating windows.** Including keyboard forwarding, theme and zoom, and shutdown.
5. **Auto-hide flyout.**
6. **Tab dragging.**

The user can choose to open steps 2 and 3, 4, and 5 and 6 as separate PRs.

## Tests

**Smoke tests: store**
- a round trip, including the existing window bounds and maximised flag;
- malformed JSON;
- an oversized file;
- an empty `Panels` array;
- a wrong JSON type for a field;
- unknown and duplicate ids;
- negative and huge `order` values;
- NaN, infinite and out-of-range fractions;
- `sessions` hidden or auto-hidden in the file;
- a newer version with extra fields;
- an old file with no `Panels` (gives the default layout);
- one bad entry leaving the rest of the file intact.

**Smoke tests: model**
- move, float and dock;
- hide and show return a panel to its last area;
- a Passive reveal does not un-hide;
- reset;
- an area becoming empty;
- every visible panel stays reachable;
- floating bounds that are off every screen, or have the caption off-screen, are clamped or
  docked;
- floating windows that are huge or overflow are clamped.

**UI tests (`Piper.UiTests`)** run each with the panel moved out of its default area:
- Send to Composer and Send to AutoResponder;
- double-click revealing the Inspector;
- a session drop onto a tab;
- the check glyphs;
- arrow keys keeping focus in the grid;
- shortcuts inside a floating window;
- theme and zoom reaching a floating window;
- floating the Inspector and docking it again: it still renders and is hardened;
- closing Piper with the Inspector floating: no exception, and everything disposed once.

## Strings and docs

- New strings live in `Locales/en.json` and are reached through `Strings.cs`.
- Menu items that show an accelerator are built with `Menus.Item`.
- The README gets a new **Window layout** section, and the new shortcuts go in **Shortcuts**. The
  existing "Layout" section describes the source tree and stays as it is.

## Left to the builder

- Class names and how the code is split across files. `MainForm` should get smaller.
- The look of the drop marker and the auto-hide strip, using `Palette` colours.
