# Arrow-Key Cursor Nudging + Real-Click Default

**Date:** 2026-06-29
**Author:** Yun Jiang
**Status:** Approved (pending implementation)
**Applies to:** `src/HuntAndPeck` (WPF / .NET Framework 4.5.1)

## Summary

Replace the current "match → Invoke-or-MoveMouse → close" hint action with a
two-mode interaction model inspired by Fluent Search:

1. **Default (no modifier): move + real click.** Completing a hint moves the
   cursor onto the target and fires a real synthesized left-click there, then
   closes the overlay. A real mouse click lands on anything clickable, including
   Chromium/Electron apps (Feishu) where the UI Automation `Invoke` pattern does
   not fire.
2. **Move-only mode (press `Space` first): continuous precision positioning.**
   The overlay stays open and becomes mouse-click-through. The user teleports
   the cursor by typing hint labels, fine-tunes with arrow keys, and clicks
   manually with the real mouse when satisfied. The overlay never auto-clicks in
   this mode.

Arrow-key nudging lives entirely inside move-only mode.

## Goals

- One-keystroke click on obvious targets (default mode).
- Precision aiming for small/off targets (move-only mode): jump near via a
  label, nudge with arrows, click manually.
- Works universally, including Chromium/Electron windows (grid mode + real
  click).
- No global mouse hook, no timers required to detect "done".

## Non-goals

- No arrow-key navigation *between* hint labels (this is cursor nudging, not
  label selection).
- No keyboard-triggered click in move-only mode (the user clicks with the real
  mouse).
- No persistent idle/auto-close timer (YAGNI; `Esc` + deactivation cover it).

## Current behavior (baseline)

- `OverlayViewModel.MatchString` (set-only, bound `OneWayToSource` to a
  `TextBox`) narrows hints by label prefix. When exactly one hint matches:
  - `HintAction == "MoveMouse"` → `Hint.MoveMouseToCenter()` (grid points jump
    via `SetCursorPos`).
  - otherwise → `Hint.Invoke()` (UI Automation Invoke pattern).
  - then `CloseOverlay()` (= `view.Close()`), ending the modal `ShowDialog()`.
- `OverlayView` is `Topmost`, `AllowsTransparency=True`, `Background=Transparent`.
  Keys are handled in `OverlayView_OnPreviewKeyDown` (only `Escape` → close).
- `User32.cs` has `SetCursorPos` but no `GetCursorPos`, `mouse_event`, or
  window-extended-style accessors.

## Target design

### Interaction model

**Mode 1 — Default (real click).** Entered when no `Space` has been pressed.

- Letters update `MatchString`; hints narrow as today.
- On unique match:
  1. `matching[0].Hint.MoveMouseToCenter()` (cursor onto target).
  2. Read the cursor position (`GetCursorPos`) → `clickPoint`.
  3. `PerformClickAndClose(clickPoint)` (view callback): make the overlay
     mouse-click-through, synthesize a left click at `clickPoint`, then close.

**Mode 2 — Move-only.** Entered by pressing `Space` while the overlay is open.

- `Space` (caught in `PreviewKeyDown`, never reaches the `TextBox`) calls
  `EnterMoveOnlyMode()`: set `IsMoveOnlyMode = true`, capture the current cursor
  position into `_nudgeX/_nudgeY`, make the overlay mouse-click-through, show
  the indicator. The overlay stays open.
- While in move-only mode:
  - **Letters** update `MatchString`. On unique match →
    `matching[0].Hint.MoveMouseToCenter()`, refresh `_nudgeX/_nudgeY` from
    `GetCursorPos`, then `ResetInput()` (clear the `TextBox` so the next label
    can be typed). The overlay stays open; **no click**.
  - **Arrows** (`Shift`-aware) → `Nudge(±step)`: update `_nudgeX/_nudgeY` and
    `SetCursorPos`, then `ResetInput()`. Stay open.
  - **Other keys** are swallowed in `PreviewKeyDown` (so the `TextBox` does not
    accumulate junk), except letters which are allowed through to drive
    `MatchString`.
- Finish:
  - **Real mouse click** → passes through to the app (click-through) → the app
    window activates → the overlay raises `Deactivated` → overlay closes. One
    click both activates the target and dismisses the overlay.
  - **`Esc`** → close without clicking.

### Why click-through makes this race-free

A real or synthesized click while the overlay is still open would otherwise hit
the overlay's own HWND (it is `Topmost`). Adding `WS_EX_TRANSPARENT` to the
overlay's extended window style makes it transparent to **mouse** hit-testing
only — keyboard focus and key routing are unaffected. Therefore:

