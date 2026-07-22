# Changelog

All notable changes to this fork are documented here. Versions track the
`v<MAJOR>.<MINOR>.<PATCH>` git tags; the GitHub release for each tag carries the
built `HuntAndPeck-<tag>.zip`.

## [Unreleased]

### Added
- **Continuous trigger mode** (`OverlayTriggerMode`, hot-reload; Grid only): the
  overlay can stay up for repeated clicks until Esc / a mouse click, instead of closing
  after one. `OneClick` (default) is the old behavior; `Continuous` keeps labels on
  screen across clicks — e.g. `af`→navigate to a page, `bd`→click another button,
  `Space`→right-click mode, `aa`→open a context menu, `bb`→click a menu item, then
  `Esc`. Press the hotkey again while the overlay is up to toggle one-click ⇄
  continuous (badge bottom-left). In continuous mode the click mode reverts to Left
  after every click. Automation stays one-shot (its labels go stale on navigation).

### Changed
- **Default hotkey is now `Ctrl+Shift+M`** (was `Ctrl+Shift+Alt+F`). Alt is removed on
  purpose: pressing Alt — even inside a chord — dismisses an open context menu, so the
  old Alt-containing hotkey was closing the very menu you wanted to label-click.
  Startup-only; restart to apply. (`Alt`/`F10` are still not swallowed globally.)
- **Overlay input model**: label typing, Esc, Space, Tab and arrows are captured by
  `OverlayKeyboardHook` (a global LL keyboard hook), not by a focused `TextBox` /
  WPF `PreviewKeyDown`. A LL mouse hook restores click-to-dismiss. The overlay is now
  modeless (`Show()`), one at a time, and dismisses on match / Esc / any mouse click.
- `ForegroundWindow` exposes `ForceForegroundOnRender` / `CloseOnDeactivate` virtuals;
  `OverlayView` opts out of both, `DebugOverlayView` keeps the old behavior.

### Fixed
- **Open context menus / popups survive the hotkey.** Pressing the hotkey no longer
  dismisses an open right-click menu (File Manager, Edge, etc.). The overlay used to
  force itself to the foreground to capture typed label chars via WPF focus, and that
  foreground transfer is what closes open menus. It now shows **non-activated**
  (`ShowActivated=False`, no force-foreground) and reads typed input through a global
  low-level **keyboard hook** (`WH_KEYBOARD_LL`) instead of WPF focus, so it never
  steals foreground — you can even label-click items inside an open context menu.
- **Label highlight at start.** All labels are highlighted (yellow) when the overlay
  appears, narrowing as you type — restoring the behavior lost when input moved to the
  keyboard hook. (Re-highlighted again after each continuous-mode click.)

## [v2.1.0] — 2026-07-13

### Added
- **Full-screen overlay (`HintBoundsSource`)**: the overlay and its hint grid now
  cover the whole monitor the foreground window is on by default (`Screen`), so
  labels fill the screen regardless of window size. Set `Window` to restore the
  prior per-window behavior. Hot-reload.
- **Monitor cycling**: in Grid + Screen, **Tab** cycles the overlay across monitors
  (sorted left-to-right, then top-to-bottom), **Shift+Tab** cycles back. Each monitor
  shows its own labels; the typed prefix and pan offset reset on switch.
- **Options dialog**: the Overlay and Keyboard tabs expose every hot-reload setting
  (hint source/bounds, hint characters, grid density, click-mode order, timing log,
  nudge steps, hotkey) — no more hand-editing `hap.exe.config`.
- **Tray menu keyboard navigation**: the tray icon uses a WinForms `NotifyIcon` +
  `ContextMenuStrip`, so `Shift+F10` and right-click open a keyboard-navigable menu
  (arrows, `O` for Options, `E` for Exit). Esc closes.
- **First-char label grouping**: hint labels are sorted so consecutive labels share
  their first character (AA, AB, AC, …, BA, BB, …), which reads cleaner and lets the
  first char encode a spatial chunk.

### Changed
- **Config freshness cache**: `hap.exe.config` is re-parsed only when its last-write
  time changes (`OverlayActionConfig.EnsureFresh`), not once per read. A Grid +
  Screen press dropped from ~12 ms to ~2 ms `enum+merge` (measured). Hot-reload is
  preserved.
- **Incremental render**: each hint label is its own `DrawingVisual`, so a keystroke
  redraws only the changed labels instead of the whole overlay (better typing
  responsiveness at 1000+ labels).

### Fixed
- **Overlapping labels in full-screen mode**: in Grid + Screen the taskbar
  enumeration produced a second full-screen grid stacked on the foreground's,
  doubling every label (e.g. "KZ" + "M" rendered as "KZM"). The taskbar merge is now
  skipped in that mode.
- **Multi-monitor taskbar merge**: merging the primary taskbar when the target is on
  a secondary monitor no longer stretches the overlay across both displays.