- In mode 1, the synthesized `mouse_event` click is dispatched after
  `WS_EX_TRANSPARENT` is applied, so it falls through to the app window beneath.
- In mode 2, the user's real click falls through to the app and activates it,
  firing `Deactivated` on the overlay.

No `Close`-then-click timing race, no global mouse hook, no timer.

### Step sizes

- Plain arrows: `NudgeStep` px (default **3**).
- `Shift`+arrows: `NudgeStepFast` px (default **15**).
- Both hot-reload via `hap.exe.config`. Deltas live in the same coordinate
  space as the existing (verified-working) `SetCursorPos` MoveMouse, so they are
  consistent at any DPI.

## Component changes

### `src/NativeMethods/User32.cs` (add)

```csharp
[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
public static extern bool GetCursorPos(out POINT lpPoint);

[DllImport("user32.dll")]
public static extern void mouse_event(uint dwFlags, uint dx, uint dy,
    uint cButtons, uint dwExtraInfo);

public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
public const uint MOUSEEVENTF_LEFTUP   = 0x0004;

[DllImport("user32.dll")]
public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

[DllImport("user32.dll")]
public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

public const int GWL_EXSTYLE = -20;
public const int WS_EX_TRANSPARENT = 0x00000020;
```

`POINT` already exists (used by `PhysicalToLogicalPoint`). `GetWindowLong`/
`SetWindowLong` are safe for `GWL_EXSTYLE` on 64-bit (the value fits in 32 bits).

### `src/HuntAndPeck/ViewModels/OverlayViewModel.cs`

- New INPC property: `bool IsMoveOnlyMode`.
- New read-only property: `string MoveOnlyHint` — the indicator text shown in
  move-only mode (e.g. `move-only • ↑↓←→ nudge (shift=fast) • type label to
  jump • click/Esc to finish`).
- Fields: `private int _nudgeX, _nudgeY;`
- New callbacks (set by the view):
  - `Action<Point> PerformClickAndClose` — move+click+close in mode 1.
  - `Action ResetInput` — clear the `TextBox` between labels in mode 2.
  - `Action CloseOverlay` (existing).
- `void EnterMoveOnlyMode()`:
  - `IsMoveOnlyMode = true;`
  - `GetCursorPos` → `_nudgeX/_nudgeY`.
- `void Nudge(int dx, int dy)`:
  - `_nudgeX += dx; _nudgeY += dy; User32.SetCursorPos(_nudgeX, _nudgeY);`
  - `ResetInput?.Invoke();`
- `MatchString` setter (restructured):
  - Compute `matching` and update `Active` states exactly as today.
  - If `matching.Count == 1`:
    - Always: `matching[0].Hint.MoveMouseToCenter();`
    - Always: `GetCursorPos` → refresh `_nudgeX/_nudgeY` (so nudging continues
      from the new position).
    - If `IsMoveOnlyMode`: `ResetInput?.Invoke();` (stay open, no click).
    - Else (mode 1): `PerformClickAndClose?.Invoke(new Point(_nudgeX, _nudgeY));`
      (closes).
- `ShouldMoveMouseInsteadOfClick()` is replaced by `ReadClickMode()` returning
  an enum/string: `RealClick` (default) or `Invoke`. In `Invoke` mode, the
  mode-1 branch calls `matching[0].Hint.Invoke()` instead of
  `PerformClickAndClose`, then `CloseOverlay?.Invoke()`. Mode 2 (move-only) is
  identical regardless of `ClickMode`.

### `src/HuntAndPeck/Views/OverlayView.xaml`

- Add a small `TextBlock` (the move-only indicator) bound to a VM string
  `MoveOnlyHint`; visibility tied to `IsMoveOnlyMode` via a `DataTrigger`
  (`Visible` when true, `Collapsed` otherwise). Positioned at a corner.
- Wire `Deactivated="OverlayView_OnDeactivated"` on the window.

### `src/HuntAndPeck/Views/OverlayView.xaml.cs`

- `OnLoaded`: capture the VM; assign callbacks:
  - `vm.PerformClickAndClose = p => { SetClickThrough(true); User32.SetCursorPos((int)p.X, (int)p.Y); DoLeftClick(); Close(); };`
  - `vm.ResetInput = () => MatchStringControl.Clear();`
  - `vm.CloseOverlay = () => Close();` (existing).
- `OnPreviewKeyDown`:
  - `Key.Escape` → `Close()`; handled.
  - `Key.Space` → if `!vm.IsMoveOnlyMode`: `vm.EnterMoveOnlyMode()` and
    `SetClickThrough(true)`; handled (swallow so it never enters the `TextBox`).
  - If `vm.IsMoveOnlyMode`:
    - Arrows (`Up/Down/Left/Right`), with `Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)`
      selecting fast vs. fine step (read from config) → `vm.Nudge(dx, dy)`;
      handled.
    - Letter keys: **not** handled (let them reach the `TextBox` to drive
      `MatchString`).
    - Other keys: handled (swallowed) — labels are letters, so only letter keys
      need to reach the `TextBox`.
  - Else (mode 1): letter keys pass through to the `TextBox` to drive matching;
    other keys pass through (current behavior).
- `OnDeactivated`: if `vm.IsMoveOnlyMode` → `Close()`.
- Helpers:
  - `void SetClickThrough(bool on)`: toggle `WS_EX_TRANSPARENT` in the extended
    style via `GetWindowLong`/`SetWindowLong` on the window's HWND
    (`WindowInteropHelper(this).Handle`).
  - `void DoLeftClick()`: `mouse_event(MOUSEEVENTF_LEFTDOWN, 0,0,0,0);` then
    `mouse_event(MOUSEEVENTF_LEFTUP, 0,0,0,0);` (uses current cursor position).

### `src/HuntAndPeck/App.config`

- Replace the `HintAction` entry with `ClickMode`:
  - `<add key="ClickMode" value="RealClick" />` (default; values: `RealClick`,
    `Invoke`).
- Add:
  - `<add key="NudgeStep" value="3" />`
  - `<add key="NudgeStepFast" value="15" />`
- Remove the obsolete `HintAction` entry (and `NudgeEnabled`, which was never
  shipped — nudge is now gated by `Space`, not a setting). Update comment prose
  to describe the two modes and the `Space` gesture.

## Edge cases & decisions

- **Letters still type while click-through is on.** `WS_EX_TRANSPARENT` affects
  mouse hit-testing only; the focused `TextBox` keeps receiving keystrokes, so
  label matching continues to work in move-only mode.
- **Labels stay visible in move-only mode** (the user must read them to type).
  Click-through ensures real clicks still reach the app over label pixels.
- **Input reset semantics.** Both a unique-label jump and any arrow press clear
  the `TextBox` in move-only mode, so every discrete action starts a fresh
  label. Predictable mental model: jump/nudge → type a new label.
- **`Space` mid-typing.** If `Space` is pressed after a non-unique prefix (e.g.
  `A`), move-only mode engages and the existing prefix continues to narrow; the
  next letter completes normally as a move-only jump. Intended usage is
  `Space`-first, but mid-typing is harmless.
- **`Invoke` mode preserved** as a setting for apps where the UIA Invoke pattern
  is preferred. Default is `RealClick` because it works on Chromium.
- **DPI.** `SetCursorPos`/`GetCursorPos` share one coordinate space (already
  verified working for the existing MoveMouse path), so nudge deltas are
  consistent. The step values are small and user-tunable.
- **No idle timer.** If the user neither clicks nor presses `Esc`, the overlay
  stays until one of those happens. Accepted trade-off (YAGNI); `Esc` always
  exits.

## Testing

Manual (on the Windows target, since this is WPF/.NET-Framework, built only on
Windows / CI):

1. **Default click:** in a native window (e.g. Notepad controls) and in Feishu
   (Chromium), press `Alt+;`, type a unique label → cursor moves and the target
   is clicked; overlay closes.
2. **Move-only teleport:** press `Alt+;`, press `Space`, type a label → cursor
   jumps, overlay stays open, indicator visible; type a second label → cursor
   jumps again; `TextBox` resets after each jump.
3. **Nudge:** in move-only mode, arrows move the cursor ~3 px; `Shift`+arrows
   ~15 px; `TextBox` resets on each arrow.
4. **Finish by click:** in move-only mode, click the real mouse on a target →
   the target is clicked **and** the overlay closes (deactivation).
5. **Cancel:** `Esc` closes the overlay in both modes with no click.
6. **Hot-reload:** edit `NudgeStep`/`NudgeStepFast`/`ClickMode` in
   `hap.exe.config`, save, re-trigger → new values apply.
7. **Grid mode (default config):** repeat 1–6 with `HintSource=Grid`.

CI: existing `.github/workflows/build.yml` must still build green (compile +
tests). No new automated UI tests (WPF input simulation is out of scope); the
xUnit project continues to compile.

## Out of scope / future

- Optional idle/auto-close timer for move-only mode if users report the overlay
  lingering.
- Optional visual crosshair drawn at the cursor during nudging (currently the
  visible Windows cursor is the feedback).
- Click-drag, right-click, or double-click synthesis (only single left-click
  here).
